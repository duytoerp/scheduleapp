using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Data;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

public class JsonPathTests
{
    private const string Json = """
        {"@odata.context":"x","value":[
          {"accountid":"a-1","name":"Công ty Ánh Dương","revenue":1500.5,"active":true,"_owner_value@OData.Community.Display.V1.FormattedValue":"Lê B","tags":["vip","hn"]},
          {"accountid":"a-2","name":"B","revenue":null,"active":false,"tags":[]}
        ],"paging":{"next":null,"total":2}}
        """;

    [Theory]
    [InlineData("value[0].name", "Công ty Ánh Dương")]
    [InlineData("$.value[1].accountid", "a-2")]
    [InlineData("value[-1].name", "B")]
    [InlineData("value[0].revenue", "1500.5")]
    [InlineData("value[1].revenue", "")]
    [InlineData("value[0].active", "true")]
    [InlineData("value[*].name", "Công ty Ánh Dương\nB")]
    [InlineData("value.length", "2")]
    [InlineData("value[0].tags", "[\"vip\",\"hn\"]")]
    [InlineData("value[0]['_owner_value@OData.Community.Display.V1.FormattedValue']", "Lê B")]
    [InlineData("paging.total", "2")]
    [InlineData("PAGING.TOTAL", "2")]
    public void Select(string path, string expected) => Assert.Equal(expected, JsonPath.Select(Json, path));

    [Fact]
    public void HelpfulErrors()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonPath.Select(Json, "value[0].ten"));
        Assert.Contains("accountid", ex.Message); // liệt kê các khóa có sẵn
        Assert.Throws<InvalidOperationException>(() => JsonPath.Select(Json, "value[5]"));
        Assert.Throws<InvalidOperationException>(() => JsonPath.Select("<html>", "a"));
    }
}

/// <summary>Máy chủ HTTP tối giản trên localhost để kiểm thử bước "Gọi API" (không cần mạng).</summary>
internal sealed class MiniHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, string, Dictionary<string, string>, string, (int Status, string Body)> _handler;

    public List<(string Method, string Path, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];
    public string BaseUrl { get; }

    public MiniHttpServer(Func<string, string, Dictionary<string, string>, string, (int, string)> handler)
    {
        _handler = handler;
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _ = Task.Run(LoopAsync);
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
            var stream = client.GetStream();
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = await stream.ReadAsync(chunk);
                if (n == 0) return;
                buffer.Write(chunk, 0, n);
                headerEnd = IndexOf(buffer.ToArray(), "\r\n\r\n"u8.ToArray());
            }
            var all = buffer.ToArray();
            var head = Encoding.ASCII.GetString(all, 0, headerEnd).Split("\r\n");
            var parts = head[0].Split(' ');
            var headers = head.Skip(1).Select(l => l.Split(':', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            int length = headers.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
            while (all.Length - headerEnd - 4 < length)
            {
                int n = await stream.ReadAsync(chunk);
                if (n == 0) break;
                buffer.Write(chunk, 0, n);
                all = buffer.ToArray();
            }
            var body = Encoding.UTF8.GetString(all, headerEnd + 4, Math.Min(length, all.Length - headerEnd - 4));
            if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                // Thân gửi theo từng khúc (vd JsonContent của PostAsJsonAsync): đọc tới khúc rỗng cuối cùng rồi ghép lại.
                while (IndexOf(all.AsSpan(headerEnd + 4).ToArray(), "0\r\n\r\n"u8.ToArray()) < 0)
                {
                    int n = await stream.ReadAsync(chunk);
                    if (n == 0) break;
                    buffer.Write(chunk, 0, n);
                    all = buffer.ToArray();
                }
                var raw = all.AsSpan(headerEnd + 4).ToArray();
                var decoded = new MemoryStream();
                for (int pos = 0; pos < raw.Length;)
                {
                    int lineEnd = IndexOf(raw[pos..], "\r\n"u8.ToArray());
                    if (lineEnd < 0) break;
                    int size = Convert.ToInt32(Encoding.ASCII.GetString(raw, pos, lineEnd).Split(';')[0].Trim(), 16);
                    if (size == 0) break;
                    decoded.Write(raw, pos + lineEnd + 2, size);
                    pos += lineEnd + 2 + size + 2;
                }
                body = Encoding.UTF8.GetString(decoded.ToArray());
            }
            lock (Requests) Requests.Add((parts[0], parts[1], headers, body));
            var (status, respBody) = _handler(parts[0], parts[1], headers, body);
            var bytes = Encoding.UTF8.GetBytes(respBody);
            var resp = $"HTTP/1.1 {status} X\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
            await stream.WriteAsync(bytes);
        }
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i <= data.Length - pattern.Length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        return -1;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

