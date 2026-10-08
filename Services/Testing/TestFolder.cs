using System.Text.Json;
using ScheduleApp.Models;

namespace ScheduleApp.Services.Testing;

/// <summary>
/// Lưu kịch bản kiểm thử thành file trong một thư mục (vd thư mục trong repo git): mỗi công việc một file JSON
/// &lt;Nhóm&gt;\&lt;Tên&gt;.json, giữ nguyên Id để các bước "Chạy công việc khác" vẫn trỏ đúng; môi trường trong environments.json.
/// Dùng để review thay đổi (diff), làm việc nhóm và chạy CI thẳng từ repo: ScheduleApp.exe --test * --test-dir "thư mục".
/// </summary>
public static class TestFolder
{
    public const string EnvironmentsFile = "environments.json";

    /// <summary>Kết quả xuất: file đã ghi và file cũ đã xóa (công việc được đổi tên / đổi nhóm).</summary>
    public sealed record ExportResult(IReadOnlyList<string> Written, IReadOnlyList<string> Removed);

    /// <summary>
    /// Kết quả nhập: số công việc cập nhật (cùng Id) và thêm mới; <paramref name="Skipped"/> = công việc trong thư mục trùng Id với
    /// công việc thường (không phải kịch bản kiểm thử) đang có — giữ nguyên công việc đang có.
    /// </summary>
    public sealed record MergeResult(int Updated, int Added, int Skipped = 0);

