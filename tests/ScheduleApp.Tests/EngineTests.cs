using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Data;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

public class EngineTests
{
    [Fact]
    public async Task LoopCountWithCounter()
    {
        var job = new Job
        {
            Name = "dem",
            Variables = [new VariableDef { Name = "dem", Value = "0" }],
            Steps = [S(StepType.Loop, s => { s.LoopKind = LoopKind.Count; s.Count = 5; }), SetVar("dem", VarSource.Calc, "{{dem}} + 1"), S(StepType.EndLoop)]
        };
        var (r, c) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("5", c.Vars["dem"]);
    }

    [Fact]
    public async Task IfElse()
    {
        var dir = NewDir();
        var job = new Job
        {
            Name = "if",
            Steps =
            [
                SetVar("x", VarSource.Value, "15"),
                S(StepType.If, s => { s.Condition = ConditionKind.Compare; s.Target = "{{x}}"; s.CompareOp = CompareOp.Greater; s.Arguments = "9"; }),
                SetVar("kq", VarSource.Value, "lớn"),
                S(StepType.Else),
                SetVar("kq", VarSource.Value, "nhỏ"),
                S(StepType.EndIf),
                S(StepType.If, s => { s.Condition = ConditionKind.Compare; s.Target = "abc"; s.CompareOp = CompareOp.Contains; s.Arguments = "Z"; }),
                SetVar("kq2", VarSource.Value, "sai"),
                S(StepType.EndIf),
                S(StepType.If, s => { s.Condition = ConditionKind.FileExists; s.Target = dir; }),
                SetVar("dirOk", VarSource.Value, "có"),
                S(StepType.EndIf)
            ]
        };
        var (r, c) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("lớn", c.Vars["kq"]);
        Assert.False(c.Vars.ContainsKey("kq2"));
        Assert.Equal("có", c.Vars["dirOk"]);
    }

    [Fact]
    public async Task RowsLinesAndBreak()
    {
        var xlsx = Path.Combine(NewDir(), "data.xlsx");
        MakeXlsx(xlsx);
        var job = new Job
        {
            Name = "rows",
            Variables = [new VariableDef { Name = "ds", Value = "" }],
            Steps =
            [
                S(StepType.Loop, s => { s.LoopKind = LoopKind.Rows; s.Target = xlsx; }),
                SetVar("ds", VarSource.Value, "{{ds}}[{{row.Mã}}:{{row.Họ tên}}:{{loop.index}}/{{loop.count}}@{{row.rowNumber}}]"),
                S(StepType.EndLoop),
                S(StepType.Loop, s => { s.LoopKind = LoopKind.Lines; s.Target = "a\nb\nc\nd"; s.Variable = "dong"; }),
                S(StepType.If, s => { s.Target = "{{dong}}"; s.CompareOp = CompareOp.Equals; s.Arguments = "c"; }),
                S(StepType.BreakLoop),
                S(StepType.EndIf),
                SetVar("last", VarSource.Value, "{{dong}}"),
                S(StepType.EndLoop)
            ]
        };
        var (r, c) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("[1:Phạm Văn D:1/2@2][2::2/2@4]", c.Vars["ds"]);
        Assert.Equal("b", c.Vars["last"]);
    }

    [Fact]
    public async Task ContinueLoopSkipsRestOfIteration()
    {
        var job = new Job
        {
            Name = "continue",
            Variables = [new VariableDef { Name = "kq", Value = "" }],
            Steps =
            [
                S(StepType.Loop, s => { s.LoopKind = LoopKind.Lines; s.Target = "1\n2\n3\n4"; s.Variable = "so"; }),
                S(StepType.If, s => { s.Target = "{{so}}"; s.CompareOp = CompareOp.Equals; s.Arguments = "2"; }),
                S(StepType.ContinueLoop),
                S(StepType.EndIf),
                SetVar("kq", VarSource.Value, "{{kq}}{{so}}"),
                S(StepType.EndLoop),
                SetVar("sau", VarSource.Value, "ok")
            ]
        };
        var (r, c) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("134", c.Vars["kq"]);
        Assert.Equal("ok", c.Vars["sau"]);
    }

