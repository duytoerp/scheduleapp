using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Một công việc không chặn được mọi công việc khác mãi mãi: thời gian chạy tối đa, nhắc nhở chờ bấm OK nhường lượt chạy (khi
/// người dùng không còn làm việc tay), nhắc nhở được che chắn khỏi flow khác, nhật ký "đang chờ công việc nào", JSON của file cũ không đổi.
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

    /// <summary>
    /// Hàng đợi cho kiểm thử: nhắc nhở chờ bấm OK nhường lượt ngay (máy "không có ai dùng"); không đọc / đổi cửa sổ thật đang được chọn.
    /// </summary>
    private static FlowRunner Runner(IUserNotifier ui, Func<Guid, Job?>? find = null, TimeSpan? minute = null, TimeSpan? notice = null) =>
        new(ui, find ?? (_ => null))
        {
            ReminderGrace = TimeSpan.Zero,
            UserIdle = () => TimeSpan.FromHours(1),
            ForegroundWindow = () => IntPtr.Zero,
            BringToFront = _ => false,
            MaxRunMinute = minute ?? TimeSpan.FromMinutes(1),
            QueueNoticeAfter = notice ?? TimeSpan.FromSeconds(3)
        };

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
        var runner = Runner(ui);
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
        var runner = Runner(ui, notice: TimeSpan.FromMilliseconds(200));
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
        var runner = Runner(ui, id => id == onFailure.Id ? onFailure : null, minute: TimeSpan.FromMilliseconds(400));
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
    public async Task WaitingToTakeTheTurnBackAfterOkDoesNotCountTowardsTheLimit()
    {
        // Một "phút" = 1 giây: giới hạn 3 giây, công việc kia giữ lượt 4 giây sau khi bấm OK.
        var ui = new WaitingUi();
        var runner = Runner(ui, minute: TimeSpan.FromSeconds(1), notice: TimeSpan.FromMilliseconds(100));
        var turns = TrackTurns(runner);
        using var log = new LogCapture();
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };
        var a = new Job { Name = "Chờ duyệt", MaxRunMinutes = 3, Steps = [Reminder("Duyệt rồi bấm OK"), Note("đã duyệt"), S(StepType.Wait, s => s.DelayMs = 60_000)] };
        var other = new Job { Name = "Đang chạy", Steps = [Note("giữ lượt")] };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.BeforeStep = ctx => ctx.RootJob.Id == other.Id ? hold.Task : Task.CompletedTask;

        var runA = runner.EnqueueAsync(a, "theo lịch");
        await WaitUntil(() => ui.OpenReminders == 1 && turns().Count == 2, "A nhường lượt chạy");
        var runOther = runner.EnqueueAsync(other, "theo lịch");
        await WaitUntil(() => { lock (events) return events.Any(e => e.JobId == other.Id && e.Step == 0); }, "công việc kia chạy");
        ui.ClickOk();                                                     // A chờ lấy lại lượt
        await WaitUntil(() => log.IndexOf("[Chờ duyệt] Đang chờ công việc \"Đang chạy\" chạy xong…") >= 0, "A chờ lấy lại lượt");
        await Task.Delay(4000);
        Assert.False(runA.IsCompleted);                                   // quá 3 giây mà chưa bị coi là quá giờ
        Assert.Equal(-1, log.IndexOf("Quá thời gian chạy tối đa"));

        var released = System.Diagnostics.Stopwatch.StartNew();
        hold.SetResult();
        Assert.True((await runOther.WaitAsync(Long))!.Ok);
        // Lấy lại lượt rồi tính tiếp phần còn lại (gần 3 giây) — bước chờ 60 giây bị dừng vì quá giờ.
        var ra = await runA.WaitAsync(Long);
        released.Stop();
        Assert.Equal((false, "Quá thời gian chạy tối đa 3 phút — đã dừng", 3), (ra!.Ok, ra.Message, ra.FailedStep));
        Assert.True(log.IndexOf("📝 đã duyệt") >= 0);
        Assert.InRange(released.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(15));
        var all = turns();
        Assert.Equal(all.Count(t => t), all.Count(t => !t));
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public async Task ReminderKeepsTheTurnWhileTheUserIsStillWorking()
    {
        var ui = new WaitingUi();
        long idleMs = 0;                                                  // người dùng đang làm việc tay (cắm USB, duyệt…)
        var runner = new FlowRunner(ui, _ => null)
        {
            ReminderGrace = TimeSpan.FromMilliseconds(300),
            HandOverIdle = TimeSpan.FromSeconds(60),
            HandOverCheck = TimeSpan.FromMilliseconds(50),
            UserIdle = () => TimeSpan.FromMilliseconds(Interlocked.Read(ref idleMs)),
            ForegroundWindow = () => IntPtr.Zero,
            BringToFront = _ => false
        };
        var turns = TrackTurns(runner);
        using var log = new LogCapture();
        var a = new Job { Name = "Chờ cắm USB", Steps = [Reminder("Cắm USB rồi bấm OK"), Note("A chạy tiếp")] };
        var b = new Job { Name = "Sao lưu", Steps = [Note("B chạy")] };

        var runA = runner.EnqueueAsync(a, "theo lịch");
        // Báo "hết thao tác" trước khi hiện nhắc (chế độ an toàn không theo dõi lúc người dùng làm việc tay) nhưng vẫn giữ lượt.
        await WaitUntil(() => ui.OpenReminders == 1 && turns().SequenceEqual([true, false]), "A hiện nhắc nhở");
        var runB = runner.EnqueueAsync(b, "theo lịch");
        await Task.Delay(900);                                            // quá thời gian ân hạn nhưng người dùng vẫn đang dùng máy
        Assert.False(runB.IsCompleted);
        Assert.Equal([true, false], turns());
        Assert.Equal(-1, log.IndexOf("tạm nhường lượt chạy"));

        Interlocked.Exchange(ref idleMs, 120_000);                        // người dùng rời máy → nhường lượt, B chạy
        var rb = await runB.WaitAsync(Long);
        Assert.True(rb!.Ok, rb.Message);
        Assert.True(log.IndexOf("máy không có ai dùng 2 phút — tạm nhường lượt chạy") >= 0, string.Join("\n", log.Lines));
        ui.ClickOk();
        Assert.True((await runA.WaitAsync(Long))!.Ok);
        Assert.Equal([true, false, true, false, true, false], turns());

        // Bấm OK trong thời gian ân hạn: không nhường lượt — công việc khác chờ A chạy xong.
        var quick = new FlowRunner(ui, _ => null)
        {
            ReminderGrace = TimeSpan.FromSeconds(30), UserIdle = () => TimeSpan.FromHours(1), ForegroundWindow = () => IntPtr.Zero, BringToFront = _ => false
        };
        var started = new List<string>();
        quick.Progress += p => { if (p is { Step: -1, Ok: null }) lock (started) started.Add(p.JobName); };
        var quickTurns = TrackTurns(quick);
        var runA2 = quick.EnqueueAsync(a, "thử");
        await WaitUntil(() => ui.OpenReminders == 1, "A hiện nhắc nhở");
        var runB2 = quick.EnqueueAsync(b, "thử");
        await Task.Delay(300);
        Assert.False(runB2.IsCompleted);
        ui.ClickOk();
        await Task.WhenAll(runA2, runB2).WaitAsync(Long);
        lock (started) Assert.Equal(["Chờ cắm USB", "Sao lưu"], started);
        Assert.Equal([true, false, true, false, true, false], quickTurns());   // A, chờ OK, A chạy tiếp, A xong, B, B xong
    }

    [Fact]
    public async Task TakingTheTurnBackRefocusesTheWindowThatWasInFront()
    {
        var ui = new WaitingUi();
        var focused = new List<IntPtr>();
        var calls = 0;
        var later = (IntPtr)333;
        var answers = new IntPtr[2];
        var runner = new FlowRunner(ui, _ => null)
        {
            ReminderGrace = TimeSpan.Zero,
            UserIdle = () => TimeSpan.FromHours(1),
            // Lần 1: lúc hiện nhắc; lần 2: lúc nhường lượt; sau đó: cửa sổ công việc kia để lại phía trước.
            ForegroundWindow = () => { int n = Interlocked.Increment(ref calls); return n <= 2 ? answers[n - 1] : later; },
            BringToFront = h => { lock (focused) focused.Add(h); return true; }
        };
        using var log = new LogCapture();
        var a = new Job { Name = "Nhập liệu", Steps = [.. NeedsScreenButNeverUsesIt(), Reminder("Mở file rồi bấm OK"), Note("gõ tiếp")] };
        var b = new Job { Name = "Khác", Steps = [.. NeedsScreenButNeverUsesIt(), Note("B chạy")] };

        async Task RunOnce()
        {
            Interlocked.Exchange(ref calls, 0);
            var runA = runner.EnqueueAsync(a, "thử");
            await WaitUntil(() => ui.OpenReminders == 1 && log.IndexOf("tạm nhường lượt chạy") >= 0, "A nhường lượt chạy");
            Assert.True((await runner.EnqueueAsync(b, "thử").WaitAsync(Long))!.Ok);
            ui.ClickOk();
            Assert.True((await runA.WaitAsync(Long))!.Ok);
            log.Clear();
        }

        // Cửa sổ đang dùng lúc nhường lượt (222) được đưa lại lên trước khi A lấy lại lượt.
        answers[0] = (IntPtr)111;
        answers[1] = (IntPtr)222;
        await RunOnce();
        lock (focused) Assert.Equal([(IntPtr)222], focused);

        // Lúc nhường lượt cửa sổ phía trước là của ScheduleApp (nhắc nhở) → cửa sổ trước khi nhắc nhở hiện.
        // Cửa sổ ẩn thật của tiến trình kiểm thử, tạo / hủy trên cùng một luồng.
        using var created = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var own = IntPtr.Zero;
        var owner = new Thread(() =>
        {
            var window = new NativeWindow();
            window.CreateHandle(new CreateParams());
            own = window.Handle;
            created.Set();
            done.Wait();
            window.DestroyHandle();
        });
        owner.Start();
        try
        {
            Assert.True(created.Wait(5000));
            Assert.True(WindowHelper.BelongsToThisApp(own));
            answers[1] = own;
            await RunOnce();
            lock (focused) Assert.Equal([(IntPtr)222, (IntPtr)111], focused);
        }
        finally
        {
            done.Set();
            owner.Join();
        }

        // Công việc kia không đổi cửa sổ phía trước → không làm gì.
        answers[1] = later;
        await RunOnce();
        lock (focused) Assert.Equal(2, focused.Count);
    }

    [Fact]
    public async Task TimeoutAfterHandingOverIsRecordedBeforeWaitingForTheTurn()
    {
        var ui = new WaitingUi();
        var onFailure = new Job { Name = "Báo lỗi", Steps = [Reminder("xử lý lỗi: {{failed.message}}", wait: false)] };
        // Một "phút" = 1 giây: hết giờ sau 2 giây chờ bấm OK, lúc công việc kia đang giữ lượt.
        var runner = Runner(ui, id => id == onFailure.Id ? onFailure : null, minute: TimeSpan.FromSeconds(1));
        var turns = TrackTurns(runner);
        var a = new Job { Name = "Chờ duyệt", MaxRunMinutes = 2, OnFailureJobId = onFailure.Id, Steps = [Reminder("Duyệt rồi bấm OK"), Note("đã duyệt")] };
        var other = new Job { Name = "Đang chạy", Steps = [Note("giữ lượt")] };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.BeforeStep = ctx => ctx.RootJob.Id == other.Id ? hold.Task : Task.CompletedTask;
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };
        var finished = new List<Guid>();
        runner.JobFinished += (id, _, _, _) => { lock (finished) finished.Add(id); };
        const string reason = "Quá thời gian chạy tối đa 2 phút — đã dừng";

        var runA = runner.EnqueueAsync(a, "theo lịch");
        await WaitUntil(() => ui.OpenReminders == 1 && turns().Count == 2, "A nhường lượt chạy");
        var runOther = runner.EnqueueAsync(other, "theo lịch");
        await WaitUntil(() => { lock (events) return events.Any(e => e.JobId == other.Id && e.Step == 0); }, "công việc kia chạy");

        // Hết giờ: kết quả được ghi và báo ngay, không đợi công việc kia chạy xong.
        await WaitUntil(() => { lock (finished) return finished.Contains(a.Id); }, "A ghi kết quả");
        Assert.False(runA.IsCompleted);
        Assert.Equal((false, reason), (RunHistory.All.Last(x => x.JobId == a.Id).Ok, RunHistory.All.Last(x => x.JobId == a.Id).Message));
        Assert.Contains(ui.Notifications, n => n.Contains(reason));
        Assert.Equal(["Duyệt rồi bấm OK"], ui.Reminders);                 // công việc xử lý lỗi (dùng chuột/phím) chờ lấy lại lượt
        // Khung trạng thái đang là của công việc kia: không hiện kết quả của A đè lên.
        lock (events) Assert.DoesNotContain(events, e => e.JobId == a.Id && e.Ok != null);

        hold.SetResult();
        Assert.True((await runOther.WaitAsync(Long))!.Ok);
        var ra = await runA.WaitAsync(Long);
        Assert.Equal((false, reason, 1), (ra!.Ok, ra.Message, ra.FailedStep));
        Assert.Equal(["Duyệt rồi bấm OK", "xử lý lỗi: " + reason], ui.Reminders);
        lock (events) Assert.Equal((false, reason), (events.Last(e => e.JobId == a.Id).Ok, events.Last(e => e.JobId == a.Id).Message));
        lock (finished) Assert.Single(finished, id => id == a.Id);
        var all = turns();
        Assert.Equal(all.Count(t => t), all.Count(t => !t));
        Assert.False(runner.IsBusy);

        // Không có công việc xử lý lỗi: kết thúc ngay, khung trạng thái của công việc kia vẫn không bị đè.
        var b = new Job { Name = "Chờ duyệt 2", MaxRunMinutes = 2, Steps = [Reminder("Duyệt rồi bấm OK")] };
        hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runB = runner.EnqueueAsync(b, "theo lịch");
        await WaitUntil(() => ui.OpenReminders == 1, "B nhường lượt chạy");
        runOther = runner.EnqueueAsync(other, "theo lịch");
        var rb = await runB.WaitAsync(Long);
        Assert.Equal((false, "Quá thời gian chạy tối đa 2 phút — đã dừng"), (rb!.Ok, rb.Message));
        lock (events) Assert.DoesNotContain(events, e => e.JobId == b.Id && e.Ok != null);
        hold.SetResult();
        await runOther.WaitAsync(Long);
    }

    [Fact]
    public async Task DebugPauseDoesNotCountTowardsTheLimit()
    {
        // Giới hạn 2 "phút" = 1 giây; dừng ở điểm dừng 2 giây rồi chạy tiếp.
        var ui = new WaitingUi { DebugPause = TimeSpan.FromSeconds(2) };
        var runner = Runner(ui, minute: TimeSpan.FromMilliseconds(500));
        var job = new Job { Name = "Gỡ lỗi", MaxRunMinutes = 2, Steps = [Note("một"), S(StepType.LogMessage, s => { s.Text = "hai"; s.Breakpoint = true; })] };
        var r = await runner.EnqueueAsync(job, "thử", new RunOptions { UseBreakpoints = true }).WaitAsync(Long);
        Assert.True(r!.Ok, r.Message);
        Assert.Equal(1, ui.DebugPauses);

        // Sau khi chạy tiếp, giới hạn vẫn tính phần còn lại.
        job.Steps.Add(S(StepType.Wait, s => s.DelayMs = 30_000));
        r = await runner.EnqueueAsync(job, "thử", new RunOptions { UseBreakpoints = true }).WaitAsync(Long);
        Assert.Equal((false, "Quá thời gian chạy tối đa 2 phút — đã dừng", 3), (r!.Ok, r.Message, r.FailedStep));
    }

    /// <summary>
    /// Nhắc nhở của flow đã nhường lượt, trong lúc flow khác thao tác: chuột / phím giả lập không bấm được nút (kể cả Enter), tự dời khỏi
    /// chỗ chuột sắp click, không lọt vào ảnh chụp màn hình, hiện mà không lấy focus. Cửa sổ thật ở ngoài màn hình; chuột / phím chỉ là
    /// thông điệp gửi thẳng tới cửa sổ (không có chuột / phím thật nào).
    /// </summary>
    [Fact]
    public void ReminderIsShieldedFromAnotherFlowsInput()
    {
        Assert.False(Win32.IsInjectedInput());                            // gọi được Windows; không có thông điệp chuột/phím nào đang xử lý
        var (savedArea, savedInjected) = (ReminderForm.TestArea, ReminderForm.IsInjectedInput);
        try
        {
            Sta(() =>
            {
                var area = new Rectangle(-20000, -20000, 1600, 900);
                ReminderForm.TestArea = area;
                bool injected = true;
                ReminderForm.IsInjectedInput = () => injected;
                ReminderForm.SetShield(true);                             // công việc khác đang giữ lượt chạy
                using var f = new ReminderForm("Chờ cắm USB", "Cắm USB rồi bấm OK", requireConfirm: true);
                bool closed = false;
                f.FormClosed += (_, _) => closed = true;
                f.Show();
                Pump();
                Assert.NotEqual(f.Handle, Win32.GetForegroundWindow());   // không giành bàn phím của flow đang chạy
                Assert.True(Win32.GetWindowDisplayAffinity(f.Handle, out var affinity));
                Assert.Equal(0x11u, affinity);                            // WDA_EXCLUDEFROMCAPTURE
                Assert.Equal(new Point(area.Right - f.Width - 12, area.Bottom - f.Height - 12), f.Location);

                var ok = (Button)f.AcceptButton!;
                int downs = 0;
                ok.MouseDown += (_, _) => downs++;
                Post(ok.Handle, WM_LBUTTONDOWN, 1, 0x00050005);
                Post(ok.Handle, WM_LBUTTONUP, 0, 0x00050005);
                Post(ok.Handle, WM_KEYDOWN, VK_RETURN, 0);                // nút mặc định (AcceptButton)
                Post(ok.Handle, WM_KEYUP, VK_RETURN, 0);
                Post(f.Handle, WM_SYSKEYDOWN, 0x73 /*F4*/, 1 << 29);      // Alt+F4
                Pump();
                Assert.Equal(0, downs);
                Assert.False(closed);

                // Chuột giả lập sắp click vào chỗ nhắc nhở (gọi từ luồng của flow) → dời sang góc khác rồi mới click.
                var target = new Point(f.Left + f.Width / 2, f.Top + f.Height / 2);
                var avoid = Task.Run(() => ReminderForm.Avoid(target));
                while (!avoid.IsCompleted) Pump();
                Assert.False(f.Bounds.Contains(target));
                Assert.Equal(new Point(area.Left + 12, area.Bottom - f.Height - 12), f.Location);

                // Hết flow thao tác: thôi che chắn — ảnh chụp màn hình thấy lại, không dời, bấm được nút (kể cả công cụ điều khiển từ xa).
                ReminderForm.SetShield(false);
                Pump();
                Assert.True(Win32.GetWindowDisplayAffinity(f.Handle, out affinity));
                Assert.Equal(0u, affinity);
                var at = f.Location;
                ReminderForm.Avoid(new Point(f.Left + 5, f.Top + 5));
                Assert.Equal(at, f.Location);
                Post(ok.Handle, WM_LBUTTONDOWN, 1, 0x00050005);
                Post(ok.Handle, WM_LBUTTONUP, 0, 0x00050005);
                Pump();
                Assert.Equal(1, downs);
                Assert.True(closed);

                // Đang che chắn nhưng là người bấm thật (không phải giả lập): Enter vẫn bấm "Đã hiểu — tiếp tục flow".
                ReminderForm.SetShield(true);
                injected = false;
                using var f2 = new ReminderForm("Chờ duyệt", "", requireConfirm: true);
                bool closed2 = false;
                f2.FormClosed += (_, _) => closed2 = true;
                f2.Show();
                Pump();
                Post(((Button)f2.AcceptButton!).Handle, WM_KEYDOWN, VK_RETURN, 0);
                Pump();
                Assert.True(closed2);
            });
        }
        finally
        {
            ReminderForm.SetShield(false);
            (ReminderForm.TestArea, ReminderForm.IsInjectedInput) = (savedArea, savedInjected);
        }
    }

    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, VK_RETURN = 0x0D;

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void Post(IntPtr h, int msg, int wParam, int lParam) => PostMessage(h, msg, wParam, lParam);

    private static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
    }

    [Fact]
    public async Task ReminderThatFailsStillTakesTheTurnBackBeforeContinuing()
    {
        // Cửa sổ nhắc lỗi bất thường, bước đặt "bỏ qua lỗi": flow chạy tiếp — phải lấy lại lượt chạy trước.
        var ui = new WaitingUi();
        var runner = Runner(ui);
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

        public void Clear()
        {
            lock (_lines) _lines.Clear();
        }

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

    /// <summary>Thời gian "người dùng" dừng ở điểm dừng / chạy từng bước trước khi bấm Chạy tiếp.</summary>
    public TimeSpan DebugPause { get; init; }

    public int DebugPauses { get; private set; }

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

    public async Task<DebugCommand> DebugPauseAsync(string jobName, int stepIndex, string stepText, string reason,
        IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        DebugPauses++;
        if (DebugPause > TimeSpan.Zero) await Task.Delay(DebugPause, ct);
        return DebugCommand.Continue;
    }

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
