using System.Text.Json.Nodes;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.Tests;

public class FlowGeneratorTests
{
    private static readonly Job Login = new()
    {
        Name = "A1 · Đăng nhập Dynamics 365",
        Steps = [new ActionStep { Type = StepType.Browser, BrowserAction = BrowserAction.Launch, Target = "Edge" }]
    };

    private static FlowGenerator.Context Ctx(IReadOnlyList<ActionStep>? steps = null, int insertAt = 0) => new()
    {
        JobName = "Nhập khách hàng",
        Steps = steps ?? [],
        InsertAt = insertAt,
        Variables = [new VariableDef { Name = "dem", Value = "0" }],
        OtherJobs = [Login],
        Connections = [("Dynamics365", "URL gốc https://org.crm5.dynamics.com/api/data/v9.2/, xác thực EntraId")],
        NotificationsEnabled = true
    };

    private static JsonObject Input(string stepsJson, string extra = "") =>
        JsonNode.Parse($$"""{"name":"Nhập khách hàng từ Excel","summary":"Tóm tắt","steps":{{stepsJson}}{{extra}}}""")!.AsObject();

    [Fact]
    public void ParsesStepsWithDefaultsAndResolvesReferences()
    {
        var gen = new FlowGenerator(Ctx());
        var r = gen.Parse(Input("""
            [
              {"type":"CallJob","target":"đăng nhập dynamics"},
              {"Type":"Browser","BrowserAction":"navigate","Text":"https://org.crm5.dynamics.com/main.aspx?pagetype=entitylist&etn=contact"},
              {"Type":"Loop","LoopKind":"Rows","Target":"%USERPROFILE%\\Documents\\khach-hang.xlsx"},
              {"Type":"If","Target":"{{row.TrangThai}}","CompareOp":"IsNotEmpty"},
              {"Type":"ContinueLoop"},
              {"Type":"EndIf"},
              {"Type":"Browser","BrowserAction":"SetValue","Text":"input[name=fullname]","Arguments":"{{row.Họ tên}}","Retries":"2"},
              {"Type":"Browser","BrowserAction":"Click","Text":"text:Lưu"},
              {"Type":"HttpRequest","Connection":"dynamics365","Method":"get","Target":"contacts?$top=1","Arguments":"value[0].contactid","Variable":"id"},
              {"Type":"WriteData","DataAction":"UpdateRow","Target":"%USERPROFILE%\\Documents\\khach-hang.xlsx","RowRef":"{{row.rowNumber}}","Text":"TrangThai=Đã nhập\nId={{id}}"},
              {"Type":"SetVariable","Variable":"dem","VarSource":"Calc","Text":"{{dem}} + 1"},
              {"Type":"EndLoop"},
              {"Type":"LaunchApp","Target":"notepad.exe"},
              {"Type":"WaitForWindow","Target":"exe:notepad"},
              {"Type":"KeyPress","Text":"Ctrl+S","Target":"exe:notepad","DelayMs":"2000","LaThuong":"bỏ qua"},
              {"Type":"Notify","Text":"Đã nhập {{dem}} khách hàng"}
            ]
            """,
            """
            ,"variables":[{"name":"{{dem}}","value":"5"},{"name":"tep","value":"a.xlsx"}],"notes":["Kiểm tra bộ chọn input[name=fullname]"," "]
            """), FlowGenerator.Mode.Replace);

        Assert.Empty(r.Problems);
        Assert.Equal("Nhập khách hàng từ Excel", r.Name);
        Assert.Equal(16, r.Steps.Count);
        Assert.Equal(Login.Id, r.Steps[0].JobRef);                 // tìm theo tên gần đúng
        Assert.Equal(Login.Name, r.Steps[0].Target);
        Assert.Equal(BrowserAction.Navigate, r.Steps[1].BrowserAction); // enum không phân biệt hoa thường
        Assert.Equal(15_000, r.Steps[1].DelayMs);                   // mặc định của bước Trình duyệt
        Assert.Equal(2, r.Steps[6].Retries);                        // số dạng chuỗi
        Assert.Equal("Dynamics365", r.Steps[8].Connection);
        Assert.Equal("GET", r.Steps[8].Method);
        Assert.Equal(15_000, r.Steps[13].DelayMs);                  // WaitForWindow mặc định
        Assert.Equal(2000, r.Steps[14].DelayMs);
        Assert.Equal(0, r.Steps[10].DelayAfterMs);                  // bước dữ liệu không nghỉ
        Assert.Equal(new[] { "dem", "tep" }, r.Variables.Select(v => v.Name));
        Assert.Single(r.Notes);
    }

