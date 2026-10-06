using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using ScheduleApp.Services.Testing;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Che bí mật ở mọi nơi kết quả đi ra (log, lịch sử, cột Kết quả, báo cáo kiểm thử, thông báo, gửi AI) và gọi mạng an toàn
/// (xác thực chỉ tới đúng máy chủ, không qua http, chuyển hướng bỏ xác thực, có hạn chờ). Máy chủ thật trên 127.0.0.1, không gọi dịch vụ ngoài.
/// </summary>
public class RedactionAndNetworkTests
{
    private static string NewSecret(string prefix) => prefix + Guid.NewGuid().ToString("N");

    /// <summary>Chuỗi DPAPI hỏng / của tài khoản khác — không giải mã được trên máy này.</summary>
    private static string Undecryptable() => "dpapi:" + Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

    // ───────────────────────────── Bộ che ─────────────────────────────

    [Fact]
    public void RedactsLongestFirstAndUrlEncodedForm()
    {
        var shortOne = NewSecret("Ab1-");
        var longOne = shortOne + "-TAIL-9f";
        Log.Mask(shortOne);
        Log.Mask(longOne);
        var text = Log.Redact($"a={longOne} b={shortOne}");
        Assert.Equal("a=*** b=***", text);                       // không còn sót "-TAIL-9f"

        var withSymbols = "p@ss/" + Guid.NewGuid().ToString("N")[..8] + "!";
        Log.Mask(withSymbols);
        Assert.Equal("u?pw=***", Log.Redact("u?pw=" + Uri.EscapeDataString(withSymbols)));
    }

    [Fact]
    public void DecryptedCredentialIsMaskedWhenDecrypted()
    {
        var secret = NewSecret("Tq9Dec_");   // không trùng mảnh bí mật ngắn test khác đã đăng ký che (vd "k-1")
        var stored = Protector.Protect(secret);
        Assert.Contains(secret, Log.Redact(secret));            // chưa giải mã → chưa biết để che
        Assert.True(Protector.TryUnprotect(stored, out var plain));
        Assert.Equal(secret, plain);
        Assert.Equal("x *** y", Log.Redact($"x {secret} y"));
    }

    // ───────────────────────────── Che trước khi cắt ─────────────────────────────

    [Fact]
    public async Task SecretSplitByLogTruncationIsStillMasked()
    {
        var secret = NewSecret("Zq9Cut-");
        SecretStore.Set("rn_cut", secret);
        var lines = new List<string>();
        void OnLog(string l) { lock (lines) lines.Add(l); }
        Log.Written += OnLog;
        try
        {
            // Giá trị biến dài 490 ký tự + bí mật: log cắt ở 500 ký tự → trước đây còn 10 ký tự đầu của bí mật.
            var job = new Job { Name = "cắt", Steps = [SetVar("x", VarSource.Value, new string('a', 490) + "{{secret:rn_cut}}")] };
            var (r, _) = await RunAsync(job);
            Assert.True(r.Ok, r.Message);
        }
        finally { Log.Written -= OnLog; SecretStore.Remove("rn_cut"); }
        lock (lines)
        {
            Assert.Contains(lines, l => l.Contains("{{x}} = \"aaa", StringComparison.Ordinal) && l.Contains("***", StringComparison.Ordinal));
            Assert.All(lines, l => Assert.DoesNotContain(secret[..9], l));
        }
    }

    [Fact]
    public async Task ReportStepDescriptionIsRedactedBeforeShortening()
    {
        var secret = NewSecret("Desc-Pw-") + NewSecret("-"); // > 50 ký tự: Describe() cắt giữa bí mật
        SecretStore.Set("rn_desc", secret);
        try
        {
            var job = new Job { Name = "mô tả", Steps = [S(StepType.LogMessage, s => s.Text = "{{secret:rn_desc}}")] };
            var recorder = new TestRecorder(null);
            var ctx = new FlowContext(job, new FakeUi(), RunOptions.Default.With(recorder), _ => null, CancellationToken.None);
            var r = await FlowEngine.RunAsync(job, ctx, 0, true);
            Assert.True(r.Ok, r.Message);
            var step = Assert.Single(recorder.Steps);
            Assert.DoesNotContain(secret[..12], step.Description);
            Assert.Contains("***", step.Description);
        }
        finally { SecretStore.Remove("rn_desc"); }
    }

    // ───────────────────────────── Nơi kết quả đi ra ─────────────────────────────

