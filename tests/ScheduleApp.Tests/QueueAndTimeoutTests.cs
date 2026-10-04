using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Một công việc không chặn được mọi công việc khác mãi mãi: thời gian chạy tối đa, nhắc nhở chờ bấm OK nhường lượt chạy,
/// nhật ký "đang chờ công việc nào", JSON của file cũ không đổi.
/// </summary>
public class QueueAndTimeoutTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "hết giờ chờ: " + what);
    }

    /// <summary>
    /// Khối "Nếu (luôn sai) … Hết khối Nếu" chứa bước đọc màn hình: flow được coi là cần màn hình (dời cửa sổ ScheduleApp đi)
    /// nhưng không bao giờ thật sự đọc màn hình hay gửi chuột / phím.
    /// </summary>
    private static ActionStep[] NeedsScreenButNeverUsesIt() =>
    [
        S(StepType.If, s => { s.Condition = ConditionKind.Compare; s.Target = "a"; s.CompareOp = CompareOp.Equals; s.Arguments = "b"; }),
        S(StepType.WaitForImage),
        S(StepType.EndIf)
    ];

    private static ActionStep Reminder(string text, bool wait = true) => S(StepType.Reminder, s => { s.WaitForUser = wait; s.Text = text; });

    private static ActionStep Note(string text) => S(StepType.LogMessage, s => s.Text = text);

    /// <summary>Ghi lại "giữ / nhả lượt chạy" (RunningChanged) theo thứ tự.</summary>
    private static Func<List<bool>> TrackTurns(FlowRunner runner)
    {
        var turns = new List<bool>();
        runner.RunningChanged += running => { lock (turns) turns.Add(running); };
        return () => { lock (turns) return [.. turns]; };
    }

    [Fact]
    public async Task MaxRunTimeStopsTheRunAndRecordsItAsFailed()
    {
        var ui = new WaitingUi();
        // Công việc xử lý lỗi nhận {{failed.message}} / {{failed.step}} như mọi lần thất bại.
        var onFailure = new Job { Name = "Xử lý lỗi", Steps = [Reminder("{{failed.message}} @ {{failed.step}}", wait: false)] };
        var job = new Job
        {
            Name = "Bị treo",
            MaxRunMinutes = 2,
            OnFailureJobId = onFailure.Id,
            Steps = [Note("bắt đầu"), S(StepType.Wait, s => s.DelayMs = 120_000)]
        };
        // Một "phút" = 0,5 giây → giới hạn 2 phút = 1 giây.
        var runner = new FlowRunner(ui, id => id == onFailure.Id ? onFailure : null) { MaxRunMinute = TimeSpan.FromMilliseconds(500) };
        var finished = new List<(bool Ok, string Message)>();
        runner.JobFinished += (_, _, ok, message) => { lock (finished) finished.Add((ok, message)); };
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };

        var sw = Stopwatch.StartNew();
        var r = await runner.EnqueueAsync(job, "theo lịch").WaitAsync(Long);
        sw.Stop();

        const string reason = "Quá thời gian chạy tối đa 2 phút — đã dừng";
        Assert.False(r!.Ok);
        Assert.Equal((reason, 2), (r.Message, r.FailedStep));      // dừng ở bước 2 (bước Chờ)
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(20));
        var record = RunHistory.All.Last(x => x.JobId == job.Id);
        Assert.Equal((false, reason, 2), (record.Ok, record.Message, record.FailedStep));
        lock (finished) Assert.Equal([(false, reason)], finished);
        lock (events) Assert.Equal((false, reason, 2), (events[^1].Ok, events[^1].Message, events[^1].FailedStep));
        Assert.Equal([reason + " @ 2"], ui.Reminders);                    // công việc xử lý lỗi đã chạy
        Assert.Contains(ui.Notifications, n => n.Contains(reason));       // thông báo lỗi như mọi lần thất bại
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public async Task StopIsNotATimeoutAndFastRunsAreUnaffected()
    {
        var ui = new WaitingUi();
        var onFailure = new Job { Name = "Xử lý lỗi", Steps = [Reminder("xử lý lỗi", wait: false)] };
        var runner = new FlowRunner(ui, id => id == onFailure.Id ? onFailure : null) { MaxRunMinute = TimeSpan.FromSeconds(10) };

        // Chạy xong trước giới hạn → thành công như thường.
        var fast = new Job { Name = "Nhanh", MaxRunMinutes = 1, OnFailureJobId = onFailure.Id, Steps = [Note("xong")] };
        var r = await runner.EnqueueAsync(fast, "thử").WaitAsync(Long);
        Assert.True(r!.Ok, r.Message);

        // Bấm Dừng trước khi hết giờ → "Đã dừng" như trước, không phải lỗi quá giờ, không chạy công việc xử lý lỗi.
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };
        var slow = new Job { Name = "Dừng tay", MaxRunMinutes = 5, OnFailureJobId = onFailure.Id, Steps = [S(StepType.Wait, s => s.DelayMs = 120_000)] };
        var run = runner.EnqueueAsync(slow, "thử");
        await WaitUntil(() => { lock (events) return events.Count > 0 && events[^1].JobId == slow.Id && events[^1].Step == 0; }, "bước Chờ bắt đầu");
        runner.StopAll();
        r = await run.WaitAsync(Long);
        Assert.Equal((false, "Đã dừng"), (r!.Ok, r.Message));
        Assert.Equal((false, "Đã dừng"), (RunHistory.All.Last(x => x.JobId == slow.Id).Ok, RunHistory.All.Last(x => x.JobId == slow.Id).Message));
        Assert.Empty(ui.Reminders);
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public async Task ReminderWaitingForOkLetsOtherJobsRun()
    {
        var ui = new WaitingUi();
        var runner = new FlowRunner(ui, _ => null);
        var turns = TrackTurns(runner);
        using var log = new LogCapture();
        var a = new Job { Name = "Chờ cắm USB", Steps = [.. NeedsScreenButNeverUsesIt(), Reminder("Cắm USB rồi bấm OK"), Note("A chạy tiếp")] };
        var b = new Job { Name = "Sao lưu", Steps = [.. NeedsScreenButNeverUsesIt(), Note("B chạy")] };

        var runA = runner.EnqueueAsync(a, "theo lịch");
        await WaitUntil(() => ui.OpenReminders == 1 && turns().SequenceEqual([true, false]), "A nhường lượt chạy");
        Assert.Equal(0, ui.Cleared);                                  // trong lúc chờ, cửa sổ ScheduleApp đã về chỗ cũ

        // B chạy xong ngay trong lúc A vẫn chờ bấm OK.
        var rb = await runner.EnqueueAsync(b, "theo lịch").WaitAsync(Long);
        Assert.True(rb!.Ok, rb.Message);
        Assert.False(runA.IsCompleted);
        Assert.True(runner.IsBusy);
        Assert.Equal(-1, log.IndexOf("A chạy tiếp"));
        Assert.Equal(-1, log.IndexOf("Đang chờ công việc"));             // B không phải chờ A

        ui.ClickOk();
        var ra = await runA.WaitAsync(Long);
        Assert.True(ra!.Ok, ra.Message);
        Assert.Equal([true, false, true, false, true, false], turns()); // A giữ, A nhường, B giữ, B nhả, A lấy lại, A nhả
        Assert.Equal((0, 1), (ui.Cleared, ui.MaxCleared));            // dời / trả cửa sổ cân bằng, không lồng nhau
        int released = log.IndexOf("tạm nhường lượt chạy"), bRan = log.IndexOf("📝 B chạy");
        int resumed = log.IndexOf("Đã lấy lại lượt chạy"), aRan = log.IndexOf("📝 A chạy tiếp");
        Assert.True(released >= 0 && released < bRan && bRan < resumed && resumed < aRan, string.Join("\n", log.Lines));
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public async Task StopWhileWaitingReleasesEverything()
    {
        var ui = new WaitingUi();
        var runner = new FlowRunner(ui, _ => null) { QueueNoticeAfter = TimeSpan.FromMilliseconds(200) };
        var turns = TrackTurns(runner);
        using var log = new LogCapture();
        Job Waiting(string name) => new() { Name = name, Steps = [Reminder("Bấm OK để chạy tiếp"), Note(name + " xong")] };

        // 1. Đang chờ bấm OK (đã nhường lượt) thì bấm Dừng: cửa sổ nhắc đóng, lần chạy kết thúc "Đã dừng".
        var runA = runner.EnqueueAsync(Waiting("A"), "thử");
        await WaitUntil(() => ui.OpenReminders == 1 && turns().Count == 2, "A nhường lượt chạy");
        runner.StopAll();
        var ra = await runA.WaitAsync(Long);
        Assert.Equal((false, "Đã dừng"), (ra!.Ok, ra.Message));
        Assert.Equal(0, ui.OpenReminders);
        Assert.False(runner.IsBusy);

        // 2. Bấm OK lúc công việc khác đang giữ lượt → chờ lấy lại lượt; bấm Dừng lúc đó: cả hai dừng.
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };
        var slow = new Job { Name = "Chậm", Steps = [S(StepType.Wait, s => s.DelayMs = 120_000)] };
        var runC = runner.EnqueueAsync(Waiting("C"), "thử");
        await WaitUntil(() => ui.OpenReminders == 1 && turns().Count == 4, "C nhường lượt chạy");
        var runSlow = runner.EnqueueAsync(slow, "thử");
        await WaitUntil(() => { lock (events) return events.Any(e => e.JobId == slow.Id && e.Step == 0); }, "công việc chậm chạy");
        ui.ClickOk();
        await WaitUntil(() => log.IndexOf("[C] Đang chờ công việc \"Chậm\" chạy xong…") >= 0, "C chờ lấy lại lượt");
        runner.StopAll();
        var rc = await runC.WaitAsync(Long);
        var rs = await runSlow.WaitAsync(Long);
        Assert.Equal((false, "Đã dừng"), (rc!.Ok, rc.Message));
        Assert.Equal((false, "Đã dừng"), (rs!.Ok, rs.Message));
        Assert.Equal(-1, log.IndexOf("C xong"));

        // Lượt chạy không bị giữ lại, không nhả hai lần (nhả thừa sẽ ném SemaphoreFullException): công việc mới chạy được ngay.
        var rq = await runner.EnqueueAsync(new Job { Name = "Sau khi dừng", Steps = [Note("chạy được")] }, "thử").WaitAsync(Long);
        Assert.True(rq!.Ok, rq.Message);
        Assert.False(runner.IsBusy);
        var all = turns();
        Assert.Equal(all.Count(t => t), all.Count(t => !t));
    }

    [Fact]
    public async Task MaxRunTimeCountsWhileWaitingForOk()
    {
        var ui = new WaitingUi();
        var onFailure = new Job { Name = "Báo lỗi", Steps = [Reminder("{{failed.message}}", wait: false)] };
        var job = new Job
        {
            Name = "Chờ người duyệt",
            MaxRunMinutes = 2,
            OnFailureJobId = onFailure.Id,
            Steps = [Note("bắt đầu"), Reminder("Duyệt rồi bấm OK"), Note("đã duyệt")]
        };
        var runner = new FlowRunner(ui, id => id == onFailure.Id ? onFailure : null) { MaxRunMinute = TimeSpan.FromMilliseconds(400) };
        var turns = TrackTurns(runner);
        using var log = new LogCapture();

        var r = await runner.EnqueueAsync(job, "theo lịch").WaitAsync(Long);
        const string reason = "Quá thời gian chạy tối đa 2 phút — đã dừng";
        Assert.Equal((false, reason, 2), (r!.Ok, r.Message, r.FailedStep));
        Assert.Equal(0, ui.OpenReminders);                                // cửa sổ nhắc đã đóng
        Assert.Equal(["Duyệt rồi bấm OK", reason], ui.Reminders);          // rồi công việc xử lý lỗi chạy
        Assert.Equal([true, false, true, false], turns());                // nhường lượt lúc chờ, lấy lại cho công việc xử lý lỗi
        Assert.Equal(-1, log.IndexOf("đã duyệt"));
        Assert.Equal((false, reason), (RunHistory.All.Last(x => x.JobId == job.Id).Ok, RunHistory.All.Last(x => x.JobId == job.Id).Message));
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public async Task TimeoutWhileWaitingToTakeTheTurnBackDoesNotWaitForTheOtherJob()
    {
        var ui = new WaitingUi();
        var runner = new FlowRunner(ui, _ => null) { MaxRunMinute = TimeSpan.FromSeconds(1), QueueNoticeAfter = TimeSpan.FromMilliseconds(100) };
        var turns = TrackTurns(runner);
        using var log = new LogCapture();
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };
        var a = new Job { Name = "Chờ duyệt", MaxRunMinutes = 3, Steps = [Reminder("Duyệt rồi bấm OK"), Note("đã duyệt")] };
        var slow = new Job { Name = "Chậm", Steps = [S(StepType.Wait, s => s.DelayMs = 120_000)] };

        var runA = runner.EnqueueAsync(a, "theo lịch");
        await WaitUntil(() => ui.OpenReminders == 1 && turns().Count == 2, "A nhường lượt chạy");
        var runSlow = runner.EnqueueAsync(slow, "theo lịch");
        await WaitUntil(() => { lock (events) return events.Any(e => e.JobId == slow.Id && e.Step == 0); }, "công việc chậm chạy");
        ui.ClickOk();                                                     // A chờ lấy lại lượt — "Chậm" còn chạy rất lâu
        await WaitUntil(() => log.IndexOf("[Chờ duyệt] Đang chờ công việc \"Chậm\" chạy xong…") >= 0, "A chờ lấy lại lượt");

        // Hết 3 "phút" trong lúc chờ lượt: A dừng ngay và ghi là quá giờ, không phải chờ "Chậm" chạy xong.
        var ra = await runA.WaitAsync(Long);
        Assert.Equal((false, "Quá thời gian chạy tối đa 3 phút — đã dừng", 1), (ra!.Ok, ra.Message, ra.FailedStep));
        Assert.False(runSlow.IsCompleted);
        Assert.Equal(-1, log.IndexOf("đã duyệt"));

        runner.StopAll();
        var rs = await runSlow.WaitAsync(Long);
        Assert.Equal((false, "Đã dừng"), (rs!.Ok, rs.Message));
        // Lượt chờ bị hủy không để lại lượt "treo": công việc mới chạy được ngay, "giữ / nhả" cân bằng.
        var rq = await runner.EnqueueAsync(new Job { Name = "Sau đó", Steps = [Note("chạy được")] }, "thử").WaitAsync(Long);
        Assert.True(rq!.Ok, rq.Message);
        var all = turns();
        Assert.Equal(all.Count(t => t), all.Count(t => !t));
    }

    [Fact]
    public async Task ReminderThatFailsStillTakesTheTurnBackBeforeContinuing()
    {
        // Cửa sổ nhắc lỗi bất thường, bước đặt "bỏ qua lỗi": flow chạy tiếp — phải lấy lại lượt chạy trước.
        var ui = new WaitingUi();
        var runner = new FlowRunner(ui, _ => null);
        var turns = TrackTurns(runner);
        var reminder = Reminder("Bấm OK");
        reminder.OnError = ErrorAction.Continue;
        var job = new Job { Name = "Bỏ qua lỗi", Steps = [reminder, Note("chạy tiếp")] };

        var run = runner.EnqueueAsync(job, "thử");
        await WaitUntil(() => ui.OpenReminders == 1 && turns().Count == 2, "nhường lượt chạy");
        ui.Fail(new InvalidOperationException("cửa sổ nhắc bị đóng bất thường"));
        var r = await run.WaitAsync(Long);
        Assert.Equal((false, "Xong, 1 bước lỗi"), (r!.Ok, r.Message));
        Assert.Equal([true, false, true, false], turns());
    }

    [Fact]
    public async Task LogsOnceWhichJobItIsWaitingForAndKeepsQueueOrder()
    {
        var runner = new FlowRunner(new FakeUi(), _ => null) { QueueNoticeAfter = TimeSpan.FromMilliseconds(300) };
        using var log = new LogCapture();
        var started = new List<string>();
        runner.Progress += p => { if (p is { Step: -1, Ok: null }) lock (started) started.Add(p.JobName); };
        var a = new Job { Name = "Báo cáo dài", Steps = [Note("a")] };
        var b = new Job { Name = "B", Steps = [Note("b")] };
        var c = new Job { Name = "C", Steps = [Note("c")] };
        // "Báo cáo dài" giữ lượt chạy tới khi kiểm thử cho chạy tiếp (BeforeStep chạy lúc đang giữ lượt).
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.BeforeStep = ctx => ctx.RootJob.Id == a.Id ? hold.Task : Task.CompletedTask;

        var runA = runner.EnqueueAsync(a, "thử");
        await WaitUntil(() => { lock (started) return started.Count == 1; }, "A chạy");
        var runB = runner.EnqueueAsync(b, "thử");
        await Task.Delay(150);
        var runC = runner.EnqueueAsync(c, "thử");
        // B chờ quá QueueNoticeAfter (ghi nhật ký) lúc C còn đang chờ: A xong ngay lúc đó thì B vẫn phải chạy trước C
        // (ghi nhật ký không làm B mất chỗ trong hàng đợi).
        await WaitUntil(() => log.IndexOf("[B] Đang chờ công việc") >= 0, "B ghi đang chờ");
        hold.SetResult();
        await Task.WhenAll(runA, runB, runC).WaitAsync(Long);

        lock (started) Assert.Equal(["Báo cáo dài", "B", "C"], started);
        Assert.Single(log.Lines, l => l.Contains("[B] Đang chờ công việc \"Báo cáo dài\" chạy xong…"));

        // Chờ ngắn (dưới 3 giây mặc định) thì không ghi gì.
        var quick = new FlowRunner(new FakeUi(), _ => null);
        var d = new Job { Name = "D", Steps = [Note("d")] };
        var e = new Job { Name = "E", Steps = [Note("e")] };
        await Task.WhenAll(quick.EnqueueAsync(d, "thử"), quick.EnqueueAsync(e, "thử")).WaitAsync(Long);
        Assert.DoesNotContain(log.Lines, l => l.Contains("[E] Đang chờ"));
    }

    [Fact]
    public void OldFilesStayUnchangedWhenThereIsNoLimit()
    {
        // Mẫu nhúng sẵn là file kiểu cũ (chưa có MaxRunMinutes): đọc rồi ghi lại không thêm trường mới.
        var resource = typeof(Job).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("vi-du-mau.json"));
        using var stream = typeof(Job).Assembly.GetManifestResourceStream(resource)!;
        var original = new StreamReader(stream).ReadToEnd();
        Assert.DoesNotContain("MaxRunMinutes", original);
        var jobs = JsonSerializer.Deserialize<List<Job>>(original, JsonDefaults.Options)!;
        Assert.NotEmpty(jobs);
        Assert.All(jobs, j => Assert.Equal(0, j.MaxRunMinutes));
        var json = JsonSerializer.Serialize(jobs, JsonDefaults.Options);
        Assert.DoesNotContain("MaxRunMinutes", json);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<List<Job>>(json, JsonDefaults.Options), JsonDefaults.Options));

        var job = new Job { Name = "Tổng hợp số liệu" };
        var before = JsonSerializer.Serialize(job, JsonDefaults.Options);
        job.MaxRunMinutes = 45;
        var limited = JsonSerializer.Serialize(job, JsonDefaults.Options);
        Assert.Contains("\"MaxRunMinutes\": 45", limited);
        Assert.Equal(45, JsonSerializer.Deserialize<Job>(limited, JsonDefaults.Options)!.MaxRunMinutes);
        Assert.Equal(45, job.Clone().MaxRunMinutes);
        Assert.Equal(45, JobStore.ImportJson($"[{limited}]").Single().MaxRunMinutes);
        // Bỏ giới hạn → JSON y như lúc chưa đặt.
        job.MaxRunMinutes = 0;
        Assert.Equal(before, JsonSerializer.Serialize(job, JsonDefaults.Options));
    }

    [Fact]
    public void EditorEditsAndValidatesMaxRunTime()
    {
        Sta(() =>
        {
            var job = new Job { Name = "Tổng hợp số liệu", MaxRunMinutes = 30 };
            var ui = new FakeUi();
            using var f = new JobEditorForm(job, new FlowRunner(ui, _ => null), [job], ui, isNew: false)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000)
            };
            f.Show();
            Application.DoEvents();
            var num = Field<NumericUpDown>(f, "_numMaxRun");
            Assert.Equal((30m, 0m, Job.MaxRunMinutesLimit), (num.Value, num.Minimum, (int)num.Maximum));
            Assert.False(f.HasUnsavedChanges);

            // Ở tab "Lỗi · thông báo", cạnh "Dừng flow khi một bước bị lỗi"; mọi ô trong tab đều thấy đủ (không bị cắt),
            // cả khi cửa sổ thu nhỏ hết cỡ.
            var page = Ancestor<TabPage>(num);
            Assert.Equal("Lỗi · thông báo", page.Text);
            ((TabControl)page.Parent!).SelectedTab = page;
            foreach (var size in new[] { f.Size, f.MinimumSize })
            {
                f.Size = size;
                Application.DoEvents();
                foreach (var c in Descendants(page).Where(c => c.Visible))
                {
                    var r = page.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
                    Assert.True(page.ClientRectangle.Contains(r), $"\"{c.Text}\" {r} bị cắt (trang {page.ClientRectangle}, cửa sổ {size})");
                }
            }

            // Lưu bị cảnh báo (một bước dài hơn giới hạn) → đưa người dùng về đúng ô, kể cả khi đang mở rộng sơ đồ (F11, tab bị ẩn).
            var tabs = (TabControl)page.Parent!;
            tabs.SelectedIndex = 0;
            Field<Action>(f, "_toggleCanvas")();
            Assert.False(tabs.Visible);
            typeof(JobEditorForm).GetMethod("FocusOnTab", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f, [num]);
            Assert.True(tabs.Visible);
            Assert.Same(page, tabs.SelectedTab);

            num.Value = 0;
            Assert.True(f.HasUnsavedChanges);
            var saved = new Job { MaxRunMinutes = 99 };
            ApplyTo(f, saved);
            Assert.Equal(0, saved.MaxRunMinutes);
            num.Value = 90;
            ApplyTo(f, saved);
            Assert.Equal(90, saved.MaxRunMinutes);
            f.AskSaveChanges = _ => DialogResult.No;
            f.Close();
        });

        // File sửa tay có giá trị ngoài khoảng: mở được, đưa về trong khoảng.
        foreach (var (stored, shown) in new[] { (-5, 0), (999_999, Job.MaxRunMinutesLimit) })
        {
            Sta(() =>
            {
                var job = new Job { Name = "Sửa tay", MaxRunMinutes = stored };
                var ui = new FakeUi();
                using var f = new JobEditorForm(job, new FlowRunner(ui, _ => null), [job], ui, isNew: false);
                Assert.Equal(shown, (int)Field<NumericUpDown>(f, "_numMaxRun").Value);
            });
        }
    }

    [Fact]
    public void WarnsWhenOneStepAloneIsLongerThanTheLimit()
    {
        var steps = new List<ActionStep>
        {
            S(StepType.Wait, s => s.DelayMs = 5 * 60_000),
            S(StepType.If, s => { s.Enabled = false; s.Target = "1"; s.Arguments = "1"; }),
            S(StepType.Wait, s => s.DelayMs = 60 * 60_000),                     // trong khối Nếu đã tắt → không chạy
            S(StepType.EndIf),
            S(StepType.PlayMedia, s => { s.Text = @"D:\video\hop-dau-tuan.mp4"; s.MediaDurationMs = 20 * 60_000; }),
            S(StepType.Wait, s => { s.Enabled = false; s.DelayMs = 90 * 60_000; })
        };
        Assert.Null(JobEditorForm.StepLongerThan(steps, 0));                   // không giới hạn
        Assert.Null(JobEditorForm.StepLongerThan(steps, 30));
        Assert.Equal(4, JobEditorForm.StepLongerThan(steps, 10));              // video 20 phút
        Assert.Equal(0, JobEditorForm.StepLongerThan(steps, 4));               // chờ 5 phút
        steps[1].Enabled = true;
        Assert.Equal(2, JobEditorForm.StepLongerThan(steps, 30));              // bật lại khối Nếu → bước chờ 60 phút có thể chạy
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.ToString());
    }

    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static void ApplyTo(JobEditorForm f, Job job) =>
        typeof(JobEditorForm).GetMethod("ApplyTo", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f, [job]);

    private static T Ancestor<T>(Control c) where T : Control
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p is T t) return t;
        throw new InvalidOperationException("Không thấy " + typeof(T).Name);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>Ghi lại nhật ký trong lúc kiểm thử.</summary>
    private sealed class LogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public LogCapture() => Log.Written += Add;

        public List<string> Lines
        {
            get { lock (_lines) return [.. _lines]; }
        }

        public int IndexOf(string text) => Lines.FindIndex(l => l.Contains(text));

        private void Add(string line)
        {
            lock (_lines) _lines.Add(line);
        }

        public void Dispose() => Log.Written -= Add;
    }
}