    [Fact]
    public void ReportsProblemsTheAiCanFix()
    {
        var gen = new FlowGenerator(Ctx());
        var r = gen.Parse(Input("""
            [
              {"Type":"Bay"},
              {"Type":"If","Target":"1","Arguments":"1"},
              {"Type":"KeyPress","Text":"Ctrl+Phím lạ"},
              {"Type":"ClickElement","Target":"exe:notepad","Text":"Nut=Lưu"},
              {"Type":"ClickImage"},
              {"Type":"HttpRequest","Connection":"CRM","Target":"accounts"},
              {"Type":"CallJob","Target":"Không có"},
              {"Type":"Browser","BrowserAction":"Click","Text":"#luu"},
              {"Type":"WriteData","Target":"a.csv","Text":"chỉ có chữ"}
            ]
            """), FlowGenerator.Mode.Replace);
        Assert.Single(r.Problems);                                    // Type sai → dừng ở bước đọc
        Assert.Contains("\"Bay\"", r.Problems[0]);

        r = gen.Parse(Input("""
            [
              {"Type":"If","Target":"1","Arguments":"1"},
              {"Type":"KeyPress","Text":"Ctrl+Phím lạ"},
              {"Type":"ClickElement","Target":"exe:notepad","Text":"Nut=Lưu"},
              {"Type":"ClickImage"},
              {"Type":"HttpRequest","Connection":"CRM","Target":"accounts"},
              {"Type":"CallJob","Target":"Không có"},
              {"Type":"Browser","BrowserAction":"Click","Text":"#luu"},
              {"Type":"WriteData","Target":"a.csv","Text":"chỉ có chữ"}
            ]
            """), FlowGenerator.Mode.Replace);
        var all = string.Join("\n", r.Problems);
        Assert.Contains("Phím lạ", all);
        Assert.Contains("Nut=Lưu", all);
        Assert.Contains("hình mẫu", all);
        Assert.Contains("\"Dynamics365\"", all);                     // gợi ý kết nối có sẵn
        Assert.Contains(Login.Name, all);                            // gợi ý công việc có sẵn
        Assert.Contains("Launch", all);                              // Browser chưa mở trình duyệt
        Assert.Contains("Tên cột=giá trị", all);
        Assert.True(r.Problems.Count >= 8, all);                     // + khối Nếu chưa đóng
    }

    [Fact]
    public void InsertModeChecksStructureOfWholeFlow()
    {
        List<ActionStep> current = [new() { Type = StepType.Loop, LoopKind = LoopKind.Count, Count = 3 }, new() { Type = StepType.EndLoop }];
        var gen = new FlowGenerator(Ctx(current, insertAt: 1));
        var ok = gen.Parse(Input("""[{"Type":"BreakLoop"}]"""), FlowGenerator.Mode.Insert);
        Assert.Empty(ok.Problems);                                   // BreakLoop nằm trong vòng lặp có sẵn

        gen = new FlowGenerator(Ctx(current, insertAt: 2));
        var bad = gen.Parse(Input("""[{"Type":"BreakLoop"}]"""), FlowGenerator.Mode.Insert);
        Assert.Contains(bad.Problems, p => p.Contains("sau khi chèn"));
    }