    [Fact]
    public async Task CommandRegexRetryAndErrorHandling()
    {
        var job = new Job
        {
            Name = "cmd",
            Steps =
            [
                S(StepType.RunCommand, s => { s.Target = "echo Ma don: 12345 & echo xong"; s.Variable = "out"; }),
                SetVar("ma", VarSource.Command, "", "echo Ma don: 98765", @"Ma don:\s*(\d+)"),
                S(StepType.RunCommand, s => { s.Target = "exit 3"; s.Retries = 2; s.RetryDelayMs = 10; s.OnError = ErrorAction.Continue; }),
                S(StepType.If, s => s.Condition = ConditionKind.LastStepFailed),
                SetVar("loiTruoc", VarSource.Value, "{{lastError}}"),
                S(StepType.EndIf),
                S(StepType.RunCommand, s => { s.Target = "exit 1"; s.OnError = ErrorAction.GotoLabel; s.ErrorLabel = "XuLy"; }),
                SetVar("khongChay", VarSource.Value, "x"),
                S(StepType.Label, s => s.Target = "XuLy"),
                SetVar("daXuLy", VarSource.Value, "ok")
            ]
        };
        var (r, c) = await RunAsync(job);
        Assert.False(r.Ok);
        Assert.Contains("2 bước lỗi", r.Message);
        Assert.Contains("Ma don: 12345", c.Vars["out"]);
        Assert.Contains("xong", c.Vars["out"]);
        Assert.Equal("98765", c.Vars["ma"]);
        Assert.Contains("mã 3", c.Vars["loiTruoc"]);
        Assert.False(c.Vars.ContainsKey("khongChay"));
        Assert.Equal("ok", c.Vars["daXuLy"]);
    }

    [Fact]
    public async Task StopsOnErrorByDefault()
    {
        var job = new Job { Name = "stop", Steps = [SetVar("a", VarSource.Value, "1"), S(StepType.RunCommand, s => s.Target = "exit 2"), SetVar("b", VarSource.Value, "2")] };
        var (r, c) = await RunAsync(job);
        Assert.False(r.Ok);
        Assert.Equal(2, r.FailedStep);
        Assert.False(c.Vars.ContainsKey("b"));
    }

    [Fact]
    public async Task CallJobDisabledBlockAskUserStopFlow()
    {
        var sub = new Job { Name = "con", Steps = [SetVar("tuCon", VarSource.Value, "Xin chào {{ten}}")] };
        var job = new Job
        {
            Name = "cha",
            Variables = [new VariableDef { Name = "ten", Value = "An" }],
            Steps =
            [
                S(StepType.CallJob, s => { s.JobRef = sub.Id; s.Target = sub.Name; }),
                S(StepType.If, s => { s.Enabled = false; s.Target = "1"; s.Arguments = "1"; }),
                SetVar("trongKhoiTat", VarSource.Value, "x"),
                S(StepType.EndIf),
                SetVar("hoi", VarSource.AskUser, "Tên?", "", "mặc định"),
                S(StepType.StopFlow, s => s.Text = "đủ rồi"),
                SetVar("sauDung", VarSource.Value, "x")
            ]
        };
        var (r, c) = await RunAsync(job, sub);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Xin chào An", c.Vars["tuCon"]);
        Assert.False(c.Vars.ContainsKey("trongKhoiTat"));
        Assert.Equal("nhập: mặc định", c.Vars["hoi"]);
        Assert.False(c.Vars.ContainsKey("sauDung"));
        Assert.Equal("đủ rồi", r.Message);
    }

    [Fact]
    public async Task WhileAndGoto()
    {
        var job = new Job
        {
            Name = "while",
            Variables = [new VariableDef { Name = "i", Value = "0" }],
            Steps =
            [
                S(StepType.Loop, s => { s.LoopKind = LoopKind.While; s.Condition = ConditionKind.Compare; s.Target = "{{i}}"; s.CompareOp = CompareOp.Less; s.Arguments = "7"; }),
                SetVar("i", VarSource.Calc, "{{i}} + 2"),
                S(StepType.EndLoop),
                S(StepType.Goto, s => s.Target = "Cuoi"),
                SetVar("boQua", VarSource.Value, "x"),
                S(StepType.Label, s => s.Target = "Cuoi")
            ]
        };
        var (r, c) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("8", c.Vars["i"]);
        Assert.False(c.Vars.ContainsKey("boQua"));
    }

