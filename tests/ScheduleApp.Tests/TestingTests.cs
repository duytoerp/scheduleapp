using System.Xml.Linq;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using ScheduleApp.Services.Testing;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

public class TestingTests
{
    private static ActionStep AssertStep(string left, CompareOp op, string right, Action<ActionStep>? cfg = null) =>
        S(StepType.Assert, s =>
        {
            s.Condition = ConditionKind.Compare;
            s.Target = left;
            s.CompareOp = op;
            s.Arguments = right;
            s.DelayMs = 0;
            cfg?.Invoke(s);
        });

    private static async Task<(FlowResult R, FlowContext C, TestRecorder Rec)> RunRecordedAsync(Job job)
    {
        var rec = new TestRecorder(null);
        var ctx = new FlowContext(job, new FakeUi(), new RunOptions { Recorder = rec }, _ => null, CancellationToken.None);
        var r = await FlowEngine.RunAsync(job, ctx, 0, true);
        return (r, ctx, rec);
    }

    [Fact]
    public async Task SoftAssertRecordsFailureAndContinues()
    {
        var job = new Job
        {
            Name = "soft",
            StopOnError = true, // Assert mặc định vẫn chạy tiếp dù công việc dừng khi lỗi
            Steps =
            [
                SetVar("tong", VarSource.Value, "150"),
                AssertStep("{{tong}}", CompareOp.Equals, "150", s => s.Message = "Tổng đúng"),
                AssertStep("{{tong}}", CompareOp.Less, "100"),
                SetVar("sau", VarSource.Value, "đã chạy")
            ]
        };
        var (r, c, rec) = await RunRecordedAsync(job);

        Assert.False(r.Ok);
        Assert.Equal("đã chạy", c.Vars["sau"]);
        var asserts = rec.Steps.Where(s => s.IsAssert).ToList();
        Assert.Equal(2, asserts.Count);
        Assert.True(asserts[0].Ok);
        Assert.Contains("Tổng đúng", asserts[0].Description);
        Assert.False(asserts[1].Ok);
        Assert.Contains("\"150\"", asserts[1].Detail); // giá trị thực tế
        Assert.Equal(4, rec.Steps.Count);
    }

    [Fact]
    public async Task HardAssertStopsFlow()
    {
        var job = new Job
        {
            Name = "hard",
            StopOnError = false,
            Steps =
            [
                AssertStep("a", CompareOp.Equals, "b", s => s.OnError = ErrorAction.Stop),
                SetVar("sau", VarSource.Value, "x")
            ]
        };
        var (r, c, rec) = await RunRecordedAsync(job);
        Assert.False(r.Ok);
        Assert.Equal(1, r.FailedStep);
        Assert.False(c.Vars.ContainsKey("sau"));
        Assert.Single(rec.Steps);
    }

    [Fact]
    public async Task AssertNegateAndWaitUntilTrue()
    {
        var file = Path.Combine(NewDir(), "xong.txt");
        var job = new Job
        {
            Name = "wait",
            Steps =
            [
                AssertStep("abc", CompareOp.Contains, "z", s => s.Negate = true),
                S(StepType.Assert, s => { s.Condition = ConditionKind.FileExists; s.Target = file; s.DelayMs = 3000; })
            ]
        };
        _ = Task.Run(async () => { await Task.Delay(500); await File.WriteAllTextAsync(file, "ok"); });
        var (r, _, rec) = await RunRecordedAsync(job);
        Assert.True(r.Ok, r.Message);
        Assert.All(rec.Steps, s => Assert.True(s.Ok));
    }

    [Fact]
    public async Task SubJobStepsAreRecordedWithDepth()
    {
        var sub = new Job { Name = "con", Steps = [AssertStep("1", CompareOp.Equals, "1")] };
        var job = new Job { Name = "cha", Steps = [S(StepType.CallJob, s => { s.JobRef = sub.Id; s.Target = sub.Name; })] };
        var rec = new TestRecorder(null);
        var ctx = new FlowContext(job, new FakeUi(), new RunOptions { Recorder = rec }, id => id == sub.Id ? sub : null, CancellationToken.None);
        var r = await FlowEngine.RunAsync(job, ctx, 0, true);
        Assert.True(r.Ok, r.Message);
        var steps = rec.Steps;
        Assert.Equal(2, steps.Count);
        Assert.Equal("cha", steps[0].JobName); // bước gọi bắt đầu trước
        Assert.Equal(0, steps[0].Depth);
        Assert.Equal("con", steps[1].JobName);
        Assert.Equal(1, steps[1].Depth);
        Assert.Same(job, ctx.CurrentJob);
    }

