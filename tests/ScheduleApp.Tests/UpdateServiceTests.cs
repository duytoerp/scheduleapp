using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Máy chủ HTTPS tối giản trên 127.0.0.1 với chứng chỉ tự ký riêng (chỉ <see cref="Client"/> tin) — kiểm thử nguồn cập nhật https
/// và API GitHub giả mà không ra mạng.
/// </summary>
internal sealed class TlsTestServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly X509Certificate2 _cert;
    private readonly Func<string, (int Status, byte[] Body)> _handler;

    /// <summary>Các yêu cầu đã nhận: đường dẫn, Accept, Authorization.</summary>
    public List<(string Path, string Accept, string Auth)> Requests { get; } = [];
    public string BaseUrl { get; }
    public HttpClient Client { get; }

    public TlsTestServer(Func<string, (int, byte[])> handler)
    {
        _handler = handler;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        using var temp = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        // SChannel cần khóa riêng nạp từ PFX (khóa tạm bị xóa khi Dispose).
        _cert = X509CertificateLoader.LoadPkcs12(temp.Export(X509ContentType.Pfx), null);
        var thumb = _cert.Thumbprint;
        Client = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, c, _, _) => c != null && c.GetCertHashString() == thumb }
        }) { Timeout = TimeSpan.FromSeconds(20) };
        _listener.Start();
        BaseUrl = $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
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
            try
            {
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(_cert);
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n") && await ssl.ReadAsync(one) == 1) head.Append((char)one[0]);
                var lines = head.ToString().Split("\r\n");
                var path = lines[0].Split(' ')[1];
                string Header(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";
                lock (Requests) Requests.Add((path, Header("Accept"), Header("Authorization")));
                var (status, body) = _handler(path);
                var header = $"HTTP/1.1 {status} X\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(header));
                await ssl.WriteAsync(body);
                await ssl.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        Client.Dispose();
        _cert.Dispose();
    }
}

public class UpdateServiceTests : IDisposable
{
    private readonly HttpClient _originalHttp = UpdateService.Http;
    private readonly List<string> _downloadDirs = [];

