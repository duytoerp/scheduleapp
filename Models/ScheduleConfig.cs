namespace ScheduleApp.Models;

public enum ScheduleType
{
    Once,
    Daily,
    Weekly,
    Monthly,
    Interval,
    Manual
}

public enum MonthlyMode
{
    /// <summary>Ngày N trong tháng (tháng ít ngày hơn → ngày cuối tháng).</summary>
    DayOfMonth,
    /// <summary>Thứ X của tuần thứ N (N = 5 nghĩa là tuần cuối).</summary>
    NthWeekday,
    LastDay,
    FirstWorkday,
    LastWorkday
}

/// <summary>Cấu hình lịch chạy của một công việc.</summary>
public sealed class ScheduleConfig
{
    public ScheduleType Type { get; set; } = ScheduleType.Daily;

    /// <summary>
    /// Once: thời điểm chạy. Daily/Weekly/Monthly: ngày bắt đầu + giờ chạy trong ngày.
    /// Interval: mốc bắt đầu lặp.
    /// </summary>
    public DateTime StartAt { get; set; } = DateTime.Today.AddHours(8);

    /// <summary>Weekly: các ngày chạy. Interval (khi bật khung giờ): các ngày được phép chạy.</summary>
    public List<DayOfWeek> Days { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Interval: chỉ chạy trong khung giờ [WindowStart, WindowEnd) của các ngày trong <see cref="Days"/>.</summary>
    public bool UseTimeWindow { get; set; }
    public TimeSpan WindowStart { get; set; } = new(8, 0, 0);
    public TimeSpan WindowEnd { get; set; } = new(17, 30, 0);

    public MonthlyMode MonthlyMode { get; set; } = MonthlyMode.DayOfMonth;
    public int DayOfMonth { get; set; } = 1;
    /// <summary>1..4 = tuần thứ N, 5 = tuần cuối cùng.</summary>
    public int WeekOfMonth { get; set; } = 1;
    public DayOfWeek MonthWeekday { get; set; } = DayOfWeek.Monday;

    public static readonly string[] TypeNames =
        ["Một lần", "Hằng ngày", "Hằng tuần", "Hằng tháng", "Lặp lại theo phút", "Chỉ chạy thủ công"];

    public static readonly string[] MonthlyModeNames =
        ["Ngày cố định trong tháng", "Thứ … của tuần thứ …", "Ngày cuối tháng", "Ngày làm việc đầu tiên", "Ngày làm việc cuối cùng"];

    public static readonly string[] WeekOfMonthNames = ["đầu tiên", "thứ hai", "thứ ba", "thứ tư", "cuối cùng"];

    /// <summary>
    /// Lần chạy kế tiếp sau thời điểm <paramref name="after"/> (giờ địa phương của <paramref name="zone"/>; null nếu không còn lần nào).
    /// <paramref name="isHoliday"/>: bỏ qua các ngày nghỉ (cả ngày). <paramref name="zone"/>: null = múi giờ của Windows.
    /// </summary>
    public DateTime? NextOccurrence(DateTime after, Func<DateTime, bool>? isHoliday = null, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        return NextOccurrenceUtc(LocalToUtc(after, zone), isHoliday, zone) is DateTime u ? UtcToLocal(u, zone) : null;
    }

    /// <summary>
    /// Lần chạy kế tiếp sau <paramref name="afterUtc"/>, tính bằng giờ UTC để không nhầm khi đồng hồ đổi giờ mùa hè:
    /// lặp theo phút đếm theo thời gian thực; "lúc HH:mm" (ngày/tuần/tháng/một lần) theo giờ đồng hồ treo tường —
    /// giờ bị bỏ qua khi đồng hồ nhảy tới thì chạy lúc vừa nhảy tới, giờ lặp lại khi lùi đồng hồ chỉ chạy một lần.
    /// </summary>
    public DateTime? NextOccurrenceUtc(DateTime afterUtc, Func<DateTime, bool>? isHoliday = null, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var next = NextRawUtc(afterUtc, isHoliday, zone);
        for (int guard = 0; next is DateTime n && isHoliday != null && guard < 400; guard++)
        {
            var day = UtcToLocal(n, zone).Date;
            if (!isHoliday(day)) break;
            next = NextRawUtc(LocalToUtc(day.AddDays(1).AddSeconds(-1), zone), isHoliday, zone);
        }
        return next;
    }

    internal static DateTime UtcToLocal(DateTime utc, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone), DateTimeKind.Unspecified);

