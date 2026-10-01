using System.Globalization;
using ScheduleApp.Models;
using ScheduleApp.Services.Data;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Services.Testing;

/// <summary>Kết quả chạy một bộ kiểm thử.</summary>
public sealed record SuiteResult(string Name, IReadOnlyList<TestCaseResult> Cases, string ReportPath)
{
    public bool Ok => Cases.Count > 0 && Cases.All(c => c.Ok);
}

/// <summary>Tùy chọn chạy bộ kiểm thử.</summary>
public sealed class SuiteOptions
{
    /// <summary>Tên môi trường (hiện trong báo cáo).</summary>
    public string? Environment { get; init; }

    /// <summary>Biến của môi trường — ghi đè biến khai báo trong kịch bản.</summary>
    public Dictionary<string, string>? Variables { get; init; }

    /// <summary>Kịch bản không đạt được chạy lại tối đa N lần; đạt ở lần chạy lại thì ghi "đạt sau khi chạy lại" (kịch bản chập chờn).</summary>
    public int Retries { get; init; }

    /// <summary>Thư mục gốc cho đường dẫn tương đối của file dữ liệu (vd thư mục kịch bản khi chạy --test-dir).</summary>
    public string? BaseDir { get; init; }

    public static readonly SuiteOptions Default = new();
}

/// <summary>Một lần chạy của kịch bản: cả kịch bản, hoặc một dòng dữ liệu của kịch bản kiểm thử theo dữ liệu.</summary>
internal sealed record TestRun(string Name, string? DataLabel, Dictionary<string, string>? Variables, string? Error = null);

