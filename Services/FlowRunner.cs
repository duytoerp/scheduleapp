using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services.Engine;
using ScheduleApp.Services.Testing;

namespace ScheduleApp.Services;

/// <summary>
/// Chạy flow của các công việc theo hàng đợi — mỗi lúc chỉ một flow giữ lượt chạy, tránh hai flow cùng điều khiển chuột/phím.
/// Flow đang chờ người dùng (nhắc nhở chờ bấm OK) tạm nhường lượt cho công việc khác; công việc đặt thời gian chạy tối đa
/// bị dừng khi quá giờ — một công việc không chặn được mọi công việc khác mãi mãi.
/// </summary>
public sealed class FlowRunner
{
    private readonly IUserNotifier _ui;
    private readonly Func<Guid, Job?> _findJob;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<Guid> _pending = [];
    private CancellationTokenSource _stopAll = new();

    /// <summary>Công việc đang giữ lượt chạy — cho dòng nhật ký "Đang chờ công việc … chạy xong".</summary>
    private string? _holder;

    /// <summary>Công việc đang nhường lượt để chờ người dùng xác nhận (nhắc nhở chờ bấm OK).</summary>
    private readonly List<string> _awaitingUser = [];

    /// <summary>Trạng thái hiện tại (mô tả bước đang chạy). Có thể phát từ luồng nền.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>(jobId, thời điểm bắt đầu, thành công?, thông điệp). Có thể phát từ luồng nền.</summary>
    public event Action<Guid, DateTime, bool, string>? JobFinished;

    /// <summary>
    /// Có / hết flow giữ lượt chạy (true = bắt đầu giữ): lúc flow bắt đầu / kết thúc, và khi flow tạm nhường lượt chờ người dùng
    /// rồi lấy lại. Lượt kế tiếp luôn báo "true" sau "false" của lượt trước. Có thể phát từ luồng nền.
    /// </summary>
    public event Action<bool>? RunningChanged;

    /// <summary>Tiến độ flow đang chạy (bắt đầu, từng bước, kết thúc) — cho khung trạng thái ở góc màn hình. Có thể phát từ luồng nền.</summary>
    public event Action<RunProgress>? Progress;

    /// <summary>Kiểm tra trước mỗi bước (chế độ an toàn).</summary>
    public Func<FlowContext, Task>? BeforeStep { get; set; }

    private FlowContext? _active;

    /// <summary>Tạm dừng flow đang chạy trước bước kế tiếp (rồi chờ Bước tiếp / Chạy tiếp / Dừng). False nếu không có flow nào đang chạy.</summary>
    public bool RequestPause()
    {
        lock (_sync)
        {
            if (_active == null) return false;
            _active.RequestPause();
        }
        Log.Info("⏸ Đã yêu cầu tạm dừng — flow sẽ dừng trước bước kế tiếp.");
        return true;
    }

    public FlowRunner(IUserNotifier ui, Func<Guid, Job?> findJob)
    {
        _ui = ui;
        _findJob = findJob;
    }

    /// <summary>Số lần người dùng bấm dừng tất cả — bộ kiểm thử dùng để biết cần ngừng chạy các kịch bản còn lại.</summary>
    public int StopCount { get; private set; }

    public bool IsBusy
    {
        get { lock (_sync) return _pending.Count > 0; }
    }

