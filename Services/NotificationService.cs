using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Http.Json;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>Gửi kết quả chạy flow ra ngoài: Telegram, email (SMTP), webhook (Teams / Slack / Discord / Google Chat).</summary>
public static class NotificationService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
    {
        Timeout = TimeSpan.FromSeconds(30),
        // Phản hồi của Telegram / webhook chỉ là vài dòng JSON — không đọc cả trang lỗi khổng lồ vào bộ nhớ.
        MaxResponseContentBufferSize = 1024 * 1024
    };

    public static async Task SendForRunAsync(Job job, RunRecord r)
    {
        bool send = job.NotifyMode switch
        {
            NotifyMode.Always => true,
            NotifyMode.OnError => !r.Ok,
            _ => false
        };
        if (!send || !AnyChannelEnabled) return;

        var title = $"{(r.Ok ? "✅" : "❌")} {job.Name}";
        var body = $"{r.Message}\n" +
                   (r.FailedStep > 0 ? $"Bước lỗi: {r.FailedStep}\n" : "") +
                   $"Kích hoạt: {r.Trigger} · Bắt đầu {r.Start:HH:mm:ss dd/MM/yyyy} · {r.Duration.TotalSeconds:0} giây\n" +
                   $"Máy: {Environment.MachineName}";
        await SendAsync(title, body, r.Screenshot);
    }

    public static bool AnyChannelEnabled
    {
        get
        {
            var s = SettingsStore.Current;
            return s.Telegram.Enabled || s.Email.Enabled || s.Webhook.Enabled;
        }
    }

    /// <summary>Gửi qua mọi kênh đang bật. Lỗi được ghi log và trả về (không ném ra ngoài). Bí mật trong tiêu đề / nội dung bị che.</summary>
    /// <param name="screenshot">Ảnh đính kèm: ảnh lỗi chỉ gửi ở kênh có bật "Kèm ảnh chụp màn hình lỗi".</param>
    /// <param name="requested">Ảnh do bước "Gửi thông báo" yêu cầu rõ ("Gửi kèm ảnh chụp màn hình hiện tại") — gửi ở mọi kênh hỗ trợ ảnh.</param>
    public static async Task<List<string>> SendAsync(string title, string body, string? screenshot, bool requested = false)
    {
        var s = SettingsStore.Current;
        title = Log.Redact(title);
        body = Log.Redact(body);
        var tasks = new List<Task<string?>>();
        if (s.Telegram.Enabled) tasks.Add(Guard("Telegram", () => SendTelegramAsync(s.Telegram, title, body, screenshot, requested)));
        if (s.Email.Enabled) tasks.Add(Guard("Email", () => SendEmailAsync(s.Email, title, body, screenshot, requested)));
        if (s.Webhook.Enabled) tasks.Add(Guard("Webhook", () => SendWebhookAsync(s.Webhook, title, body)));
        var results = await Task.WhenAll(tasks);
        return results.OfType<string>().ToList();
    }

    private static async Task<string?> Guard(string channel, Func<Task> send)
    {
        try
        {
            await send();
            return null;
        }
        catch (Exception ex)
        {
            var message = Log.Redact(ex.Message);
            Log.Error($"Không gửi được thông báo {channel}: {message}");
            return $"{channel}: {message}";
        }
    }

    public static async Task SendTelegramAsync(TelegramSettings t, string title, string body, string? screenshot, bool requested = false)
    {
        var token = Credentials.Reveal(t.BotToken, "token bot Telegram");
        if (token.Length == 0 || t.ChatId.Trim().Length == 0) throw new InvalidOperationException("Chưa nhập token bot hoặc chat id.");
        var api = $"https://api.telegram.org/bot{token}";
        var text = Log.Redact($"{title}\n{body}");

        HttpResponseMessage resp;
        if ((requested || t.SendScreenshot) && screenshot != null && File.Exists(screenshot))
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(t.ChatId.Trim()), "chat_id" },
                { new StringContent(text.Length > 1000 ? text[..1000] + "…" : text), "caption" }
            };
            var file = new ByteArrayContent(await File.ReadAllBytesAsync(screenshot));
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(file, "photo", Path.GetFileName(screenshot));
            resp = await Http.PostAsync($"{api}/sendPhoto", form);
        }
        else
        {
            resp = await Http.PostAsJsonAsync($"{api}/sendMessage", new { chat_id = t.ChatId.Trim(), text });
        }
        using (resp)
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Telegram trả về {(int)resp.StatusCode}: {await ErrorText(resp)}");
    }

    public static async Task SendEmailAsync(EmailSettings e, string title, string body, string? screenshot, bool requested = false)
    {
        if (string.IsNullOrWhiteSpace(e.Host) || string.IsNullOrWhiteSpace(e.To)) throw new InvalidOperationException("Chưa nhập máy chủ SMTP hoặc người nhận.");
        var host = e.Host.Trim();
        if (SmtpTlsProblem(e) is { } problem) throw new InvalidOperationException(problem);
        var password = Credentials.Reveal(e.Password, "mật khẩu email (SMTP)");
        var from = string.IsNullOrWhiteSpace(e.From) ? e.User : e.From;
        using var msg = new MailMessage { From = new MailAddress(from), Subject = "[ScheduleApp] " + Log.Redact(title), Body = Log.Redact(body) };
        foreach (var to in e.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) msg.To.Add(to);
        if ((requested || e.AttachScreenshot) && screenshot != null && File.Exists(screenshot)) msg.Attachments.Add(new Attachment(screenshot));

        using var smtp = new SmtpClient(host, e.Port) { EnableSsl = e.UseSsl, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 60_000 };
        if (!string.IsNullOrWhiteSpace(e.User)) smtp.Credentials = new NetworkCredential(e.User.Trim(), password);
        await smtp.SendMailAsync(msg);
    }

    /// <summary>
    /// Không có TLS thì mật khẩu và nội dung đi trên mạng dạng chữ thường — chỉ cho phép với máy chủ ngay trên máy này hoặc khi đã
    /// bật "Cho phép gửi không mã hóa". Trả về lý do từ chối, null = được gửi.
    /// </summary>
    internal static string? SmtpTlsProblem(EmailSettings e)
    {
        var host = e.Host.Trim();
        if (e.UseSsl || e.AllowNoTls || IsLoopback(host)) return null;
        return $"Máy chủ SMTP \"{host}\" đang tắt SSL/TLS — mật khẩu và nội dung sẽ gửi không mã hóa. " +
               "Bật SSL/TLS (STARTTLS, cổng 587) trong ⚙ Cài đặt → Thông báo, hoặc tick \"Cho phép gửi không mã hóa\" nếu là máy chủ nội bộ tin cậy.";
    }

    public static async Task SendWebhookAsync(WebhookSettings w, string title, string body)
    {
        // URL webhook chứa khóa bí mật (lưu mã hóa) — ai có URL là gửi được tin vào kênh.
        var url = Credentials.Reveal(w.Url, "URL webhook").Trim();
        if (url.Length == 0) throw new InvalidOperationException("Chưa nhập URL webhook.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("URL webhook không hợp lệ (cần bắt đầu bằng https://).");
        // URL chứa khóa gửi tin — qua http thì ai nghe lén mạng cũng lấy được (trừ máy chủ trên chính máy này).
        if (uri.Scheme == "http" && !IsLoopback(uri.Host))
            throw new InvalidOperationException($"URL webhook tới {uri.Host} dùng http:// (không mã hóa) — URL chứa khóa gửi tin, hãy dùng https://.");
        var text = Log.Redact($"{title}\n{body}");
        // "text": Teams / Slack / Google Chat · "content": Discord.
        using var resp = await Http.PostAsJsonAsync(uri, new { text, content = text });
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Webhook trả về {(int)resp.StatusCode}: {await ErrorText(resp)}");
    }

    /// <summary>Máy chủ ngay trên máy này (localhost, 127.x, ::1).</summary>
    internal static bool IsLoopback(string host)
    {
        host = host.Trim().Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>Nội dung lỗi máy chủ trả về: đã che bí mật, tối đa 300 ký tự.</summary>
    private static async Task<string> ErrorText(HttpResponseMessage resp)
    {
        string text;
        try { text = await resp.Content.ReadAsStringAsync(); }
        catch (HttpRequestException) { return "(không đọc được nội dung)"; }
        return ApiClient.Short(Log.Redact(text));
    }
}