    [Fact]
    public async Task FilesLoop()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "b.csv"), "x");
        File.WriteAllText(Path.Combine(dir, "a.xlsx"), "x");
        File.WriteAllText(Path.Combine(dir, "c.txt"), "x");
        var job = new Job
        {
            Name = "files",
            Variables = [new VariableDef { Name = "ten", Value = "" }],
            Steps =
            [
                S(StepType.Loop, s => { s.LoopKind = LoopKind.Files; s.Target = dir; s.Arguments = "*.csv;*.xlsx"; s.Variable = "f"; }),
                SetVar("ten", VarSource.Value, "{{ten}}{{f.name}},"),
                S(StepType.EndLoop)
            ]
        };
        var (_, c) = await RunAsync(job);
        Assert.Equal("a.xlsx,b.csv,", c.Vars["ten"]);
    }

    [Fact]
    public async Task SecretsAreMaskedInLog()
    {
        SecretStore.Set("MatKhau", "S3cret!x");
        string? logged = null;
        void OnLog(string l) { if (l.Contains("BIMAT")) logged = l; }
        Log.Written += OnLog;
        try
        {
            var job = new Job { Name = "secret", Steps = [S(StepType.LogMessage, s => s.Text = "BIMAT={{secret:MatKhau}}")] };
            var (r, _) = await RunAsync(job);
            Assert.True(r.Ok, r.Message);
        }
        finally
        {
            Log.Written -= OnLog;
        }
        Assert.NotNull(logged);
        Assert.DoesNotContain("S3cret!x", logged);
        Assert.Contains("***", logged);
    }

    [Fact]
    public async Task ListAndJsonVariables()
    {
        var job = new Job
        {
            Name = "list",
            Steps =
            [
                SetVar("ds", VarSource.ListAdd, "a@x.com"),
                SetVar("ds", VarSource.ListAdd, "b@y.com"),
                SetVar("tach", VarSource.Split, "1; 2 ;;3", "", ";"),
                SetVar("json", VarSource.Value, "{\"value\":[{\"name\":\"Công ty A\",\"@odata.etag\":\"W/1\"},{\"name\":\"B\"}]}"),
                SetVar("ten", VarSource.JsonPath, "{{json}}", "", "value[0].name"),
                SetVar("tatCa", VarSource.JsonPath, "{{json}}", "", "value[*].name"),
                SetVar("etag", VarSource.JsonPath, "{{json}}", "", "value[0][\"@odata.etag\"]"),
                SetVar("soLuong", VarSource.Value, "{{ds:count}}|{{tach:join(+)}}")
            ]
        };
        var (r, c) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("a@x.com\nb@y.com", c.Vars["ds"]);
        Assert.Equal("1\n2\n3", c.Vars["tach"]);
        Assert.Equal("Công ty A", c.Vars["ten"]);
        Assert.Equal("Công ty A\nB", c.Vars["tatCa"]);
        Assert.Equal("W/1", c.Vars["etag"]);
        Assert.Equal("2|1+2+3", c.Vars["soLuong"]);
    }

    [Fact]
    public async Task WriteResultBackToEachExcelRow()
    {
        var xlsx = Path.Combine(NewDir(), "nhap-lieu.xlsx");
        MakeXlsx(xlsx);
        var log = Path.Combine(NewDir(), "nhat-ky.csv");
        var job = new Job
        {
            Name = "ghi",
            Steps =
            [
                S(StepType.Loop, s => { s.LoopKind = LoopKind.Rows; s.Target = xlsx; }),
                S(StepType.WriteData, s =>
                {
                    s.Target = xlsx; s.DataAction = DataAction.UpdateRow; s.RowRef = "{{row.rowNumber}}";
                    s.Text = "TrangThai=Đã nhập {{row.Mã}}\nGhiChu={{row.Họ tên}}\ndòng 2";
                }),
                S(StepType.WriteData, s => { s.Target = log; s.Text = "Ma={{row.Mã}}\nLuc={{now:yyyy}}"; }),
                S(StepType.EndLoop)
            ]
        };
        // "dòng 2" không có dấu = → bước lỗi rõ ràng thay vì ghi sai.
        var (bad, _) = await RunAsync(job);
        Assert.False(bad.Ok);
        Assert.Contains("thiếu dấu =", bad.Message);

        job.Steps[1].Text = "TrangThai=Đã nhập {{row.Mã}}\nGhiChu={{row.Họ tên}}";
        var (r, c) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        var x = TabularReader.Read(xlsx);
        Assert.Equal("Đã nhập 1", x.Rows[0][x.Headers.IndexOf("TrangThai")]);
        Assert.Equal("Đã nhập 2", x.Rows[1][x.Headers.IndexOf("TrangThai")]);
        Assert.Equal("Phạm Văn D", x.Rows[0][x.Headers.IndexOf("GhiChu")]);
        var l = TabularReader.Read(log);
        Assert.Equal(["1", "2"], l.Rows.Select(row => row[0]).ToList());
        Assert.Equal("3", c.Vars["lastRow"]);
    }
}