public class ApiClientTests
{
    [Fact]
    public async Task HttpStepWithOAuthConnectionAndJsonExtraction()
    {
        int tokenCalls = 0;
        using var server = new MiniHttpServer((method, path, headers, body) =>
        {
            if (path.StartsWith("/token"))
            {
                tokenCalls++;
                return WebUtility.UrlDecode(body).Contains("client_secret=biết mật")
                    ? (200, "{\"access_token\":\"TOKEN-123\",\"expires_in\":3600}")
                    : (401, "{\"error\":\"invalid_client\",\"error_description\":\"sai secret\"}");
            }
            if (headers.GetValueOrDefault("Authorization") != "Bearer TOKEN-123") return (401, "{\"error\":{\"message\":\"Chưa đăng nhập\"}}");
            if (method == "POST" && path == "/api/data/v9.2/accounts")
                return (201, body);
            if (path.StartsWith("/api/data/v9.2/accounts"))
                return (200, "{\"value\":[{\"accountid\":\"guid-1\",\"name\":\"Ánh Dương\"}]}");
            return (404, "{\"error\":{\"code\":\"0x80060888\",\"message\":\"Resource not found for the segment 'acounts'.\"}}");
        });

        SettingsStore.Current.ApiConnections =
        [
            new ApiConnection
            {
                Name = "D365", BaseUrl = server.BaseUrl + "api/data/v9.2/", Auth = ApiAuthType.OAuthClientCredentials,
                User = "client-id", Secret = Protector.Protect("biết mật"), TokenUrl = server.BaseUrl + "token",
                Headers = "OData-Version: 4.0\nAccept: application/json"
            }
        ];

        var job = new Job
        {
            Name = "api",
            Variables = [new VariableDef { Name = "ten", Value = "Công ty \"Mới\"" }],
            Steps =
            [
                S(StepType.HttpRequest, s => { s.Connection = "D365"; s.Target = "accounts?$select=name&$top=1"; s.Arguments = "value[0].accountid"; s.Variable = "id"; }),
                S(StepType.HttpRequest, s =>
                {
                    s.Connection = "d365"; s.Method = "POST"; s.Target = "accounts"; s.Headers = "Prefer: return=representation";
                    s.Text = "{\"name\": \"{{ten:json}}\"}"; s.Arguments = "name"; s.Variable = "tenMoi";
                }),
                S(StepType.HttpRequest, s => { s.Connection = "D365"; s.Target = "acounts"; s.Force = true; s.Variable = "loi"; }),
                S(StepType.HttpRequest, s => { s.Connection = "D365"; s.Target = "acounts"; s.OnError = ErrorAction.Continue; })
            ]
        };
        var (r, c) = await RunAsync(job);
        Assert.Equal("guid-1", c.Vars["id"]);
        Assert.Equal("Công ty \"Mới\"", c.Vars["tenMoi"]);
        Assert.Equal("404", c.Vars["http.status"]);
        Assert.Contains("Resource not found", c.Vars["loi"]);            // Force: không lỗi, giữ nguyên nội dung trả về
        Assert.Contains("Resource not found", c.Vars["lastError"]);      // không Force: báo lỗi kèm thông điệp OData
        Assert.False(r.Ok);
        Assert.Equal(1, tokenCalls);                                      // token được dùng lại
        var post = server.Requests.Single(q => q.Method == "POST" && q.Path.Contains("accounts"));
        Assert.Equal("return=representation", post.Headers["Prefer"]);
        Assert.Equal("4.0", post.Headers["OData-Version"]);
        Assert.StartsWith("application/json", post.Headers["Content-Type"]);
        Assert.Equal("Công ty \"Mới\"", JsonDocument.Parse(post.Body).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task BasicAuthAndErrors()
    {
        using var server = new MiniHttpServer((_, _, headers, _) => (200, "{\"auth\":\"" + headers.GetValueOrDefault("Authorization") + "\"}"));
        var conn = new ApiConnection { Name = "b", BaseUrl = server.BaseUrl, Auth = ApiAuthType.Basic, User = "an", Secret = Protector.Protect("p@ss") };
        var r = await ApiClient.SendAsync("GET", "x", conn, "", "", 10_000, CancellationToken.None);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("an:p@ss")), JsonPath.Select(r.Body, "auth"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiClient.SendAsync("GET", "khong-phai-url", null, "", "", 1000, CancellationToken.None));
        Assert.Throws<FormatException>(() => ApiClient.ParseHeaders("thiếu dấu hai chấm").ToList());
        SettingsStore.Current.ApiConnections = [];
        Assert.Throws<InvalidOperationException>(() => ApiClient.FindConnection("khongco"));
    }

    [Theory]
    [InlineData("https://org.crm.dynamics.com/api/data/v9.2/", "accounts", "https://org.crm.dynamics.com/api/data/v9.2/accounts")]
    [InlineData("https://org.crm.dynamics.com/api/data/v9.2", "/accounts", "https://org.crm.dynamics.com/api/data/v9.2/accounts")]
    [InlineData("https://a.com/", "https://b.com/x", "https://b.com/x")]
    [InlineData("", "https://b.com/x", "https://b.com/x")]
    public void CombineUrls(string baseUrl, string url, string expected) => Assert.Equal(expected, ApiClient.Combine(baseUrl, url));
}

public class UpdateServiceTests
{
    [Fact]
    public async Task FolderSourceFindsNewerVersionAndVerifiesHash()
    {
        var share = NewDir();
        var exeBytes = Encoding.UTF8.GetBytes("ban-moi");
        File.WriteAllBytes(Path.Combine(share, "ScheduleApp.exe"), exeBytes);
        var sha = Convert.ToHexString(SHA256.HashData(exeBytes));
        File.WriteAllText(Path.Combine(share, "version.json"), $"{{\"version\":\"v99.1.0\",\"url\":\"ScheduleApp.exe\",\"notes\":\"Sửa lỗi\",\"sha256\":\"{sha}\"}}");

        var info = await UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = share });
        Assert.NotNull(info);
        Assert.Equal(new Version(99, 1, 0), info.Version);
        Assert.Equal("Sửa lỗi", info.Notes);
        var downloaded = await UpdateService.DownloadAsync(info, null, CancellationToken.None);
        Assert.Equal(exeBytes, File.ReadAllBytes(downloaded));

