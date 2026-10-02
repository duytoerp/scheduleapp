using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
}

/// <summary>
/// Nhận lệnh điều khiển qua Telegram (long polling): /list, /run, /stop, /status, /history, /screenshot,
/// và tạo công việc mới bằng AI (/new mô tả → xem trước → nhắn thêm để sửa → /ok).
/// Chỉ chấp nhận tin nhắn từ đúng chat id trong Cài đặt — người khác nhắn cho bot sẽ bị bỏ qua.
/// </summary>
public sealed class TelegramBot : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(75) };

    /// <summary>Địa chỉ Bot API (kiểm thử thay bằng máy chủ giả).</summary>
    internal static string ApiBase { get; set; } = "https://api.telegram.org";

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
        var s = SettingsStore.Current.Telegram;
        var token = Protector.Unprotect(s.BotToken);
        if (!s.AllowCommands || token.Length == 0 || !long.TryParse(s.ChatId.Trim(), out long chatId)) return;
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
                    Log.Info("Telegram: đang nhận lệnh điều khiển (/help để xem danh sách lệnh).");
                    announced = true;
                }

                foreach (var update in await GetUpdatesAsync(api, offset, 50, ct))
                {
                    offset = update.GetProperty("update_id").GetInt64() + 1;
                    if (!update.TryGetProperty("message", out var msg) || !msg.TryGetProperty("text", out var textEl)) continue;
                    long from = msg.GetProperty("chat").GetProperty("id").GetInt64();
                    if (from != chatId)
                    {
                        Log.Warn($"Telegram: bỏ qua lệnh từ chat lạ ({from}).");
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
                if (ex.Message != lastError) Log.Warn("Telegram: lỗi nhận lệnh — " + ex.Message);
                lastError = ex.Message;
                try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task<List<JsonElement>> GetUpdatesAsync(string api, long offset, int timeout, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"{api}/getUpdates?offset={offset}&timeout={timeout}&allowed_updates=%5B%22message%22%5D", ct);
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
                    reply = _host.ListJobs();
                    break;
                case "/run" or "/chay":
                    reply = arg.Length == 0 ? "Cú pháp: /run <số thứ tự hoặc tên công việc> — xem số thứ tự bằng /list." : _host.Run(arg);
                    break;
                case "/stop" or "/dung":
                    reply = _host.Stop();
                    break;
                case "/status" or "/tt":
                    reply = _host.Status();
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
        await SendAsync(api, chatId, reply, ct);
    }

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
                    try { using var _ = await Http.PostAsJsonAsync($"{api}/sendChatAction", new { chat_id = chatId, action = "typing" }, ct); }
                    catch (HttpRequestException) { }
                    await Task.WhenAny(task, Task.Delay(4500, ct));
                }
                await SendAsync(api, chatId, await task, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Warn("Telegram: lỗi khi tạo công việc — " + ex.Message);
            }
        }, ct);
    }

    private const string Help =
        "ScheduleApp — lệnh điều khiển:\n" +
        "/new <mô tả> — tạo công việc mới bằng AI (nói giờ chạy thì đặt lịch luôn), vd:\n" +
        "   /new 8h sáng các ngày làm việc mở D:\\bao-cao.xlsx, làm mới dữ liệu, lưu rồi báo cho tôi\n" +
        "   → xem bản nháp, nhắn thêm để sửa · /ok lưu · /ok chay lưu và chạy ngay · /huy bỏ\n" +
        "/list — danh sách công việc (kèm số thứ tự)\n" +
        "/run <số hoặc tên> — chạy công việc\n" +
        "/stop — dừng flow đang chạy\n" +
        "/status — đang chạy gì, lịch sắp tới\n" +
        "/history [n] — n lần chạy gần nhất\n" +
        "/screenshot — chụp màn hình máy tính";

    private static string History(int count)
    {
        var records = RunHistory.All.AsEnumerable().Reverse().Take(count).ToList();
        if (records.Count == 0) return "Chưa có lần chạy nào.";
        return string.Join("\n", records.Select(r =>
            $"{(r.Ok ? "✅" : "❌")} {r.Start:HH:mm dd/MM} {r.JobName} — {r.Message}" + (r.Duration.TotalSeconds >= 1 ? $" ({r.Duration.TotalSeconds:0}s)" : "")));
    }

    private static async Task SendAsync(string api, long chatId, string text, CancellationToken ct)
    {
        // Giới hạn 4096 ký tự / tin nhắn.
        for (int i = 0; i < text.Length || i == 0; i += 4000)
        {
            var chunk = text.Length == 0 ? "(trống)" : text.Substring(i, Math.Min(4000, text.Length - i));
            using var resp = await Http.PostAsJsonAsync($"{api}/sendMessage", new { chat_id = chatId, text = chunk }, ct);
            if (text.Length == 0) break;
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
        using var form = new MultipartFormDataContent
        {
            { new StringContent(chatId.ToString()), "chat_id" },
            { new StringContent($"{Environment.MachineName} — {DateTime.Now:HH:mm:ss dd/MM/yyyy}"), "caption" }
        };
        var file = new ByteArrayContent(jpeg);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "photo", "manhinh.jpg");
        using var resp = await Http.PostAsync($"{api}/sendPhoto", form, ct);
        if (!resp.IsSuccessStatusCode) await SendAsync(api, chatId, "Không gửi được ảnh: " + await resp.Content.ReadAsStringAsync(ct), ct);
    }
}
