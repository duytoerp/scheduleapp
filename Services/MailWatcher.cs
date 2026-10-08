using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>Một email mới đã tải về (file đính kèm đã lưu vào thư mục riêng).</summary>
public sealed record IncomingMail(string Id, string Subject, string From, string FromName, string Body, DateTime Received, List<string> Attachments, string AttachmentDir)
{
    /// <summary>Kết quả xác thực người gửi (DMARC / DKIM / SPF) theo máy chủ nhận thư.</summary>
    public MailAuth Auth { get; init; }

    /// <summary>Biến truyền vào flow: {{email.subject}}, {{email.from}}, {{email.attachments}}…</summary>
    public Dictionary<string, string> ToVariables() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["email.subject"] = Subject,
        ["email.from"] = From,
        ["email.fromName"] = FromName,
        ["email.body"] = Body,
        ["email.date"] = Received.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
        ["email.attachments"] = string.Join("\n", Attachments),
        ["email.attachmentCount"] = Attachments.Count.ToString(CultureInfo.InvariantCulture),
        ["email.attachmentDir"] = AttachmentDir
    };
}

/// <summary>Người gửi có thật là chủ tên miền trong địa chỉ "From" không — theo header Authentication-Results của máy chủ nhận thư.</summary>
public enum MailAuth
{
    /// <summary>Không rõ: không có kết quả xác thực nào cho tên miền người gửi.</summary>
    Unknown,
    /// <summary>DMARC đạt, hoặc DKIM / SPF đạt cho đúng tên miền người gửi.</summary>
    Pass,
    /// <summary>DMARC trượt, hoặc (không có DMARC) DKIM / SPF trượt cho tên miền người gửi — nhiều khả năng là thư giả mạo.</summary>
    Fail
}

/// <summary>Bộ lọc email của một trình kích hoạt.</summary>
/// <param name="Subject">Tiêu đề chứa (không phân biệt hoa thường và dấu); trống = mọi thư.</param>
/// <param name="From">Địa chỉ người gửi (ketoan@congty.vn) hoặc tên miền (@congty.vn), nhiều mục cách nhau bởi dấu ; — trống = mọi người.</param>
/// <param name="RequireAuthenticated">Chỉ nhận thư đã xác thực (<see cref="MailAuth.Pass"/>).</param>
public sealed record MailFilter(string Subject, string From, bool RequireAuthenticated)
{
    public static MailFilter Of(JobTrigger t) => new(t.Value, t.Value2, t.RequireAuthenticatedEmail);
}

/// <summary>Kết cục xử lý một email — quyết định có đánh dấu đã đọc không.</summary>
internal enum MailOutcome
{
    /// <summary>Đã xử lý xong (đính kèm đã lưu), công việc sẽ chạy.</summary>
    Processed,
    /// <summary>Bị từ chối có chủ đích (nghi giả mạo / chưa xác thực) — đã ghi nhật ký.</summary>
    Rejected,
    /// <summary>Lỗi khi xử lý riêng thư này (vd không lưu được đính kèm) — đã ghi nhật ký.</summary>
    Failed
}

/// <summary>
/// Đọc email chưa đọc từ Outlook trên máy (qua COM, không cần mật khẩu) hoặc hộp thư IMAP (Gmail, Outlook.com…),
/// lưu file đính kèm và đánh dấu đã đọc. Dùng cho trình kích hoạt "Có email mới".
/// </summary>
public static partial class MailWatcher
{
    /// <summary>Chỉ xét email nhận trong khoảng này (tránh chạy lại cho thư cũ chưa đọc).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);

    private static readonly DateTime StartedAt = DateTime.Now;
    private static readonly HashSet<string> Processed = [];

    /// <summary>Trình kích hoạt rủi ro đã cảnh báo trong phiên này (mỗi trình kích hoạt một lần).</summary>
    private static readonly HashSet<string> Warned = [];

    public static string AttachmentRoot => Path.Combine(JobStore.DataDir, "email");

    /// <summary>
    /// Lấy các email chưa đọc khớp ít nhất một bộ lọc; lưu đính kèm, đánh dấu đã đọc nếu cài đặt bật. Lỗi của một thư không chặn các thư khác.
    /// </summary>
    public static async Task<List<IncomingMail>> FetchAsync(IReadOnlyList<MailFilter> filters, CancellationToken ct)
    {
        var s = SettingsStore.Current.Inbox;
        var since = s.MarkAsRead ? DateTime.Now - MaxAge : StartedAt; // không đánh dấu đã đọc → chỉ lấy thư đến sau khi mở app

        var result = s.Source == MailSource.Imap
            ? await FetchImapAsync(s, since, filters, ct)
            : await RunSta(() => FetchOutlook(s, since, filters));
        lock (Processed) result.RemoveAll(m => !Processed.Add(m.Id));
        return result;
    }

