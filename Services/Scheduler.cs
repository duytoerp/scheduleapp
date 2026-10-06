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

    /// <summary>
    /// Bộ đếm thời gian thực (mili giây, tính cả lúc máy ngủ, không đổi khi chỉnh đồng hồ) — so với <see cref="UtcClock"/> để biết
    /// đồng hồ vừa bị chỉnh (lệch nhau) hay thời gian trôi thật (máy ngủ dậy: cả hai cùng tăng).
    /// </summary>
    internal Func<long> MonoClock { get; set; } = () => Environment.TickCount64;

    /// <summary>Đồng hồ nhảy hơn chừng này so với thời gian thực mới coi là bị chỉnh (đồng bộ giờ thường chỉ chỉnh vài giây).</summary>
    private static readonly TimeSpan ClockStepTolerance = TimeSpan.FromSeconds(30);

    private DateTime? _lastTickUtc;
    private long _lastTickMono;

    /// <summary>
    /// Giờ đồng hồ treo tường của lần chạy theo lịch gần nhất của từng công việc — đồng hồ bị chỉnh lùi / đổi sang múi giờ phía tây
    /// không làm chạy lại đúng lần vừa chạy.
    /// </summary>
    private readonly Dictionary<Job, DateTime> _firedLocal = new(ReferenceEqualityComparer.Instance);

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

    private DateTime? NextUtcFor(Job job, DateTime afterUtc)
    {
        // "Một lần" đã chạy vào (hoặc sau) giờ hẹn → không chạy nữa (kể cả khi giờ hẹn nằm trong giờ lặp lại lúc lùi đồng hồ).
        if (job.Schedule.Type == ScheduleType.Once && job.LastRun is DateTime last && last >= job.Schedule.StartAt.AddSeconds(-1)) return null;
        // Lần chạy theo lịch vừa rồi (giờ đồng hồ treo tường) còn ở "tương lai" của đồng hồ hiện tại (đồng hồ bị chỉnh lùi, đổi múi giờ
        // về phía tây) → tính từ sau lần đó, không chạy lại. Chỉ xét trong 1 ngày (chỉnh lùi nhiều ngày thì theo đồng hồ mới).
        if (_firedLocal.TryGetValue(job, out var fired))
        {
            var firedUtc = ScheduleConfig.LocalToUtc(fired, CurrentZone);
            if (firedUtc > afterUtc && firedUtc - afterUtc <= TimeSpan.FromDays(1)) afterUtc = firedUtc;
        }
        return job.Schedule.NextOccurrenceUtc(afterUtc, job.SkipHolidays ? SettingsStore.IsHoliday : null, CurrentZone);
    }

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

        // Đồng hồ vừa bị chỉnh (lệch với thời gian thực từ nhịp trước) — có khi không kèm sự kiện đổi giờ của Windows.
        var mono = MonoClock();
        var step = _lastTickUtc is DateTime prevUtc ? (nowUtc - prevUtc) - TimeSpan.FromMilliseconds(mono - _lastTickMono) : TimeSpan.Zero;
        _lastTickUtc = nowUtc;
        _lastTickMono = mono;
        bool steppedForward = step > ClockStepTolerance;
        if (step.Duration() > ClockStepTolerance)
        {
            Log.Info($"🕒 Đồng hồ máy vừa được chỉnh {(step > TimeSpan.Zero ? "tới" : "lùi")} {Describe(step.Duration())}.");
            clockChanged = true;
        }
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

            if (nowUtc - next > MissedTolerance)
            {
                // Qua giờ chỉ vì đồng hồ vừa được chỉnh tới (máy vẫn bật, thời gian thực chưa tới) → không phải lỡ lịch, không chạy bù.
                if (steppedForward) Log.Info($"[{job.Name}] Bỏ qua lần {ToLocal(next):HH:mm dd/MM} — đồng hồ vừa được chỉnh tới, không phải lỡ lịch.");
                else HandleMissed(job, ToLocal(next));
            }
            else
            {
                _firedLocal[job] = ToLocal(next);
                _ = _runner.EnqueueAsync(job, "theo lịch");
            }

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
            SettingsStore.Current.LastAliveUtc = nowUtc;
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

    /// <summary>"3 ngày 2 giờ", "5 phút", "45 giây".</summary>
    private static string Describe(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} ngày {t.Hours} giờ"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} giờ {t.Minutes} phút"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} phút" : $"{t.Seconds} giây";

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
        // Ưu tiên mốc UTC (đổi múi giờ giữa hai lần mở app không làm lệch); bản cũ chỉ có giờ địa phương.
        var aliveUtc = SettingsStore.Current.LastAliveUtc is DateTime u ? DateTime.SpecifyKind(u, DateTimeKind.Utc)
            : SettingsStore.Current.LastAlive is DateTime lastAlive ? ScheduleConfig.LocalToUtc(lastAlive, CurrentZone) : (DateTime?)null;
        if (aliveUtc == null) return;
        var nowUtc = UtcClock();
        var fromUtc = aliveUtc.Value < nowUtc - CatchUpWindow ? nowUtc - CatchUpWindow : aliveUtc.Value;

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