        var bad = info with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.DownloadAsync(bad, null, CancellationToken.None));

        File.WriteAllText(Path.Combine(share, "version.json"), "{\"version\":\"1.0\"}");
        Assert.Null(await UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = Path.Combine(share, "version.json") }));
    }

    [Fact]
    public async Task HttpManifestResolvesRelativeUrl()
    {
        using var server = new MiniHttpServer((_, path, _, _) => path == "/rel/version.json" ? (200, "{\"version\":\"50.0\",\"url\":\"files/ScheduleApp.exe\"}") : (404, ""));
        var info = await UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = server.BaseUrl + "rel/version.json" });
        Assert.Equal(server.BaseUrl + "rel/files/ScheduleApp.exe", info!.DownloadUrl);
    }

    [Fact]
    public void DebugBuildCannotSelfUpdate() => Assert.False(UpdateService.CanSelfUpdate(out _)); // testhost.exe không phải ScheduleApp.exe

    [Fact]
    public async Task UpdateScriptWaitsForAppThenSwapsFiles()
    {
        var dir = Path.Combine(NewDir(), "Thư mục cài");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "ScheduleApp.exe");
        var newExe = Path.Combine(dir, "moi.exe");
        File.WriteAllText(target, "cũ");
        File.WriteAllText(newExe, "mới");

        // Tiến trình "ứng dụng cũ" còn chạy ~2 giây — script phải chờ nó thoát rồi mới thay file.
        using var app = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        var script = Path.Combine(dir, "update.cmd");
        File.WriteAllText(script, UpdateService.BuildUpdateScript(newExe, target, app.Id, restart: false), new UTF8Encoding(false));
        using var run = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false })!;

        await Task.Delay(500);
        Assert.Equal("cũ", File.ReadAllText(target)); // chưa thay khi ứng dụng còn chạy
        await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(app.HasExited);
        Assert.Equal("mới", File.ReadAllText(target));
        Assert.Equal("cũ", File.ReadAllText(target + ".old"));
        Assert.False(File.Exists(newExe));
        Assert.False(File.Exists(script)); // script tự xóa
    }
}

public class VersionsTests
{
    [Fact]
    public void KeepsPreviousContentOnChange()
    {
        var job = new Job { Name = "Bản 1", Steps = [S(StepType.Wait)] };
        JobStore.Save([job]);
        job.LastRun = DateTime.Now;          // chỉ đổi lần chạy → không tạo phiên bản
        JobStore.Save([job]);
        Assert.Empty(JobVersions.List(job.Id));

        job.Name = "Bản 2";
        job.Steps.Add(S(StepType.Wait));
        JobStore.Save([job]);
        var versions = JobVersions.List(job.Id);
        Assert.Single(versions);
        Assert.Equal("Bản 1", versions[0].Job.Name);
        Assert.Single(versions[0].Job.Steps);
        Assert.NotNull(job.LastRun); // không bị xóa khi so sánh
    }
}

public class MailTests
{
    [Fact]
    public void FilterIgnoresCaseAndDiacritics()
    {
        var m = new IncomingMail("id", "Đơn hàng mới #123", "orders@shop.vn", "Phòng Bán Hàng", "nội dung", new DateTime(2026, 10, 1, 9, 30, 0), ["C:\\a.pdf"], "C:\\");
        Assert.True(MailWatcher.Matches(m, "don hang", ""));
        Assert.True(MailWatcher.Matches(m, "", "phong ban"));
        Assert.True(MailWatcher.Matches(m, "#123", "shop.vn"));
        Assert.False(MailWatcher.Matches(m, "hóa đơn", ""));
        Assert.False(MailWatcher.Matches(m, "đơn", "khac@x.com"));
        var v = m.ToVariables();
        Assert.Equal("01/10/2026 09:30", v["email.date"]);
        Assert.Equal("1", v["email.attachmentCount"]);
    }
}

public class SecurityTests
{
    [Fact]
    public void DpapiRoundTripAndSecretStore()
    {
        var enc = Protector.Protect("mật khẩu 123");
        Assert.StartsWith("dpapi:", enc);
        Assert.Equal("mật khẩu 123", Protector.Unprotect(enc));
        SecretStore.Set("ApiKey", "k-1");
        Assert.Equal("k-1", SecretStore.Get("apikey"));
    }
}