    [Fact]
    public void ReportWritesHtmlAndJUnit()
    {
        var dir = NewDir();
        var shot = Path.Combine(dir, "shots", "001_buoc2.png");
        Directory.CreateDirectory(Path.GetDirectoryName(shot)!);
        File.WriteAllBytes(shot, [1, 2, 3]);
        var now = DateTime.Now;
        var cases = new List<TestCaseResult>
        {
            new()
            {
                Name = "Tạo khách hàng <A&B>", Group = "CRM", Start = now, End = now.AddSeconds(12), Ok = false, Message = "Xong, 1 bước lỗi",
                Steps =
                [
                    new StepRecord { Number = 1, JobName = "x", Description = "D365: mở form account", Ok = true, Start = now, Seconds = 2 },
                    new StepRecord { Number = 2, JobName = "x", Description = "Kiểm tra: field name bằng \"A\"", IsAssert = true, Ok = false,
                                     Detail = "giá trị field: \"B\"", Start = now.AddSeconds(2), Seconds = 1, Screenshot = shot }
                ]
            },
            new() { Name = "Đăng nhập", Group = "CRM", Start = now, End = now.AddSeconds(3), Ok = true, Message = "Thành công" }
        };
        var html = TestReport.Write(dir, "Bộ CRM", cases);
        var text = File.ReadAllText(html);
        Assert.Contains("Tạo khách hàng &lt;A&amp;B&gt;", text);
        Assert.Contains("src=\"shots/001_buoc2.png\"", text);
        Assert.Contains("KHÔNG ĐẠT", text);

        var xml = XDocument.Load(Path.Combine(dir, "junit.xml"));
        var root = xml.Root!;
        Assert.Equal("testsuites", root.Name.LocalName);
        Assert.Equal("2", root.Attribute("tests")!.Value);
        Assert.Equal("1", root.Attribute("failures")!.Value);
        var failed = root.Descendants("testcase").Single(t => t.Element("failure") != null);
        Assert.Equal("CRM", failed.Attribute("classname")!.Value);
        Assert.Equal("AssertionFailed", failed.Element("failure")!.Attribute("type")!.Value);
        Assert.Contains("giá trị field", failed.Element("failure")!.Value);
        Assert.Equal("1/2 kịch bản đạt · 0/1 kiểm tra đạt · 15.0 giây", TestReport.Summary(cases));
    }

    [Fact]
    public void SuiteSelection()
    {
        Job J(string name, string group, bool test) => new() { Name = name, Group = group, IsTestCase = test };
        var jobs = new[] { J("Đăng nhập CRM", "CRM", false), J("Tạo account", "CRM", true), J("Tạo contact", "CRM", true), J("Sao lưu", "Khác", false), J("Báo cáo", "Khác", false) };
        Assert.Equal(["Tạo account", "Tạo contact"], TestSuite.Select(jobs, "crm").Select(j => j.Name));
        Assert.Equal(2, TestSuite.Select(jobs, "*").Count);
        Assert.Equal(2, TestSuite.Select(jobs, "Khác").Count); // nhóm chưa đánh dấu kịch bản → cả nhóm
        Assert.Equal("Sao lưu", Assert.Single(TestSuite.Select(jobs, "sao lưu")).Name);
        Assert.Equal("Báo cáo", Assert.Single(TestSuite.Select(jobs, "\"báo\"")).Name);
        Assert.Empty(TestSuite.Select(jobs, "Tạo")); // nhiều công việc chứa "Tạo" → không đoán
    }

    [Fact]
    public async Task FlowRunnerWritesReportForTestCase()
    {
        var job = new Job { Name = "Kịch bản đơn", IsTestCase = true, Steps = [AssertStep("1", CompareOp.Equals, "1")] };
        var runner = new FlowRunner(new FakeUi(), _ => null);
        var r = await runner.EnqueueAsync(job, "thử", new RunOptions { IsTest = true });
        Assert.True(r!.Ok, r.Message);
        var record = RunHistory.All.Last(x => x.JobId == job.Id);
        Assert.NotNull(record.Report);
        Assert.True(File.Exists(record.Report));
        Assert.Contains("ĐẠT", File.ReadAllText(record.Report!));
    }

