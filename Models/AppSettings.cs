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

    /// <summary>Hiện khung trạng thái ở góc phải dưới màn hình khi flow đang chạy (bước đang chạy, Tạm dừng / Dừng).</summary>
    public bool ShowRunOverlay { get; set; } = true;

    /// <summary>Không cho máy ngủ / tắt màn hình khi flow đang chạy.</summary>
    public bool PreventSleepWhileRunning { get; set; } = true;

    /// <summary>Trình ghi macro: ghi click vào nút/ô nhập thành "Click phần tử UI" thay vì tọa độ.</summary>
    public bool RecordElements { get; set; } = true;

    /// <summary>Trình ghi macro: chụp hình mẫu quanh mỗi click để khi chạy tìm lại theo hình ảnh rồi mới dùng tọa độ.</summary>
    public bool RecordImages { get; set; } = true;

    /// <summary>Trình ghi macro: lưu ảnh cả cửa sổ ứng dụng lúc mỗi click (đánh dấu chỗ click) để xem lại bước.</summary>
    public bool RecordWindowShots { get; set; } = true;

    /// <summary>
    /// Không còn dùng: trình duyệt điều khiển tự chọn cổng ngẫu nhiên (--remote-debugging-port=0).
    /// Giữ lại để settings.json của bản cũ vẫn đọc / ghi lại nguyên vẹn.
    /// </summary>
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

    /// <summary>
    /// Giá trị biến đã điền ở màn hình "Thiết lập mẫu" (vd tenant, d365Url) — tự điền sẵn khi thêm mẫu khác dùng cùng biến.
    /// Lưu chữ thường (biến của mẫu không đánh dấu được là mật khẩu) — mật khẩu nên để trong 🔑 Bí mật và dùng {{secret:Tên}}.
    /// </summary>
    public Dictionary<string, string> TemplateValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AiSettings Ai { get; set; } = new();

    /// <summary>Hộp thư theo dõi cho trình kích hoạt "Có email mới".</summary>
    public MailInboxSettings Inbox { get; set; } = new();

    public UpdateSettings Update { get; set; } = new();

    /// <summary>
    /// Tên máy trang đăng nhập riêng của tổ chức (ADFS / SSO, vd adfs.contoso.com) được nhận mật khẩu / mã TOTP của bước
    /// "Đăng nhập Microsoft" — ngoài login.microsoftonline.com và login.microsoft.com. Chỉ qua https.
    /// </summary>
    public List<string> TrustedLoginHosts { get; set; } = [];

    /// <summary>Lần cuối ScheduleApp còn chạy (giờ địa phương, để hiển thị / bản cũ) — dùng để phát hiện lịch bị lỡ khi app tắt.</summary>
    public DateTime? LastAlive { get; set; }

    /// <summary>Như <see cref="LastAlive"/> nhưng theo UTC — đổi múi giờ giữa hai lần mở app vẫn tính đúng lịch bị lỡ.</summary>
    public DateTime? LastAliveUtc { get; set; }

    /// <summary>Môi trường kiểm thử (Dev / Test / UAT…): mỗi môi trường là một bộ biến ghi đè biến của kịch bản.</summary>
    public List<TestEnvironment> Environments { get; set; } = [];

    /// <summary>Môi trường đang chọn trên trang Kiểm thử (trống = không dùng môi trường).</summary>
    public string CurrentEnvironment { get; set; } = "";

    /// <summary>Thư mục kịch bản kiểm thử dùng cho Xuất / Nhập (vd thư mục trong repo git).</summary>
    public string TestFolder { get; set; } = "";

    /// <summary>Cách nhìn flow trong trình soạn công việc: "graph" (sơ đồ), "list" (danh sách thụt lề) hoặc "tree" (cây).</summary>
    public string FlowView { get; set; } = "graph";

    /// <summary>Vị trí / kích thước cửa sổ đã nhớ (cửa sổ chính, trình soạn công việc…) — theo tỉ lệ màn hình, không theo pixel.</summary>
    public Dictionary<string, WindowLayout> Windows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Phiên bản cấu trúc settings.json — để chuyển đổi cài đặt cũ đúng một lần (xem SettingsStore.Upgrade).
    /// File cũ không có mục này → 0. Mặc định 0 cả khi tạo mới: file chưa có cũng đi qua bước chuyển đổi (không đổi gì).
    /// </summary>
    public int SettingsVersion { get; set; }
}

