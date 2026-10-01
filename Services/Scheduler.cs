using ScheduleApp.Models;
using ScheduleApp.Native;

namespace ScheduleApp.Services;

/// <summary>
/// Kiểm tra lịch mỗi giây (trên luồng UI) và đẩy công việc đến hạn vào hàng đợi của <see cref="FlowRunner"/>.
/// Xử lý lịch bị lỡ (chạy bù / hỏi), bỏ qua ngày nghỉ và hẹn giờ đánh thức máy.
/// </summary>
public sealed class Scheduler : IDisposable
{
    private static readonly TimeSpan MissedTolerance = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromDays(7);

    private readonly List<Job> _jobs;
    private readonly FlowRunner _runner;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private DateTime _lastAliveSaved = DateTime.MinValue;
    private DateTime? _wakeAt;

    /// <summary>Đến giờ nhắc trước khi chạy.</summary>
    public event Action<Job>? ReminderDue;

    /// <summary>Lịch của một hoặc nhiều công việc đã thay đổi.</summary>
    public event Action? Changed;

    /// <summary>Một lần chạy bị lỡ với chính sách "Hỏi tôi" (công việc, giờ lẽ ra phải chạy).</summary>
    public event Action<Job, DateTime>? MissedRunAsk;

    public Scheduler(List<Job> jobs, FlowRunner runner)
    {
        _jobs = jobs;
        _runner = runner;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        CatchUpSinceLastAlive();
        RecalculateAll();
        _timer.Start();
    }

    public static DateTime? NextFor(Job job, DateTime after) =>
        job.Schedule.NextOccurrence(after, job.SkipHolidays ? SettingsStore.IsHoliday : null);

    public void Recalculate(Job job)
    {
        job.NextRun = job.Enabled ? NextFor(job, DateTime.Now) : null;
        job.Reminded = false;
        UpdateWakeTimer();
    }

    public void RecalculateAll()
    {
        foreach (var job in _jobs)
        {
            job.NextRun = job.Enabled ? NextFor(job, DateTime.Now) : null;
            job.Reminded = false;
        }
        UpdateWakeTimer();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        bool changed = false;

        foreach (var job in _jobs.ToList())
        {
            if (!job.Enabled || job.NextRun is not DateTime next) continue;

            if (job.RemindBeforeMinutes > 0 && !job.Reminded && now < next &&
                now >= next.AddMinutes(-job.RemindBeforeMinutes))
            {
                job.Reminded = true;
                ReminderDue?.Invoke(job);
            }

            if (now < next) continue;

            if (now - next > MissedTolerance) HandleMissed(job, next);
            else _ = _runner.EnqueueAsync(job, "theo lịch");

            job.NextRun = NextFor(job, now);
            job.Reminded = false;
            changed = true;
        }

        if (now - _lastAliveSaved >= TimeSpan.FromMinutes(1))
        {
            _lastAliveSaved = now;
            SettingsStore.Current.LastAlive = now;
            SettingsStore.Save();
        }

        if (changed)
        {
            UpdateWakeTimer();
            Changed?.Invoke();
        }
    }

    private void HandleMissed(Job job, DateTime missedAt)
    {
        switch (job.MissedRunPolicy)
        {
            case MissedRunPolicy.RunOnce:
                Log.Warn($"[{job.Name}] Lỡ lịch {missedAt:HH:mm dd/MM} — chạy bù ngay.");
                _ = _runner.EnqueueAsync(job, $"chạy bù lịch {missedAt:HH:mm dd/MM}");
                break;
            case MissedRunPolicy.Ask:
                Log.Warn($"[{job.Name}] Lỡ lịch {missedAt:HH:mm dd/MM} — hỏi người dùng có chạy bù không.");
                MissedRunAsk?.Invoke(job, missedAt);
                break;
            default:
                Log.Warn($"[{job.Name}] Bỏ lỡ lịch {missedAt:HH:mm dd/MM} (máy tắt hoặc ngủ).");
                break;
        }
    }

    /// <summary>
    /// Khi ScheduleApp khởi động: tìm các lần chạy bị lỡ trong lúc app không chạy (dựa vào mốc "còn sống" lần cuối).
    /// Mỗi công việc chỉ chạy bù tối đa một lần.
    /// </summary>
    private void CatchUpSinceLastAlive()
    {
        if (SettingsStore.Current.LastAlive is not DateTime lastAlive) return;
        var now = DateTime.Now;
        var from = lastAlive < now - CatchUpWindow ? now - CatchUpWindow : lastAlive;

        foreach (var job in _jobs)
        {
            if (!job.Enabled || job.MissedRunPolicy == MissedRunPolicy.Skip || job.Schedule.Type == ScheduleType.Manual) continue;
            if (NextFor(job, from) is not DateTime missed || missed >= now) continue;
            if (job.LastRun is DateTime last && last >= missed) continue;
            HandleMissed(job, missed);
        }
    }

    /// <summary>Hẹn giờ đánh thức máy 1 phút trước lần chạy sớm nhất của các công việc bật "Đánh thức máy".</summary>
    private void UpdateWakeTimer()
    {
        var next = _jobs.Where(j => j.Enabled && j.WakeComputer && j.NextRun != null).Min(j => j.NextRun);
        var at = next?.AddMinutes(-1);
        if (at == _wakeAt) return;
        _wakeAt = at;
        if (!PowerHelper.SetWakeTimer(at) && at != null)
            Log.Warn("Không đặt được giờ đánh thức máy.");
        else if (at != null)
            Log.Info($"⏰ Sẽ đánh thức máy lúc {at:HH:mm dd/MM} (nếu máy đang ngủ).");
    }

    public void Dispose()
    {
        _timer.Dispose();
        PowerHelper.SetWakeTimer(null);
    }
}
