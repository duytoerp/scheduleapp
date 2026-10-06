using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.Tests;

/// <summary>
/// Telegram: nút bấm (▶ ở /list, Chạy lại / Màn hình / Lịch sử, Tạm dừng / Chạy tiếp, Lưu / Bỏ bản nháp), menu lệnh, /pause /tiep,
/// báo kết quả lần chạy ra lệnh từ Telegram, gửi lại khi mạng chập chờn / bị giới hạn tốc độ.
/// </summary>
public class TelegramButtonsTests
{
    private static readonly Guid JobA = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed class ButtonHost : IRemoteHost
    {
        public List<string> Calls { get; } = [];
        public RemoteRunState State = RemoteRunState.Running;
        private string Note(string call) { lock (Calls) Calls.Add(call); return "ok " + call; }
        public string ListJobs() => "1. Báo cáo\n2. (chờ duyệt) Lạ";
        public string Run(string nameOrNumber) => Note("run " + nameOrNumber);
        public string Stop() => Note("stop");
        public string Status() => "▶ Đang chạy \"Báo cáo\" — bước 2/5";
        public FlowGenerator.Context NewJobContext() => new();
        public string AddJob(Job job, bool run) => "";
        public string RunById(Guid id) => Note("runId " + id.ToString("N"));
        public IReadOnlyList<(int Number, string Name, Guid Id)> RunnableJobs() => [(1, "Báo cáo", JobA)];
        public string Pause() => Note("pause");
        public string Resume(bool oneStep) => Note(oneStep ? "step" : "go");
        public RemoteRunState RunState() => State;
    }

    /// <summary>Máy chủ Telegram giả: hàng đợi update, ghi lại mọi yêu cầu (đường dẫn + nội dung).</summary>
    private sealed class FakeTelegram : IDisposable
    {
        private readonly List<JsonObject> _updates = [];
        public MiniHttpServer Server { get; }
        public List<(string Method, JsonNode? Body)> Calls { get; } = [];

