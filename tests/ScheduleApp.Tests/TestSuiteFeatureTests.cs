using System.Text;
using System.Xml.Linq;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using ScheduleApp.Services.Testing;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Thư mục kịch bản (git), môi trường, tag, shard, chạy lại kịch bản lỗi, độ ổn định, kiểm thử theo dữ liệu và dòng lệnh --test —
/// chạy các flow thật (bước Gán biến / Kiểm tra / Ghi CSV), không cần trình duyệt.
/// </summary>
public class TestSuiteFeatureTests
{
    private static ActionStep Check(string left, CompareOp op, string right, string message = "") =>
        S(StepType.Assert, s => { s.Condition = ConditionKind.Compare; s.Target = left; s.CompareOp = op; s.Arguments = right; s.DelayMs = 0; s.Message = message; });

    private static Job Case(string name, string group, params ActionStep[] steps) =>
        new() { Name = name, Group = group, IsTestCase = true, Steps = [.. steps] };

    // ───────────────────────────── Thư mục kịch bản ─────────────────────────────

    [Fact]
    public void ExportWritesOneFilePerJobWithDependenciesAndLoadsBack()
    {
        var dir = NewDir();
        var opener = new Job { Name = "Mở app D365", Group = "Dùng chung", Steps = [SetVar("d365Url", VarSource.Value, "x")] };
        var cleanup = new Job { Name = "Dọn dẹp", Group = "Dùng chung" };
        var unrelated = new Job { Name = "Sao lưu", Group = "Khác" };
        var test = Case("Tạo khách hàng: <A>", "CRM", S(StepType.CallJob, s => { s.JobRef = opener.Id; s.Target = opener.Name; }), Check("1", CompareOp.Equals, "1"));
        test.OnFailureJobId = cleanup.Id;
        test.Tags = "smoke, crm";
        test.LastRun = DateTime.Now;
        test.LastResult = "OK";
        var all = new List<Job> { opener, cleanup, unrelated, test };
        var envs = new List<TestEnvironment> { new() { Name = "UAT", Variables = [new() { Name = "d365Url", Value = "https://uat" }] } };

        var r = TestFolder.Export(dir, [test], all, envs);

        Assert.Equal(3, r.Written.Count); // kịch bản + công việc gọi tới + công việc xử lý lỗi, không có "Sao lưu"
        Assert.True(File.Exists(Path.Combine(dir, "CRM", "Tạo khách hàng_ _A_.json")));
        Assert.True(File.Exists(Path.Combine(dir, "Dùng chung", "Mở app D365.json")));
        Assert.True(File.Exists(Path.Combine(dir, TestFolder.EnvironmentsFile)));
        var text = File.ReadAllText(Path.Combine(dir, "CRM", "Tạo khách hàng_ _A_.json"));
        Assert.DoesNotContain("\"LastResult\": \"OK\"", text); // không ghi kết quả lần chạy — diff gọn

        var loaded = TestFolder.Load(dir);
        Assert.Equal(3, loaded.Count);
        var back = loaded.Single(j => j.Id == test.Id);
        Assert.Equal(opener.Id, back.Steps[0].JobRef); // giữ Id → tham chiếu vẫn đúng
        Assert.Equal(cleanup.Id, back.OnFailureJobId);
        Assert.Equal(["smoke", "crm"], back.TagList);
        Assert.Null(back.LastRun);
        Assert.Equal("https://uat", Assert.Single(TestFolder.LoadEnvironments(dir)).Variables[0].Value);
    }

