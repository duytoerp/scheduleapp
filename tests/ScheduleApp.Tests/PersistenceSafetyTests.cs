using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScheduleApp.Models;
using ScheduleApp.Services;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// An toàn dữ liệu: settings.json / secrets.json / jobs.json hỏng không làm mất dữ liệu (giữ bản .broken-…, khôi phục từ .bak),
/// một công việc hỏng không kéo theo cả danh sách, mỗi lần lưu giữ bản trước, dữ liệu DPAPI của máy khác không làm ứng dụng báo lỗi,
/// lỗi chưa xử lý được ghi ra file crash. Dùng file thật trong thư mục dữ liệu kiểm thử.
/// </summary>
public class PersistenceSafetyTests
{
    /// <summary>Giữ nội dung các file dùng chung của thư mục dữ liệu kiểm thử và trả lại sau bài kiểm thử; xóa các file bài kiểm thử tạo thêm.</summary>
    private sealed class FileSnapshot : IDisposable
    {
        private readonly string _dir;
        private readonly HashSet<string> _before;
        private readonly Dictionary<string, byte[]?> _saved = [];

        public FileSnapshot(string dir, params string[] names)
        {
            _dir = dir;
            _before = [.. Directory.GetFiles(dir)];
            foreach (var n in names)
            {
                var p = Path.Combine(dir, n);
                _saved[p] = File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
        }

        public void Dispose()
        {
            foreach (var (p, bytes) in _saved)
            {
                if (bytes != null) File.WriteAllBytes(p, bytes);
                else File.Delete(p);
            }
            foreach (var f in Directory.GetFiles(_dir))
                if (!_before.Contains(f) && !_saved.ContainsKey(f)) File.Delete(f);
        }
    }

    private static AppSettings ReadSettings(string path) =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonDefaults.Options)!;

    [Fact]
    public void CorruptSettingsIsKeptAndRestoredFromBackupInsteadOfDefaults()
    {
        var original = SettingsStore.Current;
        var path = Path.Combine(JobStore.DataDir, "settings.json");
        using var snapshot = new FileSnapshot(JobStore.DataDir, "settings.json", "settings.json.bak");
        DataIssues.Take();
        try
        {
            SettingsStore.Replace(new AppSettings { BrowserPort = 9411, Holidays = ["24/12"] });
            SettingsStore.Current.BrowserPort = 9422;
            SettingsStore.Save();
            Assert.Equal(9411, ReadSettings(path + ".bak").BrowserPort);     // lần lưu trước nằm ở .bak
            Assert.Equal(9422, ReadSettings(path).BrowserPort);

            const string corrupt = "{\"BrowserPort\": 9422, \"Holidays\": [\"24/1";   // cắt cụt giữa chừng (mất điện)
            File.WriteAllText(path, corrupt);
            SettingsStore.ResetForTests();

            Assert.Equal(9411, SettingsStore.Current.BrowserPort);            // bản .bak, không phải mặc định 9222
            var broken = Assert.Single(Directory.GetFiles(JobStore.DataDir, "settings.json.broken-*"));
            Assert.Matches(@"settings\.json\.broken-\d{8}-\d{6}$", broken);
            Assert.Equal(corrupt, File.ReadAllText(broken));                   // bản hỏng giữ nguyên văn
            Assert.Equal(9411, ReadSettings(path).BrowserPort);               // file chính đã được đưa về bản .bak

            // Scheduler lưu mỗi phút: không ghi đè bằng cài đặt mặc định, không đụng tới bản hỏng.
            SettingsStore.Current.LastAlive = DateTime.Now;
            SettingsStore.Save();
            var saved = ReadSettings(path);
            Assert.Equal(9411, saved.BrowserPort);
            Assert.Equal(["24/12"], saved.Holidays);
            Assert.Equal(9411, ReadSettings(path + ".bak").BrowserPort);
            Assert.Equal(corrupt, File.ReadAllText(broken));

            var issue = Assert.Single(DataIssues.Take());
            Assert.Contains("settings.json (cài đặt) bị hỏng", issue);
            Assert.Contains("Đã khôi phục từ bản lưu trước", issue);
            Assert.Contains(broken, issue);
        }
        finally
        {
            SettingsStore.Replace(original);
        }
    }