    /// <summary>Chờ lượt chạy lâu hơn chừng này thì ghi nhật ký đang chờ công việc nào.</summary>
    internal TimeSpan QueueNoticeAfter { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Độ dài một phút của <see cref="Job.MaxRunMinutes"/> — kiểm thử đặt ngắn để khỏi chờ cả phút.</summary>
    internal TimeSpan MaxRunMinute { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Đưa công việc vào hàng đợi; task hoàn thành khi flow chạy xong. Null nếu bị bỏ qua / hủy trong hàng đợi.</summary>
    public async Task<FlowResult?> EnqueueAsync(Job job, string trigger, RunOptions? options = null)
    {
        string? envName = null;
        if (job.IsTestCase && options?.Recorder == null)
        {
            // Kịch bản kiểm thử chạy riêng lẻ (theo lịch, nút Chạy…): dùng biến của môi trường đang chọn (biến truyền vào vẫn được ưu tiên).
            var env = TestEnvironments.Current();
            envName = env?.Name;
            if (env != null) options = (options ?? RunOptions.Default).WithVariables(TestEnvironments.Merge(TestEnvironments.Variables(env), options?.Variables));
            if (!string.IsNullOrWhiteSpace(job.DataFile) && options?.StepMode != true && options?.StartIndex is null or 0)
            {
                // Kiểm thử theo dữ liệu: mỗi dòng một lần chạy, chung một báo cáo.
                var suite = await TestSuite.RunAsync(this, [job], job.Name, null,
                    new SuiteOptions { Environment = env?.Name, Variables = options?.Variables });
                return new FlowResult(suite.Ok, TestReport.Summary(suite.Cases));
            }
        }

        CancellationToken stopToken;
        lock (_sync)
        {
            if (!_pending.Add(job.Id))
            {
                Log.Warn($"[{job.Name}] đang chạy hoặc đã trong hàng đợi — bỏ qua lần kích hoạt ({trigger}).");
                return null;
            }
            stopToken = _stopAll.Token;
        }

        using var run = new RunState(job.Name, stopToken);
        var started = DateTime.Now;
        RunProgress? progress = null;
        try
        {
            await EnterAsync(run, stopToken);
            started = DateTime.Now;
            // Thời gian chạy tối đa tính từ lúc tới lượt — không tính lúc chờ trong hàng đợi.
            var ct = run.Start(job.MaxRunMinutes, MaxRunMinute);

            // Kịch bản kiểm thử chạy riêng lẻ (theo lịch, thủ công…) cũng có báo cáo của nó; chạy theo bộ thì bộ tự ghi báo cáo chung.
            options ??= RunOptions.Default;
            progress = new RunProgress(job.Id, job.Name, trigger, started, -1, job.Steps.Count, "", options.IsTest) { Overlay = EffectiveOverlay(job) };
            Progress?.Invoke(progress);
            string? reportFolder = null;
            if (job.IsTestCase && options.Recorder == null)
            {
                reportFolder = TestReport.NewFolder(job.Name);
                options = options.With(new TestRecorder(Path.Combine(reportFolder, "shots")));
            }

            var p = progress;
            var result = await Task.Run(() => RunFlowAsync(job, trigger, options, p, run, ct));
            Progress?.Invoke(progress with { Ok = result.Ok, Message = result.Message, FailedStep = result.FailedStep });
            progress = null;
            string? report = null;
            if (reportFolder != null)
            {
                try
                {
                    report = TestReport.Write(reportFolder, job.Name, [TestCaseResult.Create(job, started, DateTime.Now, result, options.Recorder!)], envName);
                    Log.Info($"   📄 Báo cáo kiểm thử: {report}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("   Không ghi được báo cáo kiểm thử: " + ex.Message);
                }
            }
            var record = new RunRecord
            {
                JobId = job.Id,
                JobName = job.Name,
                Trigger = trigger,
                Start = started,
                End = DateTime.Now,
                Ok = result.Ok,
                Message = result.Message,
                FailedStep = Math.Max(0, result.FailedStep),
                Screenshot = result.Screenshot,
                Report = report
            };
            RunHistory.Add(record);
            JobFinished?.Invoke(job.Id, started, result.Ok, result.Message);
            if (!result.Ok) _ui.Notify($"Flow \"{job.Name}\" không hoàn thành", result.Message, true);
            if (!options.IsTest) _ = NotificationService.SendForRunAsync(job, record);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"[{job.Name}] đã hủy khi đang chờ trong hàng đợi.");
            JobFinished?.Invoke(job.Id, started, false, "Đã hủy (trong hàng đợi)");
            return null;
        }
        finally
        {
            lock (_sync) _pending.Remove(job.Id);
            if (progress != null) Progress?.Invoke(progress with { Ok = false, Message = "Đã dừng" });
            // Không làm gì nếu lượt đã nhả (vd bị dừng / quá giờ lúc đang chờ người dùng bấm OK).
            Exit(run);
            StatusChanged?.Invoke(IsBusy ? WaitingStatus() : "Sẵn sàng");
        }
    }

    /// <summary>Dừng flow đang chạy và hủy mọi flow đang chờ.</summary>
    public void StopAll()
    {
        lock (_sync)
        {
            if (_pending.Count == 0) return;
            StopCount++;
            _stopAll.Cancel();
            _stopAll = new CancellationTokenSource();
        }
        Log.Warn("■ Đã yêu cầu dừng tất cả flow.");
    }

    private async Task<FlowResult> RunFlowAsync(Job job, string trigger, RunOptions options, RunProgress progress, RunState run, CancellationToken ct)
    {
        int total = job.Steps.Count;
        var wrapped = new RunOptions
        {
            StartIndex = options.StartIndex,
            IsTest = options.IsTest,
            StepMode = options.StepMode,
            UseBreakpoints = options.UseBreakpoints,
            Variables = options.Variables,
            Recorder = options.Recorder,
            StepStarted = i =>
            {
                run.Step = i;
                StatusChanged?.Invoke($"Đang chạy \"{job.Name}\" — bước {i + 1}/{total}: {job.Steps[i].Describe()}");
                Progress?.Invoke(progress with { Step = i, StepText = Log.Redact(job.Steps[i].Describe()), StepStarted = DateTime.Now });
                options.StepStarted?.Invoke(i);
            }
        };
        var ctx = new FlowContext(job, _ui, wrapped, _findJob, ct)
        {
            BeforeStep = BeforeStep,
            GateRelease = (c, wait) => WaitWithoutTurnAsync(run, c, wait)
        };
        lock (_sync) _active = ctx;
        try
        {
            return await RunFlowCoreAsync(job, trigger, options, ctx, total, progress, run);
        }
        finally
        {
            lock (_sync)
                if (_active == ctx) _active = null;
        }
    }

    private async Task<FlowResult> RunFlowCoreAsync(Job job, string trigger, RunOptions options, FlowContext ctx, int total, RunProgress progress,
        RunState run)
    {
        ctx.Vars["job.name"] = job.Name;
        ctx.Vars["run.trigger"] = trigger;
        ctx.Vars["run.start"] = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        ctx.Vars["computer"] = Environment.MachineName;
        ctx.Vars["user"] = Environment.UserName;
        ctx.Vars["lastError"] = "";
        ctx.Vars["lastOutput"] = "";
        Log.Info($"▶ Bắt đầu \"{job.Name}\" ({trigger}) — {total} bước" + (options.StartIndex > 0 ? $", từ bước {options.StartIndex + 1}" : ""));
        // Biến của môi trường / dòng dữ liệu kiểm thử — công thức "=hoten()"… được sinh mới ở mỗi lần chạy.
        if (options.Variables != null)
            foreach (var (k, v) in options.Variables) ctx.Vars[k] = ctx.InitialValue(k, v);

        IDisposable? awake = null;
        FlowResult? result = null;
        try
        {
            run.NeedsScreen = NeedsScreen(job, 0);
            ClearScreen(run);
            if (SettingsStore.Current.PreventSleepWhileRunning) awake = PowerHelper.KeepAwake($"ScheduleApp đang chạy \"{job.Name}\"");

            try
            {
                result = await FlowEngine.RunAsync(job, ctx, options.StartIndex, isRoot: true);
            }
            catch (OperationCanceledException) when (run.Expired(ctx.Ct))
            {
                result = TimedOut(job, run);
            }
            await AfterFlowAsync(job, ctx, run, result, progress);
        }
        catch (OperationCanceledException) when (result != null && (run.TimedOut || run.Expired(ctx.Ct)))
        {
            // Flow đã có kết quả, chỉ công việc xử lý lỗi / dọn dữ liệu test bị dừng giữa chừng → giữ kết quả của flow
            // (quá giờ rồi người dùng bấm Dừng vẫn ghi là quá giờ).
            Log.Warn($"   ■ Dừng phần xử lý sau flow — {(run.Expired(ctx.Ct) ? "quá thời gian chạy tối đa" : "người dùng bấm Dừng")}.");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"■ \"{job.Name}\" đã bị dừng.");
            return new FlowResult(false, "Đã dừng");
        }
        catch (Exception ex)
        {
            Log.Error($"✖ \"{job.Name}\" lỗi: {ex.Message}");
            await CleanupTestDataAsync(job, ctx);
            return new FlowResult(false, ex.Message, -1, ctx.LastScreenshot);
        }
        finally
        {
            awake?.Dispose();
            RestoreScreen(run);
        }

        if (result.Ok) Log.Info($"✔ Hoàn thành \"{job.Name}\".");
        else Log.Warn($"◼ \"{job.Name}\": {result.Message}");
        return result;
    }

    /// <summary>Sau flow chính: công việc xử lý lỗi (khi thất bại) rồi dọn dữ liệu test.</summary>
    private async Task AfterFlowAsync(Job job, FlowContext ctx, RunState run, FlowResult result, RunProgress progress)
    {
        var cleanup = !result.Ok && job.OnFailureJobId is Guid cleanupId && _findJob(cleanupId) is { } j && j.Id != job.Id ? j : null;
        if (run.TimedOut)
        {
            // Quá giờ lúc đang nhường lượt (chờ bấm OK): công việc xử lý lỗi có thể dùng chuột/phím → chờ lấy lại lượt
            // (chỉ nút Dừng hủy được). Nó và phần dọn dữ liệu test được thêm tối đa chừng ấy thời gian nữa.
            if (cleanup != null) await ResumeTurnAsync(run, ctx, run.Stop);
            ctx.Ct = run.Extend();
        }
        if (cleanup != null)
        {
            Log.Warn($"   ↪ Chạy công việc xử lý lỗi \"{cleanup.Name}\"…");
            // Công việc xử lý lỗi đặt "Không hiện" khung trạng thái (vd phát video / trình chiếu) → ẩn khung trong lúc nó chạy;
            // kết quả cuối của lần chạy vẫn hiện theo cài đặt của công việc chính.
            if (progress.Overlay == RunOverlayMode.Default && EffectiveOverlay(cleanup) == RunOverlayMode.Hide)
                Progress?.Invoke(progress with { Overlay = RunOverlayMode.Hide, StepText = Log.Redact("Xử lý lỗi: " + cleanup.Name) });
            ctx.Vars["failed.message"] = result.Message;
            ctx.Vars["failed.step"] = result.FailedStep.ToString();
            ctx.Depth = 1;
            var r = await FlowEngine.RunAsync(cleanup, ctx, 0, isRoot: false);
            if (!r.Ok) Log.Error($"   Công việc xử lý lỗi cũng thất bại: {r.Message}");
        }
        await CleanupTestDataAsync(job, ctx);
    }

    /// <summary>Flow chính bị dừng vì quá thời gian chạy tối đa — ghi là lỗi (khác với người dùng bấm Dừng).</summary>
    private FlowResult TimedOut(Job job, RunState run)
    {
        run.TimedOut = true;
        var message = $"Quá thời gian chạy tối đa {run.MaxMinutes} phút — đã dừng";
        Log.Warn($"⏱ \"{job.Name}\": {message}" + (run.Step >= 0 ? $" ở bước {run.Step + 1}." : "."));
        // Ảnh màn hình lúc bị treo — chỉ khi đang giữ lượt (lúc nhường lượt, trên màn hình là việc của công việc khác).
        bool held;
        lock (_sync) held = run.HasTurn;
        var shot = held ? ErrorScreenshots.Capture(job.Name, run.Step + 1) : null;
        return new FlowResult(false, message, run.Step + 1, shot);
    }

    // ───────────────────────────── Lượt chạy ─────────────────────────────

    /// <summary>
    /// Chờ tới lượt chạy (hủy được). Chờ lâu hơn <see cref="QueueNoticeAfter"/> thì ghi một lần đang chờ công việc nào;
    /// lượt chờ không bị hủy rồi xếp lại nên thứ tự hàng đợi giữ nguyên.
    /// </summary>
    private async Task EnterAsync(RunState run, CancellationToken ct)
    {
        var wait = _gate.WaitAsync(ct);
        using var waiting = new CancellationTokenSource();
        if (!wait.IsCompleted) _ = NoticeWaitingAsync(run.JobName, waiting.Token);
        try
        {
            await wait;
            lock (_sync)
            {
                run.HasTurn = true;
                _holder = run.JobName;
            }
        }
        finally
        {
            waiting.Cancel();
        }
        RunningChanged?.Invoke(true);
    }

    /// <summary>Sau <see cref="QueueNoticeAfter"/> mà vẫn chờ lượt chạy thì ghi nhật ký đang chờ công việc nào.</summary>
    private async Task NoticeWaitingAsync(string jobName, CancellationToken stillWaiting)
    {
        try
        {
            await Task.Delay(QueueNoticeAfter, stillWaiting);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        string? holder;
        lock (_sync) holder = _holder;
        Log.Info($"[{jobName}] " + (holder != null ? $"Đang chờ công việc \"{holder}\" chạy xong…" : "Đang chờ flow trước chạy xong…"));
    }

    /// <summary>Nhả lượt chạy, trả cửa sổ ScheduleApp về chỗ cũ. Không làm gì nếu đã nhả — không bao giờ nhả hai lần.</summary>
    private void Exit(RunState run)
    {
        lock (_sync)
        {
            if (!run.HasTurn) return;
            run.HasTurn = false;
            _holder = null;
        }
        try
        {
            RestoreScreen(run);
            // Báo trước khi nhả: lượt kế tiếp chỉ báo "true" sau "false" này (chế độ an toàn theo dõi đúng flow đang giữ lượt).
            RunningChanged?.Invoke(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><see cref="FlowContext.RunWithoutInputGateAsync"/>: nhường lượt chạy trong lúc chờ, lấy lại trước khi chạy tiếp.</summary>
    private async Task WaitWithoutTurnAsync(RunState run, FlowContext ctx, Func<Task> wait)
    {
        // Gọi khi còn giữ lượt: cửa sổ nhắc nhở hiện ra (và lấy focus) trước khi công việc khác bắt đầu thao tác.
        var task = wait();
        bool release;
        lock (_sync)
        {
            release = run.HasTurn && !task.IsCompleted;
            if (release) _awaitingUser.Add(run.JobName);
        }
        if (!release)
        {
            await task;
            return;
        }
        Log.Info("      ⏸ Chờ bạn xác nhận — tạm nhường lượt chạy, công việc khác trong hàng đợi vẫn chạy được.");
        Exit(run);
        StatusChanged?.Invoke(WaitingStatus());
        try
        {
            await task;
        }
        finally
        {
            lock (_sync) _awaitingUser.Remove(run.JobName);
            // Lấy lại lượt trước khi chạy tiếp — kể cả khi bước lỗi (flow có thể bỏ qua lỗi, chạy tiếp). Bị dừng / quá giờ thì không:
            // flow kết thúc ngay; công việc xử lý lỗi sau khi quá giờ tự lấy lại lượt.
            if (!ctx.Ct.IsCancellationRequested) await ResumeTurnAsync(run, ctx, ctx.Ct);
        }
        // Bấm OK đúng lúc bị dừng / quá giờ: không trả về khi chưa lấy lại lượt.
        ctx.Ct.ThrowIfCancellationRequested();
    }

    /// <summary>Lấy lại lượt chạy đã nhường (chờ nếu công việc khác đang chạy); không làm gì nếu đang giữ.</summary>
    private async Task ResumeTurnAsync(RunState run, FlowContext ctx, CancellationToken ct)
    {
        lock (_sync)
            if (run.HasTurn) return;
        await EnterAsync(run, ct);
        lock (_sync) _active = ctx;
        ClearScreen(run);
        Log.Info("      ▶ Đã lấy lại lượt chạy — chạy tiếp.");
    }

    /// <summary>Dời cửa sổ ScheduleApp khỏi vùng thao tác (flow cần màn hình) — mỗi lần giữ lượt chạy.</summary>
    private void ClearScreen(RunState run)
    {
        if (run.NeedsScreen && run.Screen == null) run.Screen = _ui.ClearScreenForAutomation();
    }

    private static void RestoreScreen(RunState run)
    {
        var screen = run.Screen;
        run.Screen = null;
        screen?.Dispose();
    }

    /// <summary>Trạng thái khi không flow nào đang chạy bước nhưng còn flow chờ: chờ người dùng xác nhận, hoặc chờ trong hàng đợi.</summary>
    private string WaitingStatus()
    {
        lock (_sync) return _awaitingUser.Count > 0 ? $"\"{_awaitingUser[^1]}\" đang chờ bạn xác nhận…" : "Đang chờ flow tiếp theo…";
    }

    /// <summary>"Tự xóa dữ liệu test": xóa các bản ghi Dynamics 365 flow đã tạo — chạy cả khi flow thất bại (không chạy khi người dùng dừng).</summary>
    private static async Task CleanupTestDataAsync(Job job, FlowContext ctx)
    {
        if (!job.CleanupTestData || string.IsNullOrWhiteSpace(ctx.Vars.GetValueOrDefault(D365Client.CreatedVar)) || ctx.Ct.IsCancellationRequested) return;
        Log.Info("   🧹 Dọn dữ liệu test đã tạo…");
        try
        {
            await D365Client.CleanupAsync(ctx, "", ctx.Ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("   Không dọn hết dữ liệu test: " + ex.Message);
        }
    }

    /// <summary>
    /// Cài đặt khung trạng thái cho cả lần chạy: theo công việc; công việc "Theo cài đặt chung" mà gọi (kể cả gọi lồng) một công việc
    /// "Không hiện" — vd công việc phát video / trình chiếu — thì cả lần chạy cũng không hiện: khung luôn nằm trên cùng sẽ che video,
    /// và công việc con không báo tiến độ riêng nên không thể chỉ ẩn trong lúc nó chạy.
    /// </summary>
    internal RunOverlayMode EffectiveOverlay(Job job) =>
        job.RunOverlay != RunOverlayMode.Default ? job.RunOverlay
        : CallsHiddenOverlayJob(job, 0) ? RunOverlayMode.Hide
        : RunOverlayMode.Default;

    private bool CallsHiddenOverlayJob(Job job, int depth)
    {
        if (depth > 8) return false;
        foreach (var s in job.Steps)
        {
            if (!s.Enabled || s.Type != StepType.CallJob || s.JobRef is not Guid id || _findJob(id) is not { } sub) continue;
            if (sub.RunOverlay == RunOverlayMode.Hide || CallsHiddenOverlayJob(sub, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>Flow (kể cả các công việc con được gọi) có giả lập chuột/phím hoặc đọc màn hình không.</summary>
    private bool NeedsScreen(Job job, int depth)
    {
        if (depth > 8) return false;
        foreach (var s in job.Steps)
        {
            if (!s.Enabled) continue;
            if (s.Type == StepType.CallJob)
            {
                if (s.JobRef is Guid id && _findJob(id) is { } sub && NeedsScreen(sub, depth + 1)) return true;
                continue;
            }
            if (s.UsesInput || s.UsesScreen) return true;
        }
        return false;
    }

    /// <summary>
    /// Một lần chạy trong hàng đợi: lượt chạy (đang giữ / đã nhường), thời gian chạy tối đa, cửa sổ ScheduleApp đã dời đi,
    /// bước đang chạy.
    /// </summary>
    private sealed class RunState(string jobName, CancellationToken stop) : IDisposable
    {
        private TimeSpan? _limit;
        private CancellationTokenSource? _main, _extra;

        public string JobName { get; } = jobName;

        /// <summary>Người dùng bấm Dừng (dừng tất cả flow).</summary>
        public CancellationToken Stop { get; } = stop;

        /// <summary>Đang giữ lượt chạy — đọc / ghi trong lock của <see cref="FlowRunner"/>.</summary>
        public bool HasTurn { get; set; }

        /// <summary>Flow cần dời cửa sổ ScheduleApp khỏi màn hình mỗi khi giữ lượt; <see cref="Screen"/> khác null khi đang dời.</summary>
        public bool NeedsScreen { get; set; }

        public IDisposable? Screen { get; set; }

        /// <summary>Bước của flow gốc đang chạy (-1 = chưa tới bước nào).</summary>
        public int Step { get; set; } = -1;

        /// <summary>Thời gian chạy tối đa (phút) của lần chạy, 0 = không giới hạn.</summary>
        public int MaxMinutes { get; private set; }

        /// <summary>Flow chính đã bị dừng vì quá thời gian chạy tối đa.</summary>
        public bool TimedOut { get; set; }

        /// <summary>Bắt đầu tính giờ; trả về token bị hủy khi người dùng bấm Dừng hoặc quá thời gian chạy tối đa.</summary>
        public CancellationToken Start(int maxMinutes, TimeSpan minute)
        {
            MaxMinutes = Math.Clamp(maxMinutes, 0, Job.MaxRunMinutesLimit);
            _limit = MaxMinutes > 0 ? minute * MaxMinutes : null;
            return (_main = Linked()).Token;
        }

        /// <summary>Sau khi quá giờ: token mới cho công việc xử lý lỗi / dọn dữ liệu test — thêm tối đa chừng ấy thời gian nữa.</summary>
        public CancellationToken Extend() => (_extra ??= Linked()).Token;

        /// <summary><paramref name="ct"/> bị hủy vì quá giờ chứ không phải vì người dùng bấm Dừng.</summary>
        public bool Expired(CancellationToken ct) => ct.IsCancellationRequested && !Stop.IsCancellationRequested;

        private CancellationTokenSource Linked()
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(Stop);
            if (_limit is { } limit) cts.CancelAfter(limit);
            return cts;
        }

        public void Dispose()
        {
            _main?.Dispose();
            _extra?.Dispose();
        }
    }
}

/// <summary>
/// Trạng thái flow đang chạy cho khung trạng thái: <see cref="Step"/> = -1 khi vừa bắt đầu (chưa tới bước nào);
/// <see cref="Ok"/> khác null khi flow đã kết thúc.
/// </summary>
public sealed record RunProgress(Guid JobId, string JobName, string Trigger, DateTime Started, int Step, int Total, string StepText, bool IsTest)
{
    public DateTime StepStarted { get; init; } = Started;
    public bool? Ok { get; init; }
    public string Message { get; init; } = "";
    public int FailedStep { get; init; } = -1;
    /// <summary>Cài đặt khung trạng thái riêng của công việc.</summary>
    public RunOverlayMode Overlay { get; init; }

    /// <summary>Có hiện khung trạng thái cho lần chạy này không (<paramref name="setting"/> = cài đặt chung).</summary>
    public bool ShowOverlay(bool setting) => Overlay switch
    {
        RunOverlayMode.Show => true,
        RunOverlayMode.Hide => false,
        _ => setting
    };
}
