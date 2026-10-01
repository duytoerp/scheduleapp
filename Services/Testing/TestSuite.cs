using ScheduleApp.Models;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Services.Testing;

/// <summary>Kết quả chạy một bộ kiểm thử.</summary>
public sealed record SuiteResult(string Name, IReadOnlyList<TestCaseResult> Cases, string ReportPath)
{
    public bool Ok => Cases.Count > 0 && Cases.All(c => c.Ok);
}

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
        if (q is "*" or "all" or "tất cả") return all.Where(j => j.IsTestCase).OrderBy(j => j.Group).ThenBy(j => j.Name).ToList();

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

    /// <param name="reportRoot">Thư mục chứa báo cáo (null = thư mục dữ liệu\test-reports).</param>
    public static async Task<SuiteResult> RunAsync(FlowRunner runner, IReadOnlyList<Job> jobs, string name, string? reportRoot = null)
    {
        var folder = TestReport.NewFolder(name, reportRoot);
        var shots = Path.Combine(folder, "shots");
        var cases = new List<TestCaseResult>();
        Log.Info($"🧪 Bộ kiểm thử \"{name}\": {jobs.Count} kịch bản.");
        int stops = runner.StopCount;
        foreach (var job in jobs)
        {
            var recorder = new TestRecorder(shots);
            var start = DateTime.Now;
            var r = await runner.EnqueueAsync(job, "kiểm thử", new RunOptions { IsTest = true, Recorder = recorder });
            cases.Add(TestCaseResult.Create(job, start, DateTime.Now, r, recorder));
            if (runner.StopCount != stops) break; // người dùng bấm dừng — không chạy tiếp các kịch bản còn lại
        }

        var report = TestReport.Write(folder, name, cases);
        Log.Info($"🧪 {TestReport.Summary(cases)}");
        Log.Info($"   📄 Báo cáo: {report}");
        return new SuiteResult(name, cases, report);
    }
}
