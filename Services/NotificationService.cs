using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Http.Json;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>Gửi kết quả chạy flow ra ngoài: Telegram, email (SMTP), webhook (Teams / Slack / Discord / Google Chat).</summary>
public static class NotificationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

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

    /// <summary>Gửi qua mọi kênh đang bật. Lỗi được ghi log và trả về (không ném ra ngoài).</summary>
    public static async Task<List<string>> SendAsync(string title, string body, string? screenshot)
    {
        var s = SettingsStore.Current;
        var tasks = new List<Task<string?>>();
        if (s.Telegram.Enabled) tasks.Add(Guard("Telegram", () => SendTelegramAsync(s.Telegram, title, body, screenshot)));
        if (s.Email.Enabled) tasks.Add(Guard("Email", () => SendEmailAsync(s.Email, title, body, screenshot)));
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
            Log.Error($"Không gửi được thông báo {channel}: {ex.Message}");
            return $"{channel}: {ex.Message}";
        }
    }

    public static async Task SendTelegramAsync(TelegramSettings t, string title, string body, string? screenshot)
    {
        var token = Protector.Unprotect(t.BotToken);
        if (token.Length == 0 || t.ChatId.Trim().Length == 0) throw new InvalidOperationException("Chưa nhập token bot hoặc chat id.");
        var api = $"https://api.telegram.org/bot{token}";
        var text = $"{title}\n{body}";

        HttpResponseMessage resp;
        if (t.SendScreenshot && screenshot != null && File.Exists(screenshot))
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
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Telegram trả về {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    public static async Task SendEmailAsync(EmailSettings e, string title, string body, string? screenshot)
    {
        if (string.IsNullOrWhiteSpace(e.Host) || string.IsNullOrWhiteSpace(e.To)) throw new InvalidOperationException("Chưa nhập máy chủ SMTP hoặc người nhận.");
        var from = string.IsNullOrWhiteSpace(e.From) ? e.User : e.From;
        using var msg = new MailMessage { From = new MailAddress(from), Subject = "[ScheduleApp] " + title, Body = body };
        foreach (var to in e.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) msg.To.Add(to);
        if (e.AttachScreenshot && screenshot != null && File.Exists(screenshot)) msg.Attachments.Add(new Attachment(screenshot));

        using var smtp = new SmtpClient(e.Host.Trim(), e.Port) { EnableSsl = e.UseSsl, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrWhiteSpace(e.User)) smtp.Credentials = new NetworkCredential(e.User.Trim(), Protector.Unprotect(e.Password));
        await smtp.SendMailAsync(msg);
    }

    public static async Task SendWebhookAsync(WebhookSettings w, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(w.Url)) throw new InvalidOperationException("Chưa nhập URL webhook.");
        var text = $"{title}\n{body}";
        // "text": Teams / Slack / Google Chat · "content": Discord.
        var resp = await Http.PostAsJsonAsync(w.Url.Trim(), new { text, content = text });
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Webhook trả về {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }
}