    [Fact]
    public void KeepsTemplateImagesThroughRef()
    {
        var image = Convert.ToBase64String(new byte[300]);
        List<ActionStep> current =
        [
            new() { Type = StepType.ClickImage, ImageData = image, ImageWidth = 40, ImageHeight = 20, Confidence = 90 },
            new() { Type = StepType.CallJob, Target = Login.Name, JobRef = Login.Id }
        ];
        var compact = FlowGenerator.Compact(current[0], 0);
        Assert.Equal(0, (int)compact["_ref"]!);
        Assert.DoesNotContain(image, compact.ToJsonString());
        Assert.Equal(90, (int)compact["Confidence"]!);
        Assert.Null(compact["DelayAfterMs"]);                        // bằng mặc định → bỏ

        var gen = new FlowGenerator(Ctx(current));
        var r = gen.Parse(Input($$"""[{{compact.ToJsonString()}}, {"Type":"ClickImage","ImageData":"(ảnh)"}]"""), FlowGenerator.Mode.Replace);
        Assert.Equal(image, r.Steps[0].ImageData);
        Assert.Equal(40, r.Steps[0].ImageWidth);
        Assert.Equal("", r.Steps[1].ImageData);                      // không có _ref → không bịa ảnh
        Assert.Contains(r.Problems, p => p.StartsWith("Bước 2") && p.Contains("hình mẫu"));
    }

    private static string ToolUse(string id, string input) => $$"""
        {"id":"msg_{{id}}","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"Đây là flow."},{"type":"tool_use","id":"{{id}}","name":"build_flow","input":{{input}} }],
         "stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":10} }
        """;

    [Fact]
    public async Task ConversationRepairsAndFollowUps()
    {
        int call = 0;
        using var server = new MiniHttpServer((_, _, _, _) => ++call switch
        {
            1 => (200, ToolUse("toolu_1", """{"name":"Thử","summary":"s","steps":[{"Type":"If","Target":"a","Arguments":"a"},{"Type":"LogMessage","Text":"x"}]}""")),
            2 => (200, ToolUse("toolu_2", """{"name":"Thử","summary":"s","steps":[{"Type":"If","Target":"a","Arguments":"a"},{"Type":"LogMessage","Text":"x"},{"Type":"EndIf"}]}""")),
            3 => (529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""),
            _ => (200, ToolUse("toolu_3", """{"name":"Thử","summary":"s","steps":[{"Type":"LogMessage","Text":"y"}],"notes":["n"]}"""))
        });
        var endpoint = AiClient.Endpoint;
        var key = SettingsStore.Current.Ai.ApiKey;
        AiClient.Endpoint = server.BaseUrl + "v1/messages";
        SettingsStore.Current.Ai.ApiKey = Protector.Protect("sk-thu");
        try
        {
            var gen = new FlowGenerator(Ctx());
            var progress = new List<string>();
            gen.Progress += progress.Add;

            var r = await gen.SendAsync("Ghi nhật ký khi a = a", FlowGenerator.Mode.Replace, CancellationToken.None);
            Assert.Empty(r.Problems);
            Assert.Equal(1, r.Repairs);
            Assert.Equal(3, r.Steps.Count);
            Assert.Equal(2, progress.Count);

            var first = JsonNode.Parse(server.Requests[0].Body)!;
            Assert.Equal("sk-thu", server.Requests[0].Headers["x-api-key"]);
            Assert.Equal("build_flow", (string)first["tool_choice"]!["name"]!);
            Assert.Equal("ephemeral", (string)first["system"]![0]!["cache_control"]!["type"]!);
            var firstText = (string)first["messages"]![0]!["content"]![0]!["text"]!;
            Assert.Contains("Ghi nhật ký khi a = a", firstText);
            Assert.Contains(Login.Name, firstText);
            Assert.Contains("Dynamics365", firstText);
            Assert.Contains("dem = \"0\"", firstText);

            // Lần 2: gửi lỗi cấu trúc cho AI tự sửa.
            var repair = JsonNode.Parse(server.Requests[1].Body)!["messages"]!.AsArray();
            Assert.Equal(3, repair.Count);
            var toolResult = repair[2]!["content"]![0]!;
            Assert.Equal("tool_result", (string)toolResult["type"]!);
            Assert.Equal("toolu_1", (string)toolResult["tool_use_id"]!);
            Assert.True((bool)toolResult["is_error"]!);

            // API lỗi → báo lỗi, hội thoại không bị hỏng; gửi lại được.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => gen.SendAsync("thêm", FlowGenerator.Mode.Replace, CancellationToken.None));
            Assert.Contains("Overloaded", ex.Message);
            r = await gen.SendAsync("Chỉ ghi nhật ký y", FlowGenerator.Mode.Replace, CancellationToken.None);
            Assert.Single(r.Steps);
            Assert.Equal(new[] { "n" }, r.Notes);

            var follow = JsonNode.Parse(server.Requests[3].Body)!["messages"]!.AsArray();
            Assert.Equal(5, follow.Count);                            // user, assistant, user(lỗi), assistant, user(sửa tiếp)
            var last = follow[4]!["content"]!.AsArray();
            Assert.Equal("toolu_2", (string)last[0]!["tool_use_id"]!);
            Assert.Null(last[0]!["is_error"]);
            Assert.Contains("Yêu cầu sửa tiếp: Chỉ ghi nhật ký y", (string)last[1]!["text"]!);
        }
        finally
        {
            AiClient.Endpoint = endpoint;
            SettingsStore.Current.Ai.ApiKey = key;
        }
    }

