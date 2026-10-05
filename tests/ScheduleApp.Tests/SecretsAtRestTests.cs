using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Bí mật lưu trên đĩa: settings.json (URL webhook, header kết nối API, mật khẩu / token cũ còn chữ thường), bước Đăng nhập D365
/// (mật khẩu, khóa TOTP trong jobs.json), chỉ điền mật khẩu vào trang đăng nhập tin cậy, {{secret:Tên}} trong biến môi trường kiểm thử.
/// </summary>
public class SecretsAtRestTests
{
    private const string Mask = "••••••••";

    // ───────────────────────────── Tiện ích ─────────────────────────────

    private static string SettingsPath => Path.Combine(JobStore.DataDir, "settings.json");

    private static void Sta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            bool check = Control.CheckForIllegalCrossThreadCalls;
            Control.CheckForIllegalCrossThreadCalls = true;
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Control.CheckForIllegalCrossThreadCalls = check; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.Message, error);
    }

    private static T Field<T>(object form, string name) =>
        (T)form.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;

    private static object? Call(object form, string name, params object[] args) =>
        form.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance, [.. args.Select(a => a.GetType())])!.Invoke(form, args);

    private static TForm Open<TForm>(TForm form) where TForm : Form
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-20000, -20000);
        form.Show();
        Application.DoEvents();
        return form;
    }

    /// <summary>Giữ cài đặt đang dùng + settings.json / .bak, trả lại khi kết thúc.</summary>
    private sealed class SettingsSnapshot : IDisposable
    {
        private readonly AppSettings _original = SettingsStore.Current;
        private readonly Dictionary<string, byte[]?> _files = new[] { SettingsPath, SettingsPath + ".bak" }
            .ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);

        public void Dispose()
        {
            SettingsStore.Replace(_original);
            foreach (var (p, bytes) in _files)
            {
                if (bytes != null) File.WriteAllBytes(p, bytes);
                else File.Delete(p);
            }
        }
    }

    private sealed class LogLines : IDisposable
    {
        private readonly List<string> _lines = [];
        public LogLines() => Log.Written += Add;
        public List<string> Lines { get { lock (_lines) return [.. _lines]; } }
        private void Add(string line) { lock (_lines) _lines.Add(line); }
        public void Dispose() => Log.Written -= Add;
    }

    // ───────────────────────────── settings.json ─────────────────────────────

    [Fact]
    public void PlaintextSecretsInSettingsAreEncryptedOnceAtLoad()
    {
        using var snapshot = new SettingsSnapshot();
        var already = Protector.Protect("token-github-da-ma-hoa");
        var old = new AppSettings
        {
            Telegram = new TelegramSettings { BotToken = "123456:bot-token-chu-thuong" },
            Email = new EmailSettings { Password = "smtp-mat-khau-chu-thuong" },
            Inbox = new MailInboxSettings { Password = "imap-mat-khau-chu-thuong" },
            Ai = new AiSettings { ApiKey = "sk-ant-chu-thuong" },
            Update = new UpdateSettings { Token = already },
            Webhook = new WebhookSettings { Enabled = true, Url = "https://hooks.slack.com/services/T000/B000/khoa-webhook-bi-mat" },
            ApiConnections = [new ApiConnection { Name = "crm", Secret = "client-secret-chu-thuong", Headers = "Accept: application/json\nx-api-key: khoa-header-bi-mat" }],
            BrowserPort = 9555
        };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(old, JsonDefaults.Options));
        SettingsStore.ResetForTests();

        var s = SettingsStore.Current;
        string[] stored = [s.Telegram.BotToken, s.Email.Password, s.Inbox.Password, s.Ai.ApiKey, s.Update.Token, s.Webhook.Url,
            s.ApiConnections[0].Secret, s.ApiConnections[0].Headers];
        Assert.All(stored, v => Assert.StartsWith("dpapi:", v));
        Assert.Equal(already, s.Update.Token);                                   // đã mã hóa → giữ nguyên, không mã hóa chồng
        Assert.Equal("123456:bot-token-chu-thuong", Protector.Unprotect(s.Telegram.BotToken));
        Assert.Equal("smtp-mat-khau-chu-thuong", Protector.Unprotect(s.Email.Password));
        Assert.Equal("imap-mat-khau-chu-thuong", Protector.Unprotect(s.Inbox.Password));
        Assert.Equal("sk-ant-chu-thuong", Protector.Unprotect(s.Ai.ApiKey));
        Assert.Equal("https://hooks.slack.com/services/T000/B000/khoa-webhook-bi-mat", Protector.Unprotect(s.Webhook.Url));
        Assert.Equal("client-secret-chu-thuong", Protector.Unprotect(s.ApiConnections[0].Secret));
        Assert.Equal("Accept: application/json\nx-api-key: khoa-header-bi-mat", Protector.Unprotect(s.ApiConnections[0].Headers));
        Assert.Equal(9555, s.BrowserPort);

        // Đã lưu ngay: file không còn chữ thường nào.
        var json = File.ReadAllText(SettingsPath);
        foreach (var plain in new[] { "bot-token-chu-thuong", "smtp-mat-khau", "imap-mat-khau", "sk-ant-chu-thuong", "khoa-webhook-bi-mat",
                     "client-secret-chu-thuong", "khoa-header-bi-mat" })
            Assert.DoesNotContain(plain, json);

        // Lần đọc sau: không còn gì để mã hóa → không ghi lại file (mã hóa DPAPI mỗi lần một khác, nên file giữ nguyên là chưa ghi).
        var bytes = File.ReadAllBytes(SettingsPath);
        var written = File.GetLastWriteTimeUtc(SettingsPath);
        SettingsStore.ResetForTests();
        Assert.Equal(stored[0], SettingsStore.Current.Telegram.BotToken);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(SettingsPath));
        Assert.False(SettingsStore.ProtectPlaintextSecrets(SettingsStore.Current));
    }

    [Fact]
    public void SettingsFormStoresWebhookEncryptedAndKeepsItWhenUnchanged()
    {
        using var snapshot = new SettingsSnapshot();
        const string hook = "https://contoso.webhook.office.com/webhookb2/abc/IncomingWebhook/khoa-bi-mat-xyz";
        Sta(() =>
        {
            using var f = Open(new SettingsForm());
            Field<CheckBox>(f, "_chkWebhook").Checked = true;
            Field<TextBox>(f, "_txtWebhook").Text = hook;
            Field<TextBox>(f, "_txtLoginHosts").Text = "adfs.contoso.com; sso.contoso.vn,  adfs.contoso.com";
            Call(f, "Save");
        });
        var url = SettingsStore.Current.Webhook.Url;
        Assert.StartsWith("dpapi:", url);
        Assert.Equal(hook, Protector.Unprotect(url));
        Assert.True(SettingsStore.Current.Webhook.Enabled);
        Assert.Equal(["adfs.contoso.com", "sso.contoso.vn"], SettingsStore.Current.TrustedLoginHosts);
        Assert.DoesNotContain("khoa-bi-mat-xyz", File.ReadAllText(SettingsPath));

        // Mở lại: ô URL che ký tự, không hiện lại URL; lưu không sửa → giữ đúng chuỗi đã mã hóa.
        Sta(() =>
        {
            using var f = Open(new SettingsForm());
            var box = Field<TextBox>(f, "_txtWebhook");
            Assert.Equal(Mask, box.Text);
            Assert.True(box.UseSystemPasswordChar);
            Assert.Equal("adfs.contoso.com, sso.contoso.vn", Field<TextBox>(f, "_txtLoginHosts").Text);
            Call(f, "Save");
        });
        Assert.Equal(url, SettingsStore.Current.Webhook.Url);
    }

    [Fact]
    public void ApiConnectionFormEncryptsHeadersAndShowsThemDecrypted()
    {
        // Kết nối cũ: header chữ thường vẫn đọc được trong form.
        var old = new ApiConnection { Name = "crm", BaseUrl = "https://contoso.example/api/", Headers = "x-api-key: khoa-cu-123" };
        ApiConnection? saved = null;
        Sta(() =>
        {
            using var f = Open(new ApiConnectionForm(old, []));
            var headers = Field<TextBox>(f, "_txtHeaders");
            Assert.Equal("x-api-key: khoa-cu-123", headers.Text);
            headers.Text = "Accept: application/json\r\nx-api-key: khoa-moi-456";
            Call(f, "Save");
            saved = f.Connection;
        });
        Assert.StartsWith("dpapi:", saved!.Headers);
        Assert.DoesNotContain("khoa-moi-456", saved.Headers);
        Assert.Equal("Accept: application/json\nx-api-key: khoa-moi-456", Protector.Unprotect(saved.Headers));
        Assert.Equal(new[] { ("Accept", "application/json"), ("x-api-key", "khoa-moi-456") }, ApiClient.ParseHeaders(Protector.Unprotect(saved.Headers)).ToList());

        // Mở lại kết nối đã mã hóa: ô header hiện chữ để sửa.
        Sta(() =>
        {
            using var f = Open(new ApiConnectionForm(saved, []));
            Assert.Equal("Accept: application/json" + Environment.NewLine + "x-api-key: khoa-moi-456", Field<TextBox>(f, "_txtHeaders").Text);
        });
    }

    // ───────────────────────────── Bước Đăng nhập D365 ─────────────────────────────

    private static ActionStep LoginStep(string password, string totp)
    {
        var s = ActionStep.CreateDefault(StepType.Dynamics);
        s.D365Action = D365Action.Login;
        s.Text = "qa@contoso.vn";
        s.Arguments = password;
        s.RowRef = totp;
        return s;
    }

    /// <summary>Mở form soạn bước, (tùy chọn) gõ mật khẩu / khóa mới, bấm Lưu; trả về trạng thái các ô lúc mở.</summary>
    private static (string Password, char PasswordChar, string Totp, bool ArgsVisible) EditLogin(ActionStep step, string? newPassword = null, string? newTotp = null)
    {
        (string, char, string, bool) shown = default;
        Sta(() =>
        {
            using var f = Open(new StepEditorForm(step));
            var pass = Field<TextBox>(f, "_txtD365Password");
            var totp = Field<TextBox>(f, "_txtD365Totp");
            shown = (pass.Text, pass.PasswordChar, totp.Text, Field<ComboBox>(f, "_cboArgs").Visible);
            Assert.True(pass.Visible && totp.Visible);
            if (newPassword != null) pass.Text = newPassword;
            if (newTotp != null) totp.Text = newTotp;
            var built = (ActionStep)Call(f, "BuildStep")!;
            Assert.Null(Call(f, "Validate", built));                            // Lưu không bị chặn bởi hộp thoại cảnh báo
            Call(f, "Save");
        });
        return shown;
    }

    [Fact]
    public void LoginStepEditorNeverSavesPlaintextPassword()
    {
        // Bước cũ: mật khẩu + khóa TOTP chữ thường trong jobs.json → mở form chỉ thấy ••••••••, Lưu là mã hóa.
        var step = LoginStep("Mat-khau-cu-123!", FakeD365Server.MfaSecret);
        var shown = EditLogin(step);
        Assert.Equal((Mask, Mask, false), (shown.Password, shown.Totp, shown.ArgsVisible));
        Assert.NotEqual('\0', shown.PasswordChar);
        Assert.StartsWith("dpapi:", step.Arguments);
        Assert.StartsWith("dpapi:", step.RowRef);
        Assert.Equal("Mat-khau-cu-123!", Protector.Unprotect(step.Arguments));
        Assert.Equal(FakeD365Server.MfaSecret, Protector.Unprotect(step.RowRef));

        // Mở lại và lưu không sửa → không mã hóa chồng, giữ nguyên chuỗi.
        var (args, rowRef) = (step.Arguments, step.RowRef);
        EditLogin(step);
        Assert.Equal((args, rowRef), (step.Arguments, step.RowRef));

        // Gõ mật khẩu mới (khoảng trắng đầu / cuối là một phần mật khẩu).
        EditLogin(step, newPassword: " Mới@2026 ");
        Assert.StartsWith("dpapi:", step.Arguments);
        Assert.Equal(" Mới@2026 ", Protector.Unprotect(step.Arguments));

        // File công việc (xuất / jobs.json) không có mật khẩu, khóa TOTP; mô tả bước cũng không.
        var job = new Job { Name = "Đăng nhập", Steps = [step] };
        var file = Path.Combine(NewDir(), "jobs.json");
        JobStore.Export(file, [job]);
        var json = File.ReadAllText(file);
        Assert.DoesNotContain("Mới@2026", json);
        Assert.DoesNotContain("Mat-khau-cu-123", json);
        Assert.DoesNotContain(FakeD365Server.MfaSecret, json);
        Assert.Contains("dpapi:", json);
        var describe = step.Describe();
        Assert.DoesNotContain("Mới@2026", describe);
        Assert.DoesNotContain("dpapi", describe);

        // {{secret:Tên}} hiện rõ (không che) và lưu nguyên để công việc chép sang máy khác vẫn chạy.
        var bySecret = LoginStep("{{secret:MatKhau}}", "{{secret:Totp}}");
        shown = EditLogin(bySecret);
        Assert.Equal(("{{secret:MatKhau}}", '\0', "{{secret:Totp}}"), (shown.Password, shown.PasswordChar, shown.Totp));
        Assert.Equal(("{{secret:MatKhau}}", "{{secret:Totp}}"), (bySecret.Arguments, bySecret.RowRef));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=x", true)]
    [InlineData("https://LOGIN.microsoft.com/", true)]
    [InlineData("https://adfs.contoso.com/adfs/ls/?client-request-id=1", true)]
    [InlineData("https://sso.contoso.vn/login", true)]
    [InlineData("http://login.microsoftonline.com/", false)]                    // không https
    [InlineData("https://login.microsoftonline.com.evil.vn/", false)]          // tên máy chỉ bắt đầu giống
    [InlineData("https://evil.vn/login.microsoftonline.com/", false)]          // chuỗi nằm trong đường dẫn
    [InlineData("https://evil.vn/?next=https://login.microsoftonline.com", false)]
    [InlineData("https://login.microsoftonline.com@evil.vn/", false)]          // tên máy thật là evil.vn
    [InlineData("https://login.live.com/", false)]
    [InlineData("https://adfs.contoso.com.evil.vn/adfs/ls", false)]
    [InlineData("http://adfs.contoso.com/adfs/ls", false)]                     // máy đã khai báo nhưng không https
    [InlineData("http://localhost:5000/adfs/ls/", false)]                       // trang giả lập khi chưa được kiểm thử cho phép
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void LoginPageMustBeMicrosoftOrConfiguredHostOverHttps(string url, bool trusted)
    {
        string[] configured = ["adfs.contoso.com", " https://sso.contoso.vn/abc ", "evil.vn/path", ""];
        Assert.Equal(trusted, D365Client.IsTrustedLoginPage(url, configured));
    }

    [Fact]
    public void TestOnlyLoginOriginAllowsExactlyThatOrigin()
    {
        Assert.False(D365Client.IsTrustedLoginPage("http://localhost:5000/adfs/ls/", []));
        using (new TrustedTestLogin("http://localhost:5000/"))
        {
            Assert.True(D365Client.IsTrustedLoginPage("http://localhost:5000/adfs/ls/", []));
            Assert.False(D365Client.IsTrustedLoginPage("http://localhost:5001/adfs/ls/", []));
            Assert.False(D365Client.IsTrustedLoginPage("http://127.0.0.1:5000/adfs/ls/", []));
        }
        Assert.False(D365Client.IsTrustedLoginPage("http://localhost:5000/adfs/ls/", []));
    }

    [LiveFact]
    public async Task D365LoginFromEncryptedAndOldPlaintextStepsOnlyOnTrustedPage()
    {
        using var server = new FakeD365Server();
        await using var browser = await LiveBrowser.StartAsync(9377, server.BaseUrl + "adfs/ls/");
        var d = new D365Steps();
        using var log = new LogLines();

        // Trang đăng nhập giả lập ở http://localhost — không phải trang Microsoft: điền email được, mật khẩu thì không.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            d.Do(D365Action.Login, "test@contoso.vn", Protector.Protect(FakeD365Server.Password), timeout: 15000));
        Assert.Contains("không phải trang đăng nhập Microsoft", ex.Message);
        Assert.Contains("user:test@contoso.vn", server.LoginEvents);
        Assert.DoesNotContain("pass", server.LoginEvents);
        Assert.Equal("", await d.Js("document.querySelector('[name=passwd]').value"));

        using (new TrustedTestLogin(server.BaseUrl))
        {
            // Bước mới: mật khẩu và khóa TOTP mã hóa trong bước.
            await LiveBrowser.GoAsync(server.BaseUrl + "adfs/ls/");
            await d.Do(D365Action.Login, "mfa@contoso.vn", Protector.Protect(FakeD365Server.Password),
                rowRef: Protector.Protect(FakeD365Server.MfaSecret), timeout: 30000);
            Assert.Contains("otp:ok", server.LoginEvents);
            Assert.DoesNotContain("otp:bad", server.LoginEvents);
            await d.Do(D365Action.GetUser, variable: "u");
            Assert.Equal("Người kiểm thử", d["u"]);

            // Bước cũ: mật khẩu chữ thường vẫn chạy.
            await LiveBrowser.GoAsync(server.BaseUrl + "adfs/ls/");
            await d.Do(D365Action.Login, "test@contoso.vn", FakeD365Server.Password, timeout: 20000);
            Assert.Equal(["user:test@contoso.vn", "pass", "kmsi"], server.LoginEvents.TakeLast(3));

            // Chuỗi mã hóa ở máy / tài khoản khác: báo rõ, không gửi mật khẩu rỗng.
            await LiveBrowser.GoAsync(server.BaseUrl + "adfs/ls/");
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                d.Do(D365Action.Login, "test@contoso.vn", "dpapi:" + Convert.ToBase64String(new byte[64]), timeout: 15000));
            Assert.Contains("Không giải mã được mật khẩu", ex.Message);
        }
        Assert.DoesNotContain(log.Lines, l => l.Contains(FakeD365Server.Password) || l.Contains(FakeD365Server.MfaSecret));
    }

    // ───────────────────────────── {{secret:Tên}} trong biến môi trường ─────────────────────────────

    [Fact]
    public async Task EnvironmentVariableSecretIsResolvedMaskedAndNotReExpanded()
    {
        const string secret = "mk-moi-truong-B4a-987";
        SecretStore.Set("B4aMatKhau", secret);
        try
        {
            var job = new Job
            {
                Name = "Biến môi trường",
                Steps =
                [
                    S(StepType.Assert, s => { s.Condition = ConditionKind.Compare; s.Target = "{{mk}}"; s.CompareOp = CompareOp.Equals; s.Arguments = secret; }),
                    S(StepType.LogMessage, s => s.Text = "Mật khẩu: {{mk}} · ghép: {{ghep}} · khác: {{khac}}")
                ]
            };
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mk"] = "{{secret:B4aMatKhau}}",
                ["ghep"] = "user:{{ secret:b4amatkhau }}",
                ["khac"] = "{{today}}"                                          // chỉ thay bí mật, biến khác giữ nguyên chữ
            };
            using var log = new LogLines();
            var runner = new FlowRunner(new FakeUi(), _ => null);
            var r = await runner.EnqueueAsync(job, "thử", new RunOptions { Variables = vars });
            Assert.True(r!.Ok, r.Message);
            var line = Assert.Single(log.Lines, l => l.Contains("📝 Mật khẩu:"));
            Assert.DoesNotContain(secret, line);
            Assert.Contains("Mật khẩu: *** · ghép: user:*** · khác: {{today}}", line);
            Assert.DoesNotContain(log.Lines, l => l.Contains(secret));

            // Bí mật chưa có → lần chạy báo lỗi rõ ràng (không gõ "{{secret:…}}" vào ứng dụng).
            var missing = await runner.EnqueueAsync(job, "thử", new RunOptions { Variables = new() { ["mk"] = "{{secret:B4aKhongCo}}" } });
            Assert.False(missing!.Ok);
            Assert.Contains("Chưa có bí mật \"B4aKhongCo\"", missing.Message);
        }
        finally
        {
            SecretStore.Remove("B4aMatKhau");
        }
    }

    [Fact]
    public void ExpandSecretsOnlyTouchesSecretPlaceholders()
    {
        SecretStore.Set("B4aKhoa", "gia-tri-{{today}}");
        try
        {
            Assert.Equal("a gia-tri-{{today}} b {{x}}", VariableExpander.ExpandSecrets("a {{secret:B4aKhoa}} b {{x}}"));
            Assert.Equal("không có gì", VariableExpander.ExpandSecrets("không có gì"));
            Assert.Equal("", VariableExpander.ExpandSecrets(""));
            Assert.Throws<InvalidOperationException>(() => VariableExpander.ExpandSecrets("{{secret:B4aKhongCo}}"));
        }
        finally
        {
            SecretStore.Remove("B4aKhoa");
        }
    }
}

/// <summary>Cho phép trang đăng nhập của máy chủ giả lập (http://localhost:…) nhận mật khẩu trong lúc kiểm thử.</summary>
internal sealed class TrustedTestLogin : IDisposable
{
    private readonly string _origin;

    public TrustedTestLogin(string baseUrl)
    {
        _origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        lock (D365Client.TestLoginOrigins) D365Client.TestLoginOrigins.Add(_origin);
    }

    public void Dispose()
    {
        lock (D365Client.TestLoginOrigins) D365Client.TestLoginOrigins.Remove(_origin);
    }
}
