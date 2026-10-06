using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>Các lệnh điều khiển từ xa mà ứng dụng cung cấp cho Telegram bot (gọi từ luồng nền).</summary>
public interface IRemoteHost
{
    string ListJobs();
    string Run(string nameOrNumber);
    string Stop();
    string Status();

    /// <summary>Bối cảnh cho AI khi tạo công việc mới (các công việc gọi được bằng CallJob, kết nối API, kênh thông báo).</summary>
    FlowGenerator.Context NewJobContext();

    /// <summary>Thêm công việc mới (đã có lịch / kích hoạt) vào danh sách; <paramref name="run"/> = chạy ngay. Trả về câu trả lời cho người dùng.</summary>
    string AddJob(Job job, bool run);

    /// <summary>Chạy công việc theo Id (nút ▶ ở /list, 🔁 Chạy lại dưới tin báo kết quả) — cùng các kiểm tra như /run.</summary>
    string RunById(Guid id) => "Không tìm thấy công việc.";

    /// <summary>Công việc có nút ▶ ở /list: (số thứ tự như /list, tên, Id) — bỏ công việc chờ duyệt / tắt / chưa có bước.</summary>
    IReadOnlyList<(int Number, string Name, Guid Id)> RunnableJobs() => [];

    /// <summary>Tạm dừng flow đang chạy trước bước kế tiếp (như nút ⏸ ở khung trạng thái).</summary>
    string Pause() => "Không hỗ trợ tạm dừng.";

    /// <summary>Flow đang tạm dừng: chạy tiếp (<paramref name="oneStep"/> = chỉ chạy một bước rồi dừng lại).</summary>
    string Resume(bool oneStep) => "Không hỗ trợ.";

    /// <summary>Có flow đang chạy / đang tạm dừng không — để gắn nút phù hợp dưới /status.</summary>
    RemoteRunState RunState() => RemoteRunState.Idle;
}

public enum RemoteRunState { Idle, Running, Paused }

