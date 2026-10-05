using System.Collections;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;
using ScheduleApp.Models;
using ScheduleApp.Services;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Vận hành lâu dài: nhật ký giới hạn dung lượng + tự xóa file cũ, dọn báo cáo kiểm thử, đổi giờ / múi giờ / giờ mùa hè,
/// trình kích hoạt tiến trình chỉ xét phiên hiện tại, kênh lệnh riêng từng tài khoản, xóa công việc xóa luôn các phiên bản cũ.
/// Dùng file thật trong thư mục kiểm thử, pipe thật với tên riêng (không kết nối tới ScheduleApp đang chạy), múi giờ tự tạo (không đổi giờ máy).
/// </summary>
public class OperationsTests
{
    // ───────────────────────────── Nhật ký ─────────────────────────────

    [Fact]
    public void LogRotatesToNextPartAtSizeCap()
    {
        var dir = NewDir();
        var day = new DateTime(2026, 1, 2, 9, 0, 0);
        var cap = Log.MaxFileBytes;
        try
        {
            Log.MaxFileBytes = 200;
            for (int i = 0; i < 20; i++) Log.AppendLine(dir, day, $"dòng số {i:00} — nội dung nhật ký");

            var files = Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToList();
            Assert.Contains("2026-01-02.log", files);
            Assert.Contains("2026-01-02-2.log", files);
            Assert.True(files.Count >= 3, string.Join(", ", files));
            // Không file nào vượt quá giới hạn quá một dòng, không mất dòng nào.
            var maxLine = System.Text.Encoding.UTF8.GetByteCount("dòng số 00 — nội dung nhật ký" + Environment.NewLine);
            Assert.All(Directory.GetFiles(dir), f => Assert.True(new FileInfo(f).Length < 200 + maxLine, f));
            var lines = Directory.GetFiles(dir).OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).SelectMany(File.ReadAllLines).ToList();
            Assert.Equal(20, lines.Count);
            Assert.Equal(Enumerable.Range(0, 20).Select(i => $"dòng số {i:00} — nội dung nhật ký"), lines);

            // Khởi động lại khi file của ngày đã đầy → ghi tiếp vào phần chưa đầy, không ghi đè.
            var other = NewDir();
            File.WriteAllText(Path.Combine(other, "2026-01-03.log"), new string('x', 300));
            Log.AppendLine(other, day.AddDays(1), "sau khi khởi động lại");
            Assert.Equal(300, new FileInfo(Path.Combine(other, "2026-01-03.log")).Length);
            Assert.Equal(["sau khi khởi động lại"], File.ReadAllLines(Path.Combine(other, "2026-01-03-2.log")));
        }
        finally
        {
            Log.MaxFileBytes = cap;
        }

        // Ghi qua Log.Info vẫn vào file yyyy-MM-dd.log của thư mục nhật ký thật (của lần kiểm thử).
        var marker = "kiểm tra ghi nhật ký " + Guid.NewGuid().ToString("N");
        Log.Info(marker);
        var today = Path.Combine(Log.LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
        Assert.Contains(marker, File.ReadAllText(today));
    }

