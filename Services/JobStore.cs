using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static readonly object KeptSync = new();

    /// <summary>
    /// Công việc không đọc được của file vừa mở (nguyên văn JSON): ghi lại y nguyên mỗi lần lưu file đó — mở bằng phiên bản ScheduleApp
    /// đọc được chúng (thường là bản mới hơn) vẫn còn đủ.
    /// </summary>
    private static (string Path, List<JsonElement> Items)? _kept;

    public static List<Job> Load() => Load(FilePath);

    /// <summary>
    /// Đọc danh sách công việc, từng công việc một: công việc hỏng (vd loại bước của phiên bản mới hơn) chỉ bị bỏ qua và báo lại,
    /// các công việc khác vẫn dùng được, file gốc đủ mọi công việc được giữ ở bản sao .broken-…. Cả file hỏng / mất → dùng bản .bak.
    /// </summary>
    internal static List<Job> Load(string path)
    {
        if (!File.Exists(path) && !File.Exists(SafeFile.BackupPath(path))) return SampleJobs(); // lần đầu dùng
        var loaded = SafeFile.Load(path, "danh sách công việc", "đang mở với danh sách công việc trống", ParseEach);
        lock (KeptSync) _kept = null;
        if (loaded.Value is not { } parsed) return [];
        if (parsed.Skipped.Count > 0)
        {
            lock (KeptSync) _kept = (Path.GetFullPath(path), parsed.Raw);
            var copy = SafeFile.PreserveBroken(loaded.Source!);
            // Hộp thoại chỉ nêu vài công việc, lý do rút gọn; nhật ký có đủ chi tiết.
            Log.Warn($"{Path.GetFileName(path)}: công việc không đọc được — {string.Join("; ", parsed.Skipped)}");
            var names = parsed.Skipped.Take(MaxListed).Select(s => s.Length > 120 ? s[..120].TrimEnd() + "…" : s);
            DataIssues.Report($"{Path.GetFileName(path)}: {parsed.Skipped.Count} công việc không đọc được nên không có trong danh sách — {string.Join("; ", names)}" +
                (parsed.Skipped.Count > MaxListed ? $"; … và {parsed.Skipped.Count - MaxListed} công việc khác (xem nhật ký)" : "") + ". " +
                "Thường do công việc được tạo bằng phiên bản ScheduleApp mới hơn. Các công việc khác vẫn dùng bình thường; khi lưu, " +
                "ScheduleApp giữ nguyên các công việc này trong file. " +
                (copy != null ? $"Công việc lỗi vẫn còn nguyên trong bản sao file gốc: {copy}"
                              : "Chưa chép được file gốc (file đang bị chương trình khác giữ?) — ScheduleApp sẽ không ghi đè lên nó."));
        }
        JobVersions.Remember(parsed.Jobs);
        return parsed.Jobs;
    }

    /// <summary>Số công việc không đọc được nêu tên trong thông báo (còn lại ghi trong nhật ký).</summary>
    private const int MaxListed = 5;

    public static void Save(IEnumerable<Job> jobs)
    {
        var list = jobs.ToList();
        Save(FilePath, list);
        JobVersions.Track(list);
    }

    /// <summary>Lưu danh sách công việc, kèm nguyên văn các công việc không đọc được lúc mở file đó (ở cuối danh sách).</summary>
    internal static void Save(string path, List<Job> jobs)
    {
        List<JsonElement> kept;
        lock (KeptSync)
            kept = _kept is { } k && string.Equals(k.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ? k.Items : [];
        if (kept.Count == 0)
        {
            SafeFile.WriteAllText(path, JsonSerializer.Serialize(jobs, JsonDefaults.Options));
            return;
        }
        var array = JsonSerializer.SerializeToNode(jobs, JsonDefaults.Options)!.AsArray();
        foreach (var item in kept) array.Add(JsonNode.Parse(item.GetRawText()));
        SafeFile.WriteAllText(path, array.ToJsonString(JsonDefaults.Options));
    }

    public static void Export(string path, IEnumerable<Job> jobs) =>
        SafeFile.WriteAllText(path, JsonSerializer.Serialize(jobs.ToList(), JsonDefaults.Options));

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

    /// <summary>Công việc đọc được + mô tả các công việc hỏng đã bỏ qua + nguyên văn JSON của chúng.</summary>
    private sealed record Parsed(List<Job> Jobs, List<string> Skipped, List<JsonElement> Raw);

    private static Parsed ParseEach(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Nội dung không phải danh sách công việc.");
        var jobs = new List<Job>();
        var skipped = new List<string>();
        var raw = new List<JsonElement>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            try
            {
                jobs.Add(item.Deserialize<Job>(JsonDefaults.Options) ?? throw new JsonException("Công việc trống (null)."));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
            {
                var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty(nameof(Job.Name), out var n) && n.ValueKind == JsonValueKind.String
                    ? $"\"{n.GetString()}\"" : $"công việc thứ {jobs.Count + skipped.Count + 1}";
                skipped.Add($"{name} ({ex.Message.TrimEnd('.')})");
                raw.Add(item.Clone());
            }
        }
        return new(jobs, skipped, raw);
    }

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

    /// <summary>Xóa mọi phiên bản cũ của công việc (gọi khi xóa công việc). Trả về false nếu không xóa được thư mục.</summary>
    public static bool Delete(Guid jobId)
    {
        lock (Sync)
        {
            LastSaved.Remove(jobId);
            var dir = Path.Combine(Dir, jobId.ToString("N"));
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Không xóa được các phiên bản cũ trong {dir}: {ex.Message}");
                return false;
            }
        }
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