/// <summary>
/// Nhận lệnh điều khiển qua Telegram (long polling): /list, /run, /stop, /status, /history, /screenshot,
/// và tạo công việc mới bằng AI (/new mô tả → xem trước → nhắn thêm để sửa → /ok).
/// Chỉ chấp nhận tin nhắn trong chat riêng với bot, từ đúng người có chat id trong Cài đặt — tin trong nhóm / kênh và người khác
/// nhắn cho bot bị bỏ qua. Công việc tạo qua Telegram chờ duyệt trên máy trước khi được chạy (<see cref="Job.NeedsApproval"/>).
/// </summary>
public sealed class TelegramBot : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(75) };

    /// <summary>Địa chỉ Bot API (kiểm thử thay bằng máy chủ giả).</summary>
    internal static string ApiBase { get; set; } = "https://api.telegram.org";

    /// <summary>Tên kích hoạt của lần chạy ra lệnh từ Telegram — kết quả luôn được báo lại về chat.</summary>
    public const string Trigger = "Telegram";

    private readonly IRemoteHost _host;
    private readonly ChatJobBuilder _builder;
    private CancellationTokenSource? _cts;

    public TelegramBot(IRemoteHost host)
    {
        _host = host;
        _builder = new ChatJobBuilder(host);
    }

    public bool IsRunning => _cts != null;

    /// <summary>Bật/tắt theo cài đặt hiện tại (gọi lại sau khi lưu Cài đặt).</summary>
    public void Restart()
    {
        Stop();
        if (!CanStart(SettingsStore.Current.Telegram, out var token, out long chatId, out var warning))
        {
            if (warning != null) Log.Warn("Telegram: " + warning);
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => LoopAsync(token, chatId, ct));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    /// <summary>Cảnh báo khi chat id là nhóm / kênh (số âm): lệnh điều khiển chỉ nhận từ chat riêng với bot.</summary>
    public const string GroupChatWarning =
        "Chat id âm là nhóm / kênh — lệnh điều khiển chỉ nhận từ chat riêng với bot (chat id dương = id của bạn). Thông báo vẫn gửi vào nhóm được.";

    /// <summary>Cảnh báo khi token đã lưu không giải mã được.</summary>
    public const string TokenWarning =
        "Không giải mã được token Telegram (dữ liệu chép từ máy / tài khoản Windows khác) — nhập lại trong ⚙ Cài đặt";

    /// <summary>
    /// Có bật nhận lệnh được không: đã bật, có token giải mã được, chat id là số dương (chat riêng). False kèm <paramref name="warning"/>
    /// khi cài đặt có vấn đề cần báo (token không giải mã được, chat id nhóm / không hợp lệ); null khi chỉ là chưa bật / chưa nhập.
    /// </summary>
    internal static bool CanStart(TelegramSettings s, out string token, out long chatId, out string? warning)
    {
        token = "";
        chatId = 0;
        warning = null;
        if (!s.AllowCommands) return false;
        if (!Protector.TryUnprotect(s.BotToken, out token))
        {
            warning = TokenWarning;
            return false;
        }
        if (token.Length == 0 || s.ChatId.Trim().Length == 0) return false;
        if (!long.TryParse(s.ChatId.Trim(), out chatId))
        {
            warning = $"chat id \"{s.ChatId.Trim()}\" không phải số — không nhận lệnh điều khiển.";
            return false;
        }
        if (chatId <= 0)
        {
            warning = GroupChatWarning;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Tin nhắn có được xử lý không: chat riêng (type "private") với đúng chat id trong Cài đặt VÀ người gửi chính là chat id đó.
    /// Tin trong nhóm (kể cả nhóm có bạn), kênh, tin chuyển tiếp từ bot khác… đều bị bỏ qua. <paramref name="who"/> = chat / người gửi để ghi nhật ký.
    /// </summary>
    internal static bool IsFromOwner(JsonElement message, long chatId, out string who)
    {
        long chat = 0, from = 0;
        string type = "";
        if (message.TryGetProperty("chat", out var c))
        {
            if (c.TryGetProperty("id", out var id) && id.TryGetInt64(out var v)) chat = v;
            if (c.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) type = t.GetString() ?? "";
        }
        if (message.TryGetProperty("from", out var f) && f.TryGetProperty("id", out var fid) && fid.TryGetInt64(out var fv)) from = fv;
        who = from != 0 && from != chat ? $"{chat}, người gửi {from}" : chat.ToString();
        if (type.Length > 0 && type != "private") who += $", {type}";
        return type == "private" && chat == chatId && from == chatId;
    }

    /// <summary>
    /// Nút bấm có được xử lý không: bấm trong chat riêng với đúng chat id, bởi chính người đó (như <see cref="IsFromOwner"/>).
    /// Nút dưới tin trong nhóm / tin gửi qua chế độ inline (không có "message") đều bị bỏ qua.
    /// </summary>
    internal static bool IsCallbackFromOwner(JsonElement callback, long chatId, out string who)
    {
        long from = 0;
        if (callback.TryGetProperty("from", out var f) && f.TryGetProperty("id", out var fid) && fid.TryGetInt64(out var fv)) from = fv;
        if (!callback.TryGetProperty("message", out var message))
        {
            who = $"nút inline, người bấm {from}";
            return false;
        }
        bool owner = IsFromOwnerChat(message, chatId, out who);
        if (from != chatId) who += $", người bấm {from}";
        return owner && from == chatId;
    }

    /// <summary>Tin (bot đã gửi) nằm trong chat riêng với đúng chat id — người gửi tin là bot nên không xét "from".</summary>
    private static bool IsFromOwnerChat(JsonElement message, long chatId, out string who)
    {
        long chat = 0;
        string type = "";
        if (message.TryGetProperty("chat", out var c))
        {
            if (c.TryGetProperty("id", out var id) && id.TryGetInt64(out var v)) chat = v;
            if (c.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) type = t.GetString() ?? "";
        }
        who = chat.ToString() + (type.Length > 0 && type != "private" ? $", {type}" : "");
        return type == "private" && chat == chatId;
    }

    /// <summary>Danh sách lệnh hiện khi gõ "/" trong Telegram (setMyCommands). Tên lệnh chỉ gồm chữ thường không dấu, số, "_".</summary>
    internal static readonly (string Command, string Description)[] Commands =
    [
        ("list", "Danh sách công việc — bấm ▶ để chạy"),
        ("run", "Chạy công việc: /run <số hoặc tên>"),
        ("status", "Đang chạy gì, tới bước mấy, lịch sắp tới"),
        ("pause", "Tạm dừng flow đang chạy trước bước kế tiếp"),
        ("tiep", "Chạy tiếp flow đang tạm dừng"),
        ("buoc", "Chạy một bước rồi dừng lại"),
        ("stop", "Dừng flow đang chạy"),
        ("history", "Các lần chạy gần nhất: /history [số lần]"),
        ("screenshot", "Chụp màn hình máy tính"),
        ("new", "Tạo công việc mới bằng AI: /new <mô tả>"),
        ("ok", "Lưu bản nháp (/ok chay = lưu và chạy)"),
        ("huy", "Bỏ bản nháp"),
        ("help", "Hướng dẫn các lệnh")
    ];

    private async Task LoopAsync(string token, long chatId, CancellationToken ct)
    {
        var api = $"{ApiBase}/bot{token}";
        long offset = 0;
        string lastError = "";
        bool announced = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!announced)
                {
                    // Bỏ qua các lệnh gửi lúc ScheduleApp đang tắt (tránh chạy lại lệnh cũ khi mở máy).
                    var pending = await GetUpdatesAsync(api, -1, 0, ct);
                    if (pending.Count > 0) offset = pending[^1].GetProperty("update_id").GetInt64() + 1;
                    await SetCommandsAsync(api, ct);
                    Log.Info("Telegram: đang nhận lệnh điều khiển (/help để xem danh sách lệnh).");
                    announced = true;
                }

                foreach (var update in await GetUpdatesAsync(api, offset, 50, ct))
                {
                    offset = update.GetProperty("update_id").GetInt64() + 1;
                    if (update.TryGetProperty("callback_query", out var callback))
                    {
                        await OnButtonAsync(api, chatId, callback, ct);
                        continue;
                    }
                    if (!update.TryGetProperty("message", out var msg) || !msg.TryGetProperty("text", out var textEl)) continue;
                    if (!IsFromOwner(msg, chatId, out var who))
                    {
                        Log.Warn($"Telegram: bỏ qua lệnh từ chat lạ ({who}).");
                        continue;
                    }
                    var text = textEl.GetString() ?? "";
                    Log.Info($"Telegram: nhận lệnh \"{text}\"");
                    await HandleAsync(api, chatId, text.Trim(), ct);
                }
                lastError = "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var message = ex.Message.Replace(token, "***");
                if (message != lastError) Log.Warn("Telegram: lỗi nhận lệnh — " + message);
                lastError = message;
                try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Đăng ký menu lệnh; không được (mạng, bot cũ…) thì thôi — lệnh gõ tay vẫn chạy.</summary>
    private static async Task SetCommandsAsync(string api, CancellationToken ct)
    {
        try
        {
            var list = new JsonArray([.. Commands.Select(c => (JsonNode)new JsonObject { ["command"] = c.Command, ["description"] = c.Description })]);
            await TelegramApi.CallAsync(Http, api, "setMyCommands", () => TelegramApi.JsonContent(new JsonObject { ["commands"] = list.DeepClone() }), ct);
        }
        catch (HttpRequestException ex)
        {
            Log.Info("Telegram: không cài được menu lệnh — " + ex.Message);
        }
    }

    private static async Task<List<JsonElement>> GetUpdatesAsync(string api, long offset, int timeout, CancellationToken ct)
    {
        // Nhận cả tin nhắn lẫn lượt bấm nút.
        using var resp = await Http.GetAsync($"{api}/getUpdates?offset={offset}&timeout={timeout}&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D", ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException((int)resp.StatusCode == 409
                ? "bot đang được dùng ở nơi khác (webhook hoặc một ScheduleApp khác cùng token)."
                : $"Telegram trả về {(int)resp.StatusCode}: {json}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("result").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task HandleAsync(string api, long chatId, string text, CancellationToken ct)
    {
        if (text.Length == 0) return;
        if (!text.StartsWith('/'))
        {
            // Đang có bản nháp: tin nhắn thường là yêu cầu sửa bản nháp đó.
            if (_builder.HasDraft) Draft(api, chatId, fresh: false, () => _builder.ReviseAsync(text, ct), ct);
            else await SendAsync(api, chatId, Help, ct);
            return;
        }
        var parts = text.Split([' ', '\n'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        int at = cmd.IndexOf('@'); // /run@TenBot trong nhóm
        if (at > 0) cmd = cmd[..at];
        var arg = parts.Length > 1 ? parts[1] : "";

        string reply;
        JsonObject? keyboard = null;
        try
        {
            switch (cmd)
            {
                case "/new" or "/tao" or "/moi":
                    if (arg.Length == 0) { reply = await _builder.CreateAsync("", ct); break; }
                    Draft(api, chatId, fresh: true, () => _builder.CreateAsync(arg, ct), ct);
                    return;
                case "/sua" or "/edit":
                    if (arg.Length == 0 || !_builder.HasDraft) { reply = await _builder.ReviseAsync(arg, ct); break; }
                    Draft(api, chatId, fresh: false, () => _builder.ReviseAsync(arg, ct), ct);
                    return;
                case "/ok" or "/luu":
                    reply = _builder.Save(run: arg.Trim().ToLowerInvariant() is "chay" or "chạy" or "run");
                    break;
                case "/huy" or "/cancel":
                    reply = _builder.Cancel();
                    break;
                case "/list" or "/ds":
                    (reply, keyboard) = ListWithButtons();
                    break;
                case "/run" or "/chay":
                    reply = arg.Length == 0 ? "Cú pháp: /run <số thứ tự hoặc tên công việc> — xem số thứ tự bằng /list (hoặc bấm ▶ ở đó)." : _host.Run(arg);
                    break;
                case "/stop" or "/dung":
                    reply = _host.Stop();
                    break;
                case "/pause" or "/tamdung":
                    reply = _host.Pause();
                    break;
                case "/tiep" or "/continue" or "/resume":
                    reply = _host.Resume(oneStep: false);
                    break;
                case "/buoc" or "/step":
                    reply = _host.Resume(oneStep: true);
                    break;
                case "/status" or "/tt":
                    (reply, keyboard) = StatusWithButtons();
                    break;
                case "/history" or "/ls":
                    reply = History(int.TryParse(arg, out int n) ? Math.Clamp(n, 1, 30) : 10);
                    break;
                case "/screenshot" or "/manhinh":
                    await SendScreenshotAsync(api, chatId, ct);
                    return;
                default:
                    reply = Help;
                    break;
            }
        }
        catch (Exception ex)
        {
            reply = "Lỗi: " + ex.Message;
        }
        await SendAsync(api, chatId, reply, ct, keyboard);
    }

    // ───────────────────────────── Nút bấm ─────────────────────────────

    /// <summary>Bấm nút dưới tin của bot: trả lời Telegram ngay (tắt vòng xoay trên nút) rồi làm như lệnh tương ứng.</summary>
    private async Task OnButtonAsync(string api, long chatId, JsonElement callback, CancellationToken ct)
    {
        var id = callback.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var data = callback.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        bool owner = IsCallbackFromOwner(callback, chatId, out var who);
        if (!owner) Log.Warn($"Telegram: bỏ qua nút bấm từ chat lạ ({who}).");
        else Log.Info($"Telegram: bấm nút \"{data}\"");
        try
        {
            await TelegramApi.CallAsync(Http, api, "answerCallbackQuery",
                () => TelegramApi.JsonContent(new JsonObject { ["callback_query_id"] = id, ["text"] = owner ? "" : "Không có quyền." }), ct);
        }
        catch (HttpRequestException) { }
        if (!owner) return;

        string reply;
        JsonObject? keyboard = null;
        try
        {
            var (action, value) = data.IndexOf(':') is int c and > 0 ? (data[..c], data[(c + 1)..]) : (data, "");
            switch (action)
            {
                case "run" when Guid.TryParseExact(value, "N", out var job):
                    reply = _host.RunById(job);
                    break;
                case "list":
                    (reply, keyboard) = ListWithButtons();
                    break;
                case "status":
                    (reply, keyboard) = StatusWithButtons();
                    break;
                case "pause":
                    reply = _host.Pause();
                    break;
                case "go":
                    reply = _host.Resume(oneStep: false);
                    break;
                case "step":
                    reply = _host.Resume(oneStep: true);
                    break;
                case "stop":
                    reply = _host.Stop();
                    break;
                case "hist":
                    reply = History(10);
                    break;
                case "shot":
                    await SendScreenshotAsync(api, chatId, ct);
                    return;
                // Nút của bản nháp mang số phiên bản: nút ở tin xem trước cũ không lưu nhầm bản nháp đã sửa sau đó.
                case "ok" or "okrun" or "huy" when int.TryParse(value, out int version):
                    reply = action == "huy" ? _builder.Cancel(version) : _builder.Save(run: action == "okrun", version);
                    break;
                default:
                    reply = "Nút này không còn dùng được — gõ /help để xem các lệnh.";
                    break;
            }
        }
        catch (Exception ex)
        {
            reply = "Lỗi: " + ex.Message;
        }
        await SendAsync(api, chatId, reply, ct, keyboard);
    }

    /// <summary>/list kèm nút ▶ cho từng công việc chạy được (tối đa <see cref="MaxRunButtons"/> nút).</summary>
    private (string Text, JsonObject? Keyboard) ListWithButtons()
    {
        var text = _host.ListJobs();
        var jobs = _host.RunnableJobs();
        if (jobs.Count == 0) return (text, null);
        var rows = jobs.Take(MaxRunButtons).Select(j => new[] { ($"▶ {j.Number}. {Short(j.Name, 40)}", "run:" + j.Id.ToString("N")) }).ToArray();
        if (jobs.Count > MaxRunButtons) text += $"\n(Chỉ có nút cho {MaxRunButtons} công việc đầu — công việc khác chạy bằng /run <số>.)";
        return (text, TelegramApi.Keyboard(rows));
    }

    internal const int MaxRunButtons = 30;

    /// <summary>/status kèm nút điều khiển theo tình trạng: đang chạy → Tạm dừng / Dừng; đang tạm dừng → Chạy tiếp / Một bước / Dừng.</summary>
    private (string Text, JsonObject? Keyboard) StatusWithButtons() => (_host.Status(), _host.RunState() switch
    {
        RemoteRunState.Running => TelegramApi.Keyboard([("⏸ Tạm dừng", "pause"), ("■ Dừng", "stop")], [("🔄 Cập nhật", "status")]),
        RemoteRunState.Paused => TelegramApi.Keyboard([("▶ Chạy tiếp", "go"), ("⏭ Một bước", "step"), ("■ Dừng", "stop")], [("🔄 Cập nhật", "status")]),
        _ => null
    });

    /// <summary>
    /// Nút dưới tin báo kết quả chạy (🔁 Chạy lại · 📷 Màn hình · 📜 Lịch sử) — chỉ khi đang bật nhận lệnh ở chat riêng
    /// (nút trong nhóm / khi chưa bật nhận lệnh thì bấm cũng không có tác dụng).
    /// </summary>
    internal static JsonObject? ResultKeyboard(RunRecord r)
    {
        if (!CanStart(SettingsStore.Current.Telegram, out _, out _, out _) || r.JobId == Guid.Empty) return null;
        var row = new List<(string, string)> { ("🔁 Chạy lại", "run:" + r.JobId.ToString("N")) };
        if (!r.Ok) row.Add(("📷 Màn hình", "shot"));
        row.Add(("📜 Lịch sử", "hist"));
        return TelegramApi.Keyboard([.. row]);
    }

    /// <summary>Nút dưới tin xem trước bản nháp — gắn số phiên bản bản nháp.</summary>
    private JsonObject? DraftKeyboard() => _builder.HasDraft
        ? TelegramApi.Keyboard([("✔ Lưu", $"ok:{_builder.DraftVersion}"), ("▶ Lưu và chạy", $"okrun:{_builder.DraftVersion}"), ("✖ Bỏ", $"huy:{_builder.DraftVersion}")])
        : null;

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Dựng / sửa bản nháp bằng AI ở nền (mất 20–60 giây) để bot vẫn trả lời các lệnh khác; trong lúc chờ hiện "đang nhập…".
    /// </summary>
    private void Draft(string api, long chatId, bool fresh, Func<Task<string>> work, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (_builder.IsBusy || !AiClient.IsConfigured)
                {
                    // Trả lời ngay lý do không dựng được (đang bận / chưa có khóa Claude).
                    await SendAsync(api, chatId, await work(), ct);
                    return;
                }
                await SendAsync(api, chatId, fresh ? "⏳ Đang dựng công việc bằng AI… (thường 20–60 giây)" : "⏳ Đang sửa bản nháp…", ct);
                var task = work();
                while (!task.IsCompleted)
                {
                    try
                    {
                        await TelegramApi.CallAsync(Http, api, "sendChatAction",
                            () => TelegramApi.JsonContent(new JsonObject { ["chat_id"] = chatId, ["action"] = "typing" }), ct);
                    }
                    catch (HttpRequestException) { }
                    await Task.WhenAny(task, Task.Delay(4500, ct));
                }
                await SendAsync(api, chatId, await task, ct, DraftKeyboard());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Warn("Telegram: lỗi khi tạo công việc — " + ex.Message);
            }
        }, ct);
    }

    private const string Help =
        "ScheduleApp — lệnh điều khiển (gõ / để hiện menu):\n" +
        "/list — danh sách công việc, bấm ▶ để chạy\n" +
        "/run <số hoặc tên> — chạy công việc (chạy xong bot báo kết quả)\n" +
        "/status — đang chạy gì, tới bước mấy, lịch sắp tới\n" +
        "/pause — tạm dừng trước bước kế tiếp · /tiep chạy tiếp · /buoc chạy một bước · /stop dừng\n" +
        "/history [n] — n lần chạy gần nhất\n" +
        "/screenshot — chụp màn hình máy tính\n" +
        "/new <mô tả> — tạo công việc mới bằng AI (nói giờ chạy thì đặt lịch luôn), vd:\n" +
        "   /new 8h sáng các ngày làm việc mở D:\\bao-cao.xlsx, làm mới dữ liệu, lưu rồi báo cho tôi\n" +
        "   → xem bản nháp, nhắn thêm để sửa · /ok lưu · /ok chay lưu và chạy ngay · /huy bỏ\n" +
        "   (công việc mới chờ bạn duyệt trên máy tính rồi mới chạy)";

    /// <summary>N lần chạy gần nhất — qua bộ che bí mật của nhật ký (thông báo lỗi có thể chứa mật khẩu / token).</summary>
    internal static string History(int count)
    {
        var records = RunHistory.All.AsEnumerable().Reverse().Take(count).ToList();
        if (records.Count == 0) return "Chưa có lần chạy nào.";
        return Log.Redact(string.Join("\n", records.Select(r =>
            $"{(r.Ok ? "✅" : "❌")} {r.Start:HH:mm dd/MM} {r.JobName} — {r.Message}" + (r.Duration.TotalSeconds >= 1 ? $" ({r.Duration.TotalSeconds:0}s)" : ""))));
    }

    /// <summary>Gửi trả lời (chia nhỏ theo giới hạn 4096 ký tự / tin); nút bấm gắn vào phần cuối. Tự thử lại khi mạng chập chờn.</summary>
    private static async Task SendAsync(string api, long chatId, string text, CancellationToken ct, JsonObject? keyboard = null)
    {
        if (text.Length == 0) text = "(trống)";
        for (int i = 0; i < text.Length; i += 4000)
        {
            var chunk = text.Substring(i, Math.Min(4000, text.Length - i));
            bool last = i + 4000 >= text.Length;
            try { await TelegramApi.SendMessageAsync(Http, api, chatId.ToString(), chunk, last ? keyboard : null, ct); }
            catch (HttpRequestException ex)
            {
                Log.Warn("Telegram: không gửi được trả lời — " + ex.Message);
                return;
            }
        }
    }

    private static async Task SendScreenshotAsync(string api, long chatId, CancellationToken ct)
    {
        byte[] jpeg;
        using (var shot = Vision.ScreenCapture.Capture(Vision.ScreenCapture.VirtualScreen))
        using (var ms = new MemoryStream())
        {
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var p = new System.Drawing.Imaging.EncoderParameters(1);
            p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            shot.Save(ms, codec, p);
            jpeg = ms.ToArray();
        }
        try
        {
            await TelegramApi.SendPhotoAsync(Http, api, chatId.ToString(), jpeg, "manhinh.jpg", "image/jpeg",
                $"{Environment.MachineName} — {DateTime.Now:HH:mm:ss dd/MM/yyyy}", null, ct);
        }
        catch (HttpRequestException ex)
        {
            await SendAsync(api, chatId, "Không gửi được ảnh: " + ex.Message, ct);
        }
    }
}
