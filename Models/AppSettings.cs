namespace ScheduleApp.Models;

/// <summary>Cài đặt chung của ứng dụng (settings.json). Mật khẩu/token được mã hóa bằng DPAPI.</summary>
public sealed class AppSettings
{
    /// <summary>Chụp màn hình khi một bước bị lỗi.</summary>
    public bool ScreenshotOnError { get; set; } = true;

    /// <summary>Tự xóa ảnh chụp lỗi cũ hơn N ngày.</summary>
    public int KeepScreenshotsDays { get; set; } = 30;

    /// <summary>Tạm dừng flow khi người dùng dùng chuột/bàn phím trong lúc flow đang chạy.</summary>
    public bool SafeMode { get; set; }

    /// <summary>Không cho máy ngủ / tắt màn hình khi flow đang chạy.</summary>
    public bool PreventSleepWhileRunning { get; set; } = true;

    /// <summary>Cổng remote debugging của Chrome/Edge cho các bước "Trình duyệt".</summary>
    public int BrowserPort { get; set; } = 9222;

    /// <summary>
    /// Ngày nghỉ: "dd/MM" lặp lại hằng năm hoặc "dd/MM/yyyy" cho một ngày cụ thể (Tết âm lịch, Giỗ Tổ…).
    /// </summary>
    public List<string> Holidays { get; set; } = ["01/01", "30/04", "01/05", "02/09"];

    public TelegramSettings Telegram { get; set; } = new();
    public EmailSettings Email { get; set; } = new();
    public WebhookSettings Webhook { get; set; } = new();

    /// <summary>Lần cuối ScheduleApp còn chạy — dùng để phát hiện lịch bị lỡ khi app tắt.</summary>
    public DateTime? LastAlive { get; set; }
}

public sealed class TelegramSettings
{
    public bool Enabled { get; set; }
    /// <summary>Token bot (mã hóa DPAPI).</summary>
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
    public bool SendScreenshot { get; set; } = true;
}

public sealed class EmailSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string User { get; set; } = "";
    /// <summary>Mật khẩu (mã hóa DPAPI).</summary>
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public bool AttachScreenshot { get; set; } = true;
}

public sealed class WebhookSettings
{
    public bool Enabled { get; set; }
    /// <summary>URL webhook Teams / Slack / Discord / Google Chat…</summary>
    public string Url { get; set; } = "";
}