        public FakeTelegram()
        {
            Server = new MiniHttpServer((_, path, _, body) =>
            {
                var method = path.Split('?')[0].Split('/')[^1];
                if (method == "getUpdates")
                {
                    var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + path).Query);
                    long offset = long.Parse(query["offset"]!);
                    JsonArray result;
                    lock (_updates) result = new JsonArray([.. _updates.Where(u => offset >= 0 && (long)u["update_id"]! >= offset).Select(u => u.DeepClone())]);
                    if (result.Count == 0) Thread.Sleep(30);
                    return (200, new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString());
                }
                JsonNode? node = null;
                try { node = JsonNode.Parse(body); } catch (JsonException) { }
                lock (Calls) Calls.Add((method, node));
                return (200, """{"ok":true,"result":true}""");
            });
        }

        public void Message(long chat, string text) => Add(new JsonObject
        {
            ["message"] = new JsonObject { ["chat"] = new JsonObject { ["id"] = chat, ["type"] = "private" }, ["from"] = new JsonObject { ["id"] = chat }, ["text"] = text }
        });

        public void Button(long chat, long from, string data, string chatType = "private") => Add(new JsonObject
        {
            ["callback_query"] = new JsonObject
            {
                ["id"] = "cb" + data, ["from"] = new JsonObject { ["id"] = from }, ["data"] = data,
                ["message"] = new JsonObject { ["chat"] = new JsonObject { ["id"] = chat, ["type"] = chatType } }
            }
        });

        private void Add(JsonObject update)
        {
            lock (_updates)
            {
                update["update_id"] = (long)(_updates.Count + 1);
                _updates.Add(update);
            }
        }

        public List<JsonNode> Sent(string method)
        {
            lock (Calls) return [.. Calls.Where(c => c.Method == method && c.Body != null).Select(c => c.Body!)];
        }

        public void Dispose() => Server.Dispose();
    }

    private static async Task Until(Func<bool> done, string what)
    {
        for (int i = 0; i < 200 && !done(); i++) await Task.Delay(25);
        Assert.True(done(), "Không thấy: " + what);
    }

    private static async Task WithBot(Func<FakeTelegram, ButtonHost, Task> body)
    {
        using var telegram = new FakeTelegram();
        var settings = SettingsStore.Current.Telegram;
        var apiBase = TelegramBot.ApiBase;
        SettingsStore.Current.Telegram = new TelegramSettings { BotToken = Protector.Protect("123:abc"), ChatId = "42", AllowCommands = true };
        TelegramBot.ApiBase = telegram.Server.BaseUrl.TrimEnd('/');
        var host = new ButtonHost();
        using var bot = new TelegramBot(host);
        try
        {
            bot.Restart();
            await Until(() => telegram.Sent("setMyCommands").Count > 0, "cài menu lệnh");
            await body(telegram, host);
        }
        finally
        {
            bot.Stop();
            SettingsStore.Current.Telegram = settings;
            TelegramBot.ApiBase = apiBase;
        }
    }

    [Fact]
    public async Task BotInstallsCommandMenuAndRunsJobsFromButtons()
    {
        await WithBot(async (telegram, host) =>
        {
            var commands = telegram.Sent("setMyCommands")[0]["commands"]!.AsArray().Select(c => (string)c!["command"]!).ToList();
            Assert.Contains("list", commands);
            Assert.Contains("pause", commands);
            Assert.All(commands, c => Assert.Matches("^[a-z0-9_]{1,32}$", c));

            // /list → nút ▶ cho công việc chạy được (không có công việc chờ duyệt).
            telegram.Message(42, "/list");
            await Until(() => telegram.Sent("sendMessage").Count > 0, "trả lời /list");
            var list = telegram.Sent("sendMessage")[0];
            var buttons = list["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).ToList();
            Assert.Single(buttons);
            Assert.Equal("▶ 1. Báo cáo", (string)buttons[0]!["text"]!);
            Assert.Equal("run:" + JobA.ToString("N"), (string)buttons[0]!["callback_data"]!);

            // Bấm ▶ đúng chủ → chạy; nút trong nhóm / người khác bấm → bỏ qua, Telegram vẫn được trả lời (tắt vòng xoay).
            telegram.Button(42, 77, "run:" + JobA.ToString("N"));
            telegram.Button(-100123, 42, "run:" + JobA.ToString("N"), "supergroup");
            telegram.Button(42, 42, "run:" + JobA.ToString("N"));
            await Until(() => host.Calls.Count > 0, "chạy từ nút");
            await Until(() => telegram.Sent("answerCallbackQuery").Count >= 3, "trả lời cả 3 lượt bấm");
            await Task.Delay(150);
            lock (host.Calls) Assert.Equal(["runId " + JobA.ToString("N")], host.Calls);
            Assert.Equal(2, telegram.Sent("answerCallbackQuery").Count(a => (string?)a["text"] == "Không có quyền."));
        });
    }

    [Fact]
    public async Task StatusOffersPauseThenResumeButtons()
    {
        await WithBot(async (telegram, host) =>
        {
            telegram.Message(42, "/status");
            await Until(() => telegram.Sent("sendMessage").Count >= 1, "trả lời /status");
            string Datas(JsonNode m) => string.Join(",", m["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => (string)b!["callback_data"]!));
            Assert.Equal("pause,stop,status", Datas(telegram.Sent("sendMessage")[0]));

            telegram.Button(42, 42, "pause");
            await Until(() => host.Calls.Contains("pause"), "tạm dừng từ nút");
            host.State = RemoteRunState.Paused;
            telegram.Message(42, "/status");
            await Until(() => telegram.Sent("sendMessage").Count >= 3, "trả lời /status lần 2");
            Assert.Equal("go,step,stop,status", Datas(telegram.Sent("sendMessage")[2]));

            telegram.Message(42, "/buoc");
            telegram.Message(42, "/tiep");
            telegram.Button(42, 42, "stop");
            await Until(() => host.Calls.Count >= 4, "một bước, chạy tiếp, dừng");
            lock (host.Calls) Assert.Equal(["pause", "step", "go", "stop"], host.Calls);

            host.State = RemoteRunState.Idle;
            telegram.Message(42, "/status");
            await Until(() => telegram.Sent("sendMessage").Count >= 7, "trả lời /status lần 3");
            Assert.Null(telegram.Sent("sendMessage")[^1]["reply_markup"]);   // rảnh → không có nút
        });
    }

    [Fact]
    public async Task OldOrUnknownButtonsDoNothingHarmful()
    {
        await WithBot(async (telegram, host) =>
        {
            telegram.Button(42, 42, "run:khong-phai-guid");
            telegram.Button(42, 42, "ok:7");          // không có bản nháp
            telegram.Button(42, 42, "xoa-het");
            await Until(() => telegram.Sent("sendMessage").Count >= 3, "trả lời 3 nút");
            Assert.Empty(host.Calls);
            var replies = telegram.Sent("sendMessage").Select(m => (string)m["text"]!).ToList();
            Assert.Equal(2, replies.Count(r => r.Contains("không còn dùng được")));
            Assert.Contains(replies, r => r.Contains("Chưa có bản nháp"));
        });
    }

    [Fact]
    public void CallbackMustComeFromOwnerInPrivateChat()
    {
        JsonElement Cb(string json) => JsonDocument.Parse(json).RootElement;
        Assert.True(TelegramBot.IsCallbackFromOwner(Cb("""{"from":{"id":42},"message":{"chat":{"id":42,"type":"private"}}}"""), 42, out _));
        Assert.False(TelegramBot.IsCallbackFromOwner(Cb("""{"from":{"id":7},"message":{"chat":{"id":42,"type":"private"}}}"""), 42, out _));
        Assert.False(TelegramBot.IsCallbackFromOwner(Cb("""{"from":{"id":42},"message":{"chat":{"id":-5,"type":"group"}}}"""), 42, out _));
        Assert.False(TelegramBot.IsCallbackFromOwner(Cb("""{"from":{"id":42},"message":{"chat":{"id":42}}}"""), 42, out _));
        Assert.False(TelegramBot.IsCallbackFromOwner(Cb("""{"from":{"id":42},"inline_message_id":"x"}"""), 42, out var who));
        Assert.Contains("inline", who);
    }

    [Fact]
    public async Task ApiRetriesFlakyNetworkAndRateLimitsButNotBadRequests()
    {
        var delays = TelegramApi.RetryDelays;
        TelegramApi.RetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];
        try
        {
            int calls = 0;
            using (var flaky = new MiniHttpServer((_, _, _, _) => Interlocked.Increment(ref calls) switch
                   {
                       1 => (502, "Bad Gateway"),
                       2 => (429, """{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 1","parameters":{"retry_after":1}}"""),
                       _ => (200, """{"ok":true,"result":{"message_id":5}}""")
                   }))
            {
                var started = DateTime.UtcNow;
                var result = await TelegramApi.SendMessageAsync(new HttpClient(), flaky.BaseUrl.TrimEnd('/') + "/botX", "42", "chào", null);
                Assert.Equal(5, result.GetProperty("message_id").GetInt32());
                Assert.Equal(3, calls);
                Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(0.9));   // chờ đúng retry_after
            }

            calls = 0;
            using var bad = new MiniHttpServer((_, _, _, _) => { Interlocked.Increment(ref calls); return (400, """{"ok":false,"description":"Bad Request: chat not found"}"""); });
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => TelegramApi.SendMessageAsync(new HttpClient(), bad.BaseUrl.TrimEnd('/') + "/botX", "42", "chào", null));
            Assert.Equal(1, calls);
            Assert.Contains("chat not found", ex.Message);

            calls = 0;
            using var down = new MiniHttpServer((_, _, _, _) => { Interlocked.Increment(ref calls); return (503, "x"); });
            await Assert.ThrowsAsync<HttpRequestException>(() => TelegramApi.SendMessageAsync(new HttpClient(), down.BaseUrl.TrimEnd('/') + "/botX", "42", "chào", null));
            Assert.Equal(3, calls);                                              // 1 lần + 2 lần thử lại
        }
        finally { TelegramApi.RetryDelays = delays; }
    }

    [Fact]
    public async Task RunsOrderedFromTelegramAlwaysReportBackWithButtons()
    {
        using var telegram = new FakeTelegram();
        var settings = SettingsStore.Current.Telegram;
        var (webhook, email) = (SettingsStore.Current.Webhook.Enabled, SettingsStore.Current.Email.Enabled);
        var apiBase = TelegramBot.ApiBase;
        TelegramBot.ApiBase = telegram.Server.BaseUrl.TrimEnd('/');
        SettingsStore.Current.Webhook.Enabled = SettingsStore.Current.Email.Enabled = false;
        try
        {
            // Công việc không bật thông báo, kênh Telegram thông báo cũng tắt — nhưng chạy bằng /run thì vẫn báo về chat ra lệnh.
            SettingsStore.Current.Telegram = new TelegramSettings { BotToken = Protector.Protect("123:abc"), ChatId = "42", AllowCommands = true, Enabled = false };
            var job = new Job { Id = JobA, Name = "Báo cáo", NotifyMode = NotifyMode.Never };
            RunRecord Rec(string trigger, bool ok = false) => new()
            {
                JobId = JobA, JobName = job.Name, Trigger = trigger, Start = DateTime.Now, End = DateTime.Now, Ok = ok,
                Message = ok ? "Xong" : "Không thấy cửa sổ", FailedStep = ok ? 0 : 3
            };
            var failed = Rec(TelegramBot.Trigger);
            await NotificationService.SendForRunAsync(job, failed);
            var sent = Assert.Single(telegram.Sent("sendMessage"));
            Assert.StartsWith("❌ Báo cáo", (string)sent["text"]!);
            Assert.Equal(42, (long)sent["chat_id"]!);
            var datas = sent["reply_markup"]!["inline_keyboard"]![0]!.AsArray().Select(b => (string)b!["callback_data"]!).ToList();
            Assert.Equal(["run:" + JobA.ToString("N"), "shot", "hist"], datas);

            // Chạy theo lịch, không bật thông báo → không gửi gì.
            await NotificationService.SendForRunAsync(job, Rec("Lịch"));
            Assert.Single(telegram.Sent("sendMessage"));

            // Bật thông báo Telegram + công việc báo mọi lần: chỉ một tin (không gửi trùng), thành công không có nút Màn hình.
            SettingsStore.Current.Telegram.Enabled = true;
            job.NotifyMode = NotifyMode.Always;
            await NotificationService.SendForRunAsync(job, Rec(TelegramBot.Trigger, ok: true));
            Assert.Equal(2, telegram.Sent("sendMessage").Count);
            var ok = telegram.Sent("sendMessage")[1];
            Assert.Equal(["run:" + JobA.ToString("N"), "hist"], ok["reply_markup"]!["inline_keyboard"]![0]!.AsArray().Select(b => (string)b!["callback_data"]!));

            // Chưa bật nhận lệnh: thông báo vẫn gửi nhưng không gắn nút (bấm cũng không ai xử lý).
            SettingsStore.Current.Telegram.AllowCommands = false;
            await NotificationService.SendForRunAsync(job, Rec("Lịch"));
            Assert.Null(telegram.Sent("sendMessage")[2]["reply_markup"]);
        }
        finally
        {
            SettingsStore.Current.Telegram = settings;
            (SettingsStore.Current.Webhook.Enabled, SettingsStore.Current.Email.Enabled) = (webhook, email);
            TelegramBot.ApiBase = apiBase;
        }
    }
}
