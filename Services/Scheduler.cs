using Microsoft.Win32;
using ScheduleApp.Models;
using ScheduleApp.Native;

namespace ScheduleApp.Services;

/// <summary>
/// Kiểm tra lịch mỗi giây (trên luồng UI) và đẩy công việc đến hạn vào hàng đợi của <see cref="FlowRunner"/>.
/// Xử lý lịch bị lỡ (chạy bù / hỏi), bỏ qua ngày nghỉ và hẹn giờ đánh thức máy.
/// So sánh giờ bằng UTC (đổi giờ mùa hè không làm chạy sớm / chạy lặp); đổi giờ hệ thống / múi giờ → tính lại lịch, máy thức dậy → kiểm tra ngay.
/// </summary>
public sealed class Scheduler : IDisposable
{
    private static readonly TimeSpan MissedTolerance = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromDays(7);

    private readonly List<Job> _jobs;
    private readonly FlowRunner _runner;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    /// <summary>Lần chạy tới của từng công việc theo UTC (<see cref="Job.NextRun"/> chỉ là giờ địa phương để hiển thị).</summary>
    private readonly Dictionary<Job, DateTime> _dueUtc = new(ReferenceEqualityComparer.Instance);
    private DateTime _lastAliveSaved = DateTime.MinValue;
    private DateTime _lastHousekeeping = DateTime.MinValue;
    private DateTime? _wakeAt;
    private SynchronizationContext? _sync;
    private int _clockChanged, _resumed;
    private bool _subscribed, _disposed;

    /// <summary>Đến giờ nhắc trước khi chạy.</summary>
    public event Action<Job>? ReminderDue;

    /// <summary>Lịch của một hoặc nhiều công việc đã thay đổi.</summary>
    public event Action? Changed;

    /// <summary>Một lần chạy bị lỡ với chính sách "Hỏi tôi" (công việc, giờ lẽ ra phải chạy).</summary>
    public event Action<Job, DateTime>? MissedRunAsk;

    /// <summary>Múi giờ tính lịch; null = múi giờ hiện tại của Windows (kiểm thử gán múi giờ có giờ mùa hè).</summary>
    internal TimeZoneInfo? Zone { get; set; }

    private TimeZoneInfo CurrentZone => Zone ?? TimeZoneInfo.Local;

    /// <summary>Đồng hồ UTC (kiểm thử giả lập đồng hồ bị chỉnh lùi / tới).</summary>
    internal Func<DateTime> UtcClock { get; set; } = () => DateTime.UtcNow;

    public Scheduler(List<Job> jobs, FlowRunner runner)
    {
        _jobs = jobs;
        _runner = runner;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        // Đăng ký ở đây (luồng UI đã có SynchronizationContext) để sự kiện hệ thống được chuyển về luồng UI.
        _sync = SynchronizationContext.Current;
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _subscribed = true;
        CatchUpSinceLastAlive();
        RecalculateAll();
        _timer.Start();
    }

    public static DateTime? NextFor(Job job, DateTime after) =>
        job.Schedule.NextOccurrence(after, job.SkipHolidays ? SettingsStore.IsHoliday : null);

    private DateTime? NextUtcFor(Job job, DateTime afterUtc) =>
        job.Schedule.NextOccurrenceUtc(afterUtc, job.SkipHolidays ? SettingsStore.IsHoliday : null, CurrentZone);

    private DateTime ToLocal(DateTime utc) => ScheduleConfig.UtcToLocal(utc, CurrentZone);

    /// <summary>Ghi lần chạy tới (UTC) và giờ địa phương tương ứng để hiển thị.</summary>
    private void SetNext(Job job, DateTime? utc)
    {
        if (utc is DateTime u) _dueUtc[job] = u;
        else _dueUtc.Remove(job);
        job.NextRun = utc is DateTime v ? ToLocal(v) : null;
    }

    private DateTime? DueUtc(Job job) =>
        _dueUtc.TryGetValue(job, out var u) ? u
        : job.NextRun is DateTime local ? ScheduleConfig.LocalToUtc(local, CurrentZone) : null;

    /// <summary>Tính lần chạy tới; công việc tắt hoặc chờ duyệt (<see cref="Job.NeedsApproval"/>) không có lịch chạy.</summary>
    public void Recalculate(Job job)
    {
        Reschedule(job, UtcClock(), DueUtc(job));
        UpdateWakeTimer();
    }

    public void RecalculateAll()
    {
        var old = _jobs.Select(DueUtc).ToList();
        _dueUtc.Clear(); // bỏ cả công việc đã xóa / đã thay bằng bản sửa
        var nowUtc = UtcClock();
        for (int i = 0; i < _jobs.Count; i++) Reschedule(_jobs[i], nowUtc, old[i]);
        UpdateWakeTimer();
    }

    /// <summary>Tính lại lần chạy tới; chỉ cho nhắc lại khi giờ chạy đổi (đã nhắc cho đúng lần chạy này thì không nhắc lần nữa).</summary>
    private void Reschedule(Job job, DateTime nowUtc, DateTime? oldUtc)
    {
        var next = job.Armed ? NextUtcFor(job, nowUtc) : null;
        SetNext(job, next);
        if (next != oldUtc) job.Reminded = false;
    }

