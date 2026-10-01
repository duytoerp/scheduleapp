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
            return ReadFile(FilePath);
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
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(jobs.ToList(), JsonDefaults.Options));
        File.Move(tmp, FilePath, true);
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