/// <summary>
/// Giao diện giả cho hàng đợi: nhắc nhở "chờ xác nhận" chỉ xong khi kiểm thử bấm OK (<see cref="ClickOk"/>), hoặc khi lần chạy bị dừng /
/// quá giờ (như MainForm: cửa sổ nhắc tự đóng). Đếm số lớp "dời cửa sổ ScheduleApp" đang có hiệu lực.
/// </summary>
internal sealed class WaitingUi : IUserNotifier
{
    private readonly object _sync = new();
    private readonly List<TaskCompletionSource> _open = [];
    private readonly List<string> _reminders = [];
    private readonly List<string> _notifications = [];
    private int _cleared, _maxCleared;

    public List<string> Reminders
    {
        get { lock (_sync) return [.. _reminders]; }
    }

    public List<string> Notifications
    {
        get { lock (_sync) return [.. _notifications]; }
    }

    public int OpenReminders
    {
        get { lock (_sync) return _open.Count; }
    }

    public int Cleared
    {
        get { lock (_sync) return _cleared; }
    }

    public int MaxCleared
    {
        get { lock (_sync) return _maxCleared; }
    }

    public Task ShowReminderAsync(string title, string message, bool waitForUser, CancellationToken ct)
    {
        lock (_sync) _reminders.Add(message);
        if (!waitForUser) return Task.CompletedTask;
        var ok = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _open.Add(ok);
        ct.Register(() =>
        {
            lock (_sync) _open.Remove(ok);
        });
        return ok.Task.WaitAsync(ct);
    }