    [Fact]
    public void D365Helpers()
    {
        Assert.Equal("accounts(0a1b2c3d-0000-0000-0000-00000000abcd)",
            D365Client.RelativeEntityUrl("https://org.crm5.dynamics.com/api/data/v9.2/accounts(0A1B2C3D-0000-0000-0000-00000000ABCD)"));
        Assert.Null(D365Client.RelativeEntityUrl(""));
        Assert.Equal("0a1b2c3d-0000-0000-0000-00000000abcd", D365Client.NormalizeId(" {0A1B2C3D-0000-0000-0000-00000000ABCD} "));
        var st = new Dictionary<string, string> { ["required"] = "required", ["disabled"] = "false", ["visible"] = "true", ["dirty"] = "false", ["empty"] = "true" };
        Assert.True(D365Client.HasState(st, "Required"));
        Assert.False(D365Client.HasState(st, "disabled"));
        Assert.True(D365Client.HasState(st, "empty"));
        Assert.Throws<InvalidOperationException>(() => D365Client.HasState(st, "abc"));
    }

    [Fact]
    public void RecordedEventsBecomeSteps()
    {
        D365Client.RecordedEvent E(string k, long t, string entity = "", string id = "", bool isNew = false, string field = "", string value = "", string label = "") =>
            new(k, entity, id, isNew, field, value, label, t);
        var steps = D365Client.ToSteps(
        [
            E("open", 0, "account", "", isNew: true),
            E("open", 10, "account", "", isNew: true),        // ghi lại từ đầu → bỏ
            E("set", 100, field: "name", value: "C"),
            E("set", 200, field: "name", value: "Contoso"),   // gộp
            E("save", 300), E("save", 310),                   // OnSave 2 lần → 1 bước
            E("command", 400, label: "+ Mới"),
            E("open", 1500, "contact", "", isNew: true),       // mở ngay sau khi bấm nút → chờ form
            E("tab", 1600, label: "Chi tiết"),
            E("dialog", 1700, label: "OK"),
            E("bpfPrev", 1800),
            E("open", 9000, "account", "a1", isNew: false)
        ]);
        Assert.Equal(
        [
            "D365: mở form account (mới)",
            "D365: name = \"Contoso\"",
            "D365: lưu bản ghi",
            "D365: bấm \"+ Mới\"",
            "D365: chờ form tải xong (tối đa 30 giây)",
            "D365: chuyển tab \"Chi tiết\"",
            "D365: hộp thoại → bấm \"OK\"",
            "D365: BPF về giai đoạn trước",
            "D365: mở form account [a1]"
        ], steps.Select(s => s.Describe()));
        Assert.Equal("contact", steps[4].Text);
        Assert.All(steps, s => Assert.Equal(ActionStep.CreateDefault(StepType.Dynamics).DelayAfterMs, s.DelayAfterMs));
    }

    [Fact]
    public void DescribeNewSteps()
    {
        var set = S(StepType.Dynamics, s => { s.D365Action = D365Action.SetField; s.Text = "name"; s.Arguments = "Contoso"; });
        Assert.Equal("D365: name = \"Contoso\"", set.Describe());
        var open = S(StepType.Dynamics, s => { s.D365Action = D365Action.OpenForm; s.Text = "account"; });
        Assert.Equal("D365: mở form account (mới)", open.Describe());
        var state = S(StepType.Assert, s => { s.Condition = ConditionKind.D365FieldState; s.Text = "telephone1"; s.Arguments = "required"; s.Negate = true; });
        Assert.Equal("Kiểm tra: KHÔNG field telephone1 bắt buộc nhập", state.Describe());
        Assert.False(TestRecorder.ShowsScreen(S(StepType.Assert)));
        Assert.True(TestRecorder.ShowsScreen(S(StepType.ClickElement)));
        Assert.Equal(5_000, ActionStep.CreateDefault(StepType.Assert).DelayMs);
        Assert.True(state.HasCondition);
        // Mọi loại bước / hành động / điều kiện mới đều có tên hiển thị.
        Assert.All(Enum.GetValues<D365Action>(), a => Assert.True(ActionStep.D365ActionNames.ContainsKey(a)));
        Assert.All(Enum.GetValues<ConditionKind>(), c => Assert.True(ActionStep.ConditionNames.ContainsKey(c)));
        Assert.All(Enum.GetValues<StepType>(), t => Assert.True(ActionStep.TypeNames.ContainsKey(t)));
    }
}