/// <summary>Chạy lần lượt các kịch bản kiểm thử (qua hàng đợi của <see cref="FlowRunner"/>) rồi xuất một báo cáo chung.</summary>
public static class TestSuite
{
    /// <summary>
    /// Chọn kịch bản theo tên nhóm hoặc tên công việc: "*" = mọi kịch bản kiểm thử; tên nhóm = các kịch bản trong nhóm
    /// (nhóm chưa đánh dấu kịch bản nào thì lấy cả nhóm); không thì công việc trùng tên (hoặc chứa tên, nếu chỉ có một).
    /// </summary>
    public static List<Job> Select(IEnumerable<Job> jobs, string query)
    {
        var all = jobs.ToList();
        var q = query.Trim().Trim('"');
        if (q is "" or "*" or "all" or "tất cả") return all.Where(j => j.IsTestCase).OrderBy(j => j.Group).ThenBy(j => j.Name).ToList();

        var group = all.Where(j => j.Group.Equals(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (group.Count > 0)
        {
            var tests = group.Where(j => j.IsTestCase).ToList();
            return (tests.Count > 0 ? tests : group).OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        var exact = all.Where(j => j.Name.Equals(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        return all.Where(j => j.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList() is { Count: 1 } one ? one : [];
    }

    /// <summary>Giữ các kịch bản có ít nhất một tag trong <paramref name="tags"/> (cách nhau dấu phẩy; trống = không lọc).</summary>
    public static List<Job> WithTags(IEnumerable<Job> jobs, string? tags)
    {
        var wanted = (tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return wanted.Length == 0
            ? jobs.ToList()
            : jobs.Where(j => j.TagList.Any(t => wanted.Contains(t, StringComparer.CurrentCultureIgnoreCase))).ToList();
    }

    /// <summary>
    /// Chia bộ kiểm thử cho nhiều máy chạy song song: "2/4" = phần thứ 2 trong 4 phần. Chia theo thứ tự nhóm, tên, Id nên
    /// mọi máy cùng danh sách kịch bản sẽ chia giống nhau và không trùng / sót kịch bản nào.
    /// </summary>
    public static List<Job> Shard(IEnumerable<Job> jobs, string spec)
    {
        var (index, count) = ParseShard(spec);
        return jobs.OrderBy(j => j.Group, StringComparer.Ordinal).ThenBy(j => j.Name, StringComparer.Ordinal).ThenBy(j => j.Id)
            .Where((_, i) => i % count == index - 1).ToList();
    }

    public static (int Index, int Count) ParseShard(string spec)
    {
        var parts = (spec ?? "").Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out int i) && int.TryParse(parts[1], out int n) && n >= 1 && i >= 1 && i <= n) return (i, n);
        throw new FormatException($"--shard \"{spec}\" không hợp lệ — dùng dạng phần/tổng, vd 1/3, 2/3, 3/3.");
    }

    /// <summary>Số lần đạt trong <paramref name="last"/> lần chạy gần nhất của công việc (độ ổn định của kịch bản).</summary>
    public static (int Passed, int Total) Stability(IEnumerable<RunRecord> history, Guid jobId, int last = 10)
    {
        var runs = history.Where(r => r.JobId == jobId).OrderByDescending(r => r.Start).Take(last).ToList();
        return (runs.Count(r => r.Ok), runs.Count);
    }

    /// <summary>
    /// Các lần chạy của một kịch bản: một lần, hoặc mỗi dòng của file dữ liệu một lần (biến {{row.TênCột}}, {{row.rowNumber}},
    /// {{data.index}}, {{data.count}}). Biến của môi trường được dùng chung, biến của dòng ghi đè lên.
    /// </summary>
    internal static List<TestRun> Runs(Job job, SuiteOptions options)
    {
        if (string.IsNullOrWhiteSpace(job.DataFile)) return [new TestRun(job.Name, null, options.Variables)];
        DataTableResult table;
        try
        {
            table = TabularReader.Read(DataPath(job.DataFile, options.BaseDir), string.IsNullOrWhiteSpace(job.DataSheet) ? null : job.DataSheet.Trim());
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [new TestRun(job.Name + " [dữ liệu]", "dữ liệu", options.Variables, "Không đọc được file dữ liệu: " + ex.Message)];
        }
        if (table.Rows.Count == 0) return [new TestRun(job.Name + " [dữ liệu]", "dữ liệu", options.Variables, "File dữ liệu không có dòng nào (ngoài dòng tiêu đề).")];

        var runs = new List<TestRun>(table.Rows.Count);
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var vars = new Dictionary<string, string>(options.Variables ?? [], StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < table.Headers.Count; c++)
            {
                vars[$"row.{table.Headers[c]}"] = row[c];
                vars[$"row.{c + 1}"] = row[c];
            }
            vars["row"] = string.Join("\t", row);
            vars["row.rowNumber"] = table.RowNumbers[r].ToString(CultureInfo.InvariantCulture);
            vars["data.index"] = (r + 1).ToString(CultureInfo.InvariantCulture);
            vars["data.count"] = table.Rows.Count.ToString(CultureInfo.InvariantCulture);
            var first = row.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
            if (first.Length > 40) first = first[..40] + "…";
            var label = $"dòng {table.RowNumbers[r]}" + (first.Length > 0 ? $": {first}" : "");
            runs.Add(new TestRun($"{job.Name} [{label}]", label, vars));
        }
        return runs;
    }

    /// <summary>Đường dẫn file dữ liệu: biến môi trường Windows được thay; đường dẫn tương đối tính từ <paramref name="baseDir"/>.</summary>
    internal static string DataPath(string file, string? baseDir)
    {
        var path = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        return Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(baseDir) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(baseDir, path));
    }

    /// <param name="reportRoot">Thư mục chứa báo cáo (null = thư mục dữ liệu\test-reports).</param>
    public static async Task<SuiteResult> RunAsync(FlowRunner runner, IReadOnlyList<Job> jobs, string name, string? reportRoot = null, SuiteOptions? options = null)
    {
        options ??= SuiteOptions.Default;
        var folder = TestReport.NewFolder(name, reportRoot);
        var shots = Path.Combine(folder, "shots");
        var cases = new List<TestCaseResult>();
        Log.Info($"🧪 Bộ kiểm thử \"{name}\": {jobs.Count} kịch bản" + (options.Environment is { Length: > 0 } env ? $" · môi trường {env}" : "") + ".");
        int stops = runner.StopCount;
        foreach (var job in jobs)
        {
            foreach (var run in Runs(job, options))
            {
                if (run.Error != null)
                {
                    Log.Warn($"   ✖ {run.Name}: {run.Error}");
                    cases.Add(TestCaseResult.Create(job, DateTime.Now, DateTime.Now, new FlowResult(false, run.Error), new TestRecorder(null), run));
                    continue;
                }
                cases.Add(await RunCaseAsync(runner, job, run, shots, options.Retries));
                if (runner.StopCount != stops) break; // người dùng bấm dừng — không chạy tiếp
            }
            if (runner.StopCount != stops) break;
        }

        var report = TestReport.Write(folder, name, cases, options.Environment);
        Log.Info($"🧪 {TestReport.Summary(cases)}");
        Log.Info($"   📄 Báo cáo: {report}");
        return new SuiteResult(name, cases, report);
    }

    private static async Task<TestCaseResult> RunCaseAsync(FlowRunner runner, Job job, TestRun run, string shots, int retries)
    {
        string? firstFailure = null;
        int stops = runner.StopCount;
        for (int attempt = 1; ; attempt++)
        {
            var recorder = new TestRecorder(shots);
            var start = DateTime.Now;
            var r = await runner.EnqueueAsync(job, "kiểm thử", new RunOptions { IsTest = true, Recorder = recorder, Variables = run.Variables });
            var result = TestCaseResult.Create(job, start, DateTime.Now, r, recorder, run, attempt, firstFailure);
            if (result.Ok || r == null || attempt > retries || runner.StopCount != stops) return result;
            firstFailure ??= result.FailureSummary;
            Log.Warn($"   ↻ \"{run.Name}\" không đạt — chạy lại lần {attempt + 1}/{retries + 1}.");
        }
    }
}