    [Fact]
    public void RenamedJobReplacesItsOldFileAndBadFilesAreReported()
    {
        var dir = NewDir();
        var test = Case("Cũ", "CRM", Check("1", CompareOp.Equals, "1"));
        TestFolder.Export(dir, [test], [test]);
        File.WriteAllText(Path.Combine(dir, "ghi-chu.txt"), "không phải json"); // file khác trong repo giữ nguyên
        test.Name = "Mới";
        test.Group = "Bán hàng";
        var r = TestFolder.Export(dir, [test], [test]);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "CRM", "Cũ.json")), Assert.Single(r.Removed));
        Assert.False(File.Exists(Path.Combine(dir, "CRM", "Cũ.json")));
        Assert.True(File.Exists(Path.Combine(dir, "Bán hàng", "Mới.json")));
        Assert.True(File.Exists(Path.Combine(dir, "ghi-chu.txt")));

        File.Copy(Path.Combine(dir, "Bán hàng", "Mới.json"), Path.Combine(dir, "Bán hàng", "Mới - Copy.json"));
        var ex = Assert.Throws<InvalidDataException>(() => TestFolder.Load(dir));
        Assert.Contains("Hai file cùng Id", ex.Message);
        File.Delete(Path.Combine(dir, "Bán hàng", "Mới - Copy.json"));
        File.WriteAllText(Path.Combine(dir, "hong.json"), "{ không phải json");
        ex = Assert.Throws<InvalidDataException>(() => TestFolder.Load(dir));
        Assert.Contains("hong.json", ex.Message);
    }

    [Fact]
    public void MergeUpdatesSameIdKeepsLastRunAndAddsNew()
    {
        var a = Case("A", "G");
        a.LastRun = new DateTime(2026, 1, 2);
        var current = new List<Job> { a };
        var incomingA = a.Clone();
        incomingA.Name = "A (sửa trong git)";
        incomingA.LastRun = null;
        var b = Case("B", "G");
        var r = TestFolder.Merge(current, [incomingA, b]);
        Assert.Equal((1, 1), (r.Updated, r.Added));
        Assert.Equal("A (sửa trong git)", current[0].Name);
        Assert.Equal(new DateTime(2026, 1, 2), current[0].LastRun);
        Assert.Same(b, current[1]);
    }

    // ───────────────────────────── Tag, shard, độ ổn định ─────────────────────────────

    [Fact]
    public void TagsShardsAndStability()
    {
        var jobs = Enumerable.Range(1, 7).Select(i => Case($"K{i}", i % 2 == 0 ? "B" : "A")).ToList();
        jobs[0].Tags = "smoke";
        jobs[1].Tags = "Smoke; regression";
        jobs[2].Tags = "regression";
        Assert.Equal(["K1", "K2"], TestSuite.WithTags(jobs, "SMOKE").Select(j => j.Name));
        Assert.Equal(3, TestSuite.WithTags(jobs, "smoke, regression").Count);
        Assert.Equal(7, TestSuite.WithTags(jobs, " ").Count);

        var parts = Enumerable.Range(1, 3).Select(i => TestSuite.Shard(jobs, $"{i}/3")).ToList();
        Assert.Equal(7, parts.Sum(p => p.Count));
        Assert.Equal(7, parts.SelectMany(p => p).Select(j => j.Id).Distinct().Count()); // không trùng, không sót
        Assert.Equal(parts[1].Select(j => j.Id), TestSuite.Shard(Enumerable.Reverse(jobs), "2/3").Select(j => j.Id)); // không phụ thuộc thứ tự đầu vào
        Assert.Throws<FormatException>(() => TestSuite.Shard(jobs, "4/3"));
        Assert.Throws<FormatException>(() => TestSuite.ParseShard("1-3"));

        var id = Guid.NewGuid();
        var now = DateTime.Now;
        var history = Enumerable.Range(0, 12).Select(i => new RunRecord { JobId = id, Start = now.AddMinutes(i), Ok = i != 11 && i % 3 != 0 })
            .Append(new RunRecord { JobId = Guid.NewGuid(), Start = now, Ok = true });
        Assert.Equal((6, 10), TestSuite.Stability(history, id)); // 10 lần gần nhất: i = 2..11
        Assert.Equal((0, 0), TestSuite.Stability(history, Guid.NewGuid()));
    }

    // ───────────────────────────── Môi trường, chạy lại, theo dữ liệu ─────────────────────────────

    [Fact]
    public async Task EnvironmentVariablesOverrideJobDefaults()
    {
        var job = Case("Môi trường", "G", Check("{{d365Url}}", CompareOp.Equals, "https://uat.crm5.dynamics.com"), Check("{{env.name}}", CompareOp.Equals, "UAT"));
        job.Variables = [new VariableDef { Name = "d365Url", Value = "https://dev.crm5.dynamics.com" }];
        var env = new TestEnvironment { Name = "UAT", Variables = [new() { Name = "d365Url", Value = "https://uat.crm5.dynamics.com" }] };
        var runner = new FlowRunner(new FakeUi(), _ => null);
        var suite = await TestSuite.RunAsync(runner, [job], "env", NewDir(), new SuiteOptions { Environment = "UAT", Variables = TestEnvironments.Variables(env) });
        Assert.True(suite.Ok, suite.Cases[0].Message);
        Assert.Contains("môi trường <b>UAT</b>", File.ReadAllText(suite.ReportPath));
        var junit = XDocument.Load(Path.Combine(Path.GetDirectoryName(suite.ReportPath)!, "junit.xml"));
        Assert.Equal("UAT", junit.Descendants("property").Single(p => p.Attribute("name")!.Value == "environment").Attribute("value")!.Value);

        // Chạy riêng lẻ (theo lịch / nút Chạy) dùng môi trường đang chọn trong Cài đặt
        var saved = (SettingsStore.Current.Environments.ToList(), SettingsStore.Current.CurrentEnvironment);
        try
        {
            SettingsStore.Current.Environments = [env];
            SettingsStore.Current.CurrentEnvironment = "uat";
            var r = await runner.EnqueueAsync(job, "theo lịch");
            Assert.True(r!.Ok, r.Message);
            SettingsStore.Current.CurrentEnvironment = "";
            r = await runner.EnqueueAsync(job, "theo lịch");
            Assert.False(r!.Ok); // không môi trường → dùng giá trị mặc định của kịch bản (dev)
        }
        finally
        {
            (SettingsStore.Current.Environments, SettingsStore.Current.CurrentEnvironment) = saved;
        }
        Assert.Throws<InvalidOperationException>(() => TestEnvironments.Find([env], "Prod"));
        Assert.Null(TestEnvironments.Find([env], ""));
    }

    [Fact]
    public async Task FailedCaseIsRetriedAndMarkedFlaky()
    {
        var dir = NewDir();
        var marker = Path.Combine(dir, "da-chay.csv");
        // Lần đầu: chưa có file → kiểm tra không đạt; bước sau tạo file → lần chạy lại thì đạt (như lỗi chập chờn).
        var job = Case("Chập chờn", "G",
            S(StepType.Assert, s => { s.Condition = ConditionKind.FileExists; s.Target = marker; s.DelayMs = 0; s.Message = "Đã có file"; }),
            S(StepType.WriteData, s => { s.Target = marker; s.Text = "Lan=1"; }));
        var stable = Case("Luôn lỗi", "G", Check("1", CompareOp.Equals, "2"));
        var runner = new FlowRunner(new FakeUi(), _ => null);

        var suite = await TestSuite.RunAsync(runner, [job, stable], "retry", dir, new SuiteOptions { Retries = 2 });

        var flaky = suite.Cases[0];
        Assert.True(flaky.Ok);
        Assert.True(flaky.Flaky);
        Assert.Equal(2, flaky.Attempts);
        Assert.Contains("Đã có file", flaky.FirstFailure);
        var failed = suite.Cases[1];
        Assert.False(failed.Ok);
        Assert.Equal(3, failed.Attempts); // 1 lần + 2 lần chạy lại
        Assert.False(suite.Ok);
        Assert.StartsWith("1/2 kịch bản đạt (1 đạt sau khi chạy lại)", TestReport.Summary(suite.Cases));
        var html = File.ReadAllText(suite.ReportPath);
        Assert.Contains("↻ chạy lại 1 lần", html);
        var junit = XDocument.Load(Path.Combine(Path.GetDirectoryName(suite.ReportPath)!, "junit.xml"));
        var tc = junit.Descendants("testcase").First();
        Assert.Null(tc.Element("failure"));
        Assert.Equal("2", tc.Descendants("property").Single(p => p.Attribute("name")!.Value == "attempts").Attribute("value")!.Value);
        Assert.StartsWith("Đạt sau khi chạy lại (lần 2)", tc.Element("system-out")!.Value);
    }

    [Fact]
    public async Task DataDrivenCaseRunsOncePerRowAsSeparateTestCases()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "khach.csv"), "Ten,Tuoi\nLan,30\n\nMinh,-1\nHoa,25\n", new UTF8Encoding(false));
        var job = Case("Tạo khách", "CRM",
            Check("{{row.Ten}}", CompareOp.IsNotEmpty, "", "Có tên"),
            Check("{{row.Tuoi}}", CompareOp.Greater, "0", "Tuổi dương"),
            Check("{{data.count}}", CompareOp.Equals, "3"));
        job.DataFile = "khach.csv"; // tương đối → tính từ BaseDir (thư mục kịch bản)
        job.TestCaseId = "1234";
        job.Tags = "data";
        var runner = new FlowRunner(new FakeUi(), _ => null);

        var suite = await TestSuite.RunAsync(runner, [job], "data", dir, new SuiteOptions { BaseDir = dir });

        Assert.Equal(["Tạo khách [dòng 2: Lan]", "Tạo khách [dòng 4: Minh]", "Tạo khách [dòng 5: Hoa]"], suite.Cases.Select(c => c.Name));
        Assert.Equal([true, false, true], suite.Cases.Select(c => c.Ok));
        Assert.Contains("\"-1\"", suite.Cases[1].Steps.Single(s => !s.Ok).Detail);
        var junit = XDocument.Load(Path.Combine(Path.GetDirectoryName(suite.ReportPath)!, "junit.xml"));
        Assert.Equal("3", junit.Root!.Attribute("tests")!.Value);
        Assert.Equal("1", junit.Root.Attribute("failures")!.Value);
        var props = junit.Descendants("testcase").First().Descendants("property").ToDictionary(p => p.Attribute("name")!.Value, p => p.Attribute("value")!.Value);
        Assert.Equal(("1234", "data", "dòng 2: Lan"), (props["testcaseid"], props["tags"], props["data"]));
        Assert.Contains("#1234", File.ReadAllText(suite.ReportPath));

        // Chạy riêng lẻ (theo lịch / nút Chạy): cũng chạy từng dòng, kết quả chung
        job.DataFile = Path.Combine(dir, "khach.csv");
        var r = await runner.EnqueueAsync(job, "theo lịch");
        Assert.False(r!.Ok);
        Assert.StartsWith("2/3 kịch bản đạt", r.Message);

        // File dữ liệu hỏng / không có → một test case lỗi rõ ràng, không làm hỏng cả bộ
        job.DataFile = Path.Combine(dir, "khong-co.csv");
        suite = await TestSuite.RunAsync(runner, [job], "data", dir);
        Assert.Contains("Không đọc được file dữ liệu", Assert.Single(suite.Cases).Message);
    }

    // ───────────────────────────── Dòng lệnh ─────────────────────────────

    [Fact]
    public void CommandLineRunsSuitesFromATestFolder()
    {
        var folder = NewDir();
        var reports = NewDir();
        var ok1 = Case("Đạt 1", "CRM", Check("{{d365Url}}", CompareOp.StartsWith, "https://uat"));
        ok1.Tags = "smoke";
        ok1.Variables = [new VariableDef { Name = "d365Url", Value = "https://dev" }];
        var ok2 = Case("Đạt 2", "CRM", Check("1", CompareOp.Equals, "1"));
        var bad = Case("Lỗi", "Khác", Check("1", CompareOp.Equals, "2"));
        bad.Tags = "regression";
        TestFolder.Export(folder, [ok1, ok2, bad], [ok1, ok2, bad],
            [new TestEnvironment { Name = "UAT", Variables = [new() { Name = "d365Url", Value = "https://uat.example" }] }]);

        int Run(out List<string> output, params string[] args)
        {
            var lines = new List<string>();
            int code = TestCli.Run(args, l => { lock (lines) lines.Add(l); });
            output = lines;
            return code;
        }

        Assert.Equal(0, Run(out var o, "--test", "--tag", "smoke", "--env", "uat", "--test-dir", folder, "--report", reports));
        Assert.Contains(o, l => l.StartsWith("ĐẠT: 1/1 kịch bản đạt", StringComparison.Ordinal));
        Assert.Equal(1, Run(out o, "--test", "*", "--test-dir", folder, "--report", reports, "--env", "UAT"));
        Assert.Contains(o, l => l.Contains("✖ KHÔNG ĐẠT") && l.Contains("Lỗi"));
        Assert.Equal(0, Run(out o, "--test", "CRM", "--test-dir", folder, "--report", reports, "--env", "UAT"));
        Assert.Equal(1, Run(out o, "--test", "CRM", "--test-dir", folder, "--report", reports)); // không môi trường → d365Url dev → không đạt

        Assert.Equal(0, Run(out o, "--test", "*", "--test-dir", folder, "--list", "--shard", "1/2"));
        var listed = o.Where(l => l.Contains(" › ")).ToList();
        Assert.Equal(0, Run(out var o2, "--test", "*", "--test-dir", folder, "--list", "--shard", "2/2"));
        Assert.Equal(3, listed.Count + o2.Count(l => l.Contains(" › ")));

        Assert.Equal(2, Run(out o, "--test", "Không có", "--test-dir", folder));
        Assert.Equal(2, Run(out o, "--test", "*", "--test-dir", folder, "--env", "Prod"));
        Assert.Contains(o, l => l.Contains("Không có môi trường \"Prod\""));
        Assert.Equal(2, Run(out o, "--test", "*", "--retry", "abc", "--test-dir", folder));
        Assert.Equal(2, Run(out o, "--test", "*", "--test-dir", Path.Combine(folder, "khong-co")));
        Assert.Equal(2, Run(out o, "--test", "*", "--test-dir"));
        Assert.Contains(o, l => l.StartsWith("Cách dùng", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandLineOptionsParse()
    {
        var o = TestCliOptions.Parse(["--test", "\"CRM\"", "--tag", "smoke", "--retry", "2", "--shard", "2/4", "--headless", "--env", "UAT"]);
        Assert.Equal(("CRM", "smoke", 2, "2/4", true, "UAT", false), (o.Query, o.Tags, o.Retries, o.Shard, o.Headless, o.Environment, o.ListOnly));
        Assert.Equal("", TestCliOptions.Parse(["--test", "--tag", "x"]).Query);
        Assert.Throws<FormatException>(() => TestCliOptions.Parse(["--test", "*", "--retry", "9"]));
        Assert.Throws<FormatException>(() => TestCliOptions.Parse(["--test", "*", "--shard", "0/2"]));
        Assert.Throws<FormatException>(() => TestCliOptions.Parse(["--test", "*", "--env"]));
        Assert.Equal("Tất cả kịch bản [tag smoke] (phần 1/2)", TestCliOptions.Parse(["--test", "*", "--tag", "smoke", "--shard", "1/2"]).SuiteName([]));
    }
}
