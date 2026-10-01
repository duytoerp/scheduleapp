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

    /// <summary>Trình ghi macro: ghi click vào nút/ô nhập thành "Click phần tử UI" thay vì tọa độ.</summary>
    public bool RecordElements { get; set; } = true;

    /// <summary>Cổng remote debugging của Chrome/Edge cho các bước "Trình duyệt".</summary>
    public int BrowserPort { get; set; } = 9222;

    /// <summary>
    /// Ngày nghỉ: "dd/MM" lặp lại hằng năm hoặc "dd/MM/yyyy" cho một ngày cụ thể (Tết âm lịch, Giỗ Tổ…).
    /// </summary>
    public List<string> Holidays { get; set; } = ["01/01", "30/04", "01/05", "02/09"];

    public TelegramSettings Telegram { get; set; } = new();
    public EmailSettings Email { get; set; } = new();
    public WebhookSettings Webhook { get; set; } = new();

    /// <summary>Kết nối API dùng cho bước "Gọi API" (URL gốc + cách xác thực).</summary>
    public List<ApiConnection> ApiConnections { get; set; } = [];

    public AiSettings Ai { get; set; } = new();

    /// <summary>Hộp thư theo dõi cho trình kích hoạt "Có email mới".</summary>
    public MailInboxSettings Inbox { get; set; } = new();

    public UpdateSettings Update { get; set; } = new();

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

    /// <summary>Nhận lệnh điều khiển từ Telegram (/run, /stop, /status…) — chỉ từ đúng chat id ở trên.</summary>
    public bool AllowCommands { get; set; }
}

public enum ApiAuthType
{
    None,
    /// <summary>Header Authorization: Bearer &lt;token&gt;.</summary>
    Bearer,
    /// <summary>Tên đăng nhập + mật khẩu (HTTP Basic).</summary>
    Basic,
    /// <summary>Khóa API trong một header (vd x-api-key).</summary>
    ApiKey,
    /// <summary>Tài khoản Windows đang đăng nhập (NTLM/Kerberos — Dynamics 365 on-premises, SharePoint nội bộ…).</summary>
    Windows,
    /// <summary>Microsoft Entra ID (Azure AD) client credentials — Dynamics 365 / Dataverse, Microsoft Graph.</summary>
    EntraId,
    /// <summary>OAuth 2.0 client credentials với URL lấy token tùy ý.</summary>
    OAuthClientCredentials
}

/// <summary>Kết nối API dùng lại cho nhiều bước: URL gốc, xác thực, header mặc định.</summary>
public sealed class ApiConnection
{
    public string Name { get; set; } = "";

    /// <summary>URL gốc, vd https://contoso.crm5.dynamics.com/api/data/v9.2/ — URL trong bước được nối vào sau.</summary>
    public string BaseUrl { get; set; } = "";

    public ApiAuthType Auth { get; set; } = ApiAuthType.None;

    /// <summary>Basic: tên đăng nhập · ApiKey: tên header · EntraId/OAuth: client id.</summary>
    public string User { get; set; } = "";

    /// <summary>Mật khẩu / token / khóa API / client secret (mã hóa DPAPI).</summary>
    public string Secret { get; set; } = "";

    /// <summary>EntraId: tenant id hoặc tên miền (contoso.onmicrosoft.com).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>EntraId/OAuth: scope; trống = &lt;gốc của BaseUrl&gt;/.default.</summary>
    public string Scope { get; set; } = "";

    /// <summary>OAuth: URL lấy token.</summary>
    public string TokenUrl { get; set; } = "";

    /// <summary>Header gửi kèm mọi lần gọi, mỗi dòng "Tên: giá trị".</summary>
    public string Headers { get; set; } = "";
}

public sealed class AiSettings
{
    /// <summary>Khóa API Anthropic (mã hóa DPAPI).</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-opus-5-5";
    public int MaxTokens { get; set; } = 2048;
}

public enum MailSource
{
    /// <summary>Outlook cài trên máy (đọc qua Outlook, không cần mật khẩu).</summary>
    Outlook,
    Imap
}

public sealed class MailInboxSettings
{
    public MailSource Source { get; set; } = MailSource.Outlook;
    public string Host { get; set; } = "imap.gmail.com";
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string User { get; set; } = "";
    /// <summary>Mật khẩu ứng dụng (mã hóa DPAPI).</summary>
    public string Password { get; set; } = "";
    /// <summary>Thư mục theo dõi; trống = Hộp thư đến.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Đánh dấu đã đọc email đã xử lý (để không chạy lại lần sau).</summary>
    public bool MarkAsRead { get; set; } = true;
}

public sealed class UpdateSettings
{
    /// <summary>
    /// Nguồn bản mới: "github:chủ/repo" (GitHub Releases), URL tới version.json, hoặc thư mục dùng chung (\\máy\thư mục) chứa version.json.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>Token GitHub cho repo riêng tư (mã hóa DPAPI).</summary>
    public string Token { get; set; } = "";

    public bool CheckOnStartup { get; set; } = true;

    /// <summary>Phiên bản người dùng đã chọn bỏ qua.</summary>
    public string SkippedVersion { get; set; } = "";
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