    /// <summary>
    /// Claude thật: các yêu cầu tiêu biểu phải ra flow qua được bước kiểm tra (sau tối đa 2 lần tự sửa).
    /// Chạy: set SCHEDULEAPP_LIVE_TESTS=1 và SCHEDULEAPP_AI_KEY=sk-ant-… (tốn vài cent mỗi lần).
    /// </summary>
    [LiveFact]
    public async Task RealClaudeBuildsValidFlows()
    {
        var apiKey = Environment.GetEnvironmentVariable("SCHEDULEAPP_AI_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return;
        var saved = (SettingsStore.Current.Ai.ApiKey, SettingsStore.Current.Ai.Model);
        SettingsStore.Current.Ai.ApiKey = Protector.Protect(apiKey);
        SettingsStore.Current.Ai.Model = Environment.GetEnvironmentVariable("SCHEDULEAPP_AI_MODEL") ?? AiClient.Models[0];
        try
        {
            string[] prompts =
            [
                "Đọc file khach-hang.xlsx trong Documents (cột Họ tên, Email, Trạng thái). Với mỗi dòng chưa có Trạng thái: mở Edge vào https://example.com/dang-ky, " +
                "điền Họ tên và Email, bấm Đăng ký, rồi ghi \"Đã nhập\" vào cột Trạng thái của dòng đó. Xong thì gửi thông báo số dòng đã nhập.",
                "Hỏi tôi nội dung công việc hôm nay, mở Notepad, gõ tiêu đề \"Báo cáo ngày\" kèm ngày hôm nay và nội dung vừa nhập, lưu vào Documents với tên bao-cao.txt rồi đóng Notepad.",
                "Đăng nhập Dynamics 365 bằng công việc có sẵn, sau đó gọi API lấy 5 khách hàng (contacts) mới nhất và ghi tên, email ra contacts.csv trong Documents."
            ];
            foreach (var prompt in prompts)
            {
                var gen = new FlowGenerator(Ctx());
                var r = await gen.SendAsync(prompt, FlowGenerator.Mode.Replace, CancellationToken.None);
                Assert.True(r.Problems.Count == 0, prompt + "\n→ " + string.Join("\n", r.Problems));
                Assert.True(r.Steps.Count >= 3);
                Assert.True(FlowStructure.Build(r.Steps).IsValid);
            }
        }
        finally
        {
            (SettingsStore.Current.Ai.ApiKey, SettingsStore.Current.Ai.Model) = saved;
        }
    }

    [Fact]
    public void SystemPromptMentionsEveryUsableStepType()
    {
        foreach (var t in Enum.GetValues<StepType>().Where(t => t is not (StepType.ClickImage or StepType.WaitForImage or StepType.MouseDrag)))
            Assert.Contains(t.ToString(), FlowGenerator.SystemPrompt);
        foreach (var a in Enum.GetValues<BrowserAction>()) Assert.Contains(a.ToString(), FlowGenerator.SystemPrompt);
        foreach (var v in Enum.GetValues<VarSource>()) Assert.Contains(v.ToString(), FlowGenerator.SystemPrompt);
        foreach (var l in Enum.GetValues<LoopKind>()) Assert.Contains(l.ToString(), FlowGenerator.SystemPrompt);
        foreach (var c in Enum.GetValues<ConditionKind>().Where(c => c != ConditionKind.ImageOnScreen)) Assert.Contains(c.ToString(), FlowGenerator.SystemPrompt);
        foreach (var o in Enum.GetValues<CompareOp>()) Assert.Contains(o.ToString(), FlowGenerator.SystemPrompt);
        foreach (var d in Enum.GetValues<D365Action>()) Assert.Contains(d.ToString(), FlowGenerator.SystemPrompt);
        foreach (var k in ActionStep.D365FieldStates.Keys.Concat(ActionStep.D365CommandStates.Keys)) Assert.Contains(k, FlowGenerator.SystemPrompt);
    }

    [Fact]
    public void ParsesDynamicsTestScenario()
    {
        var gen = new FlowGenerator(Ctx());
        var r = gen.Parse(Input("""
            [
              {"Type":"CallJob","Target":"A1 · Đăng nhập Dynamics 365"},
              {"Type":"Dynamics","D365Action":"OpenForm","Text":"account"},
              {"Type":"Assert","Condition":"D365FieldState","Text":"name","Arguments":"required","Message":"Tên là bắt buộc"},
              {"Type":"Dynamics","D365Action":"SetField","Text":"name","Arguments":"Test {{now:HHmmss}}"},
              {"Type":"Dynamics","D365Action":"Save","Variable":"accountId"},
              {"Type":"Assert","Condition":"D365Notification","Negate":true,"Message":"Lưu không báo lỗi"},
              {"Type":"Assert","Condition":"D365RecordCount","Text":"accounts?$filter=accountid eq {{accountId}}","CompareOp":"GreaterOrEqual","Arguments":"1","DelayMs":10000},
              {"Type":"Dynamics","D365Action":"WebApi","Method":"post","Arguments":"contacts","Text":"{\"lastname\":\"Test\"}"},
              {"Type":"Dynamics","D365Action":"Cleanup"}
            ]
            """), FlowGenerator.Mode.Replace);
        Assert.Empty(r.Problems);
        Assert.Equal(D365Action.OpenForm, r.Steps[1].D365Action);
        Assert.Equal(30_000, r.Steps[1].DelayMs);                    // mặc định của bước D365
        Assert.Equal(StepType.Assert, r.Steps[2].Type);
        Assert.Equal(0, r.Steps[2].DelayAfterMs);
        Assert.True(r.Steps[5].Negate);
        Assert.Equal(10_000, r.Steps[6].DelayMs);

        var bad = gen.Parse(Input("""
            [
              {"Type":"Dynamics","D365Action":"OpenForm"},
              {"Type":"Assert","Condition":"D365FieldState","Text":"name","Arguments":"bắt buộc"},
              {"Type":"Assert","Condition":"D365Command","Text":"Mscrm.Form.account.Delete","Arguments":"an"},
              {"Type":"Dynamics","D365Action":"Login","Text":"test@cty.vn","Arguments":"MatKhau123"},
              {"Type":"Dynamics","D365Action":"QuickCreate","Arguments":"contact","Text":"lastname Test"}
            ]
            """), FlowGenerator.Mode.Replace);
        var all = string.Join("\n", bad.Problems);
        Assert.Contains("tên bảng", all);
        Assert.Contains("required", all);
        Assert.Contains("enabled", all);
        Assert.Contains("{{secret:", all);
        Assert.Contains("field=giá trị", all);
        Assert.Contains("Launch", all);                               // bước D365 cần trình duyệt điều khiển
    }
}