    [Fact]
    public void HistoryAndTestReportAreRedacted()
    {
        var secret = NewSecret("Hist-");
        Log.Mask(secret);

        var record = new RunRecord { JobId = Guid.NewGuid(), JobName = "h", Start = DateTime.Now, End = DateTime.Now, Message = $"Lỗi ở bước 1: sai {secret}" };
        RunHistory.Add(record);
        Assert.DoesNotContain(secret, RunHistory.All.Last(x => x.JobId == record.JobId).Message);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(DataDir, "history.jsonl")));

        var folder = NewDir();
        var result = new TestCaseResult
        {
            JobId = Guid.NewGuid(), Name = "kịch bản", Start = DateTime.Now, End = DateTime.Now, Ok = false,
            Message = "Lỗi " + secret, FirstFailure = "lần đầu " + secret, DataLabel = "dòng 1: " + secret,
            Steps = [new StepRecord { Number = 1, Description = "Ghi: " + secret, Detail = "chi tiết " + secret, Ok = false }]
        };
        var html = TestReport.Write(folder, "bộ", [result]);
        var junit = File.ReadAllText(Path.Combine(folder, "junit.xml"));
        var page = File.ReadAllText(html);
        Assert.DoesNotContain(secret, junit);
        Assert.DoesNotContain(secret, page);
        Assert.Contains("***", junit);
        Assert.Contains("***", page);
    }

    [Fact]
    public async Task RunResultLastResultHistoryAndWebhookAreRedacted()
    {
        var secret = NewSecret("Run-");
        SecretStore.Set("rn_run", secret);
        using var hook = new CaptureServer(_ => (200, "{}", null));
        var saved = SettingsStore.Current.Webhook;
        SettingsStore.Current.Webhook = new WebhookSettings { Enabled = true, Url = Protector.Protect(hook.BaseUrl + "hook") };
        try
        {
            var job = new Job
            {
                Name = "kết quả",
                NotifyMode = NotifyMode.Always,
                Steps = [S(StepType.StopFlow, s => { s.Force = true; s.Text = "Sai mật khẩu {{secret:rn_run}}"; })]
            };
            var ui = new RecordingUi();
            var runner = new FlowRunner(ui, _ => null);
            var finished = new List<string>();
            runner.JobFinished += (_, _, _, message) => { lock (finished) finished.Add(message); };
            var r = await runner.EnqueueAsync(job, "thử");
            Assert.False(r!.Ok);

            lock (finished) Assert.DoesNotContain(secret, Assert.Single(finished));       // → Job.LastResult, jobs.json
            Assert.DoesNotContain(secret, RunHistory.All.Last(x => x.JobId == job.Id).Message);
            lock (ui.Notifications) Assert.All(ui.Notifications, n => Assert.DoesNotContain(secret, n));

            var post = await hook.WaitForAsync(q => q.Path == "/hook");               // gửi thông báo chạy nền
            Assert.DoesNotContain(secret, post.Body);
            Assert.Contains("Sai mật khẩu ***", JsonNode.Parse(post.Body)!["text"]!.GetValue<string>());
        }
        finally
        {
            SettingsStore.Current.Webhook = saved;
            SecretStore.Remove("rn_run");
        }
    }

    [Fact]
    public async Task NotifyStepRedactsTitleAndBody()
    {
        var secret = NewSecret("Ntf-");
        SecretStore.Set("rn_ntf", secret);
        using var hook = new CaptureServer(_ => (200, "{}", null));
        var saved = SettingsStore.Current.Webhook;
        SettingsStore.Current.Webhook = new WebhookSettings { Enabled = true, Url = hook.BaseUrl + "n" };
        try
        {
            var job = new Job { Name = "báo", Steps = [S(StepType.Notify, s => { s.Target = "Tiêu đề {{secret:rn_ntf}}"; s.Text = "Mật khẩu là {{secret:rn_ntf}}"; })] };
            var (r, _) = await RunAsync(job);
            Assert.True(r.Ok, r.Message);
            var post = Assert.Single(hook.Requests);
            Assert.DoesNotContain(secret, post.Body);
            Assert.Equal("Tiêu đề ***\nMật khẩu là ***", JsonNode.Parse(post.Body)!["text"]!.GetValue<string>());
        }
        finally
        {
            SettingsStore.Current.Webhook = saved;
            SecretStore.Remove("rn_ntf");
        }
    }

    // ───────────────────────────── Không giải mã được ─────────────────────────────

    [Fact]
    public async Task UndecryptableCredentialsGiveClearErrorAndSendNothing()
    {
        using var server = new CaptureServer(_ => (200, "{}", null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NotificationService.SendWebhookAsync(new WebhookSettings { Enabled = true, Url = Undecryptable() }, "t", "b"));
        Assert.Contains("Không giải mã được URL webhook", ex.Message);
        Assert.Contains("nhập lại trong ⚙ Cài đặt", ex.Message);

        ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NotificationService.SendTelegramAsync(new TelegramSettings { Enabled = true, BotToken = Undecryptable(), ChatId = "1" }, "t", "b", null));
        Assert.Contains("Không giải mã được token bot Telegram", ex.Message);

        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NotificationService.SendEmailAsync(
            new EmailSettings { Host = "127.0.0.1", Port = 1, UseSsl = true, User = "u@x.vn", Password = Undecryptable(), To = "a@x.vn" }, "t", "b", null));
        Assert.Contains("Không giải mã được mật khẩu email", ex.Message);

        var conn = new ApiConnection { Name = "hỏng", BaseUrl = server.BaseUrl, Auth = ApiAuthType.Bearer, Secret = Undecryptable() };
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiClient.SendAsync("GET", "x", conn, "", "", 5000, CancellationToken.None));
        Assert.Contains("Không giải mã được mật khẩu / token của kết nối \"hỏng\"", ex.Message);

        var headers = new ApiConnection { Name = "hdr", BaseUrl = server.BaseUrl, Headers = Undecryptable() };
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiClient.SendAsync("GET", "x", headers, "", "", 5000, CancellationToken.None));
        Assert.Contains("Không giải mã được header của kết nối \"hdr\"", ex.Message);
        Assert.Empty(server.Requests);                                   // không gửi chuỗi rỗng thay cho token

        // Chưa từng nhập (rỗng) → như cũ: báo "chưa nhập", không phải lỗi giải mã.
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NotificationService.SendWebhookAsync(new WebhookSettings { Enabled = true, Url = "" }, "t", "b"));
        Assert.Equal("Chưa nhập URL webhook.", ex.Message);

        // Header kết nối lưu mã hóa (bản mới) vẫn được đọc và gửi đúng.
        var secretHeader = new ApiConnection { Name = "enc", BaseUrl = server.BaseUrl, Headers = Protector.Protect("X-Thu: gia-tri") };
        var ok = await ApiClient.SendAsync("GET", "y", secretHeader, "", "", 5000, CancellationToken.None);
        Assert.Equal(200, ok.Status);
        Assert.Equal("gia-tri", Assert.Single(server.Requests).Headers["X-Thu"]);
    }

    [Fact]
    public async Task SmtpWithoutTlsIsRefusedExceptLocalhost()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NotificationService.SendEmailAsync(
            new EmailSettings { Host = "smtp.example.invalid", Port = 25, UseSsl = false, To = "a@x.vn", From = "b@x.vn" }, "t", "b", null));
        Assert.Contains("SSL/TLS", ex.Message);
        Assert.True(NotificationService.IsLoopback("localhost"));
        Assert.True(NotificationService.IsLoopback("127.0.0.1"));
        Assert.True(NotificationService.IsLoopback("[::1]"));
        Assert.False(NotificationService.IsLoopback("smtp.gmail.com"));

        // Chốt chặn: tắt SSL chỉ được với máy chủ trên máy này hoặc khi đã tick "Cho phép gửi không mã hóa".
        EmailSettings Smtp(string host, bool ssl, bool allow) => new() { Host = host, Port = 25, UseSsl = ssl, AllowNoTls = allow };
        Assert.Null(NotificationService.SmtpTlsProblem(Smtp("smtp.congty.vn", ssl: true, allow: false)));
        Assert.Null(NotificationService.SmtpTlsProblem(Smtp("localhost", ssl: false, allow: false)));
        Assert.Null(NotificationService.SmtpTlsProblem(Smtp(" 127.0.0.1 ", ssl: false, allow: false)));
        Assert.Null(NotificationService.SmtpTlsProblem(Smtp("relay.congty.local", ssl: false, allow: true)));
        Assert.Contains("relay.congty.local", NotificationService.SmtpTlsProblem(Smtp("relay.congty.local", ssl: false, allow: false)));

        // Đã cho phép → qua được chốt chặn, lỗi tiếp theo là không kết nối được (không phải lỗi SSL/TLS).
        var allowed = await Assert.ThrowsAnyAsync<Exception>(() => NotificationService.SendEmailAsync(
            new EmailSettings { Host = "127.0.0.2", Port = 1, UseSsl = false, AllowNoTls = true, To = "a@x.vn", From = "b@x.vn" }, "t", "b", null));
        Assert.DoesNotContain("SSL/TLS", allowed.Message);
    }

    [Fact]
    public async Task WebhookOverPlainHttpIsRefusedExceptLocalhost()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NotificationService.SendWebhookAsync(new WebhookSettings { Enabled = true, Url = "http://hooks.example.invalid/services/khoa" }, "t", "b"));
        Assert.Contains("https://", ex.Message);
        Assert.DoesNotContain("khoa", ex.Message);                       // không nhắc lại đường dẫn chứa khóa

        using var hook = new CaptureServer(_ => (200, "{}", null));
        await NotificationService.SendWebhookAsync(new WebhookSettings { Enabled = true, Url = hook.BaseUrl + "local" }, "t", "b");
        Assert.Single(hook.Requests);
    }

    [Fact]
    public void ScreenshotsAreNotSentByDefaultButSavedChoiceIsKept()
    {
        var fresh = new AppSettings();
        Assert.False(fresh.Telegram.SendScreenshot);
        Assert.False(fresh.Email.AttachScreenshot);

        // settings.json của bản cũ (chưa có dấu phiên bản) còn bật sẵn ảnh lỗi → tắt đúng một lần.
        var path = Path.Combine(NewDir(), "settings.json");
        File.WriteAllText(path, """{"Telegram":{"SendScreenshot":true},"Email":{"AttachScreenshot":true}}""");
        var old = SettingsStore.Load(path);
        Assert.True(SettingsStore.Upgrade(old));                        // có đổi → lưu ngay
        Assert.False(old.Telegram.SendScreenshot);
        Assert.False(old.Email.AttachScreenshot);
        Assert.Equal(SettingsStore.CurrentVersion, old.SettingsVersion);

        // Người dùng bật lại sau khi chuyển đổi (file có dấu phiên bản) → giữ nguyên lựa chọn.
        old.Telegram.SendScreenshot = old.Email.AttachScreenshot = true;
        File.WriteAllText(path, JsonSerializer.Serialize(old, JsonDefaults.Options));
        var again = SettingsStore.Load(path);
        Assert.False(SettingsStore.Upgrade(again));
        Assert.True(again.Telegram.SendScreenshot);
        Assert.True(again.Email.AttachScreenshot);

        // Lần đầu dùng / không có gì để tắt → chỉ đặt dấu phiên bản, không cần ghi file ngay.
        var none = new AppSettings();
        Assert.False(SettingsStore.Upgrade(none));
        Assert.Equal(SettingsStore.CurrentVersion, none.SettingsVersion);
    }

    // ───────────────────────────── Xác thực chỉ tới đúng máy chủ ─────────────────────────────

    [Fact]
    public async Task ConnectionCredentialsAreNotSentToAnotherHost()
    {
        using var server = new CaptureServer(_ => (200, "{}", null));
        var port = new Uri(server.BaseUrl).Port;
        var token = NewSecret("Bear-");
        // 127.0.0.1 và localhost cùng trỏ về máy chủ thử này, nhưng là hai TÊN máy chủ khác nhau — so đúng tên như trình duyệt,
        // nên dùng được một máy chủ thật để kiểm tra "máy chủ khác" mà không cần mạng ngoài.
        var conn = new ApiConnection { Name = "c", BaseUrl = server.BaseUrl, Auth = ApiAuthType.Bearer, Secret = Protector.Protect(token), Headers = "X-Tenant: t1" };

        var ok = await ApiClient.SendAsync("GET", "same", conn, "", "", 5000, CancellationToken.None);
        Assert.Equal(200, ok.Status);
        Assert.Equal("Bearer " + token, server.Requests[0].Headers["Authorization"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApiClient.SendAsync("GET", $"http://localhost:{port}/other", conn, "", "", 5000, CancellationToken.None));
        Assert.Contains("khác máy chủ của kết nối", ex.Message);
        Assert.Single(server.Requests);                                 // không có yêu cầu nào tới localhost

        // Kết nối không có URL gốc → không biết máy chủ nào được nhận token.
        var noBase = new ApiConnection { Name = "nb", Auth = ApiAuthType.Bearer, Secret = Protector.Protect(token) };
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiClient.SendAsync("GET", server.BaseUrl + "z", noBase, "", "", 5000, CancellationToken.None));
        Assert.Contains("chưa có URL gốc", ex.Message);
        Assert.Single(server.Requests);

        // Kết nối không xác thực, không header → URL đầy đủ tới máy khác vẫn được (không có gì để lộ).
        var plain = new ApiConnection { Name = "p", BaseUrl = server.BaseUrl };
        Assert.Equal(200, (await ApiClient.SendAsync("GET", $"http://localhost:{port}/free", plain, "", "", 5000, CancellationToken.None)).Status);

        // Cùng tên máy nhưng khác cổng là máy chủ khác.
        Assert.False(ApiClient.CredentialsAllowed(conn, new Uri($"http://127.0.0.1:{(port == 65535 ? port - 1 : port + 1)}/x"), out var reason));
        Assert.Contains("khác máy chủ", reason);
        var tls = new ApiConnection { Name = "t", BaseUrl = "https://api.example.invalid/", Auth = ApiAuthType.Bearer };
        Assert.False(ApiClient.CredentialsAllowed(tls, new Uri("https://api.example.invalid:8443/x"), out _));
        Assert.True(ApiClient.CredentialsAllowed(tls, new Uri("https://api.example.invalid:443/x"), out _));   // cổng mặc định ghi rõ
    }

    [Fact]
    public async Task NoCredentialsOverPlainHttpExceptLocalhost()
    {
        var conn = new ApiConnection { Name = "web", BaseUrl = "http://api.example.invalid/", Auth = ApiAuthType.Bearer, Secret = Protector.Protect(NewSecret("H-")) };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiClient.SendAsync("GET", "x", conn, "", "", 5000, CancellationToken.None));
        Assert.Contains("http://", ex.Message);
        Assert.Contains("https://", ex.Message);

        var https = new ApiConnection { Name = "s", BaseUrl = "https://api.example.invalid/", Auth = ApiAuthType.Bearer };
        Assert.True(ApiClient.CredentialsAllowed(https, new Uri("https://api.example.invalid/v1/a"), out _));
        Assert.False(ApiClient.CredentialsAllowed(https, new Uri("http://api.example.invalid/v1/a"), out _));
        // Tài khoản Windows: máy nội bộ tên một chữ (http://crmserver/) như vùng Intranet; tên miền đầy đủ phải dùng https.
        var windows = new ApiConnection { Name = "w", BaseUrl = "http://crmserver/org/", Auth = ApiAuthType.Windows };
        Assert.True(ApiClient.CredentialsAllowed(windows, new Uri("http://crmserver/org/api"), out _));
        var windowsFqdn = new ApiConnection { Name = "w2", BaseUrl = "http://crm.contoso.com/", Auth = ApiAuthType.Windows };
        Assert.False(ApiClient.CredentialsAllowed(windowsFqdn, new Uri("http://crm.contoso.com/api"), out _));
        // Dynamics 365 on-premises trong mạng nội bộ (tên miền nội bộ / IP riêng) qua http: NTLM không gửi mật khẩu → cho phép.
        foreach (var onPrem in new[] { "http://crm.contoso.local/", "http://10.1.2.3/", "http://192.168.1.20:5555/", "http://crm.corp/" })
            Assert.True(ApiClient.CredentialsAllowed(new ApiConnection { Name = "op", BaseUrl = onPrem, Auth = ApiAuthType.Windows }, new Uri(onPrem + "api"), out _), onPrem);
        Assert.False(ApiClient.IsIntranetHost("8.8.8.8"));
        Assert.False(ApiClient.IsIntranetHost("crm.local.evil.com"));
        // Bearer qua http tới máy nội bộ vẫn bị chặn (token gửi nguyên văn); kết nối chỉ có header thường thì được.
        Assert.False(ApiClient.CredentialsAllowed(new ApiConnection { Name = "b", BaseUrl = "http://crm.contoso.local/", Auth = ApiAuthType.Bearer }, new Uri("http://crm.contoso.local/api"), out _));
        Assert.True(ApiClient.CredentialsAllowed(new ApiConnection { Name = "h", BaseUrl = "http://api.example.invalid/" }, new Uri("http://api.example.invalid/x"), out _,
            [("OData-Version", "4.0")]));

        // Token OAuth không lấy qua http tới máy khác.
        var oauth = new ApiConnection { Name = "o", BaseUrl = "https://api.example.invalid/", Auth = ApiAuthType.OAuthClientCredentials, User = "id", TokenUrl = "http://login.example.invalid/token" };
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiClient.GetTokenAsync(oauth, CancellationToken.None));
        Assert.Contains("http://", ex.Message);
    }

    [Fact]
    public async Task CrossHostRedirectDropsAuthorizationAndApiKey()
    {
        CaptureServer? server = null;
        server = new CaptureServer(q => q.Path switch
        {
            "/start" => (302, "", new() { ["Location"] = $"http://localhost:{new Uri(server!.BaseUrl).Port}/landing" }),
            "/same" => (307, "", new() { ["Location"] = "/landing-same" }),
            _ => (200, "{\"ok\":true}", null)
        });
        using var _ = server;
        var key = NewSecret("Key-");
        var stepToken = NewSecret("Step-");
        var conn = new ApiConnection { Name = "k", BaseUrl = server.BaseUrl, Auth = ApiAuthType.ApiKey, User = "x-api-key", Secret = Protector.Protect(key) };

        var r = await ApiClient.SendAsync("GET", "start", conn, $"Authorization: Bearer {stepToken}\nX-Trace: 7", "", 10_000, CancellationToken.None);
        Assert.Equal(200, r.Status);
        Assert.EndsWith("/landing", r.Url);
        Assert.Equal(2, server.Requests.Count);
        var first = server.Requests[0];
        var second = server.Requests[1];
        Assert.StartsWith("127.0.0.1", first.Headers["Host"]);
        Assert.Equal(key, first.Headers["x-api-key"]);
        Assert.Equal("Bearer " + stepToken, first.Headers["Authorization"]);
        Assert.StartsWith("localhost", second.Headers["Host"]);
        Assert.False(second.Headers.ContainsKey("x-api-key"));          // .NET tự chuyển hướng vẫn gửi header này
        Assert.False(second.Headers.ContainsKey("Authorization"));
        Assert.Equal("7", second.Headers["X-Trace"]);                   // header thường vẫn giữ

        // Chuyển hướng trong cùng máy chủ → giữ xác thực.
        r = await ApiClient.SendAsync("GET", "same", conn, "", "", 10_000, CancellationToken.None);
        Assert.Equal(200, r.Status);
        Assert.Equal(key, server.Requests[^1].Headers["x-api-key"]);
        Assert.Equal("/landing-same", server.Requests[^1].Path);
    }

    [Fact]
    public async Task WindowsCredentialsOnlyWhenConnectionChoosesWindowsAuth()
    {
        // Máy chủ đòi NTLM: client có DefaultCredentials sẽ tự trả lời bằng header Authorization: NTLM …
        using var server = new CaptureServer(_ => (401, "", new() { ["WWW-Authenticate"] = "NTLM" }));
        var none = new ApiConnection { Name = "none", BaseUrl = server.BaseUrl };
        var r = await ApiClient.SendAsync("GET", "a", none, "", "", 10_000, CancellationToken.None);
        Assert.Equal(401, r.Status);
        Assert.Single(server.Requests);
        Assert.False(server.Requests[0].Headers.ContainsKey("Authorization"));
        r = await ApiClient.SendAsync("GET", server.BaseUrl + "b", null, "", "", 10_000, CancellationToken.None);
        Assert.Equal(2, server.Requests.Count);
        Assert.False(server.Requests[1].Headers.ContainsKey("Authorization"));

        Assert.Null(Handler(ApiClient.Plain).Credentials);
        Assert.Same(CredentialCache.DefaultCredentials, Handler(ApiClient.Windows).Credentials);

        // Đối chứng: kết nối chọn "Tài khoản Windows" thì có trả lời NTLM (chỉ tới máy chủ thử trên máy này).
        var windows = new ApiConnection { Name = "win", BaseUrl = server.BaseUrl, Auth = ApiAuthType.Windows };
        await ApiClient.SendAsync("GET", "c", windows, "", "", 10_000, CancellationToken.None);
        Assert.Contains(server.Requests.Skip(2), q => q.Headers.TryGetValue("Authorization", out var a) && a.StartsWith("NTLM ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TimeoutsRedirectsAndResponseCapAreSet()
    {
        foreach (var client in new[] { ApiClient.Plain, ApiClient.Windows })
        {
            var h = Handler(client);
            Assert.False(h.AllowAutoRedirect);
            Assert.Equal(TimeSpan.FromSeconds(30), h.ConnectTimeout);
            Assert.Equal(ApiClient.MaxResponseBytes, client.MaxResponseContentBufferSize);
        }
        Assert.Equal(TimeSpan.FromMinutes(5), ApiClient.DefaultTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), AiClient.Http.Timeout);
        Assert.False(Handler(AiClient.Http).AllowAutoRedirect);        // không gửi x-api-key theo chuyển hướng
        Assert.True(AiClient.Http.MaxResponseContentBufferSize <= 16 * 1024 * 1024);

        // Phản hồi khai báo lớn hơn giới hạn → báo lỗi ngay, không đọc vào bộ nhớ.
        using var server = new CaptureServer(_ => (200, "", new() { ["Content-Length-Override"] = (ApiClient.MaxResponseBytes + 1L).ToString() }));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ApiClient.SendAsync("GET", server.BaseUrl + "big", null, "", "", 10_000, CancellationToken.None));
        Assert.Contains("quá lớn", ex.Message);

        // Hết hạn chờ của bước (máy chủ không trả lời) → báo lỗi rõ. Bước không đặt hạn dùng DefaultTimeout (5 phút, kiểm ở trên) — quá lâu để chạy thật trong test.
        using var silent = new CaptureServer(_ => (0, "", null));
        var t = await Assert.ThrowsAsync<TimeoutException>(() => ApiClient.SendAsync("GET", silent.BaseUrl + "x", null, "", "", 300, CancellationToken.None));
        Assert.Contains("không phản hồi", t.Message);
    }

    // ───────────────────────────── Bí mật sinh ra lúc chạy, giá trị ngắn, chuyển hướng gửi lại nội dung ─────────────────────────────

    [Fact]
    public async Task TokenFromApiIsMaskedBeforeItIsLogged()
    {
        // POST /login trả token → gán vào biến "token": dòng nhật ký "{{token}} = …" phải đã bị che ngay (trước khi bước sau dùng nó trong header).
        var token = NewSecret("eyJtok");
        using var server = new CaptureServer(_ => (200, $"{{\"access_token\":\"{token}\"}}", null));
        var lines = new List<string>();
        void OnLog(string l) { lock (lines) lines.Add(l); }
        Log.Written += OnLog;
        try
        {
            var job = new Job { Steps = [S(StepType.HttpRequest, s => { s.Method = "POST"; s.Target = server.BaseUrl + "login"; s.Variable = "token"; s.Arguments = "access_token"; })] };
            var (r, ctx) = await RunAsync(job);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(token, ctx.Vars["token"]);
        }
        finally { Log.Written -= OnLog; }
        lock (lines)
        {
            Assert.DoesNotContain(lines, l => l.Contains(token, StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("{{token}} = \"***\"", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ShortGuessedValuesAreNotMaskedEverywhere()
    {
        // Biến tên kiểu mật khẩu nhưng giá trị ngắn (passCount = "1", x-api-key-version 2.0) không được che toàn cục — nếu che,
        // mọi chữ "1" / "2.0" trong nhật ký, lịch sử, báo cáo đều thành ***. Bí mật đã biết chắc (kho bí mật) vẫn che từ 3 ký tự.
        var ctx = new Services.Engine.FlowContext(new Job(), new FakeUi(), Services.Engine.RunOptions.Default, _ => null, CancellationToken.None);
        ctx.SetVar("passCount", "17");
        Assert.Equal("có 17 lần", Log.Redact("có 17 lần"));
        var longOne = NewSecret("Pw-");
        ctx.SetVar("matKhau", longOne);
        Assert.Equal("x *** y", Log.Redact($"x {longOne} y"));
        Log.MaskGuessed("ab12c");
        Assert.Equal("ab12c", Log.Redact("ab12c"));
        Log.Mask("Q7z");
        Assert.Equal("***", Log.Redact("Q7z"));
    }

    [Fact]
    public async Task BodyIsNotResentToAnotherHostOnRedirect()
    {
        // POST /login có mật khẩu trong nội dung, máy chủ trả 307 sang máy khác → không gửi lại nội dung.
        var pw = NewSecret("Body-");
        using var other = new CaptureServer(_ => (200, "{}", null));
        using var server = new CaptureServer(q => q.Path == "/login" ? (307, "", new() { ["Location"] = other.BaseUrl + "steal" }) : (200, "{}", null));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApiClient.SendAsync("POST", server.BaseUrl + "login", null, "Content-Type: application/json", $"{{\"password\":\"{pw}\"}}", 5000, CancellationToken.None));
        Assert.Contains("không gửi lại nội dung", ex.Message);
        Assert.Empty(other.Requests);

        // Cùng máy chủ → vẫn đi theo 307 như trước.
        using var same = new CaptureServer(q => q.Path == "/a" ? (307, "", new() { ["Location"] = "/b" }) : (200, "{\"ok\":1}", null));
        var r = await ApiClient.SendAsync("POST", same.BaseUrl + "a", null, "", "{\"x\":1}", 5000, CancellationToken.None);
        Assert.Equal(200, r.Status);
        Assert.Equal("{\"x\":1}", same.Requests.Last().Body);
    }

    [Fact]
    public void AiPayloadHidesSecretVariablesHeaderTemplatesAndKeyParameters()
    {
        int n = 0;
        var hider = new SecretHider(_ => $"[[bi-mat-{++n}]]");
        var lit = NewSecret("Lit-");
        var setVar = hider.Step(S(StepType.SetVariable, s => { s.Variable = "matKhau"; s.VarSource = VarSource.Value; s.Text = lit; }));
        Assert.DoesNotContain(lit, setVar.Text);

        var gmaps = NewSecret("AIza");
        var call = hider.Step(S(StepType.HttpRequest, s =>
        {
            s.Target = $"https://maps.example.invalid/geo?key={gmaps}&q=ha-noi&subscription-key={gmaps}x&monkey=chuoi";
            s.Headers = "Authorization: Basic {{cred}}\nX-Trace: 1";
        }));
        Assert.DoesNotContain(gmaps, call.Target);
        Assert.Contains("monkey=chuoi", call.Target);                        // không nhầm tên có chữ "key" ở giữa
        Assert.Contains("q=ha-noi", call.Target);
        Assert.Contains("Authorization: Basic {{cred}}", call.Headers);      // AI vẫn thấy cấu trúc
        Assert.Contains("cred", hider.SecretVariables);                      // → giá trị biến cred bị ẩn
        var cred = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("an:" + NewSecret("p")));
        Assert.DoesNotContain(cred, hider.Variable(new VariableDef { Name = "cred", Value = cred }));
        Assert.Equal("Hà Nội", hider.Variable(new VariableDef { Name = "thanhPho", Value = "Hà Nội" }));
    }

    // ───────────────────────────── Gửi cho AI ─────────────────────────────

    [Fact]
    public async Task AiGeneratorPayloadHasNoSecretValuesAndRestoresThem()
    {
        var storeSecret = NewSecret("Store-");
        SecretStore.Set("rn_ai", storeSecret);
        var varPw = NewSecret("VarPw-");
        var bearer = NewSecret("Tok");
        var bodyPw = NewSecret("BodyPw-");
        var apiKey = NewSecret("Q");
        var literalPw = NewSecret("Lit-");
        const string totp = "JBSWY3DPEHPK3PXP";
        List<ActionStep> steps =
        [
            S(StepType.Dynamics, s => { s.D365Action = D365Action.Login; s.Text = "test@contoso.vn"; s.Arguments = "{{matKhau}}"; s.RowRef = totp; }),
            S(StepType.HttpRequest, s =>
            {
                s.Method = "POST"; s.Target = $"https://api.contoso.vn/v1?api_key={apiKey}&x=1";
                s.Headers = $"Authorization: Bearer {bearer}\nAccept: application/json";
                s.Text = $"{{\"user\":\"an\",\"password\":\"{bodyPw}\"}}";
            }),
            S(StepType.SetElementText, s => { s.Target = "Đăng nhập"; s.Text = "#password"; s.Arguments = literalPw; }),
            S(StepType.LogMessage, s => s.Text = "chép tay " + storeSecret)
        ];
        string[] secrets = [storeSecret, varPw, bearer, bodyPw, apiKey, literalPw, totp];

        var sentTexts = new List<string>();
        using var server = new MiniHttpServer((_, _, _, body) =>
        {
            var text = (string)JsonNode.Parse(body)!["messages"]![0]!["content"]![0]!["text"]!;
            lock (sentTexts) sentTexts.Add(text);
            // AI giả: trả lại nguyên flow đã nhận (giữ chữ giữ chỗ).
            var flow = text[text.IndexOf("\n[", StringComparison.Ordinal)..].Trim();
            var input = $$"""{"name":"n","summary":"s","steps":{{flow}},"variables":[{"name":"matKhau","value":"{{ValueOf(text, "matKhau")}}"}]}""";
            return (200, """{"id":"m1","type":"message","role":"assistant","model":"x","content":[{"type":"tool_use","id":"t1","name":"build_flow","input":""" +
                         input + """}],"stop_reason":"tool_use","usage":{"input_tokens":1,"output_tokens":1}}""");
        });
        var (endpoint, key) = (AiClient.Endpoint, SettingsStore.Current.Ai.ApiKey);
        AiClient.Endpoint = server.BaseUrl + "v1/messages";
        SettingsStore.Current.Ai.ApiKey = Protector.Protect("sk-thu-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var gen = new FlowGenerator(new FlowGenerator.Context
            {
                Steps = steps,
                Variables = [new VariableDef { Name = "matKhau", Value = varPw }, new VariableDef { Name = "url", Value = "https://contoso.vn" }]
            });
            var r = await gen.SendAsync("Sửa flow, mật khẩu kho là " + storeSecret, FlowGenerator.Mode.Replace, CancellationToken.None);

            // Mọi lần gửi (lần đầu + các lần tự sửa lỗi) đều không có giá trị bí mật nào.
            Assert.NotEmpty(server.Requests);
            foreach (var q in server.Requests)
                foreach (var s in secrets) Assert.DoesNotContain(s, q.Body);
            var first = sentTexts[0];
            Assert.Contains("[[bi-mat-", first);
            Assert.Contains("url = \"https://contoso.vn\"", first);           // giá trị thường vẫn gửi để AI hiểu
            Assert.Contains("test@contoso.vn", first);
            Assert.DoesNotContain(varPw, first);

            // Flow AI trả về được điền lại giá trị thật.
            Assert.Equal(4, r.Steps.Count);
            Assert.Equal(totp, r.Steps[0].RowRef);
            Assert.Equal("{{matKhau}}", r.Steps[0].Arguments);
            Assert.Equal(steps[1].Headers, r.Steps[1].Headers);
            Assert.Equal(steps[1].Text, r.Steps[1].Text);
            Assert.Equal(steps[1].Target, r.Steps[1].Target);
            Assert.Equal(literalPw, r.Steps[2].Arguments);
            Assert.Equal(steps[3].Text, r.Steps[3].Text);
            Assert.Equal(varPw, Assert.Single(r.Variables).Value);
        }
        finally
        {
            (AiClient.Endpoint, SettingsStore.Current.Ai.ApiKey) = (endpoint, key);
            SecretStore.Remove("rn_ai");
        }
    }

    /// <summary>Giá trị biến trong dòng "Biến đã khai báo: tên = \"…\"" của tin nhắn gửi AI.</summary>
    private static string ValueOf(string text, string name)
    {
        int i = text.IndexOf(name + " = \"", StringComparison.Ordinal) + name.Length + 4;
        return text[i..text.IndexOf('"', i)];
    }

    // ───────────────────────────── Hỗ trợ ─────────────────────────────

    private static SocketsHttpHandler Handler(HttpClient client) =>
        (SocketsHttpHandler)typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;

    private sealed class RecordingUi : IUserNotifier
    {
        public List<string> Notifications { get; } = [];
        public Task ShowReminderAsync(string title, string message, bool waitForUser, CancellationToken ct) => Task.CompletedTask;
        public void Notify(string title, string text, bool isError) { lock (Notifications) Notifications.Add(title + " | " + text); }
        public IDisposable ClearScreenForAutomation() => new Noop();
        public Task<string?> PromptAsync(string title, string message, string defaultValue, bool password, CancellationToken ct) => Task.FromResult<string?>(defaultValue);
        public Task<DebugCommand> DebugPauseAsync(string jobName, int stepIndex, string stepText, string reason, IReadOnlyDictionary<string, string> variables, CancellationToken ct) =>
            Task.FromResult(DebugCommand.Continue);
        public Task<bool> AskContinueAsync(string title, string message, CancellationToken ct) => Task.FromResult(true);
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}

/// <summary>
/// Máy chủ HTTP tối giản trên 127.0.0.1 ghi lại từng yêu cầu (kể cả header Host) và trả về mã / nội dung / header tùy ý
/// (chuyển hướng, WWW-Authenticate…). Mã 0 = nhận yêu cầu nhưng không trả lời (thử quá thời gian).
/// </summary>
internal sealed class CaptureServer : IDisposable
{
    public sealed record Req(string Method, string Path, Dictionary<string, string> Headers, string Body);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<Req, (int Status, string Body, Dictionary<string, string>? Headers)> _handler;
    private readonly List<Req> _requests = [];

    public string BaseUrl { get; }

    public List<Req> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    public CaptureServer(Func<Req, (int, string, Dictionary<string, string>?)> handler)
    {
        _handler = handler;
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Chờ tới khi có yêu cầu thỏa điều kiện (yêu cầu gửi chạy nền).</summary>
    public async Task<Req> WaitForAsync(Func<Req, bool> match)
    {
        for (int i = 0; i < 200; i++)
        {
            var found = Requests.FirstOrDefault(match);
            if (found != null) return found;
            await Task.Delay(50);
        }
        throw new TimeoutException("Máy chủ thử không nhận được yêu cầu mong đợi.");
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                // Giữ kết nối cho nhiều yêu cầu (NTLM cần cùng kết nối).
                var pending = new List<byte>();
                var chunk = new byte[8192];
                while (true)
                {
                    int headerEnd;
                    while ((headerEnd = IndexOf(pending, "\r\n\r\n"u8)) < 0)
                    {
                        int n = await stream.ReadAsync(chunk, _cts.Token);
                        if (n == 0) return;
                        pending.AddRange(chunk.AsSpan(0, n).ToArray());
                    }
                    var head = Encoding.ASCII.GetString(pending.GetRange(0, headerEnd).ToArray()).Split("\r\n");
                    pending.RemoveRange(0, headerEnd + 4);
                    var parts = head[0].Split(' ');
                    var headers = head.Skip(1).Select(l => l.Split(':', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

                    byte[] body;
                    if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                    {
                        var decoded = new List<byte>();
                        while (true)
                        {
                            int lineEnd;
                            while ((lineEnd = IndexOf(pending, "\r\n"u8)) < 0) await ReadMoreAsync(stream, pending, chunk);
                            int size = Convert.ToInt32(Encoding.ASCII.GetString(pending.GetRange(0, lineEnd).ToArray()).Split(';')[0].Trim(), 16);
                            while (pending.Count < lineEnd + 2 + size + 2) await ReadMoreAsync(stream, pending, chunk);
                            decoded.AddRange(pending.GetRange(lineEnd + 2, size));
                            pending.RemoveRange(0, lineEnd + 2 + size + 2);
                            if (size == 0) break;
                        }
                        body = [.. decoded];
                    }
                    else
                    {
                        int length = headers.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
                        while (pending.Count < length) await ReadMoreAsync(stream, pending, chunk);
                        body = pending.GetRange(0, length).ToArray();
                        pending.RemoveRange(0, length);
                    }

                    var req = new Req(parts[0], parts[1], headers, Encoding.UTF8.GetString(body));
                    lock (_requests) _requests.Add(req);
                    var (status, respBody, extra) = _handler(req);
                    if (status == 0)
                    {
                        await Task.Delay(Timeout.Infinite, _cts.Token);
                        return;
                    }
                    var bytes = Encoding.UTF8.GetBytes(respBody);
                    var sb = new StringBuilder($"HTTP/1.1 {status} X\r\nContent-Type: application/json; charset=utf-8\r\n");
                    var length2 = extra != null && extra.TryGetValue("Content-Length-Override", out var o) ? o : bytes.Length.ToString();
                    sb.Append($"Content-Length: {length2}\r\n");
                    foreach (var (k, v) in extra ?? [])
                        if (k != "Content-Length-Override") sb.Append($"{k}: {v}\r\n");
                    sb.Append("\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _cts.Token);
                    await stream.WriteAsync(bytes, _cts.Token);
                    if (extra?.ContainsKey("Content-Length-Override") == true) return; // nội dung giả khai báo lớn: đóng kết nối
                }
            }
            catch (Exception) { /* client đóng kết nối / máy chủ dừng */ }
        }
    }

    private async Task ReadMoreAsync(NetworkStream stream, List<byte> pending, byte[] chunk)
    {
        int n = await stream.ReadAsync(chunk, _cts.Token);
        if (n == 0) throw new IOException("Kết nối đã đóng.");
        pending.AddRange(chunk.AsSpan(0, n).ToArray());
    }

    private static int IndexOf(List<byte> data, ReadOnlySpan<byte> pattern)
    {
        for (int i = 0; i <= data.Count - pattern.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length && ok; j++) ok = data[i + j] == pattern[j];
            if (ok) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