    /// <summary>File exe thật có thông tin phiên bản nhưng không ký nhúng (chỉ ký qua catalog của Windows).</summary>
    private static readonly string Hostname = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "hostname.exe");

    /// <summary>ScheduleApp.exe vừa build (apphost) — chưa ký số, đóng vai "bản đang chạy chưa ký".</summary>
    private static readonly string UnsignedApp = Path.Combine(AppContext.BaseDirectory, "ScheduleApp.exe");

    /// <summary>File ký nhúng bằng chứng chỉ ".NET" (đi kèm runtime đang chạy kiểm thử).</summary>
    private static readonly string SignedDotNet = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "createdump.exe");

    /// <summary>File ký nhúng bằng chứng chỉ khác ("Microsoft Corporation").</summary>
    private static readonly string SignedOther = Path.Combine(AppContext.BaseDirectory, "testhost.exe");

    private static Version FileVersionOf(string file)
    {
        var fv = FileVersionInfo.GetVersionInfo(file);
        return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        UpdateService.Http = _originalHttp;
        foreach (var d in _downloadDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (IOException) { }
    }

    private async Task<UpdateDownload> Download(UpdateInfo info, string? running = null)
    {
        var d = await UpdateService.DownloadAsync(info, null, CancellationToken.None, running ?? UnsignedApp);
        _downloadDirs.Add(d.Directory);
        return d;
    }

    /// <summary>Thư mục tải về đang có (tên ScheduleApp-update-…) — để kiểm tra lần tải lỗi đã dọn thư mục riêng của nó.</summary>
    private static HashSet<string> UpdateDirs() =>
        Directory.GetDirectories(Path.GetTempPath(), "ScheduleApp-update-*").ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ───────────────────────────── Nguồn thư mục / https ─────────────────────────────

    [Fact]
    public async Task FolderSourceFindsNewerVersionAndVerifiesHashVersionAndSignature()
    {
        var share = NewDir();
        var exeBytes = File.ReadAllBytes(Hostname);
        var version = FileVersionOf(Hostname);
        File.WriteAllBytes(Path.Combine(share, "ScheduleApp.exe"), exeBytes);
        File.WriteAllText(Path.Combine(share, "version.json"), $"{{\"version\":\"v{version}\",\"url\":\"ScheduleApp.exe\",\"notes\":\"Sửa lỗi\",\"sha256\":\"{Sha(exeBytes).ToLowerInvariant()}\"}}");

        var info = await UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = share });
        Assert.NotNull(info);
        Assert.Equal(version, info.Version);
        Assert.Equal("Sửa lỗi", info.Notes);
        Assert.Equal("version.json", info.HashSource);

        Assert.False(Authenticode.Inspect(UnsignedApp).Valid);
        var first = await Download(info);
        var second = await Download(info);
        Assert.Equal(exeBytes, File.ReadAllBytes(first.File));
        Assert.NotEqual(first.Directory, second.Directory); // mỗi lần tải một thư mục tạm riêng, tên ngẫu nhiên
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "ScheduleApp-update-"), first.Directory);
        Assert.Equal(Sha(exeBytes), first.Sha256);
        Assert.False(first.Signed);
        Assert.Equal("chưa ký số — chỉ kiểm SHA-256", first.Signature);

        var before = UpdateDirs();
        var bad = info with { Sha256 = new string('0', 64) };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Download(bad));
        Assert.Contains("SHA-256", ex.Message);
        Assert.True(UpdateDirs().IsSubsetOf(before)); // thư mục của lần tải lỗi đã bị xóa

        File.WriteAllText(Path.Combine(share, "version.json"), "{\"version\":\"1.0\"}");
        Assert.Null(await UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = Path.Combine(share, "version.json") }));
    }

    [Fact]
    public async Task MissingHashIsRefused()
    {
        var share = NewDir();
        File.Copy(Hostname, Path.Combine(share, "ScheduleApp.exe"));
        File.WriteAllText(Path.Combine(share, "version.json"), $"{{\"version\":\"{FileVersionOf(Hostname)}\",\"url\":\"ScheduleApp.exe\"}}");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = share }));
        Assert.Contains("sha256", ex.Message);

        File.WriteAllText(Path.Combine(share, "version.json"), $"{{\"version\":\"{FileVersionOf(Hostname)}\",\"sha256\":\"abc\"}}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = share }));

        // Gọi thẳng DownloadAsync với thông tin không có mã: từ chối trước khi tải.
        var noHash = new UpdateInfo(FileVersionOf(Hostname), Path.Combine(share, "ScheduleApp.exe"), "", null, false);
        var before = UpdateDirs();
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Download(noHash));
        Assert.Contains("SHA-256", ex.Message);
        Assert.True(UpdateDirs().IsSubsetOf(before));
    }

    [Theory]
    [InlineData("https://ban-moi.example.com/version.json", true)]
    [InlineData(@"\\may-chu\chia-se\ScheduleApp", false)]
    [InlineData(@"C:\Cap nhat\version.json", false)]
    [InlineData(@"D:\Cap nhat", false)]
    public void HttpsAndLocalSourcesAreAccepted(string source, bool web) => Assert.Equal(web, UpdateService.IsWebSource(source));

    [Theory]
    [InlineData("http://ban-moi.example.com/version.json")]
    [InlineData("HTTP://ban-moi.example.com/")]
    [InlineData("ftp://ban-moi.example.com/version.json")]
    public void PlainHttpAndOtherSchemesAreRefused(string source) =>
        Assert.Throws<InvalidOperationException>(() => UpdateService.IsWebSource(source));

    [Fact]
    public async Task HttpSourceIsRefusedWithoutAnyRequest()
    {
        using var server = new MiniHttpServer((_, _, _, _) => (200, "{\"version\":\"50.0\",\"sha256\":\"" + new string('A', 64) + "\"}"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = server.BaseUrl + "rel/version.json" }));
        Assert.Contains("https://", ex.Message);
        Assert.Empty(server.Requests);

        var info = new UpdateInfo(new Version(50, 0, 0), server.BaseUrl + "ScheduleApp.exe", "", new string('A', 64), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Download(info));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task HttpsManifestResolvesRelativeUrlAndDownloads()
    {
        var exeBytes = File.ReadAllBytes(Hostname);
        var version = FileVersionOf(Hostname);
        string manifest = $"{{\"version\":\"{version}\",\"url\":\"files/ScheduleApp.exe\",\"sha256\":\"{Sha(exeBytes)}\"}}";
        using var server = new TlsTestServer(path => path switch
        {
            "/rel/version.json" => (200, Encoding.UTF8.GetBytes(manifest)),
            "/rel/files/ScheduleApp.exe" => (200, exeBytes),
            _ => (404, [])
        });
        UpdateService.Http = server.Client;

        var info = await UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = server.BaseUrl + "rel/version.json" });
        Assert.Equal(server.BaseUrl + "rel/files/ScheduleApp.exe", info!.DownloadUrl);
        var d = await Download(info);
        Assert.Equal(exeBytes, File.ReadAllBytes(d.File));

        // version.json trên https nhưng trỏ file exe qua http:// → từ chối.
        manifest = $"{{\"version\":\"{version}\",\"url\":\"http://127.0.0.1:1/ScheduleApp.exe\",\"sha256\":\"{Sha(exeBytes)}\"}}";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = server.BaseUrl + "rel/version.json" }));
        Assert.Contains("https://", ex.Message);
    }

    // ───────────────────────────── GitHub Releases (API giả trên https cục bộ) ─────────────────────────────

    private static string Release(string baseUrl, Version version, string? digest, bool withManifest) =>
        $$"""
        {"tag_name":"v{{version}}","body":"Ghi chú","assets":[
          {"name":"ScheduleApp.exe","url":"{{baseUrl}}assets/1"{{(digest != null ? $",\"digest\":\"sha256:{digest}\"" : "")}}}
          {{(withManifest ? $",{{\"name\":\"version.json\",\"url\":\"{baseUrl}assets/2\"}}" : "")}}
        ]}
        """;

    [Fact]
    public async Task GitHubUsesDigestThenVersionJsonElseRefuses()
    {
        var exeBytes = File.ReadAllBytes(Hostname);
        var version = FileVersionOf(Hostname);
        string release = "", manifest = "";
        using var server = new TlsTestServer(path => path switch
        {
            "/repos/chu/repo/releases/latest" => (200, Encoding.UTF8.GetBytes(release)),
            "/assets/1" => (200, exeBytes),
            "/assets/2" => (200, Encoding.UTF8.GetBytes(manifest)),
            _ => (404, [])
        });
        UpdateService.Http = server.Client;
        var settings = new UpdateSettings { Source = "github:chu/repo", Token = Protector.Protect("tok-123") };

        // 1) GitHub có digest của file → dùng digest.
        release = Release(server.BaseUrl, version, Sha(exeBytes).ToLowerInvariant(), withManifest: true);
        var info = await UpdateService.CheckAsync(CancellationToken.None, settings, server.BaseUrl);
        Assert.Equal(Sha(exeBytes), info!.Sha256!.ToUpperInvariant());
        Assert.Equal("digest của GitHub", info.HashSource);
        Assert.DoesNotContain(server.Requests, r => r.Path == "/assets/2");
        var d = await Download(info);
        Assert.Equal(exeBytes, File.ReadAllBytes(d.File));
        var exeRequest = server.Requests.Last(r => r.Path == "/assets/1");
        Assert.Equal("application/octet-stream", exeRequest.Accept);
        Assert.Equal("Bearer tok-123", exeRequest.Auth);

        // 2) Không có digest → lấy sha256 trong version.json của cùng bản phát hành.
        release = Release(server.BaseUrl, version, null, withManifest: true);
        manifest = $"{{\"version\":\"{version}\",\"url\":\"ScheduleApp.exe\",\"sha256\":\"{Sha(exeBytes)}\"}}";
        info = await UpdateService.CheckAsync(CancellationToken.None, settings, server.BaseUrl);
        Assert.Equal(Sha(exeBytes), info!.Sha256);
        Assert.Equal("version.json của bản phát hành", info.HashSource);
        var manifestRequest = server.Requests.Last(r => r.Path == "/assets/2");
        Assert.Equal("application/octet-stream", manifestRequest.Accept);
        Assert.Equal("Bearer tok-123", manifestRequest.Auth);

        // 3) version.json ghi bản khác tag → từ chối.
        manifest = $"{{\"version\":\"99.0.0\",\"sha256\":\"{Sha(exeBytes)}\"}}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.CheckAsync(CancellationToken.None, settings, server.BaseUrl));

        // 4) version.json không có sha256, hoặc không có version.json → từ chối.
        manifest = $"{{\"version\":\"{version}\"}}";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.CheckAsync(CancellationToken.None, settings, server.BaseUrl));
        Assert.Contains("SHA-256", ex.Message);
        release = Release(server.BaseUrl, version, null, withManifest: false);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.CheckAsync(CancellationToken.None, settings, server.BaseUrl));
        Assert.Contains("SHA-256", ex.Message);

        // Bản trên GitHub không mới hơn → không báo, không đòi mã.
        release = Release(server.BaseUrl, UpdateService.Current, null, withManifest: false);
        Assert.Null(await UpdateService.CheckAsync(CancellationToken.None, settings, server.BaseUrl));

        // API GitHub qua http:// → từ chối.
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.CheckAsync(CancellationToken.None, settings, "http://127.0.0.1:1/"));
    }

    // ───────────────────────────── Phiên bản trong file tải về ─────────────────────────────

    [Fact]
    public async Task RelabelledOrOlderExeIsRefused()
    {
        var share = NewDir();
        var exeBytes = File.ReadAllBytes(Hostname);
        File.WriteAllBytes(Path.Combine(share, "ScheduleApp.exe"), exeBytes);
        // File thật là bản 10.0.x nhưng version.json ghi 99.1.0 (file cũ bị đổi nhãn).
        File.WriteAllText(Path.Combine(share, "version.json"), $"{{\"version\":\"99.1.0\",\"sha256\":\"{Sha(exeBytes)}\"}}");
        var info = await UpdateService.CheckAsync(CancellationToken.None, new UpdateSettings { Source = share });
        var before = UpdateDirs();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Download(info!));
        Assert.Contains($"là bản {FileVersionOf(Hostname)}, không phải bản 99.1.0", ex.Message);
        Assert.True(UpdateDirs().IsSubsetOf(before));

        // File đúng bản đang chạy (ScheduleApp.dll vừa build) → không mới hơn.
        var current = Path.Combine(NewDir(), "ScheduleApp.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ScheduleApp.dll"), current);
        var bytes = File.ReadAllBytes(current);
        ex = Assert.Throws<InvalidOperationException>(() => UpdateService.VerifyDownloaded(current, UpdateService.Current, Sha(bytes), UnsignedApp));
        Assert.Contains("không mới hơn", ex.Message);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Download(new UpdateInfo(UpdateService.Current, current, "", Sha(bytes), false)));
        Assert.Contains("không mới hơn", ex.Message);

        // File không có thông tin phiên bản (không phải exe).
        var text = Path.Combine(NewDir(), "ScheduleApp.exe");
        File.WriteAllText(text, "khong-phai-exe");
        ex = Assert.Throws<InvalidOperationException>(() =>
            UpdateService.VerifyDownloaded(text, new Version(99, 0, 0), Sha(File.ReadAllBytes(text)), UnsignedApp));
        Assert.Contains("thông tin phiên bản", ex.Message);
    }

    // ───────────────────────────── Chữ ký số Authenticode ─────────────────────────────

    [Fact]
    public void AuthenticodeReadsEmbeddedSignatures()
    {
        var dotnet = Authenticode.Inspect(SignedDotNet);
        Assert.True(dotnet.Valid, dotnet.Error);
        Assert.Contains("CN=.NET", dotnet.Subject);
        var other = Authenticode.Inspect(SignedOther);
        Assert.True(other.Valid, other.Error);
        Assert.NotEqual(dotnet.Thumbprint, other.Thumbprint);

        var unsigned = Authenticode.Inspect(UnsignedApp);
        Assert.False(unsigned.Valid);
        Assert.Equal("không có chữ ký số", unsigned.Error);
        Assert.False(Authenticode.Inspect(Hostname).Valid); // chỉ ký qua catalog của Windows, không ký nhúng

        // Sửa một byte giữa file đã ký → chữ ký không còn khớp.
        var tampered = Path.Combine(NewDir(), "createdump.exe");
        var bytes = File.ReadAllBytes(SignedDotNet);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(tampered, bytes);
        Assert.False(Authenticode.Inspect(tampered).Valid);
    }

    [Fact]
    public void SignedRunningExeRequiresSameCertificate()
    {
        var dir = NewDir();
        var sameCert = Path.Combine(dir, "ScheduleApp.exe");
        File.Copy(SignedDotNet, sameCert);
        var (signed, text) = UpdateService.CheckSignature(SignedDotNet, sameCert);
        Assert.True(signed);
        Assert.Contains("cùng chứng chỉ", text);

        var otherCert = Path.Combine(dir, "khac.exe");
        File.Copy(SignedOther, otherCert);
        var ex = Assert.Throws<InvalidOperationException>(() => UpdateService.CheckSignature(SignedDotNet, otherCert));
        Assert.Contains("chứng chỉ khác", ex.Message);

        var unsignedCopy = Path.Combine(dir, "chua-ky.exe");
        File.Copy(UnsignedApp, unsignedCopy);
        ex = Assert.Throws<InvalidOperationException>(() => UpdateService.CheckSignature(SignedDotNet, unsignedCopy));
        Assert.Contains("không có chữ ký số", ex.Message);

        var tampered = Path.Combine(dir, "sua.exe");
        var bytes = File.ReadAllBytes(SignedDotNet);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(tampered, bytes);
        Assert.Throws<InvalidOperationException>(() => UpdateService.CheckSignature(SignedDotNet, tampered));

        // Bản đang chạy chưa ký → chỉ dựa vào SHA-256, không chặn.
        (signed, text) = UpdateService.CheckSignature(UnsignedApp, unsignedCopy);
        Assert.False(signed);
        Assert.Equal("chưa ký số — chỉ kiểm SHA-256", text);
        (signed, _) = UpdateService.CheckSignature(UnsignedApp, sameCert);
        Assert.True(signed);
    }

    // ───────────────────────────── Script thay file + kiểm tra bản mới khởi động được ─────────────────────────────

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
        using var run = Process.Start(new ProcessStartInfo(UpdateService.SystemCmd, $"/d /c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false })!;

        await Task.Delay(500);
        Assert.Equal("cũ", File.ReadAllText(target)); // chưa thay khi ứng dụng còn chạy
        await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(app.HasExited);
        Assert.Equal("mới", File.ReadAllText(target));
        Assert.Equal("cũ", File.ReadAllText(target + ".old"));
        Assert.False(File.Exists(newExe));
        Assert.False(File.Exists(script)); // script tự xóa
    }

    /// <summary>Hộp cát: thư mục cài (tên có dấu + ngoặc), bản cũ, thư mục tải về chứa bản mới, script — rồi chạy script thật.</summary>
    private sealed record Sandbox(string Target, string OldBytesFrom, string DownloadDir, string NewExe, string Marker, string Script);

    private static Sandbox MakeSandbox(string appName, string oldExe, string newExe, int healthSeconds, string launchArgs, int waitPid)
    {
        var dir = Path.Combine(NewDir(), "Thư mục cài (x86)");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, appName + ".exe");
        File.Copy(oldExe, target);
        var download = Path.Combine(NewDir(), "ScheduleApp-update-thu");
        Directory.CreateDirectory(download);
        var fresh = Path.Combine(download, "ScheduleApp.exe");
        File.Copy(newExe, fresh);
        var marker = Path.Combine(download, "started.ok");
        var script = Path.Combine(download, "update.cmd");
        File.WriteAllText(script, UpdateService.BuildUpdateScript(fresh, target, waitPid, restart: true, markerPath: marker,
            healthSeconds: healthSeconds, cleanupDir: download, newVersion: "2.9.0", launchArgs: launchArgs.Replace("{marker}", marker)), new UTF8Encoding(false));
        return new Sandbox(target, oldExe, download, fresh, marker, script);
    }

    private static Process OldApp() =>
        Process.Start(new ProcessStartInfo(UpdateService.SystemCmd, "/d /c ping -n 2 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;

    private static Process RunScript(Sandbox s) =>
        Process.Start(new ProcessStartInfo(UpdateService.SystemCmd, $"/d /c \"{s.Script}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() })!;

    private static void KillByName(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
                try { p.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [Fact]
    public async Task NewVersionExitingWithoutMarkerRollsBackToOldVersion()
    {
        var cmd = UpdateService.SystemCmd;
        using var app = OldApp();
        // "Bản mới" = bản sao cmd.exe thoát ngay (không tạo file đánh dấu); "bản cũ" = hostname.exe chạy xong tự thoát, không mở cửa sổ.
        var s = MakeSandbox("UngDungThuA", Hostname, cmd, healthSeconds: 30, launchArgs: "/d /c exit 3", app.Id);
        var watch = Stopwatch.StartNew();
        using var run = RunScript(s);
        await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(25), $"phải nhận ra bản mới đã thoát, không chờ hết giờ ({watch.Elapsed})");
        Assert.Equal(File.ReadAllBytes(Hostname), File.ReadAllBytes(s.Target)); // đã khôi phục bản cũ
        Assert.False(File.Exists(s.Target + ".old"));
        Assert.False(File.Exists(s.Target + ".failed"));
        Assert.False(Directory.Exists(s.DownloadDir)); // dọn thư mục tải về (kể cả script)

        var note = UpdateService.TakeRollbackNote(s.Target);
        Assert.NotNull(note);
        Assert.Contains("Bản mới 2.9.0 không khởi động được", note);
        Assert.Contains("đã thoát ngay khi mở", note);
        Assert.Contains("đã quay về bản cũ", note);
        Assert.Null(UpdateService.TakeRollbackNote(s.Target)); // chỉ báo một lần
    }

    [Fact]
    public async Task NewVersionWritingMarkerKeepsNewAndRemovesOld()
    {
        var cmd = UpdateService.SystemCmd;
        using var app = OldApp();
        // "Bản mới" = bản sao cmd.exe tạo file đánh dấu (như ScheduleApp sau khi khởi động xong) rồi thoát.
        var s = MakeSandbox("UngDungThuB", Hostname, cmd, healthSeconds: 30, launchArgs: "/d /c copy /y nul \"{marker}\"", app.Id);
        using var run = RunScript(s);
        await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(app.HasExited);
        Assert.Equal(File.ReadAllBytes(cmd), File.ReadAllBytes(s.Target)); // giữ bản mới
        Assert.False(File.Exists(s.Target + ".old"));                       // bản cũ chỉ xóa sau khi bản mới báo khởi động xong
        Assert.False(File.Exists(s.Target + UpdateService.RollbackNoteSuffix));
        Assert.False(Directory.Exists(s.DownloadDir));
    }

    [Fact]
    public async Task HungNewVersionIsStoppedAndRolledBack()
    {
        var cmd = UpdateService.SystemCmd;
        const string name = "UngDungThuC";
        using var app = OldApp();
        // "Bản mới" chạy mãi (vòng lặp trong cmd, không mở tiến trình con) mà không báo khởi động xong;
        // PID ghi vào started.ok.pid như ScheduleApp làm lúc mở.
        var s = MakeSandbox(name, Hostname, cmd, healthSeconds: 4, launchArgs: "/d /c \"for /l %%i in (0,0,1) do @rem\"", app.Id);
        try
        {
            using var run = RunScript(s);
            Process? hung = null;
            for (int i = 0; i < 100 && hung == null; i++)
            {
                hung = Process.GetProcessesByName(name).FirstOrDefault();
                if (hung == null) await Task.Delay(100);
            }
            Assert.NotNull(hung);
            File.WriteAllText(s.Marker + ".pid", hung.Id.ToString());
            await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

            Assert.True(hung.WaitForExit(5000), "script phải dừng bản mới bị treo");
            Assert.Equal(File.ReadAllBytes(Hostname), File.ReadAllBytes(s.Target));
            Assert.False(File.Exists(s.Target + ".old"));
            Assert.False(File.Exists(s.Target + ".failed"));
            var note = UpdateService.TakeRollbackNote(s.Target);
            Assert.Contains("không báo khởi động xong", note);
            hung.Dispose();
        }
        finally
        {
            KillByName(name);
        }
    }

    [Fact]
    public void StartupCheckWritesPidThenMarker()
    {
        var dir = NewDir();
        var marker = Path.Combine(dir, "started.ok");
        Environment.SetEnvironmentVariable(UpdateService.MarkerVariable, marker);
        try
        {
            UpdateService.BeginStartupCheck();
            Assert.Null(Environment.GetEnvironmentVariable(UpdateService.MarkerVariable)); // chương trình do flow mở không thừa hưởng
            Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(marker + ".pid"));
            Assert.False(File.Exists(marker));
            UpdateService.ConfirmStarted();
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateService.MarkerVariable, null);
        }

        var exe = Path.Combine(dir, "ScheduleApp.exe");
        File.WriteAllText(exe + ".old", "cũ");
        File.WriteAllText(exe + ".failed", "hỏng");
        UpdateService.CleanupOldVersion(exe);
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".failed"));
    }

    [Fact]
    public void ErrorsAreExplainedInVietnamese()
    {
        Assert.Contains("Chứng chỉ HTTPS", UpdateService.Explain(new HttpRequestException("x", new System.Security.Authentication.AuthenticationException())));
        Assert.Contains("404", UpdateService.Explain(new HttpRequestException("x", null, HttpStatusCode.NotFound)));
        Assert.Contains("Không kết nối được", UpdateService.Explain(new HttpRequestException("No such host")));
        Assert.Contains("Hết thời gian", UpdateService.Explain(new TaskCanceledException()));
        Assert.Contains("JSON", UpdateService.Explain(new System.Text.Json.JsonException()));
        Assert.Equal("Không thấy file \"C:\\x\\ScheduleApp.exe\".", UpdateService.Explain(new FileNotFoundException("Could not find", "C:\\x\\ScheduleApp.exe")));
    }

    [Fact]
    public void DebugBuildCannotSelfUpdate() => Assert.False(UpdateService.CanSelfUpdate(out _)); // testhost.exe không phải ScheduleApp.exe
}