    [Fact]
    public void CorruptSettingsWithoutBackupKeepsBrokenCopy()
    {
        var original = SettingsStore.Current;
        var path = Path.Combine(JobStore.DataDir, "settings.json");
        using var snapshot = new FileSnapshot(JobStore.DataDir, "settings.json", "settings.json.bak");
        DataIssues.Take();
        try
        {
            File.Delete(path + ".bak");
            byte[] corrupt = [0, 0, 0, 0, 0, 0];                              // file toàn byte 0 sau khi mất điện
            File.WriteAllBytes(path, corrupt);
            SettingsStore.ResetForTests();

            Assert.Equal(new AppSettings().BrowserPort, SettingsStore.Current.BrowserPort);   // không còn bản nào → mặc định
            var broken = Assert.Single(Directory.GetFiles(JobStore.DataDir, "settings.json.broken-*"));
            Assert.Equal(corrupt, File.ReadAllBytes(broken));
            Assert.Equal(corrupt, File.ReadAllBytes(path));                    // đọc không ghi gì lên file chính
            var issue = Assert.Single(DataIssues.Take());
            Assert.Contains("đang dùng cài đặt mặc định", issue);
            Assert.Contains(broken, issue);

            SettingsStore.Save();                                              // ghi mặc định: bản gốc vẫn còn ở .broken-… và .bak
            Assert.Equal(corrupt, File.ReadAllBytes(broken));
            Assert.Equal(corrupt, File.ReadAllBytes(path + ".bak"));
        }
        finally
        {
            SettingsStore.Replace(original);
        }
    }

    /// <summary>Đặt thời gian thử đọc lại file đang bị giữ ngắn cho kiểm thử, trả lại khi xong.</summary>
    private sealed class ShortRetry : IDisposable
    {
        private readonly TimeSpan _read = SafeFile.ReadRetry, _lock = SafeFile.LockWait;

        public ShortRetry()
        {
            SafeFile.ReadRetry = TimeSpan.FromMilliseconds(300);
            SafeFile.LockWait = TimeSpan.FromSeconds(10);
        }

        public void Dispose() => (SafeFile.ReadRetry, SafeFile.LockWait) = (_read, _lock);
    }

