using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Services;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Lịch chạy khi đồng hồ / múi giờ thay đổi: không chạy lại khi đồng hồ lùi, không coi là lỡ lịch khi đồng hồ được chỉnh tới,
/// lặp theo khung giờ giữ đúng giờ đồng hồ qua giờ mùa hè, giờ hẹn lưu theo giờ đồng hồ treo tường, thư mục nhật ký bị xóa thì tạo lại.
/// </summary>
public class ScheduleClockTests
{
    private static readonly TimeZoneInfo Plus7 = TimeZoneInfo.CreateCustomTimeZone("ScheduleApp Clock +7", TimeSpan.FromHours(7), "+7", "+7");
    private static readonly TimeZoneInfo Plus9 = TimeZoneInfo.CreateCustomTimeZone("ScheduleApp Clock +9", TimeSpan.FromHours(9), "+9", "+9");

    /// <summary>Múi giờ kiểu Bắc Mỹ: UTC−5, giờ mùa hè từ Chủ nhật thứ hai tháng 3 tới Chủ nhật đầu tháng 11.</summary>
    private static TimeZoneInfo DstZone() => TimeZoneInfo.CreateCustomTimeZone("ScheduleApp Clock DST", TimeSpan.FromHours(-5), "DST", "STD", "DST",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))
    ]);

    private static Job Daily(TimeSpan at, MissedRunPolicy policy = MissedRunPolicy.Skip) => new()
    {
        Name = "Lịch " + at,
        Schedule = new ScheduleConfig { Type = ScheduleType.Daily, StartAt = new DateTime(2026, 1, 1).Add(at) },
        MissedRunPolicy = policy,
        Steps = [S(StepType.LogMessage, s => s.Text = "chạy")]
    };

    /// <summary>Bộ lập lịch với đồng hồ giả (UTC + bộ đếm thời gian thực) và đếm số lần chạy.</summary>
    private sealed class Rig : IDisposable
    {
        public DateTime Utc;
        public long Mono = 1_000_000;
        public int Runs;
        public readonly Scheduler Scheduler;

        public Rig(Job job, TimeZoneInfo zone, DateTime utc)
        {
            Utc = utc;
            var runner = new FlowRunner(new FakeUi(), _ => null);
            runner.JobFinished += (_, _, _, _) => Interlocked.Increment(ref Runs);
            Scheduler = new Scheduler([job], runner) { Zone = zone, UtcClock = () => Utc, MonoClock = () => Mono };
            Scheduler.RecalculateAll();
            Scheduler.Tick();
        }

        /// <summary>Thời gian trôi thật <paramref name="real"/>; đồng hồ máy bị chỉnh thêm <paramref name="step"/>.</summary>
        public void Advance(TimeSpan real, TimeSpan step = default)
        {
            Utc += real + step;
            Mono += (long)real.TotalMilliseconds;
            Scheduler.Tick();
        }

        public async Task<int> RunsAsync()
        {
            await Task.Delay(400);
            return Volatile.Read(ref Runs);
        }

        public void Dispose() => Scheduler.Dispose();
    }

    [Fact]
    public async Task ClockSteppedBackRightAfterARunDoesNotRunItAgain()
    {
        // Daily 08:00 (+7), đồng hồ máy nhanh 90 giây: chạy lúc 08:00, rồi đồng bộ giờ lùi đồng hồ về 07:58:40.
        var job = Daily(new TimeSpan(8, 0, 0));
        using var rig = new Rig(job, Plus7, new DateTime(2026, 10, 5, 0, 59, 59, DateTimeKind.Utc));
        rig.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await rig.RunsAsync());
        Assert.Equal(new DateTime(2026, 10, 6, 8, 0, 0), job.NextRun);

        rig.Advance(TimeSpan.FromSeconds(10), step: TimeSpan.FromSeconds(-90));   // không cần sự kiện đổi giờ của Windows
        Assert.Equal(new DateTime(2026, 10, 6, 8, 0, 0), job.NextRun);            // không quay về 08:00 hôm nay
        rig.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(1, await rig.RunsAsync());
    }

    [Fact]
    public async Task WestwardTimeZoneChangeDoesNotRepeatTodaysRun()
    {
        // Chạy 09:00 ở múi +9, rồi bay sang múi +7: 09:00 (+7) hôm nay là cùng "lần 09:00 hôm nay" → không chạy lần nữa.
        var job = Daily(new TimeSpan(9, 0, 0));
        using var rig = new Rig(job, Plus9, new DateTime(2026, 10, 5, 23, 59, 59, DateTimeKind.Utc).AddDays(-1));
        rig.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await rig.RunsAsync());

        rig.Scheduler.Zone = Plus7;
        rig.Scheduler.OnTimeChanged(null, EventArgs.Empty);
        rig.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new DateTime(2026, 10, 6, 9, 0, 0), job.NextRun);
        rig.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));          // 09:01 (+7) hôm nay
        Assert.Equal(1, await rig.RunsAsync());
    }

    [Fact]
    public async Task ClockCorrectedForwardIsNotAMissedRun()
    {
        // Đồng hồ chậm 3 ngày (pin CMOS…): người dùng chỉnh lại → lịch "chạy bù" không được chạy hàng loạt, vì máy vẫn bật suốt.
        var job = Daily(new TimeSpan(8, 0, 0), MissedRunPolicy.RunOnce);
        using var rig = new Rig(job, Plus7, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));          // 07:00 theo đồng hồ sai
        rig.Advance(TimeSpan.FromSeconds(1), step: TimeSpan.FromDays(3) + TimeSpan.FromHours(5));          // giờ đúng: 08/10 12:00
        Assert.Equal(0, await rig.RunsAsync());
        Assert.Equal(new DateTime(2026, 10, 9, 8, 0, 0), job.NextRun);
    }

    [Fact]
    public async Task SleepingPastTheScheduleIsStillAMissedRun()
    {
        // Máy ngủ qua giờ chạy (thời gian trôi thật, bộ đếm cũng tăng) → vẫn là lỡ lịch, "chạy bù" chạy một lần.
        var job = Daily(new TimeSpan(8, 0, 0), MissedRunPolicy.RunOnce);
        using var rig = new Rig(job, Plus7, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        rig.Advance(TimeSpan.FromHours(5));
        Assert.Equal(1, await rig.RunsAsync());
        Assert.Equal(new DateTime(2026, 10, 6, 8, 0, 0), job.NextRun);
    }

    [Fact]
    public void OnceRunInFirstPassDoesNotRunAgain()
    {
        // 1/11/2026 01:xx lặp lại hai lần. "Một lần" lúc 01:30 đã chạy ở lần đầu → tính lại ở lần thứ hai không chạy nữa;
        // tạo mới ở lần thứ hai (chưa chạy) → chạy lúc 01:30 lần thứ hai.
        var zone = DstZone();
        var secondPass = new DateTime(2026, 11, 1, 6, 10, 0, DateTimeKind.Utc);
        var job = new Job { Name = "Một lần", Schedule = new ScheduleConfig { Type = ScheduleType.Once, StartAt = new DateTime(2026, 11, 1, 1, 30, 0) }, Steps = [S(StepType.LogMessage)] };
        using (var fresh = new Scheduler([job], new FlowRunner(new FakeUi(), _ => null)) { Zone = zone, UtcClock = () => secondPass })
        {
            fresh.RecalculateAll();
            Assert.Equal(new DateTime(2026, 11, 1, 1, 30, 0), job.NextRun);
        }
        job.LastRun = new DateTime(2026, 11, 1, 1, 30, 2);
        using var ran = new Scheduler([job], new FlowRunner(new FakeUi(), _ => null)) { Zone = zone, UtcClock = () => secondPass };
        ran.RecalculateAll();
        Assert.Null(job.NextRun);
    }

    [Fact]
    public void WindowedIntervalKeepsWallClockHoursAcrossDaylightSaving()
    {
        // Mỗi 120 phút trong 08:00–17:30, thứ 2–6, bắt đầu tháng 1 (giờ chuẩn): tháng 7 (giờ mùa hè) vẫn 8, 10, 12, 14, 16 giờ.
        var zone = DstZone();
        var s = new ScheduleConfig
        {
            Type = ScheduleType.Interval, IntervalMinutes = 120, UseTimeWindow = true, WindowStart = new TimeSpan(8, 0, 0), WindowEnd = new TimeSpan(17, 30, 0),
            StartAt = new DateTime(2026, 1, 5, 8, 0, 0)
        };
        var after = ScheduleConfig.LocalToUtc(new DateTime(2026, 7, 6, 7, 0, 0), zone);   // thứ Hai
        var hours = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            var next = s.NextOccurrenceUtc(after, null, zone)!.Value;
            hours.Add(ScheduleConfig.UtcToLocal(next, zone).Hour);
            after = next.AddSeconds(1);
        }
        Assert.Equal([8, 10, 12, 14, 16, 8], hours);

        // Lặp mỗi 1440 phút (mỗi ngày) lúc 08:00: sang giờ mùa hè vẫn 08:00, không thành 09:00.
        var daily = new ScheduleConfig { Type = ScheduleType.Interval, IntervalMinutes = 1440, StartAt = new DateTime(2026, 1, 5, 8, 0, 0) };
        var july = daily.NextOccurrenceUtc(ScheduleConfig.LocalToUtc(new DateTime(2026, 7, 6, 7, 0, 0), zone), null, zone)!.Value;
        Assert.Equal(new DateTime(2026, 7, 6, 8, 0, 0), ScheduleConfig.UtcToLocal(july, zone));

        // Lặp ngắn không khung giờ vẫn đếm theo thời gian thực (giữ như trước).
        var hourly = new ScheduleConfig { Type = ScheduleType.Interval, IntervalMinutes = 60, StartAt = new DateTime(2026, 3, 8, 0, 0, 0) };
        var a = hourly.NextOccurrenceUtc(new DateTime(2026, 3, 8, 6, 30, 0, DateTimeKind.Utc), null, zone)!.Value;
        var b = hourly.NextOccurrenceUtc(a, null, zone)!.Value;
        Assert.Equal(TimeSpan.FromHours(1), b - a);
    }

    [Fact]
    public void StartTimeIsStoredAsWallClock()
    {
        var s = new ScheduleConfig { StartAt = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Local) };
        var json = JsonSerializer.Serialize(s, JsonDefaults.Options);
        Assert.Contains("\"StartAt\": \"2026-10-05T08:00:00\"", json.Replace("\":\"", "\": \""));
        Assert.DoesNotContain("+0", json);

        // File cũ ghi kèm độ lệch múi giờ: đọc đúng giờ đã ghi (08:00), kể cả khi máy đang ở múi giờ khác.
        foreach (var old in new[] { "2026-10-05T08:00:00+07:00", "2026-10-05T08:00:00+09:00", "2026-10-05T08:00:00" })
        {
            var back = JsonSerializer.Deserialize<ScheduleConfig>($$"""{"Type":"Daily","StartAt":"{{old}}"}""", JsonDefaults.Options)!;
            Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0), back.StartAt);
        }
    }

    [Fact]
    public void DeletedLogFolderIsRecreated()
    {
        var dir = Path.Combine(NewDir(), "logs");
        var now = new DateTime(2026, 10, 6, 9, 0, 0);
        Log.AppendLine(dir, now, "dòng 1");
        Directory.Delete(dir, true);                  // người dùng xóa thư mục nhật ký khi app đang chạy
        Log.AppendLine(dir, now, "dòng 2");
        Assert.Contains("dòng 2", File.ReadAllText(Path.Combine(dir, "2026-10-06.log")));
    }
}
