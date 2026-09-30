using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>
/// Kiểm tra lịch mỗi giây (trên luồng UI) và đẩy công việc đến hạn vào hàng đợi của <see cref="FlowRunner"/>.
/// </summary>
public sealed class Scheduler : IDisposable
{
    private static readonly TimeSpan MissedTolerance = TimeSpan.FromMinutes(2);

    private readonly List<Job> _jobs;
    private readonly FlowRunner _runner;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    /// <summary>Đến giờ nhắc trước khi chạy.</summary>
    public event Action<Job>? ReminderDue;

    /// <summary>Lịch của một hoặc nhiều công việc đã thay đổi.</summary>
    public event Action? Changed;

    public Scheduler(List<Job> jobs, FlowRunner runner)
    {
        _jobs = jobs;
        _runner = runner;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        RecalculateAll();
        _timer.Start();
    }

    public void Recalculate(Job job)
    {
        job.NextRun = job.Enabled ? job.Schedule.NextOccurrence(DateTime.Now) : null;
        job.Reminded = false;
    }

    public void RecalculateAll()
    {
        foreach (var job in _jobs) Recalculate(job);
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

            if (now - next > MissedTolerance)
                Log.Warn($"[{job.Name}] Bỏ lỡ lịch {next:HH:mm dd/MM} (máy tắt hoặc ngủ).");
            else
                _ = _runner.EnqueueAsync(job, "theo lịch");

            job.NextRun = job.Schedule.NextOccurrence(now);
            job.Reminded = false;
            changed = true;
        }

        if (changed) Changed?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