    /// <summary>Thử kết nối — trả về mô tả số thư chưa đọc.</summary>
    public static async Task<string> TestAsync(MailInboxSettings s, CancellationToken ct)
    {
        if (s.Source == MailSource.Imap)
        {
            using var client = await ConnectImapAsync(s, ct);
            var folder = await OpenFolderAsync(client, s, FolderAccess.ReadOnly, ct);
            var unread = await folder.SearchAsync(SearchQuery.NotSeen, ct);
            await client.DisconnectAsync(true, ct);
            return $"Kết nối IMAP thành công — thư mục \"{folder.FullName}\" có {unread.Count} thư chưa đọc.";
        }
        return await RunSta(() =>
        {
            dynamic app = OutlookApp();
            dynamic folder = OutlookFolder(app.GetNamespace("MAPI"), s.Folder);
            int count = folder.UnReadItemCount;
            return $"Đọc được Outlook — thư mục \"{folder.Name}\" có {count} thư chưa đọc.";
        });
    }

    /// <summary>
    /// Email khớp bộ lọc của một trình kích hoạt: tiêu đề chứa (không phân biệt hoa thường và dấu), đúng địa chỉ người gửi (không xét
    /// tên hiển thị — ai cũng đặt được), không bị máy chủ xác định là giả mạo, và đã xác thực nếu bộ lọc yêu cầu.
    /// </summary>
    public static bool Matches(IncomingMail m, MailFilter f) =>
        MatchesIgnoringAuth(m.Subject, m.From, f) && m.Auth != MailAuth.Fail && (!f.RequireAuthenticated || m.Auth == MailAuth.Pass);

    private static bool MatchesIgnoringAuth(string subject, string from, MailFilter f) => Contains(subject, f.Subject) && SenderMatches(from, f.From);

    private static bool Contains(string text, string filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        Vision.ScreenOcr.RemoveDiacritics(text).Contains(Vision.ScreenOcr.RemoveDiacritics(filter.Trim()), StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────── Người gửi ─────────────────────────────

    private static readonly char[] SenderSeparators = [';', ',', ' ', '\t', '\r', '\n'];

    /// <summary>
    /// Địa chỉ người gửi khớp bộ lọc: trống = mọi người; mỗi mục là địa chỉ đầy đủ (so đúng cả địa chỉ) hoặc tên miền "@congty.vn"
    /// (so đúng tên miền — "a@congty.vn.evil.com" không khớp). Không so tên hiển thị và không so kiểu "chứa".
    /// </summary>
    public static bool SenderMatches(string address, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var addr = address.Trim().ToLowerInvariant();
        var domain = DomainOf(addr);
        if (domain.Length == 0 || addr.IndexOf('@') <= 0) return false;
        return SenderEntries(filter, out _).Any(e => e.StartsWith('@') ? domain == e[1..] : addr == e);
    }

    /// <summary>
    /// Các mục hợp lệ của bộ lọc người gửi (chữ thường; tên miền dạng "@tênmiền", viết thiếu @ như "congty.vn" cũng coi là tên miền);
    /// <paramref name="invalid"/> = mục không phải địa chỉ / tên miền (vd tên hiển thị "Phòng kế toán").
    /// </summary>
    internal static List<string> SenderEntries(string filter, out List<string> invalid)
    {
        var entries = new List<string>();
        invalid = [];
        foreach (var raw in filter.Split(SenderSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var e = raw.Trim().Trim('<', '>', '"', '\'').ToLowerInvariant();
            int at = e.LastIndexOf('@');
            var domain = at < 0 ? e : e[(at + 1)..];
            if (!IsDomain(domain) || (at > 0 && e[..at].Contains('@'))) invalid.Add(raw);
            else entries.Add(at > 0 ? e : "@" + domain);
        }
        return entries;
    }

    private static bool IsDomain(string d) =>
        d.Length is > 2 and < 254 && d.Contains('.') && !d.StartsWith('.') && !d.EndsWith('.') && !d.Contains("..") &&
        d.All(c => char.IsLetterOrDigit(c) || c is '-' or '.');

    /// <summary>Lỗi của bộ lọc người gửi (để trình soạn báo); null = hợp lệ.</summary>
    public static string? SenderFilterError(string filter)
    {
        SenderEntries(filter, out var invalid);
        return invalid.Count == 0 ? null
            : "Người gửi chỉ nhận địa chỉ email (vd ketoan@congty.vn) hoặc tên miền (vd @congty.vn), nhiều mục cách nhau bởi dấu ; — " +
              $"không dùng tên hiển thị vì ai cũng đặt được. Mục không hợp lệ: {string.Join(", ", invalid.Select(x => $"\"{x}\""))}.";
    }