    [Fact]
    public void FileHeldByAnotherProgramIsNotTreatedAsCorruptAndNeverOverwritten()
    {
        using var retry = new ShortRetry();
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        SafeFile.WriteAllText(path, JsonSerializer.Serialize(new AppSettings { BrowserPort = 9501 }, JsonDefaults.Options));
        SafeFile.WriteAllText(path, JsonSerializer.Serialize(new AppSettings { BrowserPort = 9502 }, JsonDefaults.Options));
        var original = File.ReadAllBytes(path);
        DataIssues.Take();

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))   // chương trình khác giữ chặt file
        {
            Assert.Equal(9501, SettingsStore.Load(path).BrowserPort);                  // dùng tạm bản .bak
            Assert.True(SafeFile.IsReadOnly(path));
        }
        var issue = Assert.Single(DataIssues.Take());
        Assert.Contains("settings.json (cài đặt) không đọc được", issue);
        Assert.Contains("đóng chương trình đang giữ file", issue);
        Assert.Contains("mở lại ScheduleApp", issue);

        // Hết bị giữ nhưng phiên này đang dùng bản cũ hơn: không bao giờ ghi đè file mới hơn, không coi nó là file hỏng.
        var refused = Assert.Throws<IOException>(() => SafeFile.WriteAllText(path, "{}"));
        Assert.Contains("mở lại ScheduleApp", refused.Message);
        SettingsStore.Save();                                                          // lưu mỗi phút: bỏ qua, không ghi nhật ký lỗi
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(9501, ReadSettings(path + ".bak").BrowserPort);
        Assert.Empty(Directory.GetFiles(dir, "settings.json.broken-*"));
        Assert.Empty(DataIssues.Take());

        // Mở lại: đọc được file chính (bản mới nhất), ghi lại bình thường.
        Assert.Equal(9502, SettingsStore.Load(path).BrowserPort);
        Assert.False(SafeFile.IsReadOnly(path));
        SafeFile.WriteAllText(path, "{}");
        Assert.Equal("{}", File.ReadAllText(path));
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        Assert.Empty(Directory.GetFiles(dir, "settings.json.broken-*"));
    }

    [Fact]
    public void LockedBackupWithMissingMainFileIsNeverRotatedAway()
    {
        // jobs.json mất (mất điện lúc thay file), jobs.json.bak — bản tốt duy nhất — đang bị giữ lúc mở.
        using var retry = new ShortRetry();
        var dir = NewDir();
        var path = Path.Combine(dir, "jobs.json");
        var good = JsonSerializer.Serialize(new[] { new Job { Name = "Chỉ còn trong .bak" } }, JsonDefaults.Options);
        File.WriteAllText(path + ".bak", good);
        DataIssues.Take();

        using (new FileStream(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(JobStore.Load(path));                                          // mở với danh sách trống…
            Assert.Contains("mở lại ScheduleApp", Assert.Single(DataIssues.Take()));
        }
        // …lúc đóng lưu danh sách trống, lần mở sau lưu tiếp: không ghi gì, .bak vẫn còn nguyên.
        Assert.Throws<IOException>(() => JobStore.Save(path, []));
        Assert.Throws<IOException>(() => JobStore.Save(path, [new Job { Name = "Mới" }]));
        Assert.False(File.Exists(path));
        Assert.Equal(good, File.ReadAllText(path + ".bak"));

        // Mở lại khi hết bị giữ: khôi phục từ .bak.
        Assert.Equal("Chỉ còn trong .bak", Assert.Single(JobStore.Load(path)).Name);
        Assert.Equal(good, File.ReadAllText(path));
        Assert.Contains("Đã khôi phục từ bản lưu trước (jobs.json.bak)", Assert.Single(DataIssues.Take()));
    }

    [Fact]
    public void BackupThatCouldNotBeCopiedIsNotRotatedAwayAndPartialCopiesAreRemoved()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"BrowserPort\": 95");                           // file chính hỏng
        File.WriteAllText(path + ".bak", "{\"BrowserPort\": 96");                  // .bak cũng hỏng
        DataIssues.Take();
        // Đĩa đầy đúng lúc chép bản .bak hỏng ra .broken-…: bản sao dở bị xóa, .bak được đánh dấu chưa giữ được.
        int failures = 0;
        SafeFile.AfterBrokenCopyCreated = copy =>
        {
            if (copy.Contains(".bak.broken-"))
            {
                failures++;
                throw new IOException("Không đủ chỗ trên đĩa.");
            }
        };
        try
        {
            Assert.Equal(new AppSettings().BrowserPort, SettingsStore.Load(path).BrowserPort);
            Assert.Equal(1, failures);
            Assert.Empty(Directory.GetFiles(dir, "settings.json.bak.broken-*"));       // không để lại bản sao thiếu
            Assert.Single(Directory.GetFiles(dir, "settings.json.broken-*"));

            // Lưu khi vẫn chưa chép được .bak: ghi file chính, không đẩy .bak đi.
            SafeFile.WriteAllText(path, "{\"BrowserPort\": 97}");
            Assert.Equal(2, failures);
            Assert.Equal("{\"BrowserPort\": 96", File.ReadAllText(path + ".bak"));
            Assert.Equal("{\"BrowserPort\": 97}", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, "settings.json.bak.broken-*"));
        }
        finally
        {
            SafeFile.AfterBrokenCopyCreated = null;
        }

        // Chép được rồi: giữ bản .bak cũ ở .bak.broken-… trước, rồi mới thay .bak như thường.
        SafeFile.WriteAllText(path, "{\"BrowserPort\": 98}");
        Assert.Equal("{\"BrowserPort\": 96", File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "settings.json.bak.broken-*"))));
        Assert.Equal("{\"BrowserPort\": 97}", File.ReadAllText(path + ".bak"));
        Assert.Equal("{\"BrowserPort\": 98}", File.ReadAllText(path));
    }

    [Fact]
    public void MissingMainFilePrefersAFinishedTempFileOverTheBackup()
    {
        // Mất điện giữa hai lần đổi tên của ReplaceFile: file chính mất, .bak là bản trước, file tạm là bản vừa lưu (đã ghi xong).
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path + ".bak", JsonSerializer.Serialize(new AppSettings { BrowserPort = 9701 }, JsonDefaults.Options));
        var newest = JsonSerializer.Serialize(new AppSettings { BrowserPort = 9702 }, JsonDefaults.Options);
        File.WriteAllText(path + ".1a2b3c4d.tmp", newest);
        File.WriteAllText(path + ".9f8e7d6c.tmp", "{\"BrowserPort\": 97");                // file tạm ghi dở (mới hơn nữa) — bỏ qua
        File.SetLastWriteTimeUtc(path + ".1a2b3c4d.tmp", DateTime.UtcNow.AddMinutes(-1));
        DataIssues.Take();

        Assert.Equal(9702, SettingsStore.Load(path).BrowserPort);
        Assert.Equal(newest, File.ReadAllText(path));
        Assert.Contains("Đã khôi phục từ bản vừa lưu (settings.json.1a2b3c4d.tmp)", Assert.Single(DataIssues.Take()));

        // Lần lưu kế tiếp dọn các file tạm còn sót; không còn file tạm nào sau khi lưu.
        SafeFile.WriteAllText(path, "{}");
        Assert.Equal([path, path + ".bak"], Directory.GetFiles(dir).Order());
        Assert.Equal(newest, File.ReadAllText(path + ".bak"));

        // Tên file tạm kiểu cũ (settings.json.tmp) cũng được nhận.
        var other = Path.Combine(NewDir(), "settings.json");
        File.WriteAllText(other + ".tmp", JsonSerializer.Serialize(new AppSettings { BrowserPort = 9703 }, JsonDefaults.Options));
        Assert.Equal(9703, SettingsStore.Load(other).BrowserPort);
        DataIssues.Take();
    }

    [Fact]
    public async Task WritesWaitWhileAnotherScheduleAppHoldsTheFileLock()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        SafeFile.WriteAllText(path, "1");
        var name = SafeFile.FileLock.Name(path);
        Assert.StartsWith(@"Local\ScheduleApp-", name);
        Assert.Equal(name, SafeFile.FileLock.Name(path.ToUpperInvariant()));                // cùng file → cùng khóa
        Assert.NotEqual(name, SafeFile.FileLock.Name(path + ".khac"));

        // "Tiến trình khác" (luồng khác — mutex có chủ là luồng) giữ khóa của file.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(false, name);
            m.WaitOne();
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        holder.Start();
        Assert.True(held.Wait(5000));
        var saved = SafeFile.LockWait;
        try
        {
            SafeFile.LockWait = TimeSpan.FromMilliseconds(200);
            var busy = Assert.Throws<IOException>(() => SafeFile.WriteAllText(path, "2"));   // giữ quá lâu → báo, không ghi chồng
            Assert.Contains("tiến trình ScheduleApp khác", busy.Message);
            Assert.Equal("1", File.ReadAllText(path));

            SafeFile.LockWait = TimeSpan.FromSeconds(10);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var write = Task.Run(() => SafeFile.WriteAllText(path, "3"));
            await Task.Delay(400);
            Assert.False(write.IsCompleted);                                                  // chờ bên kia ghi xong
            release.Set();
            await write.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.InRange(sw.ElapsedMilliseconds, 350, 6000);
            Assert.Equal("3", File.ReadAllText(path));
            Assert.Equal("1", File.ReadAllText(path + ".bak"));
        }
        finally
        {
            SafeFile.LockWait = saved;
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void UnreadableJobsAreKeptInTheFileWhenSaving()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "jobs.json");
        Job[] jobs = [new() { Name = "Một" }, new() { Name = "Từ bản mới", Steps = [S(StepType.Wait)] }, new() { Name = "Ba" }];
        var array = JsonNode.Parse(JsonSerializer.Serialize(jobs, JsonDefaults.Options))!.AsArray();
        array[1]!["Steps"]![0]!["Type"] = "BuocCuaPhienBanMoi";
        array[1]!["TruongMoi"] = new JsonObject { ["a"] = 1.50, ["b"] = "x" };            // trường bản này chưa biết
        File.WriteAllText(path, array.ToJsonString(JsonDefaults.Options));
        var unreadable = array[1]!.DeepClone();
        DataIssues.Take();

        var loaded = JobStore.Load(path);
        Assert.Equal(["Một", "Ba"], loaded.Select(j => j.Name));
        loaded[0].Name = "Một (đã sửa)";
        loaded.RemoveAt(1);
        JobStore.Save(path, loaded);

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        Assert.Equal(2, saved.Count);
        Assert.Equal("Một (đã sửa)", saved[0]!["Name"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(unreadable, saved[1]), saved[1]!.ToJsonString());   // nguyên văn, không mất
        // Mở lại: vẫn bỏ qua công việc đó, vẫn giữ khi lưu tiếp.
        Assert.Equal(["Một (đã sửa)"], JobStore.Load(path).Select(j => j.Name));
        Assert.Contains("giữ nguyên các công việc này", Assert.Single(DataIssues.Take().Skip(1)));

        // Lưu một file khác (vd xuất) không kèm công việc của file này.
        var other = Path.Combine(dir, "khac.json");
        JobStore.Save(other, [new Job { Name = "X" }]);
        Assert.Single(JsonNode.Parse(File.ReadAllText(other))!.AsArray());
    }

    [Fact]
    public void NoticeListsAFewShortItemsOnly()
    {
        // 8 công việc hỏng: thông báo nêu 5, lý do rút gọn; nhật ký có đủ.
        var dir = NewDir();
        var path = Path.Combine(dir, "jobs.json");
        var array = new JsonArray();
        for (int i = 1; i <= 8; i++)
            array.Add(new JsonObject { ["Name"] = $"Hỏng {i}", ["Steps"] = new JsonArray(new JsonObject { ["Type"] = "BuocMoi" + new string('x', 300) }) });
        File.WriteAllText(path, array.ToJsonString());
        var logged = new List<string>();
        void OnLog(string line)
        {
            lock (logged) logged.Add(line);
        }
        DataIssues.Take();
        Log.Written += OnLog;
        try
        {
            Assert.Empty(JobStore.Load(path));
        }
        finally
        {
            Log.Written -= OnLog;
        }
        var issue = Assert.Single(DataIssues.Take());
        Assert.Contains("8 công việc không đọc được", issue);
        Assert.Contains("\"Hỏng 5\"", issue);
        Assert.DoesNotContain("\"Hỏng 6\"", issue);
        Assert.Contains("… và 3 công việc khác (xem nhật ký)", issue);
        Assert.True(issue.Length < 2000, issue.Length.ToString());
        lock (logged) Assert.Contains(logged, l => l.Contains("\"Hỏng 8\""));

        // Hộp thoại: tối đa 5 mục, mỗi mục rút gọn.
        var items = Enumerable.Range(1, 7).Select(i => $"Sự cố {i}: " + new string('a', 1000)).ToList();
        var text = DataIssues.Summarize(items);
        Assert.Equal(6, text.Split("\n\n").Length);
        Assert.Contains("• Sự cố 5", text);
        Assert.DoesNotContain("Sự cố 6", text);
        Assert.EndsWith("… và 2 mục khác — xem nhật ký.", text);
        Assert.True(text.Length < 5 * 410 + 100, text.Length.ToString());
        Assert.Equal("• một\n\n• hai", DataIssues.Summarize(["một", "hai"]));
    }

    [Fact]
    public void OneJobWithUnknownStepTypeDoesNotDropTheOthers()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "jobs.json");
        Job[] jobs =
        [
            new() { Name = "Một", Steps = [S(StepType.Wait)] },
            new() { Name = "Từ bản mới", Steps = [S(StepType.Wait), S(StepType.Wait)] },
            new() { Name = "Ba", Steps = [S(StepType.LaunchApp, s => s.Target = "notepad.exe")] }
        ];
        var array = JsonNode.Parse(JsonSerializer.Serialize(jobs, JsonDefaults.Options))!.AsArray();
        array[1]!["Steps"]![1]!["Type"] = "BuocCuaPhienBanMoi";             // loại bước bản này chưa có (bản mới hơn / hạ cấp)
        var json = array.ToJsonString(JsonDefaults.Options);
        File.WriteAllText(path, json);
        DataIssues.Take();

        var loaded = JobStore.Load(path);

        Assert.Equal(["Một", "Ba"], loaded.Select(j => j.Name));
        Assert.Equal(jobs[2].Id, loaded[1].Id);
        var broken = Assert.Single(Directory.GetFiles(dir, "jobs.json.broken-*"));
        Assert.Equal(json, File.ReadAllText(broken));                           // file gốc đủ cả công việc hỏng
        Assert.Equal(json, File.ReadAllText(path));                             // đọc không ghi đè file chính
        var issue = Assert.Single(DataIssues.Take());
        Assert.Contains("1 công việc không đọc được", issue);
        Assert.Contains("\"Từ bản mới\"", issue);
        Assert.Contains("StepType", issue);
        Assert.Contains(broken, issue);

        // Mở lại khi file chưa được lưu lại (vd chạy --test nhiều lần): không tạo thêm bản sao trùng nội dung.
        Assert.Equal(2, JobStore.Load(path).Count);
        Assert.Single(Directory.GetFiles(dir, "jobs.json.broken-*"));
        Assert.Contains(broken, Assert.Single(DataIssues.Take()));
    }

    [Fact]
    public void TruncatedJobsFileFallsBackToBackup()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "jobs.json");
        SafeFile.WriteAllText(path, JsonSerializer.Serialize(new[] { new Job { Name = "Bản trước" } }, JsonDefaults.Options));
        SafeFile.WriteAllText(path, JsonSerializer.Serialize(new[] { new Job { Name = "Bản trước" }, new Job { Name = "Bản mới" } }, JsonDefaults.Options));
        var good = File.ReadAllText(path + ".bak");
        const string truncated = "[{\"Name\": \"Bản mới\", \"Ste";
        File.WriteAllText(path, truncated);
        DataIssues.Take();

        var loaded = JobStore.Load(path);

        Assert.Equal("Bản trước", Assert.Single(loaded).Name);
        Assert.Equal(good, File.ReadAllText(path));                             // file chính được thay bằng bản .bak
        Assert.Equal(truncated, File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "jobs.json.broken-*"))));
        Assert.Contains("Đã khôi phục từ bản lưu trước (jobs.json.bak)", Assert.Single(DataIssues.Take()));

        // Cả .bak cũng hỏng → danh sách trống (không phải công việc mẫu), cả hai bản hỏng đều được giữ.
        File.WriteAllText(path, truncated);
        File.WriteAllText(path + ".bak", "rác");
        Assert.Empty(JobStore.Load(path));
        Assert.Single(Directory.GetFiles(dir, "jobs.json.broken-*"));
        Assert.Equal("rác", File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "jobs.json.bak.broken-*"))));
        Assert.Contains("danh sách công việc trống", Assert.Single(DataIssues.Take()));
    }

    [Fact]
    public void MissingMainFileWithBackupIsRestoredNotTreatedAsFirstRun()
    {
        // Mất điện đúng lúc thay file (giữa hai lần đổi tên của ReplaceFile): chỉ còn .bak.
        var dir = NewDir();
        var settings = Path.Combine(dir, "settings.json");
        var jobs = Path.Combine(dir, "jobs.json");
        File.WriteAllText(settings + ".bak", JsonSerializer.Serialize(new AppSettings { BrowserPort = 9601 }, JsonDefaults.Options));
        File.WriteAllText(jobs + ".bak", JsonSerializer.Serialize(new[] { new Job { Name = "Còn trong .bak" } }, JsonDefaults.Options));
        DataIssues.Take();

        Assert.Equal(9601, SettingsStore.Load(settings).BrowserPort);
        Assert.Equal("Còn trong .bak", Assert.Single(JobStore.Load(jobs)).Name);     // không phải công việc mẫu
        Assert.Equal(File.ReadAllText(settings + ".bak"), File.ReadAllText(settings));
        Assert.Equal(File.ReadAllText(jobs + ".bak"), File.ReadAllText(jobs));
        var issues = DataIssues.Take();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Contains("không còn", i));
        Assert.Empty(Directory.GetFiles(dir, "*.broken-*"));

        // Chưa có file nào (lần đầu dùng) → mặc định / công việc mẫu, không báo gì.
        var fresh = NewDir();
        Assert.Equal(new AppSettings().BrowserPort, SettingsStore.Load(Path.Combine(fresh, "settings.json")).BrowserPort);
        Assert.Equal("Ví dụ: Mở Notepad và gõ chữ", Assert.Single(JobStore.Load(Path.Combine(fresh, "jobs.json"))).Name);
        Assert.Empty(DataIssues.Take());
    }

    [Fact]
    public void ClearingHistoryAlsoRemovesItsBackup()
    {
        var path = Path.Combine(JobStore.DataDir, "history.jsonl");
        File.WriteAllText(path + ".bak", "{\"JobName\":\"cũ\"}" + Environment.NewLine);   // bản trước của lần rút gọn lịch sử
        RunHistory.Clear();
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void SaveKeepsPreviousVersionAsBak()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "du-lieu.json");
        SafeFile.WriteAllText(path, "1");
        Assert.False(File.Exists(path + ".bak"));
        SafeFile.WriteAllText(path, "2");
        SafeFile.WriteAllText(path, "3");
        Assert.Equal("3", File.ReadAllText(path));
        Assert.Equal("2", File.ReadAllText(path + ".bak"));
        Assert.Equal([path, path + ".bak"], Directory.GetFiles(dir).Order());   // không còn file tạm

        // Xuất công việc đè lên file cũ cũng giữ bản trước.
        var export = Path.Combine(dir, "xuat.json");
        JobStore.Export(export, [new Job { Name = "Lần 1" }]);
        JobStore.Export(export, [new Job { Name = "Lần 2" }]);
        Assert.Equal("Lần 2", Assert.Single(JobStore.Import(export)).Name);
        Assert.Equal("Lần 1", Assert.Single(JobStore.Import(export + ".bak")).Name);
    }

    [Fact]
    public void SaveStillWritesMainFileWhenBackupIsHeld()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        SafeFile.WriteAllText(path, "1");
        SafeFile.WriteAllText(path, "2");
        using (new FileStream(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read))   // vd phần mềm diệt virus đang quét .bak
        {
            SafeFile.WriteAllText(path, "3");
        }
        Assert.Equal("3", File.ReadAllText(path));                              // file chính vẫn lưu được
        Assert.Equal("1", File.ReadAllText(path + ".bak"));                     // .bak chỉ là bản trước cũ hơn, không hỏng
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void UnprotectOfForeignOrDamagedDataReturnsEmptyWithoutThrowing()
    {
        var good = Protector.Protect("mật khẩu thật");
        var blob = Convert.FromBase64String(good["dpapi:".Length..]);
        blob[blob.Length / 2] ^= 0x5A;                                          // mã hóa ở máy / tài khoản khác hoặc hỏng: DPAPI từ chối
        string[] bad =
        [
            "dpapi:" + Convert.ToBase64String(blob),
            "dpapi:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(96)),
            "dpapi:@@không-phải-base64@@"
        ];

        var warnings = new List<string>();
        void OnLog(string line)
        {
            if (line.Contains("Không giải mã được")) lock (warnings) warnings.Add(line);
        }
        Log.Written += OnLog;
        try
        {
            foreach (var stored in bad)
            {
                Assert.Equal("", Protector.Unprotect(stored));
                Assert.Equal("", Protector.Unprotect(stored));                 // giao diện gọi liên tục → không ghi thêm cảnh báo
                Assert.False(Protector.TryUnprotect(stored, out var plain));
                Assert.Equal("", plain);
            }
        }
        finally
        {
            Log.Written -= OnLog;
        }

        Assert.Equal(3, warnings.Count);                                        // mỗi giá trị hỏng cảnh báo đúng một lần
        Assert.All(warnings, w => Assert.DoesNotContain(bad[0][6..30], w));     // không ghi nội dung đã lưu
        Assert.Equal("mật khẩu thật", Protector.Unprotect(good));               // dữ liệu tốt vẫn giải mã được
        Assert.Equal("chưa mã hóa", Protector.Unprotect("chưa mã hóa"));

        // Giao diện hỏi "đã nhập khóa AI chưa?" với khóa chép từ máy khác: trả lời "chưa", không báo lỗi.
        var key = SettingsStore.Current.Ai.ApiKey;
        try
        {
            SettingsStore.Current.Ai.ApiKey = bad[0];
            Assert.False(AiClient.IsConfigured);
        }
        finally
        {
            SettingsStore.Current.Ai.ApiKey = key;
        }
    }

    [Fact]
    public void SecretStoreKeepsUndecryptableEntriesWhenSaving()
    {
        var path = Path.Combine(JobStore.DataDir, "secrets.json");
        using var snapshot = new FileSnapshot(JobStore.DataDir, "secrets.json", "secrets.json.bak");
        var foreign = "dpapi:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(120));   // mã hóa ở máy / tài khoản khác
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(
                new Dictionary<string, string> { ["MayCu"] = foreign, ["Tot"] = Protector.Protect("giá trị tốt") }, JsonDefaults.Options));
            SecretStore.ResetForTests();

            Assert.Equal("giá trị tốt", SecretStore.Get("tot"));
            Assert.True(SecretStore.Contains("MayCu"));
            // Bước dùng {{secret:MayCu}} báo lỗi rõ ràng, không âm thầm gõ chuỗi rỗng.
            var error = Assert.Throws<InvalidOperationException>(() => SecretStore.Get("MayCu"));
            Assert.Contains("Không giải mã được bí mật \"MayCu\"", error.Message);

            SecretStore.Set("Moi", "x");
            SecretStore.Remove("Tot");
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonDefaults.Options)!;
            Assert.Equal(["MayCu", "Moi"], saved.Keys.Order());
            Assert.Equal(foreign, saved["MayCu"]);                              // giữ nguyên, không bỏ, không thành chuỗi rỗng
            Assert.Equal("x", SecretStore.Get("Moi"));
        }
        finally
        {
            SecretStore.ResetForTests();                                        // đọc lại secrets.json gốc (snapshot trả lại khi kết thúc)
        }
    }

    [Fact]
    public void CorruptSecretsFileRestoresBackup()
    {
        var path = Path.Combine(JobStore.DataDir, "secrets.json");
        using var snapshot = new FileSnapshot(JobStore.DataDir, "secrets.json", "secrets.json.bak");
        DataIssues.Take();
        try
        {
            File.Delete(path);
            File.Delete(path + ".bak");
            SecretStore.ResetForTests();
            SecretStore.Set("A", "1");
            SecretStore.Set("B", "2");                                          // .bak = lúc mới có A
            File.WriteAllText(path, "{\"A\": \"dpapi:");
            SecretStore.ResetForTests();

            Assert.Equal("1", SecretStore.Get("A"));
            Assert.False(SecretStore.Contains("B"));
            Assert.Equal("{\"A\": \"dpapi:", File.ReadAllText(Assert.Single(Directory.GetFiles(JobStore.DataDir, "secrets.json.broken-*"))));
            Assert.Contains("secrets.json (bí mật) bị hỏng", Assert.Single(DataIssues.Take()));
        }
        finally
        {
            SecretStore.ResetForTests();
        }
    }

    [Fact]
    public void CrashFileHasVersionOsAndExceptionButNoSecrets()
    {
        Log.Mask("bi-mat-khong-duoc-lo");
        Exception error;
        try
        {
            throw new InvalidTimeZoneException("Lỗi thử: bi-mat-khong-duoc-lo · https://api.telegram.org/bot123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw/getUpdates");
        }
        catch (InvalidTimeZoneException ex)
        {
            error = ex;                                                         // có stack trace như lỗi thật
        }

        var file = CrashLog.Write(error, "kiểm thử");
        try
        {
            Assert.NotNull(file);
            Assert.Equal(Log.LogDir, Path.GetDirectoryName(file));
            Assert.Matches(@"^crash-\d{8}-\d{6}\.txt$", Path.GetFileName(file));
            var text = File.ReadAllText(file!);
            Assert.Contains("System.InvalidTimeZoneException", text);
            Assert.Contains("Lỗi thử", text);
            Assert.Contains(nameof(CrashFileHasVersionOsAndExceptionButNoSecrets), text);
            Assert.Contains($"ScheduleApp {UpdateService.Current}", text);
            Assert.Contains(RuntimeInformation.OSDescription, text);
            Assert.DoesNotContain("bi-mat-khong-duoc-lo", text);
            Assert.DoesNotContain("AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", text);

            // Lỗi dồn dập ngay sau đó: ghi tiếp vào cùng file, không tạo hàng loạt file.
            Assert.Equal(file, CrashLog.Write(new ArgumentException("lỗi lặp lại"), "kiểm thử"));
            Assert.Contains("System.ArgumentException: lỗi lặp lại", File.ReadAllText(file!));
        }
        finally
        {
            if (file != null) File.Delete(file);
        }
    }

    /// <summary>
    /// Lỗi thứ hai xảy ra trong lúc hộp thoại báo lỗi thứ nhất còn mở vẫn được ghi nhật ký + file crash: hộp thoại không mở trong
    /// trình xử lý Application.ThreadException (WinForms bỏ qua mọi lỗi trong lúc trình xử lý còn chạy). Vòng lặp thông điệp thật,
    /// cửa sổ ngoài màn hình; "hộp thoại" là form modal thật.
    /// </summary>
    [Fact]
    public void ErrorWhileTheErrorDialogIsOpenIsStillLogged()
    {
        var logged = new List<string>();
        void OnLog(string line)
        {
            if (line.Contains("Lỗi chưa được xử lý (luồng giao diện")) lock (logged) logged.Add(line);
        }
        int dialogs = 0;
        Exception? failure = null;
        Log.Written += OnLog;
        try
        {
            var t = new Thread(() =>
            {
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
                    using var main = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
                    var reporter = new UiErrorReporter(new ErrorDialogGate(), (_, _) =>
                    {
                        dialogs++;
                        // "Hộp thoại" modal: trong lúc nó mở, giao diện gặp lỗi thứ hai; đóng khi lỗi đó đã được ghi (tối đa 5 giây).
                        using var dialog = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
                        var timer = new System.Windows.Forms.Timer { Interval = 50 };
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        timer.Tick += (_, _) =>
                        {
                            bool second;
                            lock (logged) second = logged.Count >= 2;
                            if (second || sw.ElapsedMilliseconds > 5000) dialog.Close();
                        };
                        dialog.Shown += (_, _) =>
                        {
                            dialog.BeginInvoke(new MethodInvoker(() => throw new InvalidOperationException("lỗi thứ hai")));
                            timer.Start();
                        };
                        dialog.ShowDialog();
                        timer.Dispose();
                        main.Close();
                    });
                    Application.ThreadException += (_, e) => reporter.Handle(e.Exception, interactive: true);
                    main.Shown += (_, _) => main.BeginInvoke(new MethodInvoker(() => throw new ArgumentException("lỗi thứ nhất")));
                    Application.Run(main);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(20000), "vòng lặp thông điệp không kết thúc");
        }
        finally
        {
            Log.Written -= OnLog;
        }
        Assert.Null(failure);
        Assert.Equal(1, dialogs);                                               // lỗi thứ hai: không chồng hộp thoại
        lock (logged)
        {
            Assert.Equal(2, logged.Count);
            Assert.Contains("lỗi thứ nhất", logged[0]);
            Assert.Contains("lỗi thứ hai", logged[1]);
        }
        foreach (var f in Directory.GetFiles(Log.LogDir, "crash-*.txt"))
            if (File.ReadAllText(f).Contains("lỗi thứ hai")) File.Delete(f);
    }

    [Fact]
    public void ErrorDialogsNeverStackAndRepeatsAreOnlyLogged()
    {
        var gate = new ErrorDialogGate();
        var t = new DateTime(2026, 10, 5, 9, 0, 0);
        Assert.True(gate.TryOpen(t));                                           // lỗi đầu tiên → hộp thoại
        Assert.False(gate.TryOpen(t.AddSeconds(30)));                           // hộp thoại còn mở → không chồng thêm
        gate.Close(t.AddSeconds(40));
        Assert.False(gate.TryOpen(t.AddSeconds(45)));                           // vừa đóng xong
        Assert.False(gate.TryOpen(t.AddSeconds(54)));                           // lỗi lặp lại liên tục (cách lỗi trước < 10 giây)
        Assert.True(gate.TryOpen(t.AddSeconds(70)));                            // yên 10 giây rồi mới lỗi tiếp → báo lại
    }
}