/// <summary>
/// Môi trường kiểm thử: tên + các biến (vd d365Url, tài khoản test) dùng chung cho mọi kịch bản khi chạy ở môi trường này.
/// Giá trị lưu chữ thường (còn xuất ra thư mục kiểm thử) — mật khẩu ghi dạng {{secret:Tên}}, được thay khi chạy.
/// </summary>
/// <summary>
/// Vị trí / kích thước cửa sổ theo tỉ lệ vùng làm việc của màn hình (0–1) — mở lại trên màn hình / máy có độ phân giải khác vẫn
/// chiếm đúng phần đó của màn hình.
/// </summary>
public sealed class WindowLayout
{
    /// <summary>Tên màn hình (\\.\DISPLAY1…); không còn màn hình đó thì dùng màn hình chính.</summary>
    public string Screen { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public bool Maximized { get; set; }
}

public sealed class TestEnvironment
{
    public string Name { get; set; } = "";
    public List<VariableDef> Variables { get; set; } = [];
}

public sealed class TelegramSettings
{
    public bool Enabled { get; set; }
    /// <summary>Token bot (mã hóa DPAPI).</summary>
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";

    /// <summary>Kèm ảnh chụp màn hình lỗi (cả màn hình, có thể lộ thông tin khác) — mặc định tắt, người dùng tự bật.</summary>
    public bool SendScreenshot { get; set; }

    /// <summary>Nhận lệnh điều khiển từ Telegram (/run, /stop, /status…) — chỉ từ đúng chat id ở trên.</summary>
    public bool AllowCommands { get; set; }

    /// <summary>
    /// Không an toàn: công việc tạo qua Telegram chạy ngay, không cần duyệt trên máy. Mặc định tắt — công việc từ Telegram
    /// chờ duyệt (<see cref="Job.NeedsApproval"/>).
    /// </summary>
    public bool RunWithoutApproval { get; set; }
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

    /// <summary>Header gửi kèm mọi lần gọi, mỗi dòng "Tên: giá trị" — cả chuỗi mã hóa DPAPI (thường chứa khóa / token).</summary>
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
    /// <summary>
    /// IMAP không SSL: vẫn đăng nhập khi máy chủ không hỗ trợ STARTTLS (mật khẩu đi qua mạng không mã hóa). Mặc định tắt — khi tắt,
    /// máy chủ không mã hóa được thì báo lỗi thay vì gửi mật khẩu (máy chủ localhost luôn được phép).
    /// </summary>
    public bool AllowPlaintext { get; set; }
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

    /// <summary>Đính kèm ảnh chụp màn hình lỗi (cả màn hình, có thể lộ thông tin khác) — mặc định tắt, người dùng tự bật.</summary>
    public bool AttachScreenshot { get; set; }

    /// <summary>
    /// Cho phép gửi qua máy chủ SMTP không có TLS (máy chủ chuyển tiếp nội bộ cũ, cổng 25) — ô "Cho phép gửi không mã hóa" trong
    /// ⚙ Cài đặt → Thông báo. Mặc định bắt buộc SSL/TLS, trừ máy chủ trên chính máy này (localhost).
    /// </summary>
    public bool AllowNoTls { get; set; }
}

public sealed class WebhookSettings
{
    public bool Enabled { get; set; }
    /// <summary>URL webhook Teams / Slack / Discord / Google Chat… (mã hóa DPAPI — URL chứa sẵn khóa gửi tin).</summary>
    public string Url { get; set; } = "";
}