    /// <summary>Đổi giờ hệ thống / múi giờ (Windows gửi WM_TIMECHANGE): bỏ thông tin múi giờ đã nhớ và tính lại lịch ở lần kiểm tra tới.</summary>
    internal void OnTimeChanged(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData();
        Interlocked.Exchange(ref _clockChanged, 1);
        RequestTick();
    }

    /// <summary>Máy vừa thức dậy: kiểm tra lịch ngay (không đợi nhịp kế tiếp) và hẹn lại giờ đánh thức.</summary>
    internal void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Interlocked.Exchange(ref _resumed, 1);
        RequestTick();
    }

    private void RequestTick() => _sync?.Post(_ => Tick(), null);

    internal void Tick()
    {
        if (_disposed) return;
        bool clockChanged = Interlocked.Exchange(ref _clockChanged, 0) == 1;
        bool resumed = Interlocked.Exchange(ref _resumed, 0) == 1;
        var nowUtc = UtcClock();
        var now = DateTime.Now;
        bool changed = false;
        if (clockChanged) Log.Info("🕒 Giờ hệ thống hoặc múi giờ vừa thay đổi — tính lại lịch chạy.");
        if (resumed) Log.Info("💤 Máy vừa thức dậy — kiểm tra lại lịch chạy.");

        foreach (var job in _jobs.ToList())
        {
            if (!job.Armed || DueUtc(job) is not DateTime next) continue;

            if (job.RemindBeforeMinutes > 0 && !job.Reminded && nowUtc < next &&
                nowUtc >= next.AddMinutes(-job.RemindBeforeMinutes))
            {
                job.Reminded = true;
                ReminderDue?.Invoke(job);
            }

            if (nowUtc < next) continue;

            if (nowUtc - next > MissedTolerance) HandleMissed(job, ToLocal(next));
            else _ = _runner.EnqueueAsync(job, "theo lịch");

            SetNext(job, NextUtcFor(job, nowUtc));
            job.Reminded = false;
            changed = true;
        }

        if (clockChanged)
        {
            // Đồng hồ lùi → lần chạy tới phải sớm lại; múi giờ mới → "lúc HH:mm" ứng với thời điểm khác.
            RecalculateAll();
            changed = true;
        }
        if (resumed)
        {
            if (_wakeAt != null) _wakeAt = DateTime.MinValue; // buộc hẹn lại giờ đánh thức cho lần ngủ sau
            changed = true;
        }

        if (now - _lastAliveSaved >= TimeSpan.FromMinutes(1))
        {
            _lastAliveSaved = now;
            SettingsStore.Current.LastAlive = now;
            SettingsStore.Save();
        }

        if (now.Date != _lastHousekeeping.Date)
        {
            _lastHousekeeping = now;
            _ = Task.Run(Housekeeping.Run); // dọn nhật ký / báo cáo / ảnh lỗi cũ mỗi ngày một lần
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
        var nowUtc = UtcClock();
        var aliveUtc = ScheduleConfig.LocalToUtc(lastAlive, CurrentZone);
        var fromUtc = aliveUtc < nowUtc - CatchUpWindow ? nowUtc - CatchUpWindow : aliveUtc;

        foreach (var job in _jobs)
        {
            if (!job.Armed || job.MissedRunPolicy == MissedRunPolicy.Skip || job.Schedule.Type == ScheduleType.Manual) continue;
            if (NextUtcFor(job, fromUtc) is not DateTime missedUtc || missedUtc >= nowUtc) continue;
            var missed = ToLocal(missedUtc);
            if (job.LastRun is DateTime last && last >= missed) continue;
            HandleMissed(job, missed);
        }
    }

    /// <summary>Hẹn giờ đánh thức máy 1 phút trước lần chạy sớm nhất của các công việc bật "Đánh thức máy".</summary>
    private void UpdateWakeTimer()
    {
        var nextUtc = _jobs.Where(j => j.Armed && j.WakeComputer).Select(DueUtc).Where(d => d != null).Min();
        // PowerHelper đổi giờ địa phương của Windows sang FILETIME — luôn dùng múi giờ thật ở đây.
        DateTime? at = nextUtc is DateTime u ? ScheduleConfig.UtcToLocal(u.AddMinutes(-1), TimeZoneInfo.Local) : null;
        if (at == _wakeAt) return;
        _wakeAt = at;
        if (!PowerHelper.SetWakeTimer(at) && at != null)
            Log.Warn("Không đặt được giờ đánh thức máy.");
        else if (at != null)
            Log.Info($"⏰ Sẽ đánh thức máy lúc {at:HH:mm dd/MM} (nếu máy đang ngủ).");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_subscribed)
        {
            SystemEvents.TimeChanged -= OnTimeChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        _timer.Dispose();
        if (_wakeAt != null) PowerHelper.SetWakeTimer(null); // chỉ hủy hẹn giờ do chính lịch này đặt
    }
}
