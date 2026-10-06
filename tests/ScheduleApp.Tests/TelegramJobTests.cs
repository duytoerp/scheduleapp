using System.Text.Json.Nodes;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.Tests;

/// <summary>Tạo công việc qua Telegram: AI trả kèm lịch chạy / kích hoạt, bản nháp, lưu — với Claude và Telegram giả lập.</summary>
public class TelegramJobTests
{
    private static FlowGenerator.Result Parse(string extra)
    {
        var gen = new FlowGenerator(new FlowGenerator.Context());
        return gen.Parse(JsonNode.Parse($$"""{"name":"Báo cáo","summary":"s","steps":[{"Type":"LogMessage","Text":"x"}]{{extra}}}""")!.AsObject(),
            FlowGenerator.Mode.Replace);
    }

    [Fact]
    public void ParsesScheduleFromAi()
    {
        Assert.Null(Parse("").Schedule);

        var daily = Parse(""","schedule":{"type":"Daily","time":"08:00","skipHolidays":true}""");
        Assert.Empty(daily.Problems);
        Assert.Equal(ScheduleType.Daily, daily.Schedule!.Type);
        Assert.Equal(new TimeSpan(8, 0, 0), daily.Schedule.StartAt.TimeOfDay);
        Assert.True(daily.SkipHolidays);
        Assert.Equal("Hằng ngày lúc 08:00", daily.Schedule.Describe());

        var weekly = Parse(""","schedule":{"type":"weekly","days":["Mon","fri","T4"],"time":"17h"}""");
        Assert.Empty(weekly.Problems);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday, DayOfWeek.Wednesday], weekly.Schedule!.Days);
        Assert.Equal("T2, T4, T6 lúc 17:00", weekly.Schedule.Describe());

        var interval = Parse(""","schedule":{"type":"Interval","everyMinutes":15,"between":"08:00-17:30","days":["Mon","Tue","Wed","Thu","Fri"]}""");
        Assert.Empty(interval.Problems);
        Assert.True(interval.Schedule!.UseTimeWindow);
        Assert.Equal((15, new TimeSpan(8, 0, 0), new TimeSpan(17, 30, 0), 5),
            (interval.Schedule.IntervalMinutes, interval.Schedule.WindowStart, interval.Schedule.WindowEnd, interval.Schedule.Days.Count));

        var monthly = Parse(""","schedule":{"type":"Monthly","monthly":"LastDay","time":"18:00"}""");
        Assert.Equal("Ngày cuối tháng lúc 18:00", monthly.Schedule!.Describe());
        Assert.Equal("Ngày 5 hằng tháng lúc 08:00", Parse(""","schedule":{"type":"Monthly","dayOfMonth":"5"}""").Schedule!.Describe());

        var tomorrow = DateTime.Today.AddDays(1);
        var once = Parse($$""","schedule":{"type":"Once","date":"{{tomorrow:yyyy-MM-dd}}","time":"15:00"}""");
        Assert.Empty(once.Problems);
        Assert.Equal(tomorrow.AddHours(15), once.Schedule!.StartAt);
        // Không ghi ngày → hôm nay, giờ đã qua thì ngày mai.
        var noDate = Parse(""","schedule":{"type":"Once","time":"00:00"}""").Schedule!;
        Assert.Equal(tomorrow, noDate.StartAt);
        Assert.Equal(ScheduleType.Manual, Parse(""","schedule":{"type":"Manual"}""").Schedule!.Type);

        Assert.Contains(Parse(""","schedule":{"type":"Once","date":"2020-01-01","time":"08:00"}""").Problems, p => p.Contains("đã qua"));
        Assert.Contains(Parse(""","schedule":{"type":"Weekly","time":"08:00"}""").Problems, p => p.Contains("days"));
        Assert.Contains(Parse(""","schedule":{"type":"Interval"}""").Problems, p => p.Contains("everyMinutes"));
        Assert.Contains(Parse(""","schedule":{"type":"Daily","time":"25:00"}""").Problems, p => p.Contains("HH:mm"));
        Assert.Contains(Parse(""","schedule":{"type":"Hourly"}""").Problems, p => p.Contains("Hourly"));
        Assert.Contains(Parse(""","schedule":{"type":"Weekly","days":["Funday"]}""").Problems, p => p.Contains("Funday"));
    }

    [Fact]
    public void ParsesTriggersFromAi()
    {
        var r = Parse(""","triggers":[{"type":"Hotkey","value":"Ctrl+Alt+7"},{"type":"EmailReceived","value":"hóa đơn","value2":"@ncc.vn"},{"type":"FileCreated","value":"D:\\Scan","value2":"*.pdf"}]""");
        Assert.Empty(r.Problems);
        Assert.Equal([TriggerType.Hotkey, TriggerType.EmailReceived, TriggerType.FileCreated], r.Triggers.Select(t => t.Type));
        Assert.Equal(5, r.Triggers[1].Minutes);
        Assert.Contains("hóa đơn", r.Triggers[1].Describe());

        Assert.Contains(Parse(""","triggers":[{"type":"Hotkey","value":"Ctrl+Alt"}]""").Problems, p => p.Contains("phím tắt"));
        Assert.Contains(Parse(""","triggers":[{"type":"FileCreated"}]""").Problems, p => p.Contains("thư mục"));
        Assert.Contains(Parse(""","triggers":[{"type":"Webhook"}]""").Problems, p => p.Contains("Webhook"));
    }

    // ───────────────────────────── Bản nháp qua tin nhắn ─────────────────────────────

    private sealed class FakeHost : IRemoteHost
    {
        public List<(Job Job, bool Run)> Added { get; } = [];
        public string ListJobs() => "1. A";
        public string Run(string nameOrNumber) => "";
        public string Stop() => "";
        public string Status() => "";
        public FlowGenerator.Context NewJobContext() => new() { NotificationsEnabled = true };

        public string AddJob(Job job, bool run)
        {
            lock (Added) Added.Add((job, run));
            return $"✅ Đã lưu \"{job.Name}\"";
        }
    }

    [Fact]
    public async Task DraftButtonsOnlyActOnTheDraftTheyWereSentWith()
    {
        await WithFakeClaude(async _ =>
        {
            var host = new FakeHost();
            var builder = new ChatJobBuilder(host);
            await builder.CreateAsync("8h sáng mở file báo cáo", CancellationToken.None);
            int first = builder.DraftVersion;
            await builder.ReviseAsync("chỉ thứ 2 và thứ 6", CancellationToken.None);
            Assert.Equal(first + 1, builder.DraftVersion);

            // Nút Lưu / Bỏ ở tin xem trước cũ → không đụng tới bản nháp đã sửa.
            Assert.Contains("Bản nháp đã thay đổi", builder.Save(run: true, version: first));
            Assert.Contains("Bản nháp đã thay đổi", builder.Cancel(version: first));
            Assert.True(builder.HasDraft);
            Assert.Empty(host.Added);

            Assert.StartsWith("✅ Đã lưu", builder.Save(run: false, version: builder.DraftVersion));
            var (job, _) = Assert.Single(host.Added);
            Assert.Equal(ScheduleType.Weekly, job.Schedule.Type);              // đúng bản đã sửa
            Assert.Contains("Chưa có bản nháp", builder.Save(run: false, version: builder.DraftVersion));
        });
    }

    private static string ToolUse(string id, string input) => $$"""
        {"id":"msg_{{id}}","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"tool_use","id":"{{id}}","name":"build_flow","input":{{input}} }],
         "stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":10} }
        """;

    private const string Draft1 = """
        {"name":"Báo cáo doanh thu sáng","summary":"Mở file báo cáo, làm mới dữ liệu rồi lưu.",
         "variables":[{"name":"file","value":"%USERPROFILE%\\Documents\\bao-cao.xlsx"}],
         "steps":[{"Type":"LaunchApp","Target":"{{file}}"},{"Type":"WaitForWindow","Target":"exe:EXCEL"},{"Type":"KeyPress","Target":"exe:EXCEL","Text":"Ctrl+Alt+F5"},
                  {"Type":"KeyPress","Target":"exe:EXCEL","Text":"Ctrl+S"}],
         "notes":["Sửa đường dẫn file trong tab Biến."],
         "schedule":{"type":"Daily","time":"08:00"}}
        """;

    private const string Draft2 = """
        {"name":"Báo cáo doanh thu sáng","summary":"Như trên, chỉ thứ 2 và thứ 6.",
         "steps":[{"Type":"LaunchApp","Target":"excel.exe"},{"Type":"Notify","Text":"Đã làm mới báo cáo"}],
         "schedule":{"type":"Weekly","days":["Mon","Fri"],"time":"08:00"}}
        """;

    private static async Task WithFakeClaude(Func<MiniHttpServer, Task> body)
    {
        int call = 0;
        using var server = new MiniHttpServer((_, _, _, _) => Interlocked.Increment(ref call) == 1 ? (200, ToolUse("toolu_1", Draft1)) : (200, ToolUse("toolu_2", Draft2)));
        var (endpoint, key) = (AiClient.Endpoint, SettingsStore.Current.Ai.ApiKey);
        AiClient.Endpoint = server.BaseUrl + "v1/messages";
        SettingsStore.Current.Ai.ApiKey = Protector.Protect("sk-thu");
        try { await body(server); }
        finally { (AiClient.Endpoint, SettingsStore.Current.Ai.ApiKey) = (endpoint, key); }
    }

    [Fact]
    public async Task DraftIsPreviewedRevisedAndSaved()
    {
        await WithFakeClaude(async server =>
        {
            var host = new FakeHost();
            var builder = new ChatJobBuilder(host);
            Assert.Contains("Chưa có bản nháp", builder.Save(run: false));
            Assert.Contains("Cú pháp: /new", await builder.CreateAsync(" ", CancellationToken.None));

            var preview = await builder.CreateAsync("8h sáng mở file báo cáo, làm mới dữ liệu rồi lưu", CancellationToken.None);
            Assert.True(builder.HasDraft);
            Assert.Contains("✨ Bản nháp: Báo cáo doanh thu sáng", preview);
            Assert.Contains("⏰ Hằng ngày lúc 08:00", preview);
            Assert.Contains("🔧 4 bước:", preview);
            Assert.Contains("3. Nhấn Ctrl+Alt+F5", preview);
            Assert.Contains("file = \"%USERPROFILE%\\Documents\\bao-cao.xlsx\"", preview);
            Assert.Contains("• Sửa đường dẫn file trong tab Biến.", preview);
            Assert.Contains("/ok chay", preview);

            // Sửa tiếp trong cùng hội thoại với AI.
            preview = await builder.ReviseAsync("chỉ chạy thứ 2 và thứ 6, báo cho tôi khi xong", CancellationToken.None);
            Assert.Contains("⏰ T2, T6 lúc 08:00", preview);
            var follow = JsonNode.Parse(server.Requests[1].Body)!["messages"]!.AsArray();
            Assert.Equal(3, follow.Count);
            Assert.Contains("Yêu cầu sửa tiếp: chỉ chạy thứ 2 và thứ 6", (string)follow[2]!["content"]![1]!["text"]!);

            // Mặc định công việc từ Telegram chờ duyệt trên máy: "/ok chay" lưu nhưng không chạy ngay.
            var saved = builder.Save(run: true);
            Assert.Contains("✅ Đã lưu \"Báo cáo doanh thu sáng\"", saved);
            Assert.Contains("🔒 Chờ duyệt trên máy tính", saved);
            Assert.Contains("Vì vậy chưa chạy ngay", saved);
            var (job, run) = Assert.Single(host.Added);
            Assert.False(run);
            Assert.True(job.NeedsApproval);
            Assert.StartsWith("Tạo qua Telegram ", job.ApprovalReason);
            Assert.Equal("Telegram", job.Group);
            Assert.Equal(ScheduleType.Weekly, job.Schedule.Type);
            Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], job.Schedule.Days);
            Assert.Equal([StepType.LaunchApp, StepType.Notify], job.Steps.Select(s => s.Type));
            Assert.False(builder.HasDraft);
            Assert.Contains("Không có bản nháp", builder.Cancel());
        });
    }

    [Fact]
    public async Task DraftNeedsClaudeKey()
    {
        var key = SettingsStore.Current.Ai.ApiKey;
        SettingsStore.Current.Ai.ApiKey = "";
        try
        {
            var builder = new ChatJobBuilder(new FakeHost());
            Assert.Contains("Chưa có khóa Claude", await builder.CreateAsync("mở notepad", CancellationToken.None));
            Assert.False(builder.HasDraft);
        }
        finally { SettingsStore.Current.Ai.ApiKey = key; }
    }

    [Fact]
    public void JobWithoutScheduleIsManual()
    {
        var r = Parse("");
        var job = ChatJobBuilder.ToJob(r);
        Assert.Equal(ScheduleType.Manual, job.Schedule.Type);
        Assert.Contains("⏰ Không đặt lịch — chạy bằng /run", ChatJobBuilder.Preview(r));
    }

    // ───────────────────────────── Đầu-cuối qua bot Telegram giả lập ─────────────────────────────

    [Fact]
    public async Task BotCreatesJobFromChat()
    {
        await WithFakeClaude(async _ =>
        {
            const string token = "123:abc";
            var updates = new List<(long Id, long Chat, string Text)>();
            var sent = new List<(long Chat, string Text)>();
            void Queue(long chat, string text) { lock (updates) updates.Add((updates.Count + 1, chat, text)); }

            using var telegram = new MiniHttpServer((method, path, _, body) =>
            {
                if (path.Contains("/sendMessage"))
                {
                    var msg = JsonNode.Parse(body)!;
                    var text = (string)msg["text"]!;
                    lock (sent) sent.Add(((long)msg["chat_id"]!, text));
                    // Thấy bản nháp → người dùng nhắn sửa, rồi /ok.
                    if (text.StartsWith("✨") && text.Contains("Hằng ngày")) Queue(42, "chỉ thứ 2 và thứ 6 thôi");
                    else if (text.StartsWith("✨")) Queue(42, "/ok");
                    return (200, """{"ok":true,"result":{}}""");
                }
                if (path.Contains("/sendChatAction")) return (200, """{"ok":true,"result":true}""");
                if (!path.Contains("/getUpdates")) return (404, """{"ok":false}""");

                var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + path).Query);
                long offset = long.Parse(query["offset"]!);
                List<(long Id, long Chat, string Text)> pending;
                lock (updates) pending = offset < 0 ? [] : updates.Where(u => u.Id >= offset).ToList();
                if (pending.Count == 0) Thread.Sleep(50);
                var result = new JsonArray([.. pending.Select(u => (JsonNode)new JsonObject
                {
                    ["update_id"] = u.Id,
                    ["message"] = new JsonObject
                    {
                        ["chat"] = new JsonObject { ["id"] = u.Chat, ["type"] = "private" }, ["from"] = new JsonObject { ["id"] = u.Chat }, ["text"] = u.Text
                    }
                })]);
                return (200, new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString());
            });

            var settings = SettingsStore.Current.Telegram;
            var apiBase = TelegramBot.ApiBase;
            SettingsStore.Current.Telegram = new TelegramSettings { BotToken = Protector.Protect(token), ChatId = "42", AllowCommands = true };
            TelegramBot.ApiBase = telegram.BaseUrl.TrimEnd('/');
            var host = new FakeHost();
            var logs = new List<string>();
            Action<string> onLog = l => { lock (logs) logs.Add(l); };
            Log.Written += onLog;
            using var bot = new TelegramBot(host);
            try
            {
                Queue(999, "/new xóa hết file của tôi");   // chat lạ → bỏ qua
                Queue(42, "/new 8h sáng mở file báo cáo, làm mới dữ liệu rồi lưu");
                bot.Restart();
                for (int i = 0; i < 300 && host.Added.Count == 0; i++) await Task.Delay(50);
                await Task.Delay(300);
            }
            finally
            {
                bot.Stop();
                Log.Written -= onLog;
                SettingsStore.Current.Telegram = settings;
                TelegramBot.ApiBase = apiBase;
            }

            lock (logs) Assert.Contains(logs, l => l.Contains("bỏ qua lệnh từ chat lạ (999)"));
            List<string> texts;
            lock (sent) texts = sent.Select(s => s.Text).ToList();
            Assert.All(sent, s => Assert.Equal(42, s.Chat));
            Assert.Contains(texts, t => t.StartsWith("⏳ Đang dựng công việc"));
            Assert.Contains(texts, t => t.StartsWith("⏳ Đang sửa bản nháp"));
            Assert.Contains(texts, t => t.Contains("⏰ T2, T6 lúc 08:00"));
            Assert.Contains(texts, t => t.StartsWith("✅ Đã lưu"));
            var (job, run) = Assert.Single(host.Added);
            Assert.False(run);
            Assert.Equal(ScheduleType.Weekly, job.Schedule.Type);
            Assert.All(telegram.Requests, r => Assert.StartsWith("/bot" + token + "/", r.Path));
        });
    }
}
