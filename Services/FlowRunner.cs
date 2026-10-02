using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services.Engine;
using ScheduleApp.Services.Testing;

namespace ScheduleApp.Services;

/// <summary>
/// Chạy flow của các công việc theo hàng đợi — mỗi lúc chỉ một flow, tránh hai flow cùng điều khiển chuột/phím.
/// </summary>
public sealed class FlowRunner
{
    private readonly IUserNotifier _ui;
    private readonly Func<Guid, Job?> _findJob;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<Guid> _pending = [];
    private CancellationTokenSource _stopAll = new();

    /// <summary>Trạng thái hiện tại (mô tả bước đang chạy). Có thể phát từ luồng nền.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>(jobId, thời điểm bắt đầu, thành công?, thông điệp). Có thể phát từ luồng nền.</summary>
    public event Action<Guid, DateTime, bool, string>? JobFinished;

    /// <summary>Flow bắt đầu / kết thúc chạy (true = bắt đầu). Có thể phát từ luồng nền.</summary>
    public event Action<bool>? RunningChanged;

    /// <summary>Kiểm tra trước mỗi bước (chế độ an toàn).</summary>
    public Func<FlowContext, Task>? BeforeStep { get; set; }

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

        bool entered = false;
        var started = DateTime.Now;
        try
        {
            if (_gate.CurrentCount == 0) Log.Info($"[{job.Name}] chờ trong hàng đợi…");
            await _gate.WaitAsync(stopToken);
            entered = true;
            started = DateTime.Now;
            RunningChanged?.Invoke(true);

            // Kịch bản kiểm thử chạy riêng lẻ (theo lịch, thủ công…) cũng có báo cáo của nó; chạy theo bộ thì bộ tự ghi báo cáo chung.
            options ??= RunOptions.Default;
            string? reportFolder = null;
            if (job.IsTestCase && options.Recorder == null)
            {
                reportFolder = TestReport.NewFolder(job.Name);
                options = options.With(new TestRecorder(Path.Combine(reportFolder, "shots")));
            }

            var result = await Task.Run(() => RunFlowAsync(job, trigger, options, stopToken));
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
            if (entered)
            {
                _gate.Release();
                RunningChanged?.Invoke(false);
            }
            StatusChanged?.Invoke(IsBusy ? "Đang chờ flow tiếp theo…" : "Sẵn sàng");
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

    private async Task<FlowResult> RunFlowAsync(Job job, string trigger, RunOptions options, CancellationToken ct)
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
                StatusChanged?.Invoke($"Đang chạy \"{job.Name}\" — bước {i + 1}/{total}: {job.Steps[i].Describe()}");
                options.StepStarted?.Invoke(i);
            }
        };
        var ctx = new FlowContext(job, _ui, wrapped, _findJob, ct) { BeforeStep = BeforeStep };
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

        IDisposable? screen = null;
        IDisposable? awake = null;
        try
        {
            if (NeedsScreen(job, 0)) screen = _ui.ClearScreenForAutomation();
            if (SettingsStore.Current.PreventSleepWhileRunning) awake = PowerHelper.KeepAwake($"ScheduleApp đang chạy \"{job.Name}\"");

            var result = await FlowEngine.RunAsync(job, ctx, options.StartIndex, isRoot: true);

            if (!result.Ok && job.OnFailureJobId is Guid cleanupId && _findJob(cleanupId) is { } cleanup && cleanup.Id != job.Id)
            {
                Log.Warn($"   ↪ Chạy công việc xử lý lỗi \"{cleanup.Name}\"…");
                ctx.Vars["failed.message"] = result.Message;
                ctx.Vars["failed.step"] = result.FailedStep.ToString();
                ctx.Depth = 1;
                var r = await FlowEngine.RunAsync(cleanup, ctx, 0, isRoot: false);
                if (!r.Ok) Log.Error($"   Công việc xử lý lỗi cũng thất bại: {r.Message}");
            }
            await CleanupTestDataAsync(job, ctx);

            if (result.Ok) Log.Info($"✔ Hoàn thành \"{job.Name}\".");
            else Log.Warn($"◼ \"{job.Name}\": {result.Message}");
            return result;
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
            screen?.Dispose();
        }
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
}
