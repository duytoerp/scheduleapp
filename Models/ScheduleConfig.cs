namespace ScheduleApp.Models;

public enum ScheduleType
{
    Once,
    Daily,
    Weekly,
    Interval,
    Manual
}

/// <summary>Cấu hình lịch chạy của một công việc.</summary>
public sealed class ScheduleConfig
{
    public ScheduleType Type { get; set; } = ScheduleType.Daily;

    /// <summary>
    /// Once: thời điểm chạy. Daily/Weekly: ngày bắt đầu + giờ chạy trong ngày.
    /// Interval: mốc bắt đầu lặp.
    /// </summary>
    public DateTime StartAt { get; set; } = DateTime.Today.AddHours(8);

    public List<DayOfWeek> Days { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public int IntervalMinutes { get; set; } = 60;

    public static readonly string[] TypeNames =
        ["Một lần", "Hằng ngày", "Hằng tuần", "Lặp lại theo phút", "Chỉ chạy thủ công"];

    /// <summary>Lần chạy kế tiếp sau thời điểm <paramref name="after"/> (null nếu không còn lần nào).</summary>
    public DateTime? NextOccurrence(DateTime after)
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

            case ScheduleType.Interval:
            {
                var step = TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
                if (after < start) return start;
                long n = (after - start).Ticks / step.Ticks + 1;
                return start + TimeSpan.FromTicks(step.Ticks * n);
            }

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
            : $"{string.Join(", ", Days.OrderBy(d => ((int)d + 6) % 7).Select(DayName))} lúc {StartAt:HH:mm}",
        ScheduleType.Interval => $"Mỗi {IntervalMinutes} phút (từ {StartAt:HH:mm dd/MM})",
        _ => "Chạy thủ công"
    };

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

    private static DateTime TrimToSecond(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second);
}
