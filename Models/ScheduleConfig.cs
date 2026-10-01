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
    /// Lần chạy kế tiếp sau thời điểm <paramref name="after"/> (null nếu không còn lần nào).
    /// <paramref name="isHoliday"/>: bỏ qua các ngày nghỉ (cả ngày).
    /// </summary>
    public DateTime? NextOccurrence(DateTime after, Func<DateTime, bool>? isHoliday = null)
    {
        var next = NextRaw(after, isHoliday);
        for (int guard = 0; next is DateTime n && isHoliday != null && isHoliday(n.Date) && guard < 400; guard++)
            next = NextRaw(n.Date.AddDays(1).AddSeconds(-1), isHoliday);
        return next;
    }

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

            case ScheduleType.Interval:
            {
                var step = TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
                var candidate = after < start ? start : GridAfter(start, step, after);
                if (!UseTimeWindow || WindowEnd <= WindowStart) return candidate;
                if (Days.Count == 0) return null;

                for (int guard = 0; guard < 30; guard++)
                {
                    var t = candidate.TimeOfDay;
                    bool dayOk = Days.Contains(candidate.DayOfWeek);
                    if (dayOk && t >= WindowStart && t < WindowEnd) return candidate;
                    var target = dayOk && t < WindowStart ? candidate.Date + WindowStart : candidate.Date.AddDays(1) + WindowStart;
                    candidate = target <= start ? start : GridAtOrAfter(start, step, target);
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

    private string DaysText() => string.Join(", ", Days.OrderBy(d => ((int)d + 6) % 7).Select(DayName));

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
