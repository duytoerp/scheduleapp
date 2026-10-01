using System.Text.Json;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>Lưu/đọc danh sách công việc dạng JSON trong %AppData%\ScheduleApp.</summary>
public static class JobStore
{
    /// <summary>Thư mục dữ liệu: %AppData%\ScheduleApp, hoặc biến môi trường SCHEDULEAPP_DATA_DIR (bản portable / kiểm thử).</summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("SCHEDULEAPP_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(custom))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScheduleApp");

    private static string FilePath => Path.Combine(DataDir, "jobs.json");

    public static List<Job> Load()
    {
        if (!File.Exists(FilePath)) return SampleJobs();
        try
        {
            var jobs = ReadFile(FilePath);
            JobVersions.Remember(jobs);
            return jobs;
        }
        catch (Exception ex)
        {
            var backup = FilePath + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Copy(FilePath, backup, true); } catch { }
            Log.Error($"Không đọc được jobs.json ({ex.Message}). Đã sao lưu file lỗi sang {backup}.");
            return [];
        }
    }

    public static void Save(IEnumerable<Job> jobs)
    {
        var list = jobs.ToList();
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonDefaults.Options));
        File.Move(tmp, FilePath, true);
        JobVersions.Track(list);
    }

    public static void Export(string path, IEnumerable<Job> jobs) =>
        File.WriteAllText(path, JsonSerializer.Serialize(jobs.ToList(), JsonDefaults.Options));

    /// <summary>Đọc công việc từ file; gán Id mới để không trùng với công việc đang có.</summary>
    public static List<Job> Import(string path) => Renew(ReadFile(path));

    /// <summary>Đọc công việc từ chuỗi JSON (vd mẫu nhúng sẵn); gán Id mới.</summary>
    public static List<Job> ImportJson(string json) =>
        Renew(JsonSerializer.Deserialize<List<Job>>(json, JsonDefaults.Options) ?? []);

    /// <summary>
    /// Gán Id mới cho các công việc, đồng thời cập nhật tham chiếu giữa chúng (bước "Chạy công việc khác",
    /// công việc xử lý lỗi) để vẫn trỏ đúng sau khi nhập.
    /// </summary>
    private static List<Job> Renew(List<Job> jobs)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var j in jobs)
        {
            var id = Guid.NewGuid();
            map.TryAdd(j.Id, id);
            j.Id = id;
        }
        foreach (var j in jobs)
        {
            j.LastRun = null;
            j.LastResult = null;
            if (j.OnFailureJobId is Guid f) j.OnFailureJobId = map.TryGetValue(f, out var nf) ? nf : null;
            foreach (var s in j.Steps)
                if (s.JobRef is Guid r && map.TryGetValue(r, out var nr)) s.JobRef = nr;
        }
        return jobs;
    }

    private static List<Job> ReadFile(string path) =>
        JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(path), JsonDefaults.Options) ?? [];

    private static List<Job> SampleJobs() =>
    [
        new Job
        {
            Name = "Ví dụ: Mở Notepad và gõ chữ",
            Schedule = new ScheduleConfig { Type = ScheduleType.Manual },
            Steps =
            [
                new ActionStep
                {
                    Type = StepType.Reminder, Target = "Bắt đầu demo",
                    Text = "ScheduleApp sẽ mở Notepad và tự gõ một đoạn văn bản.\nBấm \"Đã hiểu\" để tiếp tục.",
                    WaitForUser = true
                },
                new ActionStep { Type = StepType.LaunchApp, Target = "notepad.exe" },
                new ActionStep { Type = StepType.WaitForWindow, Target = "notepad", DelayMs = 15000 },
                new ActionStep { Type = StepType.FocusWindow, Target = "notepad", DelayMs = 5000 },
                new ActionStep { Type = StepType.TypeText, Target = "notepad", Text = "Xin chào! Đoạn này do ScheduleApp tự động gõ.\n" },
                new ActionStep { Type = StepType.KeyPress, Target = "notepad", Text = "Enter*2" },
                new ActionStep { Type = StepType.TypeText, Target = "notepad", Text = "Chúc một ngày làm việc hiệu quả 🎉" }
            ]
        }
    ];
}

/// <summary>
/// Lịch sử phiên bản của từng công việc: mỗi lần nội dung công việc thay đổi (sửa bước, lịch, biến…), bản trước đó được lưu vào
/// versions\&lt;id&gt;\ để khôi phục khi sửa nhầm. Giữ <see cref="Keep"/> bản gần nhất. Lần chạy (LastRun) không tính là thay đổi.
/// </summary>
public static class JobVersions
{
    public const int Keep = 30;

    private static readonly object Sync = new();
    private static readonly Dictionary<Guid, string> LastSaved = [];

    public sealed record Version(string Path, DateTime SavedAt, Job Job);

    private static string Dir => Path.Combine(JobStore.DataDir, "versions");

    /// <summary>Ghi nhớ nội dung hiện tại (lúc mở ứng dụng) làm mốc so sánh.</summary>
    public static void Remember(IEnumerable<Job> jobs)
    {
        lock (Sync)
            foreach (var j in jobs) LastSaved[j.Id] = Normalize(j);
    }

    /// <summary>Gọi sau khi lưu: công việc nào đổi nội dung thì lưu bản cũ thành một phiên bản.</summary>
    public static void Track(IEnumerable<Job> jobs)
    {
        lock (Sync)
        {
            foreach (var j in jobs)
            {
                var now = Normalize(j);
                if (LastSaved.TryGetValue(j.Id, out var before) && before != now)
                {
                    try { Write(j.Id, before); }
                    catch (Exception ex) { Log.Warn($"Không lưu được phiên bản cũ của \"{j.Name}\": {ex.Message}"); }
                }
                LastSaved[j.Id] = now;
            }
        }
    }

    public static List<Version> List(Guid jobId)
    {
        var dir = Path.Combine(Dir, jobId.ToString("N"));
        if (!Directory.Exists(dir)) return [];
        var result = new List<Version>();
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderDescending())
        {
            try
            {
                var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(file), JsonDefaults.Options);
                if (job != null) result.Add(new Version(file, File.GetLastWriteTime(file), job));
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return result;
    }

    private static void Write(Guid id, string json)
    {
        var dir = Path.Combine(Dir, id.ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"), json);
        foreach (var old in Directory.GetFiles(dir, "*.json").OrderDescending().Skip(Keep))
            try { File.Delete(old); } catch (IOException) { }
    }

    /// <summary>Nội dung công việc bỏ các trường thay đổi mỗi lần chạy.</summary>
    private static string Normalize(Job job)
    {
        var (lastRun, lastResult) = (job.LastRun, job.LastResult);
        try
        {
            job.LastRun = null;
            job.LastResult = null;
            return JsonSerializer.Serialize(job, JsonDefaults.Options);
        }
        finally
        {
            (job.LastRun, job.LastResult) = (lastRun, lastResult);
        }
    }
}
