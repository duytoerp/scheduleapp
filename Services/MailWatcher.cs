using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>Một email mới đã tải về (file đính kèm đã lưu vào thư mục riêng).</summary>
public sealed record IncomingMail(string Id, string Subject, string From, string FromName, string Body, DateTime Received, List<string> Attachments, string AttachmentDir)
{
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

    public static string AttachmentRoot => Path.Combine(JobStore.DataDir, "email");

    /// <summary>
    /// Lấy các email chưa đọc khớp ít nhất một bộ lọc (tiêu đề chứa, người gửi chứa); lưu đính kèm, đánh dấu đã đọc nếu cài đặt bật.
    /// </summary>
    public static async Task<List<IncomingMail>> FetchAsync(IReadOnlyList<(string Subject, string From)> filters, CancellationToken ct)
    {
        var s = SettingsStore.Current.Inbox;
        var since = s.MarkAsRead ? DateTime.Now - MaxAge : StartedAt; // không đánh dấu đã đọc → chỉ lấy thư đến sau khi mở app
        bool Wanted(string subject, string from, string fromName) =>
            filters.Any(f => Contains(subject, f.Subject) && (Contains(from, f.From) || Contains(fromName, f.From)));

        var result = s.Source == MailSource.Imap
            ? await FetchImapAsync(s, since, Wanted, ct)
            : await RunSta(() => FetchOutlook(s, since, Wanted));
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

    /// <summary>Email khớp bộ lọc của một trình kích hoạt (tiêu đề chứa, người gửi chứa; không phân biệt hoa thường và dấu).</summary>
    public static bool Matches(IncomingMail m, string subject, string from) =>
        Contains(m.Subject, subject) && (Contains(m.From, from) || Contains(m.FromName, from));

    private static bool Contains(string text, string filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        Vision.ScreenOcr.RemoveDiacritics(text).Contains(Vision.ScreenOcr.RemoveDiacritics(filter.Trim()), StringComparison.OrdinalIgnoreCase);

    private static string NewAttachmentDir(DateTime received, string id)
    {
        var tag = Math.Abs(id.GetHashCode()).ToString("x8", CultureInfo.InvariantCulture);
        return Path.Combine(AttachmentRoot, $"{received:yyyyMMdd-HHmmss}-{tag}");
    }

    private static string SafeFileName(string name, HashSet<string> used)
    {
        var safe = string.Concat((string.IsNullOrWhiteSpace(name) ? "dinh-kem" : name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var unique = safe;
        for (int i = 2; !used.Add(unique.ToLowerInvariant()); i++)
            unique = $"{Path.GetFileNameWithoutExtension(safe)} ({i}){Path.GetExtension(safe)}";
        return unique;
    }

    // ───────────────────────────── IMAP ─────────────────────────────

    private static async Task<ImapClient> ConnectImapAsync(MailInboxSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.User))
            throw new InvalidOperationException("Chưa nhập máy chủ IMAP hoặc tài khoản (⚙ Cài đặt → Tích hợp).");
        var client = new ImapClient { Timeout = 60_000 };
        try
        {
            await client.ConnectAsync(s.Host.Trim(), s.Port, s.UseSsl ? MailKit.Security.SecureSocketOptions.SslOnConnect : MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable, ct);
            await client.AuthenticateAsync(s.User.Trim(), Protector.Unprotect(s.Password), ct);
            return client;
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

    private static async Task<List<IncomingMail>> FetchImapAsync(MailInboxSettings s, DateTime since, Func<string, string, string, bool> wanted, CancellationToken ct)
    {
        var result = new List<IncomingMail>();
        using var client = await ConnectImapAsync(s, ct);
        var folder = await OpenFolderAsync(client, s, s.MarkAsRead ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, ct);
        var uids = await folder.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.DeliveredAfter(since.Date.AddDays(-1))), ct);
        if (uids.Count > 0)
        {
            var summaries = await folder.FetchAsync(uids, MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate, ct);
            foreach (var m in summaries.OrderBy(x => x.InternalDate))
            {
                if (m.Envelope is not { } env) continue;
                var received = (m.InternalDate ?? env.Date ?? DateTimeOffset.Now).LocalDateTime;
                if (received < since) continue;
                var sender = env.From.Mailboxes.FirstOrDefault();
                var subject = env.Subject ?? "";
                if (!wanted(subject, sender?.Address ?? "", sender?.Name ?? "")) continue;

                var id = "imap:" + (env.MessageId ?? $"{folder.FullName}/{m.UniqueId}");
                lock (Processed) if (Processed.Contains(id)) continue;
                var message = await folder.GetMessageAsync(m.UniqueId, ct);
                var dir = NewAttachmentDir(received, id);
                var files = new List<string>();
                var used = new HashSet<string>();
                foreach (var part in message.Attachments.OfType<MimePart>())
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, SafeFileName(part.FileName ?? "", used));
                    await using (var fs = File.Create(path)) if (part.Content != null) await part.Content.DecodeToAsync(fs, ct);
                    files.Add(path);
                }
                var body = message.TextBody ?? HtmlToText(message.HtmlBody ?? "");
                result.Add(new IncomingMail(id, subject, sender?.Address ?? "", sender?.Name ?? "", body.Trim(), received, files, files.Count > 0 ? dir : ""));
                if (s.MarkAsRead) await folder.AddFlagsAsync(m.UniqueId, MessageFlags.Seen, true, ct);
            }
        }
        await client.DisconnectAsync(true, ct);
        return result;
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

    private static List<IncomingMail> FetchOutlook(MailInboxSettings s, DateTime since, Func<string, string, string, bool> wanted)
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
            if (!wanted(subject, from, fromName)) continue;
            string id = "outlook:" + (string)item.EntryID;
            lock (Processed) if (Processed.Contains(id)) continue;

            DateTime received = item.ReceivedTime;
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
                var path = Path.Combine(dir, SafeFileName((string)att.FileName, used));
                att.SaveAsFile(path);
                files.Add(path);
            }
            string body = item.Body ?? "";
            result.Add(new IncomingMail(id, subject, from, fromName, body.Trim(), received, files, files.Count > 0 ? dir : ""));
            if (s.MarkAsRead) item.UnRead = false;
        }
        return result;
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