    /// <summary>Người dùng bấm "Đã hiểu — tiếp tục flow" trên cửa sổ nhắc mở sớm nhất.</summary>
    public void ClickOk() => Take().SetResult();

    /// <summary>Cửa sổ nhắc mở sớm nhất bị lỗi bất thường.</summary>
    public void Fail(Exception error) => Take().SetException(error);

    private TaskCompletionSource Take()
    {
        lock (_sync)
        {
            var ok = _open[0];
            _open.RemoveAt(0);
            return ok;
        }
    }

    public void Notify(string title, string text, bool isError)
    {
        lock (_sync) _notifications.Add(title + ": " + text);
    }

    public IDisposable ClearScreenForAutomation()
    {
        lock (_sync) _maxCleared = Math.Max(_maxCleared, ++_cleared);
        return new Restore(this);
    }

    public Task<string?> PromptAsync(string title, string message, string defaultValue, bool password, CancellationToken ct) =>
        Task.FromResult<string?>(defaultValue);

    public Task<DebugCommand> DebugPauseAsync(string jobName, int stepIndex, string stepText, string reason,
        IReadOnlyDictionary<string, string> variables, CancellationToken ct) => Task.FromResult(DebugCommand.Continue);

    public Task<bool> AskContinueAsync(string title, string message, CancellationToken ct) => Task.FromResult(true);

    private sealed class Restore(WaitingUi ui) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                lock (ui._sync) ui._cleared--;
        }
    }
}
