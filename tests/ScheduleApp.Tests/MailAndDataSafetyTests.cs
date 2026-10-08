using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MailKit.Security;
using MimeKit;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Data;
using ScheduleApp.Services.Engine;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>An toàn của trình kích hoạt email (người gửi, xác thực, đính kèm, mật khẩu) — thư MimeKit tạo trong bộ nhớ, không dùng máy chủ thư thật.</summary>
public class MailSafetyTests
{
    private const string GmailPass =
        "mx.google.com;\r\n       dkim=pass header.i=@shop.vn header.s=s1 header.b=AbCd;\r\n       spf=pass (google.com: domain of orders@shop.vn designates 1.2.3.4 as permitted sender) smtp.mailfrom=orders@shop.vn;\r\n       dmarc=pass (p=REJECT sp=REJECT dis=NONE) header.from=shop.vn";

    private static MimeMessage Mail(string from, string subject = "Đơn hàng #1", string? authResults = null, params (string Name, byte[] Data)[] attachments)
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse("toi@congty.vn"));
        m.Subject = subject;
        if (authResults != null) m.Headers.Add("Authentication-Results", authResults);
        // Tạo phần đính kèm thủ công để giữ nguyên tên file "độc" (BodyBuilder tự cắt bỏ thư mục trong tên).
        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "nội dung" } };
        foreach (var (name, data) in attachments)
            mixed.Add(new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream(data)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = name },
                ContentTransferEncoding = ContentEncoding.Base64
            });
        m.Body = mixed;
        return m;
    }

    private static List<string> CaptureLog(Action action)
    {
        var lines = new List<string>();
        void OnLog(string l) { lock (lines) lines.Add(l); }
        Log.Written += OnLog;
        try { action(); }
        finally { Log.Written -= OnLog; }
        return lines;
    }

    private static async Task<(IncomingMail? Mail, List<string> Log)> ProcessAsync(MimeMessage m, params MailFilter[] filters)
    {
        IncomingMail? mail = null;
        var lines = new List<string>();
        void OnLog(string l) { lock (lines) lines.Add(l); }
        Log.Written += OnLog;
        try { mail = await MailWatcher.ProcessMessageAsync(m, "test:" + Guid.NewGuid(), DateTime.Now, filters, CancellationToken.None); }
        finally { Log.Written -= OnLog; }
        return (mail, lines);
    }

    [Fact]
    public void SenderMatchesAddressOrDomainOnly()
    {
        Assert.True(MailWatcher.SenderMatches("Orders@Shop.vn", "orders@shop.vn"));
        Assert.True(MailWatcher.SenderMatches("ketoan@shop.vn", "@shop.vn"));
        Assert.True(MailWatcher.SenderMatches("ketoan@shop.vn", "shop.vn")); // bộ lọc cũ viết thiếu @ vẫn là tên miền
        Assert.True(MailWatcher.SenderMatches("b@khac.vn", "a@shop.vn; @khac.vn, c@x.vn"));
        Assert.True(MailWatcher.SenderMatches("ai@cung.vn", ""));
        Assert.False(MailWatcher.SenderMatches("orders@shop.vn.evil.com", "@shop.vn"));
        Assert.False(MailWatcher.SenderMatches("xorders@shop.vn", "orders@shop.vn"));
        Assert.False(MailWatcher.SenderMatches("orders@evilshop.vn", "@shop.vn"));
        Assert.False(MailWatcher.SenderMatches("ke@toan.vn", "Phòng kế toán")); // tên hiển thị không khớp gì cả
        Assert.False(MailWatcher.SenderMatches("", "@shop.vn"));

        Assert.Null(MailWatcher.SenderFilterError("ketoan@congty.vn; @shop.vn"));
        Assert.Null(MailWatcher.SenderFilterError(""));
        var error = MailWatcher.SenderFilterError("Phòng kế toán");
        Assert.NotNull(error);
        Assert.Contains("\"Phòng\"", error);
    }

    [Fact]
    public async Task DisplayNameSpoofIsRejected()
    {
        var filter = new MailFilter("", "orders@shop.vn", false);
        // Tên hiển thị là địa chỉ cần tin, địa chỉ thật là của kẻ gian.
        var (spoof, _) = await ProcessAsync(Mail("\"orders@shop.vn\" <attacker@evil.com>"), filter);
        Assert.Null(spoof);
        var (real, _) = await ProcessAsync(Mail("Shop <orders@shop.vn>"), filter);
        Assert.NotNull(real);
        Assert.Equal("orders@shop.vn", real.From);
        Assert.Equal("Shop", real.FromName);
    }

    [Fact]
    public void AuthenticationResultsParsing()
    {
        Assert.Equal(MailAuth.Pass, MailWatcher.EvaluateAuthentication(GmailPass, null, "orders@shop.vn").Result);
        Assert.Equal(MailAuth.Fail, MailWatcher.EvaluateAuthentication("mx.google.com; dkim=pass header.i=@evil.com; spf=pass smtp.mailfrom=x@evil.com; dmarc=fail (p=NONE) header.from=shop.vn", null, "orders@shop.vn").Result);
        // Định dạng Microsoft 365 (không có authserv-id, "action=" sau dmarc).
        Assert.Equal(MailAuth.Pass, MailWatcher.EvaluateAuthentication("spf=pass (sender IP is 1.2.3.4) smtp.mailfrom=shop.vn; dkim=pass (signature was verified) header.d=shop.vn;dmarc=pass action=none header.from=shop.vn;compauth=pass reason=100", null, "a@shop.vn").Result);
        Assert.Equal(MailAuth.Fail, MailWatcher.EvaluateAuthentication("spf=none smtp.mailfrom=evil.com; dkim=none header.d=none;dmarc=none action=none header.from=shop.vn;compauth=fail reason=001", null, "a@shop.vn").Result);
        // Không có DMARC: DKIM/SPF của đúng tên miền người gửi quyết định.
        Assert.Equal(MailAuth.Fail, MailWatcher.EvaluateAuthentication("mx.x.vn; dkim=fail (bad signature) header.d=shop.vn", null, "a@shop.vn").Result);
        Assert.Equal(MailAuth.Fail, MailWatcher.EvaluateAuthentication("mx.x.vn; spf=fail smtp.mailfrom=a@shop.vn", null, "a@shop.vn").Result);
        Assert.Equal(MailAuth.Pass, MailWatcher.EvaluateAuthentication("mx.x.vn; spf=pass smtp.mailfrom=bounce@mail.shop.vn", null, "a@shop.vn").Result);
        // DKIM đạt nhưng cho tên miền khác → không chứng minh được gì.
        Assert.Equal(MailAuth.Unknown, MailWatcher.EvaluateAuthentication("mx.x.vn; dkim=pass header.d=evil.com; spf=softfail smtp.mailfrom=evil.com", null, "a@shop.vn").Result);
        Assert.Equal(MailAuth.Unknown, MailWatcher.EvaluateAuthentication(null, null, "a@shop.vn").Result);
        Assert.Equal(MailAuth.Pass, MailWatcher.EvaluateAuthentication(null, "Internal", "a@shop.vn").Result);
    }

    [Fact]
    public void OnlyTheTopmostAuthenticationResultsIsTrusted()
    {
        // Máy chủ nhận thư thêm header lên trên cùng; header phía dưới do người gửi tự chèn ("dmarc=pass") không được tin.
        var m = Mail("orders@shop.vn", authResults: "mx.google.com; dmarc=fail (p=NONE) header.from=shop.vn");
        m.Headers.Add("Authentication-Results", "evil.com; dmarc=pass header.from=shop.vn");
        Assert.Equal(MailAuth.Fail, MailWatcher.EvaluateAuthentication(m.Headers, "orders@shop.vn", exchangeMailbox: false).Result);

        // Khối header thô (Outlook) cũng chỉ lấy header đầu tiên, kể cả khi bị gập dòng.
        var raw = "Received: from x\r\nAuthentication-Results: mx.google.com;\r\n\tdmarc=pass header.from=shop.vn\r\nAuthentication-Results: evil; dmarc=fail\r\nFrom: orders@shop.vn\r\n";
        Assert.Equal(MailAuth.Pass, MailWatcher.EvaluateAuthentication(MailWatcher.ParseHeaders(raw), "orders@shop.vn", exchangeMailbox: false).Result);
    }

    [Fact]
    public async Task FailedAuthenticationIsAlwaysRejectedAndLogged()
    {
        var m = Mail("orders@shop.vn", authResults: "mx.google.com; dmarc=fail (p=NONE) header.from=shop.vn");
        var (mail, log) = await ProcessAsync(m, new MailFilter("", "orders@shop.vn", RequireAuthenticated: false));
        Assert.Null(mail);
        Assert.Contains(log, l => l.Contains("[WARN]") && l.Contains("giả mạo") && l.Contains("dmarc=fail"));
    }

    [Fact]
    public async Task RequireAuthenticatedOption()
    {
        var unknown = Mail("orders@shop.vn"); // không có Authentication-Results
        var (off, _) = await ProcessAsync(unknown, new MailFilter("", "@shop.vn", false));
        Assert.NotNull(off);
        Assert.Equal(MailAuth.Unknown, off.Auth);

        var (on, log) = await ProcessAsync(unknown, new MailFilter("", "@shop.vn", true));
        Assert.Null(on);
        Assert.Contains(log, l => l.Contains("Chỉ nhận email đã xác thực"));

        var (passed, _) = await ProcessAsync(Mail("orders@shop.vn", authResults: GmailPass), new MailFilter("", "@shop.vn", true));
        Assert.NotNull(passed);
        Assert.Equal(MailAuth.Pass, passed.Auth);

        // Hai trình kích hoạt cùng khớp: một cái không đòi xác thực → thư vẫn chạy (cho đúng trình kích hoạt đó).
        var (mixed, _) = await ProcessAsync(unknown, new MailFilter("", "@shop.vn", true), new MailFilter("Đơn", "", false));
        Assert.NotNull(mixed);
        Assert.False(MailWatcher.Matches(mixed, new MailFilter("", "@shop.vn", true)));
        Assert.True(MailWatcher.Matches(mixed, new MailFilter("Đơn", "", false)));
    }

    [Fact]
    public void OptionDefaultsOnForNewTriggersOffForSavedOnes()
    {
        Assert.True(new JobTrigger { Type = TriggerType.EmailReceived }.RequireAuthenticatedEmail);
        var old = JsonSerializer.Deserialize<JobTrigger>("{\"Type\":\"EmailReceived\",\"Value\":\"hóa đơn\",\"Value2\":\"\",\"Minutes\":5}", JsonDefaults.Options)!;
        Assert.False(old.RequireAuthenticatedEmail);
        var saved = JsonSerializer.Serialize(new JobTrigger { Type = TriggerType.EmailReceived }, JsonDefaults.Options);
        Assert.True(JsonSerializer.Deserialize<JobTrigger>(saved, JsonDefaults.Options)!.RequireAuthenticatedEmail);
        var off = JsonSerializer.Serialize(new JobTrigger { Type = TriggerType.EmailReceived, RequireAuthenticatedEmail = false }, JsonDefaults.Options);
        Assert.False(JsonSerializer.Deserialize<JobTrigger>(off, JsonDefaults.Options)!.RequireAuthenticatedEmail);
        // Sửa trình kích hoạt (bản sao) giữ nguyên lựa chọn.
        Assert.False(old.Clone().RequireAuthenticatedEmail);
        Assert.Equal("hóa đơn", old.Clone().Value);
    }

    [Fact]
    public void OpenSenderWithRiskyStepsWarnsInEditorAndLogsOnce()
    {
        var trigger = new JobTrigger { Type = TriggerType.EmailReceived, Value = "lệnh" };
        var risky = new List<ActionStep> { S(StepType.Wait), S(StepType.RunCommand, s => s.Target = "cmd /c echo {{email.subject}}") };
        var warning = MailWatcher.OpenSenderWarning(trigger, risky);
        Assert.NotNull(warning);
        Assert.Contains(ActionStep.TypeNames[StepType.RunCommand], warning);

        Assert.Null(MailWatcher.OpenSenderWarning(trigger, [S(StepType.Wait), S(StepType.LogMessage)]));
        Assert.Null(MailWatcher.OpenSenderWarning(trigger, [S(StepType.TypeText, s => s.Enabled = false)]));
        Assert.NotNull(MailWatcher.OpenSenderWarning(trigger, [S(StepType.TypeText)]));
        Assert.NotNull(MailWatcher.OpenSenderWarning(trigger, [S(StepType.LaunchApp)]));
        Assert.Null(MailWatcher.OpenSenderWarning(new JobTrigger { Type = TriggerType.EmailReceived, Value2 = "@congty.vn" }, risky));

        var job = new Job { Name = "Mở theo email " + Guid.NewGuid().ToString("N")[..6], Steps = risky, Triggers = [trigger] };
        var log = CaptureLog(() =>
        {
            MailWatcher.WarnRiskyTriggers([(job, trigger)]);
            MailWatcher.WarnRiskyTriggers([(job, trigger)]); // lần kiểm tra thư sau: không ghi lại
        });
        Assert.Single(log, l => l.Contains(job.Name) && l.Contains("không giới hạn người gửi"));
    }

    [Fact]
    public async Task AttachmentsGetMarkOfTheWeb()
    {
        var data = Encoding.UTF8.GetBytes("Mã,Tên\n1,An\n");
        var (mail, _) = await ProcessAsync(Mail("orders@shop.vn", attachments: ("don-hang.csv", data)), new MailFilter("", "", false));
        Assert.NotNull(mail);
        var path = Assert.Single(mail.Attachments);
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.StartsWith(MailWatcher.AttachmentRoot, path);
        var zone = File.ReadAllText(path + ":Zone.Identifier");
        Assert.StartsWith("[ZoneTransfer]\r\nZoneId=3", zone);
    }

    [Fact]
    public async Task EvilAttachmentNamesAreSanitized()
    {
        var longName = new string('a', 300) + ".pdf";
        var names = new[] { @"..\..\evil.exe", "../x.bat", "CON.txt", "nul", longName, "hoadon\u202Efdp.exe", "a:b?.txt", "..", "", "ten. . ." };
        var m = Mail("orders@shop.vn", attachments: [.. names.Select(n => (n, new byte[] { 1, 2, 3 }))]);
        var (mail, _) = await ProcessAsync(m, new MailFilter("", "", false));
        Assert.NotNull(mail);
        Assert.Equal(names.Length, mail.Attachments.Count);
        Assert.Equal(names.Length, mail.Attachments.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var path in mail.Attachments)
        {
            Assert.Equal(Path.GetFullPath(mail.AttachmentDir), Path.GetDirectoryName(Path.GetFullPath(path)));
            var name = Path.GetFileName(path);
            Assert.True(name.Length <= MailWatcher.MaxFileNameLength + 6, name);
            Assert.DoesNotContain('\u202E', name);
            Assert.False(name.EndsWith('.') || name.EndsWith(' '), name);
            Assert.True(File.Exists(path), path);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        }
        var saved = mail.Attachments.Select(Path.GetFileName).ToList();
        Assert.Contains("_CON.txt", saved);
        Assert.Contains("_nul", saved);
        Assert.Contains(saved, n => n!.EndsWith(".pdf") && n.StartsWith("aaaa"));
        Assert.Contains("dinh-kem", saved);
    }

    [Fact]
    public async Task ErrorInOneMessageDoesNotBlockTheNext()
    {
        var received = DateTime.Now;
        string badId = "imap:bad-" + Guid.NewGuid(), goodId = "imap:good-" + Guid.NewGuid();
        // Chặn thư mục đính kèm của thư đầu bằng một file trùng tên → lưu đính kèm lỗi thật.
        var blocked = MailWatcher.NewAttachmentDir(received, badId);
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
        File.WriteAllText(blocked, "chặn");

        var result = new List<IncomingMail>();
        var filters = new[] { new MailFilter("", "@shop.vn", false) };
        var log = new List<string>();
        void OnLog(string l) { lock (log) log.Add(l); }
        Log.Written += OnLog;
        MailOutcome first, second;
        try
        {
            first = await MailWatcher.ProcessIsolatedAsync(Mail("a@shop.vn", "Thư lỗi", attachments: ("x.txt", [1])), badId, received, filters, result, CancellationToken.None);
            second = await MailWatcher.ProcessIsolatedAsync(Mail("a@shop.vn", "Thư tốt", attachments: ("y.txt", [2])), goodId, received, filters, result, CancellationToken.None);
        }
        finally
        {
            Log.Written -= OnLog;
        }
        Assert.Equal(MailOutcome.Failed, first);
        Assert.Equal(MailOutcome.Processed, second);
        Assert.Equal("Thư tốt", Assert.Single(result).Subject);
        Assert.Contains(log, l => l.Contains("Thư lỗi") && l.Contains("bỏ qua thư này"));

        // Chỉ thư đã xử lý xong mới được đánh dấu đã đọc.
        Assert.False(MailWatcher.ShouldMarkSeen(first, markAsRead: true));
        Assert.True(MailWatcher.ShouldMarkSeen(second, markAsRead: true));
        Assert.False(MailWatcher.ShouldMarkSeen(second, markAsRead: false));
        Assert.False(MailWatcher.ShouldMarkSeen(MailOutcome.Rejected, markAsRead: true));
    }

    [Fact]
    public void ImapWithoutSslRequiresStartTls()
    {
        static SecureSocketOptions Opt(string host, bool ssl, bool plain = false) =>
            MailWatcher.SocketOptions(new MailInboxSettings { Host = host, UseSsl = ssl, AllowPlaintext = plain });
        Assert.Equal(SecureSocketOptions.SslOnConnect, Opt("imap.gmail.com", true));
        Assert.Equal(SecureSocketOptions.StartTls, Opt("mail.congty.vn", false));
        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, Opt("mail.congty.vn", false, plain: true));
        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, Opt("localhost", false));
        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, Opt("127.0.0.1", false));
        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, Opt("[::1]", false));
        Assert.Equal(SecureSocketOptions.StartTls, Opt("10.0.0.5", false));
    }

    [Fact]
    public async Task UndecryptablePasswordStopsBeforeConnecting()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var s = new MailInboxSettings
            {
                Source = MailSource.Imap, Host = "127.0.0.1", Port = port, UseSsl = false, User = "toi@congty.vn",
                Password = "dpapi:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("không phải dữ liệu DPAPI"))
            };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => MailWatcher.TestAsync(s, CancellationToken.None));
            Assert.Contains("Không giải mã được mật khẩu email", ex.Message);
            Assert.Contains("⚙ Cài đặt", ex.Message);
            Assert.False(listener.Pending()); // không hề kết nối tới máy chủ
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>An toàn khi đọc / ghi dữ liệu: file Excel chỉ số vô lý, chèn công thức vào CSV, đường dẫn mạng lấy từ biến.</summary>
public class DataSafetyTests
{
    private static string SheetXlsx(string sheetData)
    {
        var path = Path.Combine(NewDir(), "x.xlsx");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");
        Add("xl/workbook.xml", $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        Add("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData>{sheetData}</sheetData></worksheet>");
        return path;
    }

    private static string Cell(string r, string text) => $"<c r=\"{r}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    [Theory]
    [InlineData("<row r=\"1\">" + "<c r=\"XFE1\"><v>1</v></c></row>")]                    // cột 16.385
    [InlineData("<row r=\"1\">" + "<c r=\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA1\"><v>1</v></c></row>")] // tràn số cũ
    [InlineData("<row r=\"1048577\"><c r=\"A1048577\"><v>1</v></c></row>")]
    [InlineData("<row r=\"99999999999999\"><c><v>1</v></c></row>")]
    [InlineData("<row r=\"-3\"><c><v>1</v></c></row>")]
    public void XlsxIndexOutsideExcelLimitsIsRejectedFast(string sheetData)
    {
        var path = SheetXlsx(sheetData);
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidDataException>(() => TabularReader.Read(path));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), sw.Elapsed.ToString());
        Assert.Contains("giới hạn của Excel", ex.Message);
    }

    [Fact]
    public void XlsxSparseFarCellsAreRejectedFast()
    {
        // 2.000 dòng, mỗi dòng chỉ có ô XFD (cột cuối) → 32 triệu ô trống phải chèn.
        var rows = new StringBuilder();
        for (int r = 1; r <= 2000; r++) rows.Append($"<row r=\"{r}\"><c r=\"XFD{r}\"><v>{r}</v></c></row>");
        var path = SheetXlsx(rows.ToString());
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidDataException>(() => TabularReader.Read(path));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.Contains("quá xa", ex.Message);
    }

    [Fact]
    public void XlsxLastValidCellStillReads()
    {
        var path = SheetXlsx($"<row r=\"1\">{Cell("A1", "Mã")}{Cell("XFD1", "Cuối")}</row><row r=\"1048576\">{Cell("A1048576", "x")}</row>");
        var t = TabularReader.Read(path);
        Assert.Equal(TabularReader.MaxColumns, t.Headers.Count);
        Assert.Equal("Cuối", t.Headers[^1]);
        Assert.Equal([TabularReader.MaxRows], t.RowNumbers);
        Assert.Equal(TabularReader.MaxColumns - 1, TabularReader.ColumnIndex("XFD1"));
        Assert.Equal(TabularReader.MaxColumns, TabularReader.ColumnIndex("ZZZZZZZZZZZZZZ1"));
    }

    [Fact]
    public void CsvFormulaInjectionIsNeutralizedButNumbersAreNot()
    {
        var csv = Path.Combine(NewDir(), "kq.csv");
        File.WriteAllText(csv, "A;B;C;D;E;F;G;H;I;J\n", new UTF8Encoding(true));
        TabularWriter.Write(csv, null, DataAction.AppendRow, "", [
            new("A", "=HYPERLINK(\"http://evil\",\"Bấm\")"), new("B", "+1+cmd|' /C calc'!A0"), new("C", "-5"), new("D", "-3.2"),
            new("E", "@SUM(A1)"), new("F", "\tTab"), new("G", "-1,5"), new("H", "-"), new("I", "binh thuong"), new("J", "1e5")
        ]);
        var t = TabularReader.Read(csv);
        var row = t.Rows[0];
        Assert.Equal("'=HYPERLINK(\"http://evil\",\"Bấm\")", row[0]);
        Assert.Equal("'+1+cmd|' /C calc'!A0", row[1]);
        Assert.Equal("-5", row[2]);
        Assert.Equal("-3.2", row[3]);
        Assert.Equal("'@SUM(A1)", row[4]);
        Assert.Equal("'\tTab", row[5]);
        Assert.Equal("-1,5", row[6]);
        Assert.Equal("'-", row[7]);
        Assert.Equal("binh thuong", row[8]);
        Assert.Equal("1e5", row[9]);
        // Dữ liệu cũ trong file không bị thêm dấu khi ghi lại.
        TabularWriter.Write(csv, null, DataAction.UpdateRow, "2", [new("I", "sửa")]);
        Assert.Equal("'=HYPERLINK(\"http://evil\",\"Bấm\")", TabularReader.Read(csv).Rows[0][0]);
    }

    [Fact]
    public void XlsxValuesAreWrittenUnchanged()
    {
        var xlsx = Path.Combine(NewDir(), "kq.xlsx");
        MakeXlsx(xlsx);
        int row = TabularWriter.Write(xlsx, null, DataAction.AppendRow, "", [new("Mã", "=1+1"), new("Họ tên", "-5")]);
        var t = TabularReader.Read(xlsx);
        int i = t.RowNumbers.IndexOf(row);
        Assert.Equal("=1+1", t.Rows[i][0]);
        Assert.Equal("-5", t.Rows[i][1]);
    }

    [Fact]
    public void NetworkPathDetection()
    {
        Assert.True(LoopFrame.IsNetworkPath(@"\\attacker\share\x.txt"));
        Assert.True(LoopFrame.IsNetworkPath("//attacker/share/x.txt"));
        Assert.True(LoopFrame.IsNetworkPath(@"\\?\UNC\attacker\share\x.txt"));
        Assert.True(LoopFrame.IsNetworkPath(@"\\attacker@SSL\DavWWWRoot\x.txt"));
        Assert.False(LoopFrame.IsNetworkPath(Path.Combine(DataDir, "a.txt")));
    }

    private static async Task<string> LinesLoopAsync(string target, string? pathVar)
    {
        var job = new Job
        {
            Name = "lines",
            Variables = [new VariableDef { Name = "kq", Value = "" }, new VariableDef { Name = "p", Value = pathVar ?? "" }],
            Steps =
            [
                S(StepType.Loop, s => { s.LoopKind = LoopKind.Lines; s.Target = target; s.Variable = "dong"; }),
                SetVar("kq", VarSource.Value, "{{kq}}[{{dong}}]"),
                S(StepType.EndLoop)
            ]
        };
        var (r, c) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        return c.Vars["kq"];
    }

    [Fact]
    public async Task LinesLoopDoesNotOpenNetworkPathFromVariable()
    {
        var file = Path.Combine(NewDir(), "ds.txt");
        File.WriteAllText(file, "mot\nhai\n");
        // \\?\ là đường dẫn dạng UNC trỏ về file có thật trên máy → phân biệt được "đã mở file" với "coi là nội dung".
        var unc = @"\\?\" + file;

        var log = new List<string>();
        void OnLog(string l) { lock (log) log.Add(l); }
        Log.Written += OnLog;
        string fromVariable;
        try { fromVariable = await LinesLoopAsync("{{p}}", unc); }
        finally { Log.Written -= OnLog; }
        Assert.Equal($"[{unc}]", fromVariable); // lấy từ biến (vd nội dung email) → không mở, coi là nội dung
        Assert.Contains(log, l => l.Contains("đường dẫn mạng lấy từ biến"));

        Assert.Equal("[mot][hai]", await LinesLoopAsync(unc, null));       // ghi thẳng trong bước → vẫn đọc file
        Assert.Equal("[mot][hai]", await LinesLoopAsync("{{p}}", file));   // đường dẫn trên máy lấy từ biến → vẫn đọc file như trước
    }
}