    /// <summary>Các công việc cần mang theo: công việc được chọn và mọi công việc chúng gọi tới (Chạy công việc khác, công việc xử lý lỗi).</summary>
    public static List<Job> WithDependencies(IEnumerable<Job> selected, IReadOnlyList<Job> all)
    {
        var byId = all.GroupBy(j => j.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<Job>();
        var seen = new HashSet<Guid>();
        var queue = new Queue<Job>(selected);
        while (queue.Count > 0)
        {
            var job = queue.Dequeue();
            if (!seen.Add(job.Id)) continue;
            result.Add(job);
            var refs = job.Steps.Where(s => s.Type == StepType.CallJob && s.JobRef != null).Select(s => s.JobRef!.Value);
            if (job.OnFailureJobId is Guid f) refs = refs.Append(f);
            foreach (var id in refs)
                if (byId.TryGetValue(id, out var dep)) queue.Enqueue(dep);
        }
        return result;
    }

    /// <summary>Đường dẫn tương đối của file một công việc: &lt;Nhóm&gt;\&lt;Tên&gt;.json (ký tự không hợp lệ thay bằng "_").</summary>
    public static string RelativePath(Job job)
    {
        static string Safe(string s)
        {
            var clean = string.Concat(s.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            return clean.Length == 0 ? "_" : clean.Length > 80 ? clean[..80] : clean;
        }
        var file = Safe(job.Name) + ".json";
        return job.Group.Trim().Length == 0 ? file : Path.Combine(Safe(job.Group), file);
    }

    /// <summary>
    /// Ghi các công việc (kèm công việc phụ thuộc) vào <paramref name="folder"/>. File cũ của cùng công việc ở chỗ khác (đổi tên / nhóm)
    /// được xóa; các file khác trong thư mục giữ nguyên. Không ghi thời điểm / kết quả lần chạy cuối để diff gọn.
    /// <paramref name="stripSecrets"/> = bỏ bí mật ghi thẳng trong công việc / biến môi trường (<see cref="SecretHider.WithoutLiteralSecrets(Job)"/>).
    /// </summary>
    public static ExportResult Export(string folder, IEnumerable<Job> jobs, IReadOnlyList<Job> all, IReadOnlyList<TestEnvironment>? environments = null,
        bool stripSecrets = false)
    {
        Directory.CreateDirectory(folder);
        var existing = Scan(folder);
        var written = new List<string>();
        var removed = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in WithDependencies(jobs, all))
        {
            var rel = RelativePath(job);
            // Hai công việc trùng nhóm + tên → thêm đuôi Id cho file thứ hai.
            if (!used.Add(rel)) rel = Path.ChangeExtension(rel, null) + "_" + job.Id.ToString("N")[..8] + ".json";
            var path = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var copy = stripSecrets ? SecretHider.WithoutLiteralSecrets(job) : job.Clone();
            copy.LastRun = null;
            copy.LastResult = null;
            JobApproval.Approve(copy); // duyệt là việc của từng máy — máy nhập về tự đánh dấu chờ duyệt
            File.WriteAllText(path, JsonSerializer.Serialize(copy, JsonDefaults.Options) + Environment.NewLine);
            written.Add(path);
            if (existing.TryGetValue(job.Id, out var old) && !old.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(old);
                removed.Add(old);
            }
        }
        if (environments is { Count: > 0 })
            File.WriteAllText(Path.Combine(folder, EnvironmentsFile), JsonSerializer.Serialize(stripSecrets ? environments.Select(SecretHider.WithoutLiteralSecrets).ToList() : environments, JsonDefaults.Options) + Environment.NewLine);
        return new ExportResult(written, removed);
    }

    /// <summary>Đọc mọi công việc trong thư mục (đệ quy). Hai file cùng Id là lỗi (thường do sao chép file bằng tay).</summary>
    public static List<Job> Load(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Không có thư mục kịch bản \"{folder}\".");
        var jobs = new List<Job>();
        var where = new Dictionary<Guid, string>();
        foreach (var file in JobFiles(folder))
        {
            Job job;
            try
            {
                job = JsonSerializer.Deserialize<Job>(File.ReadAllText(file), JsonDefaults.Options)
                      ?? throw new JsonException("file trống");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"File \"{Path.GetRelativePath(folder, file)}\" không phải công việc ScheduleApp hợp lệ: {ex.Message}");
            }
            if (where.TryGetValue(job.Id, out var other))
                throw new InvalidDataException($"Hai file cùng Id công việc: \"{Path.GetRelativePath(folder, other)}\" và \"{Path.GetRelativePath(folder, file)}\".");
            where[job.Id] = file;
            jobs.Add(job);
        }
        return jobs;
    }

    /// <summary>Môi trường trong environments.json của thư mục (không có file = danh sách trống).</summary>
    public static List<TestEnvironment> LoadEnvironments(string folder)
    {
        var path = Path.Combine(folder, EnvironmentsFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<TestEnvironment>>(File.ReadAllText(path), JsonDefaults.Options) ?? [] : [];
    }

    /// <summary>
    /// Nhập công việc từ thư mục vào danh sách hiện có: cùng Id với một kịch bản kiểm thử thì thay (giữ lần chạy cuối), chưa có thì thêm.
    /// Không bao giờ thay công việc thường (không phải kịch bản) — thư mục lạ không ghi đè được công việc theo lịch của bạn.
    /// Công việc thêm / thay đổi được đánh dấu chờ duyệt (<paramref name="reason"/>); kịch bản không đổi gì giữ nguyên trạng thái duyệt.
    /// </summary>
    public static MergeResult Merge(List<Job> current, IEnumerable<Job> incoming, string? reason = null)
    {
        reason ??= JobApproval.ImportReason("thư mục kịch bản");
        int updated = 0, added = 0, skipped = 0;
        foreach (var job in incoming)
        {
            int i = current.FindIndex(j => j.Id == job.Id);
            if (i >= 0)
            {
                var old = current[i];
                if (!old.IsTestCase || !job.IsTestCase)
                {
                    skipped++;
                    continue;
                }
                bool same = !old.NeedsApproval && Content(old) == Content(job);
                job.LastRun = old.LastRun;
                job.LastResult = old.LastResult;
                if (same) JobApproval.Approve(job);
                else JobApproval.Require(job, reason);
                current[i] = job;
                updated++;
            }
            else
            {
                JobApproval.Require(job, reason);
                current.Add(job);
                added++;
            }
        }
        return new MergeResult(updated, added, skipped);
    }

    /// <summary>Đếm trước kết quả <see cref="Merge"/> (cùng quy tắc) để hỏi người dùng đúng con số: (cập nhật, thêm mới, bỏ qua — trùng Id công việc thường).</summary>
    public static (int Updated, int Added, List<string> Skipped) PreviewMerge(IReadOnlyList<Job> current, IEnumerable<Job> incoming)
    {
        int updated = 0, added = 0;
        var skipped = new List<string>();
        foreach (var job in incoming)
        {
            var old = current.FirstOrDefault(j => j.Id == job.Id);
            if (old == null) added++;
            else if (!old.IsTestCase || !job.IsTestCase) skipped.Add(old.Name);
            else updated++;
        }
        return (updated, added, skipped);
    }

    /// <summary>
    /// Thay đổi biến môi trường nếu nhập <paramref name="incoming"/> (cùng tên thì thay cả môi trường): mỗi dòng một môi trường mới / biến thêm,
    /// đổi giá trị, bị bỏ. Biến môi trường ghi đè biến của mọi kịch bản chạy với môi trường đó (kể cả công việc được gọi) → phải hỏi riêng.
    /// Giá trị được che bí mật và rút gọn. Không có thay đổi → danh sách rỗng.
    /// </summary>
    public static List<string> DescribeEnvironmentChanges(IReadOnlyList<TestEnvironment> current, IEnumerable<TestEnvironment> incoming)
    {
        static string Show(string value)
        {
            var v = Log.Redact(value).Replace("\r", "").Replace("\n", " ⏎ ");
            return v.Length > 60 ? v[..57] + "…" : v;
        }
        var lines = new List<string>();
        foreach (var env in incoming)
        {
            var old = current.FirstOrDefault(e => e.Name.Trim().Equals(env.Name.Trim(), StringComparison.CurrentCultureIgnoreCase));
            var before = (old?.Variables ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Name))
                .GroupBy(v => v.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
            var after = env.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name))
                .GroupBy(v => v.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
            var changes = new List<string>();
            foreach (var (name, value) in after)
            {
                if (!before.TryGetValue(name, out var was)) changes.Add($"+ {name} = {Show(value)}");
                else if (was != value) changes.Add($"~ {name}: {Show(was)} → {Show(value)}");
            }
            changes.AddRange(before.Keys.Where(k => !after.ContainsKey(k)).Select(k => $"− {k}"));
            if (changes.Count == 0) continue;
            lines.Add($"Môi trường \"{env.Name.Trim()}\"{(old == null ? " (mới)" : "")}:");
            lines.AddRange(changes.Select(c => "   " + c));
        }
        return lines;
    }

    /// <summary>Nội dung công việc để so sánh (bỏ lần chạy cuối và trạng thái duyệt).</summary>
    private static string Content(Job job)
    {
        var copy = job.Clone();
        copy.LastRun = null;
        copy.LastResult = null;
        JobApproval.Approve(copy);
        return JsonSerializer.Serialize(copy, JsonDefaults.Options);
    }

    private static IEnumerable<string> JobFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals(EnvironmentsFile, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>Id công việc → file đang chứa nó (bỏ qua file không đọc được).</summary>
    private static Dictionary<Guid, string> Scan(string folder)
    {
        var map = new Dictionary<Guid, string>();
        foreach (var file in JobFiles(folder))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Id", out var id) && id.TryGetGuid(out var g))
                    map.TryAdd(g, Path.GetFullPath(file));
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return map;
    }
}

