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
}

/// <summary>
/// Nhận lệnh điều khiển qua Telegram (long polling): /list, /run, /stop, /status, /history, /screenshot.
/// Chỉ chấp nhận tin nhắn từ đúng chat id trong Cài đặt — người khác nhắn cho bot sẽ bị bỏ qua.
/// </summary>
public sealed class TelegramBot : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(75) };

    private readonly IRemoteHost _host;
    private CancellationTokenSource? _cts;

    public TelegramBot(IRemoteHost host) => _host = host;

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
        var api = $"https://api.telegram.org/bot{token}";
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
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;
        var cmd = parts[0].ToLowerInvariant();
        int at = cmd.IndexOf('@'); // /run@TenBot trong nhóm
        if (at > 0) cmd = cmd[..at];
        var arg = parts.Length > 1 ? parts[1] : "";

        string reply;
        try
        {
            switch (cmd)
            {
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

    private const string Help =
        "ScheduleApp — lệnh điều khiển:\n" +
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