    [Fact]
    public void CleanupDeletesOldLogsAndCrashFilesOnly()
    {
        var dir = NewDir();
        string F(string name) { var p = Path.Combine(dir, name); File.WriteAllText(p, "x"); return p; }
        var oldLog = F("2026-08-01.log");
        var oldPart = F("2026-08-01-2.log");
        var boundary = F("2026-09-05.log");
        var recent = F("2026-10-04.log");
        var oldCrash = F("crash-20260801-101010.txt");
        var youngCrash = F("crash-20260930-101010.txt");
        var other = F("ghi-chu.txt");
        File.SetLastWriteTime(other, new DateTime(2020, 1, 1));
        var shots = Path.Combine(dir, "screenshots", "2026-08-01");
        Directory.CreateDirectory(shots);

        int deleted = Log.Cleanup(dir, new DateTime(2026, 10, 5, 9, 0, 0), 30);

        Assert.Equal(3, deleted);
        Assert.False(File.Exists(oldLog));
        Assert.False(File.Exists(oldPart));
        Assert.False(File.Exists(oldCrash));
        Assert.True(File.Exists(boundary));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(youngCrash));
        Assert.True(File.Exists(other));
        Assert.True(Directory.Exists(shots)); // ảnh lỗi do ErrorScreenshots.Cleanup lo theo cài đặt riêng
        Assert.Equal(0, Log.Cleanup(dir, new DateTime(2026, 10, 5), 0)); // 0 = giữ mãi
    }

    [Fact]
    public void FileForPicksThePartHoldingTheRun()
    {
        var dir = NewDir();
        string F(string name, int hour) { var p = Path.Combine(dir, name); File.WriteAllText(p, "x"); File.SetLastWriteTime(p, new DateTime(2026, 1, 2, hour, 0, 0)); return p; }
        var part1 = F("2026-01-02.log", 9);
        var part2 = F("2026-01-02-2.log", 11);
        var part10 = F("2026-01-02-10.log", 15); // xếp theo tên thì "-10" đứng trước "-2" — phải xếp theo số phần
        F("2026-01-02-ghi-chu.log", 23);
        F("2026-01-03.log", 8);

        Assert.Equal(part1, Log.FileFor(dir, new DateTime(2026, 1, 2, 8, 0, 0)));
        Assert.Equal(part2, Log.FileFor(dir, new DateTime(2026, 1, 2, 10, 0, 0))); // chạy lúc 10:00 → phần 2 (phần 1 đã đầy lúc 09:00)
        Assert.Equal(part10, Log.FileFor(dir, new DateTime(2026, 1, 2, 12, 0, 0)));
        Assert.Equal(part10, Log.FileFor(dir, new DateTime(2026, 1, 2, 16, 0, 0))); // sau lần ghi cuối → phần cuối
        Assert.Null(Log.FileFor(dir, new DateTime(2026, 1, 4, 9, 0, 0)));
        Assert.Null(Log.FileFor(Path.Combine(dir, "khong-co"), new DateTime(2026, 1, 2, 9, 0, 0)));
    }

    [Fact]
    public void PruneTestReportsByAgeAndCount()
    {
        var root = NewDir();
        string D(string name) { var p = Path.Combine(root, name); Directory.CreateDirectory(p); File.WriteAllText(Path.Combine(p, "index.html"), "<html/>"); return p; }
        var old = D("2026-08-01_080000_Cu");
        var recent = Enumerable.Range(0, 5).Select(i => D($"2026-10-0{i + 1}_120000_Moi{i}")).ToList();
        var custom = D("bao-cao-tu-luu");
        File.WriteAllText(Path.Combine(root, "latest.json"), "{}");

        int deleted = Housekeeping.PruneTestReports(root, new DateTime(2026, 10, 5, 13, 0, 0), keepDays: 30, keepCount: 3);

        Assert.Equal(3, deleted); // 1 quá hạn + 2 ngoài 3 báo cáo mới nhất
        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(recent[0]));
        Assert.False(Directory.Exists(recent[1]));
        Assert.All(recent.Skip(2), d => Assert.True(Directory.Exists(d), d));
        Assert.True(Directory.Exists(custom));
        Assert.True(File.Exists(Path.Combine(root, "latest.json")));
    }

    // ───────────────────────────── Đổi giờ / múi giờ / giờ mùa hè ─────────────────────────────

    /// <summary>Múi giờ giả kiểu Bắc Mỹ: UTC−5, giờ mùa hè từ 02:00 Chủ nhật thứ hai tháng 3 tới 02:00 Chủ nhật đầu tháng 11.</summary>
    private static TimeZoneInfo DstZone() => TimeZoneInfo.CreateCustomTimeZone("ScheduleApp Test DST", TimeSpan.FromHours(-5), "Test DST", "Test STD", "Test DST",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))
    ]);

    private static List<DateTime> Chain(ScheduleConfig s, DateTime firstAfterUtc, TimeZoneInfo zone, int count)
    {
        var list = new List<DateTime>();
        var after = firstAfterUtc;
        for (int i = 0; i < count; i++)
        {
            var next = s.NextOccurrenceUtc(after, null, zone) ?? throw new InvalidOperationException("hết lịch");
            Assert.True(next > after);
            list.Add(next);
            after = next.AddSeconds(1); // Scheduler tính lần tới ngay sau khi chạy
        }
        return list;
    }

    [Fact]
    public void IntervalUsesRealElapsedTimeAcrossSpringForward()
    {
        // 8/3/2026: 02:00 nhảy thành 03:00.
        var zone = DstZone();
        var s = new ScheduleConfig { Type = ScheduleType.Interval, IntervalMinutes = 60, StartAt = new DateTime(2026, 3, 8, 0, 0, 0) };
        var runs = Chain(s, new DateTime(2026, 3, 8, 4, 0, 0, DateTimeKind.Utc), zone, 4);
        Assert.All(runs.Zip(runs.Skip(1)), p => Assert.Equal(TimeSpan.FromHours(1), p.Second - p.First));
        Assert.Equal([0, 1, 3, 4], runs.Select(u => ScheduleConfig.UtcToLocal(u, zone).Hour));
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0), s.NextOccurrence(new DateTime(2026, 3, 8, 1, 30, 0), null, zone));
    }

    [Fact]
    public void IntervalRunsEachRealHourAcrossFallBack()
    {
        // 1/11/2026: 02:00 (giờ mùa hè) lùi về 01:00 — giờ 01:xx xuất hiện hai lần.
        var zone = DstZone();
        var s = new ScheduleConfig { Type = ScheduleType.Interval, IntervalMinutes = 60, StartAt = new DateTime(2026, 11, 1, 0, 0, 0) };
        var runs = Chain(s, new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc), zone, 4);
        Assert.Equal(
            [new DateTime(2026, 11, 1, 4, 0, 0), new DateTime(2026, 11, 1, 5, 0, 0), new DateTime(2026, 11, 1, 6, 0, 0), new DateTime(2026, 11, 1, 7, 0, 0)],
            runs);
        Assert.Equal([0, 1, 1, 2], runs.Select(u => ScheduleConfig.UtcToLocal(u, zone).Hour));
    }

    [Fact]
    public void DailyAtSkippedHourRunsOnceWhenClockJumps()
    {
        var zone = DstZone();
        var s = new ScheduleConfig { Type = ScheduleType.Daily, StartAt = new DateTime(2026, 1, 1, 2, 30, 0) };
        var runs = Chain(s, new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc), zone, 3); // 00:00 ngày đổi giờ
        Assert.Equal(
            [new DateTime(2026, 3, 8, 3, 0, 0), new DateTime(2026, 3, 9, 2, 30, 0), new DateTime(2026, 3, 10, 2, 30, 0)],
            runs.Select(u => ScheduleConfig.UtcToLocal(u, zone)));
        Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0), runs[0]); // 03:00 giờ mùa hè = đúng lúc đồng hồ nhảy tới
    }

    [Fact]
    public void DailyAtRepeatedHourRunsOnceWhenClockFallsBack()
    {
        var zone = DstZone();
        var s = new ScheduleConfig { Type = ScheduleType.Daily, StartAt = new DateTime(2026, 1, 1, 1, 30, 0) };
        var runs = Chain(s, new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), zone, 2);
        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0), runs[0]); // 01:30 lần đầu (giờ mùa hè)
        Assert.Equal(new DateTime(2026, 11, 2, 1, 30, 0), ScheduleConfig.UtcToLocal(runs[1], zone)); // không chạy lại lúc 01:30 lần hai
        // Chạy lại ứng dụng giữa hai lần 01:xx (sau 01:30 lần đầu) cũng không chạy lần nữa trong ngày.
        Assert.Equal(new DateTime(2026, 11, 2, 1, 30, 0), s.NextOccurrence(new DateTime(2026, 11, 1, 1, 40, 0), null, zone));
        // Tính lại lịch giữa lần 01:00 thứ hai và 01:30 (đổi giờ, khởi động lại, sửa công việc) → 01:30 đã chạy ở lần đầu, không chạy nữa.
        var secondPass = new DateTime(2026, 11, 1, 6, 10, 0, DateTimeKind.Utc); // 01:10 giờ chuẩn
        Assert.Equal(new DateTime(2026, 11, 2, 1, 30, 0), ScheduleConfig.UtcToLocal(s.NextOccurrenceUtc(secondPass, null, zone)!.Value, zone));
        // Trước 01:30 lần đầu vẫn chạy lúc 01:30 lần đầu.
        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0), s.NextOccurrenceUtc(new DateTime(2026, 11, 1, 5, 10, 0, DateTimeKind.Utc), null, zone));

        var weekly = new ScheduleConfig { Type = ScheduleType.Weekly, Days = [DayOfWeek.Sunday, DayOfWeek.Monday], StartAt = new DateTime(2026, 1, 1, 1, 30, 0) };
        Assert.Equal(new DateTime(2026, 11, 2, 1, 30, 0), ScheduleConfig.UtcToLocal(weekly.NextOccurrenceUtc(secondPass, null, zone)!.Value, zone));
        var monthly = new ScheduleConfig { Type = ScheduleType.Monthly, MonthlyMode = MonthlyMode.DayOfMonth, DayOfMonth = 1, StartAt = new DateTime(2026, 1, 1, 1, 30, 0) };
        Assert.Equal(new DateTime(2026, 12, 1, 1, 30, 0), ScheduleConfig.UtcToLocal(monthly.NextOccurrenceUtc(secondPass, null, zone)!.Value, zone));
        var once = new ScheduleConfig { Type = ScheduleType.Once, StartAt = new DateTime(2026, 11, 1, 1, 30, 0) };
        Assert.Null(once.NextOccurrenceUtc(secondPass, null, zone));
        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0), once.NextOccurrenceUtc(new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc), null, zone));
    }

    [Fact]
    public void VietnamScheduleIsUnchanged()
    {
        var vn = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        var interval = new ScheduleConfig
        {
            Type = ScheduleType.Interval, IntervalMinutes = 30, StartAt = new DateTime(2026, 1, 1, 0, 0, 0),
            UseTimeWindow = true, WindowStart = new TimeSpan(8, 0, 0), WindowEnd = new TimeSpan(17, 30, 0)
        };
        Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0), interval.NextOccurrence(new DateTime(2026, 10, 2, 17, 20, 0), null, vn));
        Assert.Equal(new DateTime(2026, 10, 2, 10, 30, 0), interval.NextOccurrence(new DateTime(2026, 10, 2, 10, 5, 0), null, vn));
        Assert.Equal(new DateTime(2026, 10, 2, 3, 30, 0, DateTimeKind.Utc), interval.NextOccurrenceUtc(new DateTime(2026, 10, 2, 3, 5, 0, DateTimeKind.Utc), null, vn));

        var daily = new ScheduleConfig { Type = ScheduleType.Daily, StartAt = new DateTime(2026, 1, 1, 7, 0, 0) };
        Assert.Equal(new DateTime(2026, 9, 3, 7, 0, 0), daily.NextOccurrence(new DateTime(2026, 9, 1, 8, 0, 0), d => d.Month == 9 && d.Day == 2, vn));
        Assert.Equal(new DateTime(2026, 9, 3, 0, 0, 0), daily.NextOccurrenceUtc(new DateTime(2026, 9, 1, 1, 0, 0), d => d.Month == 9 && d.Day == 2, vn));

        var monthly = new ScheduleConfig { Type = ScheduleType.Monthly, MonthlyMode = MonthlyMode.LastWorkday, StartAt = new DateTime(2026, 1, 1, 8, 0, 0) };
        Assert.Equal(new DateTime(2026, 4, 29, 8, 0, 0), monthly.NextOccurrence(new DateTime(2026, 4, 1), d => d.Day == 30 && d.Month == 4, vn));
        var once = new ScheduleConfig { Type = ScheduleType.Once, StartAt = new DateTime(2026, 12, 31, 23, 59, 0) };
        Assert.Equal(new DateTime(2026, 12, 31, 23, 59, 0), once.NextOccurrence(new DateTime(2026, 12, 31, 23, 0, 0), null, vn));
        Assert.Null(once.NextOccurrence(new DateTime(2026, 12, 31, 23, 59, 0), null, vn));
    }

    private static Job DailyJob(TimeSpan at) => new()
    {
        Name = "Kiểm tra lịch",
        Schedule = new ScheduleConfig { Type = ScheduleType.Daily, StartAt = new DateTime(2026, 1, 1).Add(at) },
        Steps = [S(StepType.Wait)]
    };

    [Fact]
    public void TimeZoneChangeRecomputesNextRun()
    {
        var zoneA = TimeZoneInfo.CreateCustomTimeZone("ScheduleApp Test +7", TimeSpan.FromHours(7), "+7", "+7");
        var zoneB = TimeZoneInfo.CreateCustomTimeZone("ScheduleApp Test +9", TimeSpan.FromHours(9), "+9", "+9");
        // Giờ chạy = 1 tiếng nữa theo múi A; ở múi B (sớm hơn 2 tiếng) giờ đó đã qua → lần tới là ngày mai.
        var nowA = ScheduleConfig.UtcToLocal(DateTime.UtcNow, zoneA).AddHours(1);
        var job = DailyJob(new TimeSpan(nowA.Hour, nowA.Minute, 0));
        var jobs = new List<Job> { job };
        using var scheduler = new Scheduler(jobs, new FlowRunner(new FakeUi(), _ => null)) { Zone = zoneA };
        scheduler.RecalculateAll();
        var before = job.NextRun;
        Assert.Equal(job.Schedule.NextOccurrence(ScheduleConfig.UtcToLocal(DateTime.UtcNow, zoneA), null, zoneA), before);

        scheduler.Zone = zoneB;
        scheduler.OnTimeChanged(null, EventArgs.Empty);
        Assert.Equal(before, job.NextRun); // chỉ đánh dấu; tính lại ở nhịp kiểm tra (luồng UI)
        bool changed = false;
        scheduler.Changed += () => changed = true;
        scheduler.Tick();

        Assert.True(changed);
        Assert.Equal(job.Schedule.NextOccurrence(ScheduleConfig.UtcToLocal(DateTime.UtcNow, zoneB), null, zoneB), job.NextRun);
        Assert.Equal(before!.Value.AddDays(1), job.NextRun);
    }

    [Fact]
    public void ClockMovedBackRecomputesEarlierNextRun()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("ScheduleApp Test +7b", TimeSpan.FromHours(7), "+7", "+7");
        var job = DailyJob(new TimeSpan(8, 0, 0));
        var real = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc); // 07:00 giờ địa phương
        var clock = real.AddDays(3);                                    // đồng hồ đang chạy sai, nhanh 3 ngày
        using var scheduler = new Scheduler([job], new FlowRunner(new FakeUi(), _ => null)) { Zone = zone, UtcClock = () => clock };
        scheduler.RecalculateAll();
        Assert.Equal(new DateTime(2026, 10, 8, 8, 0, 0), job.NextRun);

        clock = real; // người dùng chỉnh lại giờ đúng
        scheduler.Tick();
        Assert.Equal(new DateTime(2026, 10, 8, 8, 0, 0), job.NextRun); // chưa có sự kiện đổi giờ → giữ lịch cũ (không tính lại mỗi giây)
        scheduler.OnTimeChanged(null, EventArgs.Empty);
        scheduler.Tick();
        Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0), job.NextRun); // chạy đúng 08:00 hôm nay, không đợi 3 ngày
    }

    [Fact]
    public void TimeChangeDoesNotRepeatShownReminder()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("ScheduleApp Test +7c", TimeSpan.FromHours(7), "+7", "+7");
        var job = DailyJob(new TimeSpan(8, 0, 0));
        job.RemindBeforeMinutes = 10;
        var clock = new DateTime(2026, 10, 5, 0, 55, 0, DateTimeKind.Utc); // 07:55 giờ địa phương
        using var scheduler = new Scheduler([job], new FlowRunner(new FakeUi(), _ => null)) { Zone = zone, UtcClock = () => clock };
        int reminders = 0;
        scheduler.ReminderDue += _ => reminders++;
        scheduler.RecalculateAll();

        scheduler.Tick();
        Assert.Equal(1, reminders);
        // Windows đồng bộ giờ trong 10 phút nhắc trước → tính lại lịch, giờ chạy không đổi → không nhắc lại.
        clock = clock.AddMinutes(1);
        scheduler.OnTimeChanged(null, EventArgs.Empty);
        scheduler.Tick();
        scheduler.Tick();
        Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0), job.NextRun);
        Assert.Equal(1, reminders);
        scheduler.Recalculate(job);
        scheduler.Tick();
        Assert.Equal(1, reminders);

        // Giờ chạy thật sự đổi (sửa công việc sang 08:05) → nhắc cho lần chạy mới.
        job.Schedule.StartAt = new DateTime(2026, 1, 1, 8, 5, 0);
        scheduler.Recalculate(job);
        scheduler.Tick();
        Assert.Equal(2, reminders);
    }

    [Fact]
    public void ResumeAndTimeEventsAreUnsubscribedOnDispose()
    {
        var scheduler = new Scheduler([], new FlowRunner(new FakeUi(), _ => null));
        scheduler.Start();
        Assert.Equal(2, SystemEventHandlersOf(scheduler));
        scheduler.OnPowerModeChanged(null, new PowerModeChangedEventArgs(PowerModes.Resume));
        scheduler.Tick();
        scheduler.Dispose();
        Assert.Equal(0, SystemEventHandlersOf(scheduler));
        scheduler.Tick(); // đã hủy → không làm gì, không lỗi
    }

    /// <summary>Số handler của <paramref name="target"/> đang đăng ký trong SystemEvents (đọc danh sách nội bộ).</summary>
    private static int SystemEventHandlersOf(object target)
    {
        var field = typeof(SystemEvents).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .First(f => typeof(IDictionary).IsAssignableFrom(f.FieldType));
        if (field.GetValue(null) is not IDictionary handlers) return 0;
        int count = 0;
        lock (handlers)
        {
            foreach (var list in handlers.Values.OfType<IEnumerable>())
                foreach (var info in list)
                {
                    var del = info.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                        .Select(f => f.GetValue(info)).OfType<Delegate>().FirstOrDefault();
                    if (del?.Target == target) count++;
                }
        }
        return count;
    }

    // ───────────────────────────── Tiến trình, kênh lệnh, phiên bản ─────────────────────────────

    [Fact]
    public void ProcessTriggerSeesOnlyCurrentSession()
    {
        var names = TriggerManager.NamesInSession(
            [("notepad", 1), ("Explorer", 1), ("svchost", 0), ("calc", 2), ("notepad", 2)], 1);
        Assert.Equal(2, names.Count);
        Assert.Contains("NOTEPAD", names);
        Assert.Contains("explorer", names);
        Assert.DoesNotContain("svchost", names);
        Assert.DoesNotContain("calc", names);
    }

    [Fact]
    public async Task CommandPipeIsPerUserAndServerClientAgree()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        Assert.Equal(CommandServer.PipeNameFor(sid), CommandServer.PipeName);
        Assert.Equal(CommandServer.PipeNameFor(sid), CommandServer.PipeNameFor(sid.ToLowerInvariant()));
        Assert.NotEqual(CommandServer.PipeNameFor("S-1-5-21-1-2-3-1001"), CommandServer.PipeNameFor("S-1-5-21-1-2-3-1002"));
        Assert.DoesNotContain(sid, CommandServer.PipeName);
        Assert.DoesNotContain('\\', CommandServer.PipeName);

        // Pipe thật với "người dùng" giả — không bao giờ chạm tới kênh lệnh của ScheduleApp đang chạy.
        var name = CommandServer.PipeNameFor("S-1-5-21-test-" + Guid.NewGuid());
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var listening = CommandServer.ListenAsync(cmd => received.TrySetResult(cmd), cts.Token, name);
        Assert.True(await Task.Run(() => CommandServer.Send("run Báo cáo ngày", name)));
        Assert.Equal("run Báo cáo ngày", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        // Tên của người dùng khác → không có ai nghe.
        Assert.False(await Task.Run(() => CommandServer.Send("show", CommandServer.PipeNameFor("S-1-5-21-khac-" + Guid.NewGuid()), 300)));
        cts.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void DeletingJobRemovesItsVersions()
    {
        var job = new Job { Name = "Có phiên bản", Steps = [S(StepType.Wait)] };
        JobVersions.Remember([job]);
        job.Name = "Có phiên bản (sửa 1)";
        JobVersions.Track([job]);
        Thread.Sleep(20); // tên file phiên bản theo mili giây
        job.Name = "Có phiên bản (sửa 2)";
        JobVersions.Track([job]);
        var dir = Path.Combine(JobStore.DataDir, "versions", job.Id.ToString("N"));
        Assert.Equal(2, JobVersions.List(job.Id).Count);
        Assert.True(Directory.Exists(dir));

        var keep = new Job { Name = "Giữ lại" };
        JobVersions.Remember([keep]);
        keep.Name = "Giữ lại (sửa)";
        JobVersions.Track([keep]);

        Assert.True(JobVersions.Delete(job.Id));
        Assert.False(Directory.Exists(dir));
        Assert.Empty(JobVersions.List(job.Id));
        Assert.Single(JobVersions.List(keep.Id)); // công việc khác không bị ảnh hưởng
        Assert.True(JobVersions.Delete(job.Id)); // xóa lần nữa (không còn gì) vẫn ổn
    }
}