/// <summary>Biến của môi trường kiểm thử.</summary>
public static class TestEnvironments
{
    /// <summary>Môi trường theo tên (không phân biệt hoa thường); null nếu tên trống.</summary>
    public static TestEnvironment? Find(IEnumerable<TestEnvironment> environments, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return environments.FirstOrDefault(e => e.Name.Trim().Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase))
               ?? throw new InvalidOperationException($"Không có môi trường \"{name.Trim()}\". Có: {string.Join(", ", environments.Select(e => e.Name))}.");
    }

    /// <summary>Biến của môi trường (null = không có biến nào) — kèm {{env.name}}.</summary>
    public static Dictionary<string, string>? Variables(TestEnvironment? env)
    {
        if (env == null) return null;
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["env.name"] = env.Name.Trim() };
        foreach (var v in env.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name))) vars[v.Name.Trim()] = v.Value;
        return vars;
    }

    /// <summary>Môi trường đang chọn trên trang Kiểm thử (null nếu không chọn / môi trường đã bị xóa).</summary>
    public static TestEnvironment? Current()
    {
        var s = SettingsStore.Current;
        return string.IsNullOrWhiteSpace(s.CurrentEnvironment)
            ? null
            : s.Environments.FirstOrDefault(e => e.Name.Trim().Equals(s.CurrentEnvironment.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Gộp biến: <paramref name="overrides"/> ghi đè <paramref name="baseVars"/> (null + null = null).</summary>
    public static Dictionary<string, string>? Merge(Dictionary<string, string>? baseVars, Dictionary<string, string>? overrides)
    {
        if (baseVars == null) return overrides;
        if (overrides == null) return baseVars;
        var merged = new Dictionary<string, string>(baseVars, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in overrides) merged[k] = v;
        return merged;
    }
}