    private static readonly StepType[] RiskySteps = [StepType.RunCommand, StepType.LaunchApp, StepType.TypeText];

    /// <summary>
    /// Cảnh báo khi trình kích hoạt email không giới hạn người gửi mà công việc có bước Chạy lệnh / Mở ứng dụng / Gõ chữ: bất kỳ ai gửi
    /// thư tới hộp thư cũng chạy được công việc, và nội dung thư ({{email.*}}) có thể lọt vào lệnh. Null = không có gì đáng ngại.
    /// </summary>
    public static string? OpenSenderWarning(JobTrigger t, IEnumerable<ActionStep> steps)
    {
        if (t.Type != TriggerType.EmailReceived || !string.IsNullOrWhiteSpace(t.Value2)) return null;
        var risky = steps.Where(s => s.Enabled && RiskySteps.Contains(s.Type)).Select(s => ActionStep.TypeNames[s.Type]).Distinct().ToList();
        return risky.Count == 0 ? null
            : "Trình kích hoạt email không giới hạn người gửi — bất kỳ ai gửi thư tới hộp thư này cũng chạy được công việc, mà công việc có bước " +
              $"\"{string.Join("\", \"", risky)}\" (nội dung thư như {{{{email.subject}}}}, {{{{email.body}}}} có thể lọt vào lệnh). " +
              "Hãy nhập người gửi (vd ketoan@congty.vn hoặc @congty.vn) và bật \"Chỉ nhận email đã xác thực\".";
    }

    /// <summary>Ghi nhật ký (mỗi trình kích hoạt một lần trong phiên) trình kích hoạt email rủi ro: không giới hạn người gửi, bộ lọc người gửi không hợp lệ.</summary>
    public static void WarnRiskyTriggers(IEnumerable<(Job Job, JobTrigger Trigger)> triggers)
    {
        foreach (var (job, t) in triggers)
        {
            var problems = new[] { OpenSenderWarning(t, job.Steps), SenderFilterError(t.Value2) }.OfType<string>().ToList();
            if (problems.Count == 0) continue;
            lock (Warned) if (!Warned.Add($"{job.Id}|{t.Value}|{t.Value2}")) continue;
            foreach (var p in problems) Log.Warn($"✉ Công việc \"{job.Name}\": {p}");
        }
    }

    // ───────────────────────────── Xác thực người gửi ─────────────────────────────

    private sealed record AuthEntry(string Method, string Result, Dictionary<string, string> Props)
    {
        public string? Prop(string key) => Props.GetValueOrDefault(key);
    }

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex AuthComment();

    /// <summary>
    /// Xác thực người gửi theo các header của thư (Authentication-Results đầu tiên, X-MS-Exchange-Organization-AuthAs).
    /// AuthAs chỉ được tin khi <paramref name="exchangeMailbox"/>: Exchange xóa header này ở thư từ ngoài vào, còn Gmail / IMAP thường
    /// giữ nguyên — người gửi tự chèn "AuthAs: Internal" là qua mặt được "chỉ nhận email đã xác thực".
    /// </summary>
    internal static (MailAuth Result, string Detail) EvaluateAuthentication(HeaderList headers, string fromAddress, bool exchangeMailbox) =>
        EvaluateAuthentication(headers[HeaderId.AuthenticationResults], exchangeMailbox ? headers["X-MS-Exchange-Organization-AuthAs"] : null, fromAddress);

    /// <summary>Máy chủ IMAP của Exchange Online / Outlook.com (outlook.office365.com, outlook.office.com, imap-mail.outlook.com…).</summary>
    internal static bool IsExchangeOnlineHost(string? host)
    {
        var h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        return h is "office365.com" or "outlook.com" or "office.com"
            || h.EndsWith(".office365.com", StringComparison.Ordinal) || h.EndsWith(".outlook.com", StringComparison.Ordinal)
            || h.EndsWith(".office.com", StringComparison.Ordinal);
    }

    /// <summary>
    /// Đánh giá người gửi theo header Authentication-Results ĐẦU TIÊN — do máy chủ nhận thư của bạn thêm lên trên cùng; các header
    /// phía dưới có thể do chính người gửi chèn vào nên không tin. DMARC (hoặc compauth của Microsoft) quyết định nếu có; không có thì
    /// xét DKIM / SPF của đúng tên miền người gửi. <paramref name="exchangeAuthAs"/> = "Internal" → thư nội bộ Exchange đã đăng nhập.
    /// </summary>
    internal static (MailAuth Result, string Detail) EvaluateAuthentication(string? authResults, string? exchangeAuthAs, string fromAddress)
    {
        var fromDomain = DomainOf(fromAddress);
        if (fromDomain.Length == 0) return (MailAuth.Unknown, "thiếu địa chỉ người gửi");
        var entries = ParseAuthResults(authResults ?? "");
        bool Aligned(string? d) =>
            d is { Length: > 0 } && (d == fromDomain || fromDomain.EndsWith("." + d, StringComparison.Ordinal) || d.EndsWith("." + fromDomain, StringComparison.Ordinal));

        foreach (var method in new[] { "dmarc", "compauth" })
        {
            var list = entries.Where(e => e.Method == method).ToList();
            if (list.Any(e => e.Result == "fail")) return (MailAuth.Fail, method + "=fail");
            if (list.Any(e => e.Result is "pass" or "bestguesspass" && (e.Prop("header.from") is not { } hf || Aligned(DomainOf(hf)))))
                return (MailAuth.Pass, method + "=pass");
        }
        if (string.Equals(exchangeAuthAs?.Trim(), "Internal", StringComparison.OrdinalIgnoreCase)) return (MailAuth.Pass, "thư nội bộ Exchange");

        var dkim = entries.Where(e => e.Method == "dkim").Select(e => (e.Result, Domain: DomainOf(e.Prop("header.d") ?? e.Prop("header.i") ?? ""))).ToList();
        var spf = entries.Where(e => e.Method == "spf").Select(e => (e.Result, Domain: DomainOf(e.Prop("smtp.mailfrom") ?? ""))).ToList();
        if (dkim.Any(x => x.Result == "pass" && Aligned(x.Domain))) return (MailAuth.Pass, "dkim=pass");
        if (spf.Any(x => x.Result == "pass" && Aligned(x.Domain))) return (MailAuth.Pass, "spf=pass");
        if (dkim.Any(x => x.Result == "fail" && Aligned(x.Domain))) return (MailAuth.Fail, "dkim=fail");
        if (spf.Any(x => x.Result == "fail" && Aligned(x.Domain))) return (MailAuth.Fail, "spf=fail");
        return (MailAuth.Unknown, entries.Count == 0 ? "không có kết quả xác thực" : $"không có DMARC / DKIM / SPF đạt cho {fromDomain}");
    }

    /// <summary>"mx.google.com; dkim=pass header.d=a.vn; spf=fail (…) smtp.mailfrom=b@a.vn" → các mục (phương thức, kết quả, thuộc tính).</summary>
    private static List<AuthEntry> ParseAuthResults(string header)
    {
        var text = header.Replace('\r', ' ').Replace('\n', ' ');
        for (string prev = ""; prev != text;) (prev, text) = (text, AuthComment().Replace(text, " "));
        var result = new List<AuthEntry>();
        foreach (var part in text.Split(';'))
        {
            AuthEntry? entry = null;
            foreach (var token in part.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0) continue;
                var key = token[..eq].ToLowerInvariant();
                var value = token[(eq + 1)..].Trim('"').ToLowerInvariant();
                if (entry == null)
                {
                    // Mục đầu tiên của mỗi phần (sau dấu ;) là "phương thức=kết quả"; "action=", "reason=" phía sau bị bỏ qua.
                    if (key.Contains('.')) continue;
                    entry = new AuthEntry(key.Split('/')[0], value, new(StringComparer.Ordinal));
                    result.Add(entry);
                }
                else if (key.Contains('.')) entry.Props.TryAdd(key, value);
            }
        }
        return result;
    }

    /// <summary>"a@Cong.Ty.vn" / "@cong.ty.vn" / "cong.ty.vn" → "cong.ty.vn".</summary>
    private static string DomainOf(string value)
    {
        var v = value.Trim().Trim('<', '>', '"').ToLowerInvariant();
        return v[(v.LastIndexOf('@') + 1)..].TrimEnd('.');
    }

    /// <summary>Đọc khối header thô (Outlook: PR_TRANSPORT_MESSAGE_HEADERS); không đọc được → danh sách trống.</summary>
    internal static HeaderList ParseHeaders(string raw)
    {
        try
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw.TrimEnd() + "\r\n\r\n"));
            return HeaderList.Load(ms);
        }
        catch (FormatException)
        {
            return new HeaderList();
        }
    }

    /// <summary>
    /// Thư có chạy công việc không (đã khớp tiêu đề + người gửi của ít nhất một bộ lọc). Thư bị loại vì xác thực được ghi nhật ký —
    /// email.* sẽ chảy vào các bước của công việc nên không chạy với thư giả mạo.
    /// </summary>
    private static bool Accept(IncomingMail m, IReadOnlyList<MailFilter> filters, string authDetail)
    {
        var candidates = filters.Where(f => MatchesIgnoringAuth(m.Subject, m.From, f)).ToList();
        if (candidates.Count == 0) return false;
        if (candidates.Any(f => Matches(m, f))) return true;
        Log.Warn(m.Auth == MailAuth.Fail
            ? $"✉ Bỏ qua email \"{m.Subject}\" từ {m.From}: người gửi không qua xác thực ({authDetail}) — nhiều khả năng là thư giả mạo."
            : $"✉ Bỏ qua email \"{m.Subject}\" từ {m.From}: chưa xác thực được người gửi ({authDetail}) mà trình kích hoạt bật \"Chỉ nhận email đã xác thực\".");
        return false;
    }

    /// <summary>
    /// Chỉ đánh dấu đã đọc thư đã xử lý xong. Thư bị từ chối (nghi giả mạo, chưa xác thực) hoặc bị lỗi đã được ghi nhật ký và để nguyên
    /// chưa đọc để bạn tự xem; trong phiên này app không xét lại các thư đó.
    /// </summary>
    internal static bool ShouldMarkSeen(MailOutcome outcome, bool markAsRead) => markAsRead && outcome == MailOutcome.Processed;

    // ───────────────────────────── File đính kèm ─────────────────────────────

    internal static string NewAttachmentDir(DateTime received, string id)
    {
        var tag = Math.Abs(id.GetHashCode()).ToString("x8", CultureInfo.InvariantCulture);
        return Path.Combine(AttachmentRoot, $"{received:yyyyMMdd-HHmmss}-{tag}");
    }

    /// <summary>Độ dài tối đa của tên file đính kèm đã lưu (NTFS cho tối đa 255; chừa chỗ cho " (2)" và đường dẫn thư mục).</summary>
    internal const int MaxFileNameLength = 120;

    private static readonly HashSet<char> InvalidNameChars = [.. Path.GetInvalidFileNameChars()];

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" }),
         "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tên file an toàn cho file đính kèm: thay ký tự không hợp lệ, dấu phân cách thư mục và ký tự đảo chiều chữ (dùng để giả đuôi file,
    /// vd "hoadon‮fdp.exe"); bỏ dấu chấm / khoảng trắng cuối (".." → tên mặc định); tránh tên thiết bị (CON, NUL…); cắt bớt tên quá dài;
    /// không trùng tên đã dùng.
    /// </summary>
    internal static string SafeFileName(string name, HashSet<string> used)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(InvalidNameChars.Contains(c) || char.IsControl(c) || IsBidiControl(c) ? '_' : c);
        var safe = sb.ToString().Trim().TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "dinh-kem";
        if (ReservedNames.Contains(safe.Split('.')[0].TrimEnd(' '))) safe = "_" + safe;

        var ext = Path.GetExtension(safe);
        if (ext.Length > 20) ext = "";
        safe = Cut(safe[..^ext.Length], MaxFileNameLength - ext.Length) + ext;

        var unique = safe;
        for (int i = 2; !used.Add(unique.ToLowerInvariant()); i++)
            unique = $"{Path.GetFileNameWithoutExtension(safe)} ({i}){Path.GetExtension(safe)}";
        return unique;
    }

    private static bool IsBidiControl(char c) => c is '‎' or '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩';

    private static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        s = s[..max];
        return char.IsHighSurrogate(s[^1]) ? s[..^1] : s;
    }

    /// <summary>
    /// Gắn Mark-of-the-Web (luồng Zone.Identifier, vùng Internet) như trình duyệt làm với file tải về — Windows / Office sẽ cảnh báo
    /// hoặc mở ở chế độ Protected View khi mở file đính kèm.
    /// </summary>
    internal static void MarkFromInternet(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", Encoding.ASCII);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn($"Không gắn được dấu \"tải từ Internet\" cho \"{Path.GetFileName(path)}\" (ổ đĩa không hỗ trợ?): {ex.Message}");
        }
    }

    // ───────────────────────────── IMAP ─────────────────────────────

    /// <summary>
    /// Cách mã hóa kết nối IMAP: "SSL" → mã hóa ngay từ đầu; không SSL → bắt buộc STARTTLS (không gửi mật khẩu dạng chữ thường qua mạng),
    /// trừ máy chủ ngay trên máy này (localhost) hoặc khi đã chọn "Cho phép gửi mật khẩu không mã hóa".
    /// </summary>
    internal static SecureSocketOptions SocketOptions(MailInboxSettings s) =>
        s.UseSsl ? SecureSocketOptions.SslOnConnect
        : s.AllowPlaintext || IsLoopback(s.Host) ? SecureSocketOptions.StartTlsWhenAvailable
        : SecureSocketOptions.StartTls;

    private static bool IsLoopback(string host)
    {
        var h = host.Trim().Trim('[', ']');
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip));
    }

    private static async Task<ImapClient> ConnectImapAsync(MailInboxSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.User))
            throw new InvalidOperationException("Chưa nhập máy chủ IMAP hoặc tài khoản (⚙ Cài đặt → Tích hợp).");
        // Mật khẩu không giải mã được thì không kết nối (đăng nhập bằng mật khẩu rỗng chỉ ra lỗi khó hiểu, có thể bị khóa tài khoản).
        if (!Protector.TryUnprotect(s.Password, out var password))
            throw new InvalidOperationException("Không giải mã được mật khẩu email (dữ liệu chép từ máy / tài khoản Windows khác) — nhập lại trong ⚙ Cài đặt → Tích hợp.");
        var options = SocketOptions(s);
        var client = new ImapClient { Timeout = 60_000 };
        try
        {
            await client.ConnectAsync(s.Host.Trim(), s.Port, options, ct);
            await client.AuthenticateAsync(s.User.Trim(), password, ct);
            return client;
        }
        catch (NotSupportedException ex) when (options == SecureSocketOptions.StartTls)
        {
            client.Dispose();
            throw new InvalidOperationException("Máy chủ IMAP không hỗ trợ mã hóa (STARTTLS) nên ScheduleApp không gửi mật khẩu. Bật SSL (thường là cổng 993), " +
                                                "hoặc chọn \"Cho phép gửi mật khẩu không mã hóa\" trong ⚙ Cài đặt → Tích hợp nếu đây là máy chủ nội bộ tin cậy.", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<IMailFolder> OpenFolderAsync(ImapClient client, MailInboxSettings s, FolderAccess access, CancellationToken ct)
    {
        var folder = string.IsNullOrWhiteSpace(s.Folder) ? client.Inbox : await client.GetFolderAsync(s.Folder.Trim(), ct);
        await folder.OpenAsync(access, ct);
        return folder;
    }

    private static async Task<List<IncomingMail>> FetchImapAsync(MailInboxSettings s, DateTime since, IReadOnlyList<MailFilter> filters, CancellationToken ct)
    {
        var result = new List<IncomingMail>();
        using var client = await ConnectImapAsync(s, ct);
        var folder = await OpenFolderAsync(client, s, s.MarkAsRead ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, ct);
        var uids = await folder.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.DeliveredAfter(since.Date.AddDays(-1))), ct);
        try
        {
            if (uids.Count > 0)
            {
                var summaries = await folder.FetchAsync(uids, MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate, ct);
                foreach (var m in summaries.OrderBy(x => x.InternalDate))
                {
                    if (m.Envelope is not { } env) continue;
                    var received = (m.InternalDate ?? env.Date ?? DateTimeOffset.Now).LocalDateTime;
                    if (received < since) continue;
                    var from = env.From.Mailboxes.FirstOrDefault()?.Address ?? "";
                    var subject = env.Subject ?? "";
                    if (!filters.Any(f => MatchesIgnoringAuth(subject, from, f))) continue;

                    var id = "imap:" + (env.MessageId ?? $"{folder.FullName}/{m.UniqueId}");
                    lock (Processed) if (Processed.Contains(id)) continue;
                    var message = await folder.GetMessageAsync(m.UniqueId, ct);
                    var outcome = await ProcessIsolatedAsync(message, id, received, filters, result, ct, IsExchangeOnlineHost(s.Host));
                    if (ShouldMarkSeen(outcome, s.MarkAsRead)) await folder.AddFlagsAsync(m.UniqueId, MessageFlags.Seen, true, ct);
                }
            }
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && result.Count > 0)
        {
            // Mất kết nối giữa chừng: vẫn chạy công việc cho các thư đã xử lý xong (thư đã đánh dấu đã đọc sẽ không được lấy lại).
            Log.Warn("Kiểm tra email bị gián đoạn: " + ex.Message);
        }
        return result;
    }

    /// <summary>
    /// Xử lý một thư và cô lập lỗi: thư xử lý xong được thêm vào <paramref name="result"/>; lỗi của riêng thư này (vd không lưu được
    /// đính kèm) được ghi nhật ký để các thư sau vẫn chạy. Thư bị từ chối / lỗi không được xét lại trong phiên này.
    /// </summary>
    internal static async Task<MailOutcome> ProcessIsolatedAsync(MimeMessage message, string id, DateTime received, IReadOnlyList<MailFilter> filters,
        List<IncomingMail> result, CancellationToken ct, bool exchangeMailbox = false)
    {
        MailOutcome outcome;
        try
        {
            var mail = await ProcessMessageAsync(message, id, received, filters, ct, exchangeMailbox);
            if (mail != null) result.Add(mail);
            outcome = mail != null ? MailOutcome.Processed : MailOutcome.Rejected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"✉ Không xử lý được email \"{message.Subject}\" từ {message.From.Mailboxes.FirstOrDefault()?.Address}: {ex.Message} — bỏ qua thư này (vẫn để chưa đọc).");
            outcome = MailOutcome.Failed;
        }
        if (outcome != MailOutcome.Processed) lock (Processed) Processed.Add(id);
        return outcome;
    }

    /// <summary>
    /// Xử lý một email đã tải: kiểm tra bộ lọc + xác thực người gửi, lưu đính kèm (tên đã làm sạch, gắn Mark-of-the-Web). Null = không
    /// khớp hoặc bị từ chối (đã ghi nhật ký). Ném lỗi nếu không lưu được đính kèm — người gọi ghi nhật ký rồi xử lý thư tiếp theo.
    /// </summary>
    internal static async Task<IncomingMail?> ProcessMessageAsync(MimeMessage message, string id, DateTime received, IReadOnlyList<MailFilter> filters, CancellationToken ct, bool exchangeMailbox = false)
    {
        var sender = message.From.Mailboxes.FirstOrDefault();
        var from = sender?.Address ?? "";
        var (auth, detail) = EvaluateAuthentication(message.Headers, from, exchangeMailbox);
        var mail = new IncomingMail(id, message.Subject ?? "", from, sender?.Name ?? "", "", received, [], "") { Auth = auth };
        if (!Accept(mail, filters, detail)) return null;

        var dir = NewAttachmentDir(received, id);
        var files = new List<string>();
        var used = new HashSet<string>();
        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SafeFileName(part.FileName ?? "", used));
            await using (var fs = File.Create(path)) if (part.Content != null) await part.Content.DecodeToAsync(fs, ct);
            MarkFromInternet(path);
            files.Add(path);
        }
        var body = message.TextBody ?? HtmlToText(message.HtmlBody ?? "");
        return mail with { Body = body.Trim(), Attachments = files, AttachmentDir = files.Count > 0 ? dir : "" };
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyle();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</tr>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    private static string HtmlToText(string html)
    {
        var text = ScriptStyle().Replace(html, "");
        text = LineBreakTags().Replace(text, "\n");
        text = Tags().Replace(text, "");
        return System.Net.WebUtility.HtmlDecode(text);
    }

    // ───────────────────────────── Outlook (COM) ─────────────────────────────

    private const int OlFolderInbox = 6, OlMailItem = 43, OlByValue = 1;

    /// <summary>COM của Outlook cần luồng STA riêng.</summary>
    private static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                ? new InvalidOperationException("Không đọc được Outlook: " + ex.Message, ex) : ex); }
        }) { IsBackground = true, Name = "Outlook" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static dynamic OutlookApp()
    {
        var type = Type.GetTypeFromProgID("Outlook.Application")
                   ?? throw new InvalidOperationException("Máy chưa cài Outlook (bản cài đặt trên máy). Outlook mới (\"new Outlook\") không hỗ trợ — dùng IMAP.");
        return Activator.CreateInstance(type)!;
    }

    /// <summary>Thư mục theo đường dẫn "Hộp thư đến/Đơn hàng" hoặc "Đơn hàng" (tính từ Hộp thư đến), trống = Hộp thư đến.</summary>
    private static dynamic OutlookFolder(dynamic ns, string path)
    {
        dynamic inbox = ns.GetDefaultFolder(OlFolderInbox);
        if (string.IsNullOrWhiteSpace(path)) return inbox;
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Thử tính từ Hộp thư đến trước, rồi từ gốc hộp thư (để nhập được cả "Hộp thư đến/Đơn hàng").
        foreach (object start in new object[] { inbox, inbox.Parent })
        {
            dynamic? current = start;
            foreach (var part in parts)
            {
                current = Child(current!, part);
                if (current == null) break;
            }
            if (current != null) return current;
        }
        throw new InvalidOperationException($"Không tìm thấy thư mục Outlook \"{path}\".");
    }

    private static dynamic? Child(dynamic folder, string name)
    {
        dynamic folders = folder.Folders;
        int n = folders.Count;
        for (int i = 1; i <= n; i++)
        {
            dynamic f = folders.Item(i);
            if (string.Equals((string)f.Name, name, StringComparison.CurrentCultureIgnoreCase)) return f;
        }
        return null;
    }

    private static List<IncomingMail> FetchOutlook(MailInboxSettings s, DateTime since, IReadOnlyList<MailFilter> filters)
    {
        var result = new List<IncomingMail>();
        dynamic app = OutlookApp();
        dynamic folder = OutlookFolder(app.GetNamespace("MAPI"), s.Folder);
        dynamic items = folder.Items.Restrict("[UnRead] = True");
        var candidates = new List<dynamic>();
        int count = items.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic item = items.Item(i);
            if ((int)item.Class != OlMailItem) continue;
            DateTime received = item.ReceivedTime;
            if (received < since) continue;
            candidates.Add(item);
        }
        // Đánh dấu đã đọc làm thay đổi tập Restrict → duyệt danh sách đã chép.
        foreach (dynamic item in candidates.OrderBy(i => (DateTime)i.ReceivedTime))
        {
            string subject = item.Subject ?? "";
            string fromName = item.SenderName ?? "";
            string from = SenderAddress(item);
            if (!filters.Any(f => MatchesIgnoringAuth(subject, from, f))) continue;
            string id = "outlook:" + (string)item.EntryID;
            lock (Processed) if (Processed.Contains(id)) continue;

            MailOutcome outcome;
            try
            {
                DateTime received = item.ReceivedTime;
                (MailAuth auth, string detail) = OutlookAuthentication((object)item, from);
                var mail = new IncomingMail(id, subject, from, fromName, "", received, [], "") { Auth = auth };
                outcome = Accept(mail, filters, detail) ? MailOutcome.Processed : MailOutcome.Rejected;
                if (outcome == MailOutcome.Processed)
                {
                    var dir = NewAttachmentDir(received, id);
                    var files = new List<string>();
                    var used = new HashSet<string>();
                    dynamic atts = item.Attachments;
                    int n = atts.Count;
                    for (int j = 1; j <= n; j++)
                    {
                        dynamic att = atts.Item(j);
                        if ((int)att.Type != OlByValue || IsHidden(att)) continue; // bỏ ảnh nhúng trong chữ ký
                        Directory.CreateDirectory(dir);
                        var path = Path.Combine(dir, SafeFileName((string)(att.FileName ?? ""), used));
                        att.SaveAsFile(path);
                        MarkFromInternet(path);
                        files.Add(path);
                    }
                    string body = item.Body ?? "";
                    result.Add(mail with { Body = body.Trim(), Attachments = files, AttachmentDir = files.Count > 0 ? dir : "" });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"✉ Không xử lý được email \"{subject}\" từ {from}: {ex.Message} — bỏ qua thư này (vẫn để chưa đọc).");
                outcome = MailOutcome.Failed;
            }
            if (outcome != MailOutcome.Processed) lock (Processed) Processed.Add(id); // đã ghi nhật ký — không xét lại trong phiên này
            if (ShouldMarkSeen(outcome, s.MarkAsRead)) item.UnRead = false;
        }
        return result;
    }

    private const string TransportHeadersProperty = "http://schemas.microsoft.com/mapi/proptag/0x007D001F";

    /// <summary>Xác thực người gửi của thư Outlook theo header Internet; thư nội bộ Exchange (không qua Internet nên không có header) coi là đã xác thực.</summary>
    private static (MailAuth, string) OutlookAuthentication(object mailItem, string from)
    {
        dynamic item = mailItem;
        string raw = "";
        try { raw = (string)item.PropertyAccessor.GetProperty(TransportHeadersProperty) ?? ""; }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }
        // Người gửi là tài khoản Exchange (Outlook đã khớp với danh bạ tổ chức) → thư đi trong Exchange, header AuthAs là của Exchange.
        // Thư từ ngoài (người gửi kiểu SMTP, kể cả tài khoản IMAP mở trong Outlook) bỏ qua AuthAs do người gửi có thể tự chèn.
        bool exchange = false;
        try { exchange = (string)item.SenderEmailType == "EX"; }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        if (raw.Trim().Length > 0) return EvaluateAuthentication(ParseHeaders(raw), from, exchange);
        return exchange ? (MailAuth.Pass, "thư nội bộ Exchange") : (MailAuth.Unknown, "không có header Internet");
    }

    private static string SenderAddress(dynamic item)
    {
        try
        {
            // Người gửi trong Exchange: SenderEmailAddress là địa chỉ X.500 → lấy SMTP thật.
            if ((string)item.SenderEmailType == "EX")
            {
                dynamic? user = item.Sender?.GetExchangeUser();
                if (user != null) return (string)user.PrimarySmtpAddress;
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return item.SenderEmailAddress ?? "";
    }

    private static bool IsHidden(dynamic att)
    {
        try { return (bool)att.PropertyAccessor.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x7FFE000B"); }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { return false; }
    }
}