    /// <summary>Giờ địa phương → UTC. Giờ không tồn tại (đồng hồ nhảy tới) → lúc đồng hồ vừa nhảy tới; giờ lặp lại (lùi đồng hồ) → luôn lần đầu.</summary>
    internal static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
            for (int guard = 0; zone.IsInvalidTime(t) && guard < 24 * 60; guard++) t = t.AddMinutes(1);
            local = t;
        }
        if (zone.IsAmbiguousTime(local))
            return zone.GetAmbiguousTimeOffsets(local).Select(o => DateTime.SpecifyKind(local - o, DateTimeKind.Utc)).Min();
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private DateTime? NextRawUtc(DateTime afterUtc, Func<DateTime, bool>? isHoliday, TimeZoneInfo zone)
    {
        if (Type == ScheduleType.Interval) return NextIntervalUtc(afterUtc, zone);
        var after = UtcToLocal(afterUtc, zone);
        for (int guard = 0; guard < 3; guard++)
        {
            if (NextRaw(after, isHoliday) is not DateTime local) return null;
            var utc = LocalToUtc(local, zone);
            if (utc > afterUtc) return utc;
            // Đang ở lần thứ hai của giờ lặp lại mà giờ chạy đã qua ở lần đầu → coi như đã chạy, tính từ sau giờ đó.
            after = local;
        }
        return null;
    }

    /// <summary>Lặp theo phút: lưới thời gian tính bằng UTC (đổi giờ mùa hè không làm khoảng cách lệch), khung giờ/ngày xét theo giờ địa phương.</summary>
    private DateTime? NextIntervalUtc(DateTime afterUtc, TimeZoneInfo zone)
    {
        var start = LocalToUtc(TrimToSecond(StartAt), zone);
        var step = TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
        var candidate = afterUtc < start ? start : GridAfter(start, step, afterUtc);
        if (!UseTimeWindow || WindowEnd <= WindowStart) return candidate;
        if (Days.Count == 0) return null;

        for (int guard = 0; guard < 30; guard++)
        {
            var local = UtcToLocal(candidate, zone);
            var t = local.TimeOfDay;
            bool dayOk = Days.Contains(local.DayOfWeek);
            if (dayOk && t >= WindowStart && t < WindowEnd) return candidate;
            var target = LocalToUtc(dayOk && t < WindowStart ? local.Date + WindowStart : local.Date.AddDays(1) + WindowStart, zone);
            candidate = target <= start ? start : GridAtOrAfter(start, step, target);
        }
        return null;
    }

    /// <summary>Các lịch "lúc HH:mm" (không gồm lặp theo phút), tính theo giờ đồng hồ địa phương.</summary>
    private DateTime? NextRaw(DateTime after, Func<DateTime, bool>? isHoliday)
    {
        var start = TrimToSecond(StartAt);
        var timeOfDay = start.TimeOfDay;
        var from = after < start ? start.AddSeconds(-1) : after;

        switch (Type)
        {
            case ScheduleType.Once:
                return start > after ? start : null;

            case ScheduleType.Daily:
            {
                var candidate = from.Date + timeOfDay;
                return candidate > from ? candidate : candidate.AddDays(1);
            }

            case ScheduleType.Weekly:
            {
                if (Days.Count == 0) return null;
                for (int i = 0; i <= 7; i++)
                {
                    var candidate = from.Date.AddDays(i) + timeOfDay;
                    if (candidate > from && Days.Contains(candidate.DayOfWeek)) return candidate;
                }
                return null;
            }

            case ScheduleType.Monthly:
            {
                var month = new DateTime(from.Year, from.Month, 1);
                for (int m = 0; m < 24; m++, month = month.AddMonths(1))
                {
                    var day = DayInMonth(month, isHoliday);
                    if (day == null) continue;
                    var candidate = day.Value + timeOfDay;
                    if (candidate > from) return candidate;
                }
                return null;
            }

            default:
                return null;
        }
    }

    private static DateTime GridAfter(DateTime start, TimeSpan step, DateTime after)
    {
        long n = (after - start).Ticks / step.Ticks + 1;
        return start + TimeSpan.FromTicks(step.Ticks * n);
    }

    private static DateTime GridAtOrAfter(DateTime start, TimeSpan step, DateTime target)
    {
        long n = ((target - start).Ticks + step.Ticks - 1) / step.Ticks;
        return start + TimeSpan.FromTicks(step.Ticks * n);
    }

    /// <summary>Ngày chạy trong tháng <paramref name="month"/> (ngày 1 của tháng) theo chế độ hằng tháng.</summary>
    private DateTime? DayInMonth(DateTime month, Func<DateTime, bool>? isHoliday)
    {
        int days = DateTime.DaysInMonth(month.Year, month.Month);
        bool Workday(DateTime d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && isHoliday?.Invoke(d) != true;

        switch (MonthlyMode)
        {
            case MonthlyMode.DayOfMonth:
                return month.AddDays(Math.Clamp(DayOfMonth, 1, days) - 1);
            case MonthlyMode.LastDay:
                return month.AddDays(days - 1);
            case MonthlyMode.NthWeekday:
            {
                if (WeekOfMonth >= 5)
                {
                    var last = month.AddDays(days - 1);
                    while (last.DayOfWeek != MonthWeekday) last = last.AddDays(-1);
                    return last;
                }
                var first = month;
                while (first.DayOfWeek != MonthWeekday) first = first.AddDays(1);
                return first.AddDays(7 * (Math.Max(1, WeekOfMonth) - 1));
            }
            case MonthlyMode.FirstWorkday:
                for (int d = 0; d < days; d++)
                    if (Workday(month.AddDays(d))) return month.AddDays(d);
                return null;
            case MonthlyMode.LastWorkday:
                for (int d = days - 1; d >= 0; d--)
                    if (Workday(month.AddDays(d))) return month.AddDays(d);
                return null;
            default:
                return null;
        }
    }

    public string Describe() => Type switch
    {
        ScheduleType.Once => $"Một lần lúc {StartAt:HH:mm dd/MM/yyyy}",
        ScheduleType.Daily => $"Hằng ngày lúc {StartAt:HH:mm}",
        ScheduleType.Weekly => Days.Count == 0
            ? "Hằng tuần (chưa chọn ngày)"
            : $"{DaysText()} lúc {StartAt:HH:mm}",
        ScheduleType.Monthly => MonthlyMode switch
        {
            MonthlyMode.DayOfMonth => $"Ngày {DayOfMonth} hằng tháng",
            MonthlyMode.NthWeekday => $"{DayLongName(MonthWeekday)} {WeekOfMonthNames[Math.Clamp(WeekOfMonth, 1, 5) - 1]} của tháng",
            MonthlyMode.LastDay => "Ngày cuối tháng",
            MonthlyMode.FirstWorkday => "Ngày làm việc đầu tháng",
            MonthlyMode.LastWorkday => "Ngày làm việc cuối tháng",
            _ => "Hằng tháng"
        } + $" lúc {StartAt:HH:mm}",
        ScheduleType.Interval => $"Mỗi {IntervalMinutes} phút" +
                                 (UseTimeWindow ? $", {WindowStart:hh\\:mm}–{WindowEnd:hh\\:mm} {DaysText()}" : $" (từ {StartAt:HH:mm dd/MM})"),
        _ => "Chạy thủ công"
    };

    /// <summary>Các ngày trong tuần, gộp ngày liên tiếp (≥ 3 ngày) thành khoảng: "T2–T6", "T2–T4, T7", "T2, T6".</summary>
    private string DaysText()
    {
        var order = Days.Select(d => ((int)d + 6) % 7).Distinct().Order().ToList();   // T2 = 0 … CN = 6
        if (order.Count == 7) return "Mọi ngày";
        var parts = new List<string>();
        for (int i = 0; i < order.Count;)
        {
            int j = i;
            while (j + 1 < order.Count && order[j + 1] == order[j] + 1) j++;
            string Name(int k) => DayName((DayOfWeek)((k + 1) % 7));
            if (j - i >= 2) parts.Add($"{Name(order[i])}–{Name(order[j])}");
            else for (int k = i; k <= j; k++) parts.Add(Name(order[k]));
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    public static string DayName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "T2",
        DayOfWeek.Tuesday => "T3",
        DayOfWeek.Wednesday => "T4",
        DayOfWeek.Thursday => "T5",
        DayOfWeek.Friday => "T6",
        DayOfWeek.Saturday => "T7",
        _ => "CN"
    };

    public static string DayLongName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "Thứ Hai",
        DayOfWeek.Tuesday => "Thứ Ba",
        DayOfWeek.Wednesday => "Thứ Tư",
        DayOfWeek.Thursday => "Thứ Năm",
        DayOfWeek.Friday => "Thứ Sáu",
        DayOfWeek.Saturday => "Thứ Bảy",
        _ => "Chủ Nhật"
    };

    private static DateTime TrimToSecond(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second);
}
