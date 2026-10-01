using ScheduleApp.Models;
using ScheduleApp.Services;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

public class ScheduleTests
{
    [Fact]
    public void MonthlyDay31UsesLastDayOfShortMonths()
    {
        var s = new ScheduleConfig { Type = ScheduleType.Monthly, MonthlyMode = MonthlyMode.DayOfMonth, DayOfMonth = 31, StartAt = new DateTime(2026, 1, 1, 9, 0, 0) };
        Assert.Equal(new DateTime(2026, 2, 28, 9, 0, 0), s.NextOccurrence(new DateTime(2026, 2, 1)));
        Assert.Equal(new DateTime(2026, 1, 31, 9, 0, 0), s.NextOccurrence(new DateTime(2026, 1, 5)));
    }

    [Fact]
    public void NthWeekday()
    {
        var s = new ScheduleConfig { Type = ScheduleType.Monthly, MonthlyMode = MonthlyMode.NthWeekday, WeekOfMonth = 5, MonthWeekday = DayOfWeek.Friday, StartAt = new DateTime(2026, 1, 1, 17, 0, 0) };
        Assert.Equal(new DateTime(2026, 10, 30, 17, 0, 0), s.NextOccurrence(new DateTime(2026, 10, 1)));
        s.WeekOfMonth = 2;
        s.MonthWeekday = DayOfWeek.Monday;
        Assert.Equal(new DateTime(2026, 10, 12, 17, 0, 0), s.NextOccurrence(new DateTime(2026, 10, 1)));
    }

    [Fact]
    public void Workdays()
    {
        // 30/4/2026 là thứ Năm nhưng là ngày lễ → ngày làm việc cuối tháng là 29/4.
        var s = new ScheduleConfig { Type = ScheduleType.Monthly, MonthlyMode = MonthlyMode.LastWorkday, StartAt = new DateTime(2026, 1, 1, 8, 0, 0) };
        Assert.Equal(new DateTime(2026, 4, 29, 8, 0, 0), s.NextOccurrence(new DateTime(2026, 4, 1), d => d.Day == 30 && d.Month == 4));
        // 1/8/2026 là thứ Bảy → thứ Hai 3/8.
        s.MonthlyMode = MonthlyMode.FirstWorkday;
        Assert.Equal(new DateTime(2026, 8, 3, 8, 0, 0), s.NextOccurrence(new DateTime(2026, 7, 31, 12, 0, 0)));
    }

    [Fact]
    public void IntervalWithinTimeWindow()
    {
        var s = new ScheduleConfig
        {
            Type = ScheduleType.Interval, IntervalMinutes = 30, StartAt = new DateTime(2026, 1, 1, 0, 0, 0),
            UseTimeWindow = true, WindowStart = new TimeSpan(8, 0, 0), WindowEnd = new TimeSpan(17, 30, 0)
        };
        Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0), s.NextOccurrence(new DateTime(2026, 10, 2, 17, 20, 0)));
        Assert.Equal(new DateTime(2026, 10, 2, 10, 30, 0), s.NextOccurrence(new DateTime(2026, 10, 2, 10, 5, 0)));
        Assert.Equal(new DateTime(2026, 10, 2, 8, 0, 0), s.NextOccurrence(new DateTime(2026, 10, 2, 6, 0, 0)));
    }

    [Fact]
    public void DailySkipsHolidays()
    {
        var s = new ScheduleConfig { Type = ScheduleType.Daily, StartAt = new DateTime(2026, 1, 1, 7, 0, 0) };
        Assert.Equal(new DateTime(2026, 9, 3, 7, 0, 0), s.NextOccurrence(new DateTime(2026, 9, 1, 8, 0, 0), d => d.Month == 9 && d.Day == 2));
        Assert.True(SettingsStore.IsHoliday(new DateTime(2027, 4, 30)));
        Assert.False(SettingsStore.IsHoliday(new DateTime(2027, 4, 29)));
    }
}

public class StructureTests
{
    [Fact]
    public void ValidBlocks()
    {
        var fs = FlowStructure.Build([
            S(StepType.If), S(StepType.Loop), S(StepType.BreakLoop), S(StepType.ContinueLoop), S(StepType.EndLoop), S(StepType.Else), S(StepType.EndIf),
            S(StepType.Label, s => s.Target = "A"), S(StepType.Goto, s => s.Target = "A")
        ]);
        Assert.True(fs.IsValid, string.Join("; ", fs.Errors));
        Assert.Equal(6, fs.Match[0]);
        Assert.Equal(5, fs.ElseOf[0]);
        Assert.Equal(1, fs.Depth[1]);
        Assert.Equal(2, fs.Depth[2]);
        Assert.Equal(2, fs.Depth[3]);
        Assert.Equal(0, fs.Depth[5]);
    }

    [Fact]
    public void ReportsErrors()
    {
        var fs = FlowStructure.Build([S(StepType.If), S(StepType.EndLoop), S(StepType.BreakLoop), S(StepType.ContinueLoop), S(StepType.Goto, s => s.Target = "X")]);
        Assert.False(fs.IsValid);
        Assert.True(fs.Errors.Count >= 5, string.Join("; ", fs.Errors));
        Assert.Contains(fs.Errors, e => e.Contains("Bỏ qua, sang lần lặp kế"));
    }
}
