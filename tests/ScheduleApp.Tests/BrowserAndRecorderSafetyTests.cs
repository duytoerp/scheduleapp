using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Recording;
using ScheduleApp.Services;
using static ScheduleApp.Recording.MacroRecorder;

namespace ScheduleApp.Tests;

/// <summary>
/// An toàn khi điều khiển trình duyệt và ghi macro: chỉ kết nối cổng DevTools do đúng trình duyệt ScheduleApp mở lắng nghe,
/// URL không thành tham số dòng lệnh, hồ sơ trình duyệt nằm ở thư mục cục bộ chỉ người dùng hiện tại đọc được,
/// chữ gõ vào ô mật khẩu không bao giờ được lưu thật. Không mở trình duyệt, không gửi phím / chuột thật.
/// </summary>
public class BrowserAndRecorderSafetyTests
{
    // ───────────────────────────── DevToolsActivePort ─────────────────────────────

    [Fact]
    public void ActivePortFileIsParsedStrictly()
    {
        Assert.Equal(54321, BrowserClient.ParseActivePort("54321\n/devtools/browser/0b5c-11aa"));
        Assert.Equal(9222, BrowserClient.ParseActivePort("9222\r\n/devtools/browser/x\r\n"));
        Assert.Null(BrowserClient.ParseActivePort(""));
        Assert.Null(BrowserClient.ParseActivePort("54321"));                          // trình duyệt chưa ghi xong dòng 2
        Assert.Null(BrowserClient.ParseActivePort("54321\n/json/list"));
        Assert.Null(BrowserClient.ParseActivePort("0\n/devtools/browser/x"));
        Assert.Null(BrowserClient.ParseActivePort("70000\n/devtools/browser/x"));
        Assert.Null(BrowserClient.ParseActivePort("-1\n/devtools/browser/x"));
        Assert.Null(BrowserClient.ParseActivePort("+80\n/devtools/browser/x"));
        Assert.Null(BrowserClient.ParseActivePort(" 80\n/devtools/browser/x"));
    }

    // ───────────────────────────── Chủ sở hữu cổng ─────────────────────────────

    private static ProcessNet.Listener L(string ip, int port, int pid) => new(IPAddress.Parse(ip), port, pid);

    [Fact]
    public void OwnershipAcceptsOnlyBrowserProcessTree()
    {
        // 100 = trình duyệt ScheduleApp mở; 200, 300 = con / cháu của nó; 400 = chương trình khác.
        var parents = new Dictionary<int, int> { [100] = 50, [200] = 100, [300] = 200, [400] = 1, [500] = 600, [600] = 500 };
        var ok = BrowserClient.Ownership.Trusted;

        Assert.Equal((ok, 100), BrowserClient.CheckOwner(9222, [L("127.0.0.1", 9222, 100)], parents, 100));
        Assert.Equal(ok, BrowserClient.CheckOwner(9222, [L("127.0.0.1", 9222, 300)], parents, 100).Result);
        Assert.Equal((BrowserClient.Ownership.Foreign, 400), BrowserClient.CheckOwner(9222, [L("127.0.0.1", 9222, 400)], parents, 100));
        // Tiến trình lạ cùng chiếm cổng ở 0.0.0.0 / [::] (dual-stack) / [::ffff:127.0.0.1] → từ chối dù trình duyệt cũng lắng nghe.
        Assert.Equal((BrowserClient.Ownership.Foreign, 400),
            BrowserClient.CheckOwner(9222, [L("127.0.0.1", 9222, 100), L("0.0.0.0", 9222, 400)], parents, 100));
        Assert.Equal(BrowserClient.Ownership.Foreign, BrowserClient.CheckOwner(9222, [L("::", 9222, 400)], parents, 100).Result);
        Assert.Equal(BrowserClient.Ownership.Foreign, BrowserClient.CheckOwner(9222, [L("::ffff:127.0.0.1", 9222, 400)], parents, 100).Result);
        // Địa chỉ mà kết nối tới 127.0.0.1 không thể tới, cổng khác → không liên quan.
        Assert.Equal((ok, 100), BrowserClient.CheckOwner(9222,
            [L("127.0.0.1", 9222, 100), L("::1", 9222, 400), L("127.0.0.2", 9222, 400), L("127.0.0.1", 9223, 400)], parents, 100));
        Assert.Equal(BrowserClient.Ownership.NoListener, BrowserClient.CheckOwner(9222, [L("127.0.0.1", 9223, 100)], parents, 100).Result);
        // Cây tiến trình có vòng (PID bị dùng lại) / không rõ cha → không treo, không tin.
        Assert.Equal(BrowserClient.Ownership.Foreign, BrowserClient.CheckOwner(9222, [L("127.0.0.1", 9222, 500)], parents, 100).Result);
        Assert.Equal(BrowserClient.Ownership.Foreign, BrowserClient.CheckOwner(9222, [L("127.0.0.1", 9222, 999)], parents, 100).Result);
    }

    /// <summary>Tiến trình con thật để làm "trình duyệt" giả: chỉ chờ, không mở cổng nào.</summary>
    private static Process StartIdleChild() => Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
    })!;

    [Fact]
    public void RealTcpTableAndProcessTreeIdentifyOwner()
    {
        using var server = new MiniHttpServer((_, _, _, _) => (200, "{}"));
        int port = new Uri(server.BaseUrl).Port;
        using var child = StartIdleChild();
        try
        {
            var listeners = ProcessNet.TcpListeners();
            Assert.Contains(listeners, l => l.Port == port && l.Pid == Environment.ProcessId && l.Address.Equals(IPAddress.Loopback));
            var parents = ProcessNet.ParentMap();
            Assert.Equal(Environment.ProcessId, parents[child.Id]);

            Assert.Equal((BrowserClient.Ownership.Trusted, Environment.ProcessId), BrowserClient.CheckOwner(port, listeners, parents, Environment.ProcessId));
            // "Trình duyệt" là tiến trình con, cổng lại do tiến trình cha (chương trình khác) lắng nghe → không tin.
            Assert.Equal((BrowserClient.Ownership.Foreign, Environment.ProcessId), BrowserClient.CheckOwner(port, listeners, parents, child.Id));

            Assert.Contains("ping.exe", ProcessNet.CommandLine(child.Id));
            Assert.Null(ProcessNet.CommandLine(0));
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task ForeignListenerIsRefusedWithoutSendingAnything()
    {
        using var server = new MiniHttpServer((_, path, _, _) => (200, path.StartsWith("/json/list")
            ? $$"""[{"id":"1","type":"page","title":"t","url":"https://x/","webSocketDebuggerUrl":"ws://127.0.0.1:1/devtools/page/1"}]"""
            : """{"webSocketDebuggerUrl":"ws://127.0.0.1:1/devtools/browser/x"}"""));
        int port = new Uri(server.BaseUrl).Port;
        using var child = StartIdleChild();
        try
        {
            // ScheduleApp "mở" trình duyệt là tiến trình con, nhưng cổng do chương trình khác (tiến trình kiểm thử) lắng nghe.
            BrowserClient.Track(child, port);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BrowserClient.EvalAsync("", "document.cookie", CancellationToken.None));
            Assert.Contains("chương trình khác", ex.Message);
            Assert.Contains($"PID {Environment.ProcessId}", ex.Message);
            Assert.Empty(server.Requests);   // không một yêu cầu nào tới cổng lạ

            // Sau khi từ chối, phiên bị bỏ → lần sau báo cần mở trình duyệt, vẫn không kết nối.
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BrowserClient.EvalAsync("", "1", CancellationToken.None));
            Assert.Contains("Mở trình duyệt ở chế độ điều khiển", ex.Message);
            BrowserClient.Track(child, port);
            Assert.False(await BrowserClient.IsRunningAsync(CancellationToken.None));
            Assert.Empty(server.Requests);

            // Cùng cổng nhưng đúng tiến trình đã "mở" (ở đây là tiến trình kiểm thử) → được kết nối.
            BrowserClient.Track(Process.GetCurrentProcess(), port);
            Assert.True(await BrowserClient.IsRunningAsync(CancellationToken.None));
            Assert.Contains(server.Requests, r => r.Path == "/json/version");
            // Tab có địa chỉ WebSocket trỏ sang cổng khác bị bỏ qua, không kết nối tới đó.
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BrowserClient.EvalAsync("", "1", CancellationToken.None));
            Assert.Contains("không có tab", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            BrowserClient.Track(null, 0);
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void WebSocketAddressMustPointToTheVerifiedPort()
    {
        Assert.True(BrowserClient.IsLocalDevToolsUrl("ws://127.0.0.1:5000/devtools/page/1", 5000));
        Assert.True(BrowserClient.IsLocalDevToolsUrl("ws://localhost:5000/devtools/page/1", 5000));
        Assert.False(BrowserClient.IsLocalDevToolsUrl("ws://127.0.0.1:5001/devtools/page/1", 5000));
        Assert.False(BrowserClient.IsLocalDevToolsUrl("ws://evil.example:5000/devtools/page/1", 5000));
        Assert.False(BrowserClient.IsLocalDevToolsUrl("wss://127.0.0.1:5000/devtools/page/1", 5000));
        Assert.False(BrowserClient.IsLocalDevToolsUrl("", 5000));
    }

    [Fact]
    public void ReattachRequiresBrowserCommandLineWithSameProfile()
    {
        var dir = Path.Combine(TestSupport.NewDir(), "browser-edge Kế toán");
        Assert.True(BrowserClient.UsesProfile($"\"C:\\Edge\\msedge.exe\" --remote-debugging-port=0 \"--user-data-dir={dir}\" --no-first-run", dir));
        Assert.True(BrowserClient.UsesProfile($"msedge.exe --user-data-dir=\"{dir}\\\" --flag", dir + "\\"));
        Assert.True(BrowserClient.UsesProfile($"chrome.exe --USER-DATA-DIR={dir.Replace(" ", "")}", dir.Replace(" ", "")));
        Assert.False(BrowserClient.UsesProfile($"chrome.exe \"--user-data-dir={dir}-khac\"", dir));
        Assert.False(BrowserClient.UsesProfile($"chrome.exe --remote-debugging-port=9222", dir));
        Assert.False(BrowserClient.UsesProfile($"evil.exe --x=\"--user-data-dir={Path.GetDirectoryName(dir)}\"", dir));
    }

    // ───────────────────────────── URL & tham số dòng lệnh ─────────────────────────────

    [Theory]
    [InlineData("example.com/a", "https://example.com/a")]
    [InlineData("  http://example.com  ", "http://example.com")]
    [InlineData("localhost:8080/x", "https://localhost:8080/x")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("file:///C:/Bao cao/a.html", "file:///C:/Bao%20cao/a.html")]
    [InlineData("https://x/?q=a b\"c'd<e>f`", "https://x/?q=a%20b%22c%27d%3Ce%3Ef%60")]
    [InlineData("--renderer-cmd-prefix=calc.exe", "https://--renderer-cmd-prefix=calc.exe")]
    [InlineData("-x", "https://-x")]
    [InlineData("https://x/\" --renderer-cmd-prefix=calc \"", "https://x/%22%20--renderer-cmd-prefix=calc%20%22")]
    [InlineData("https://cty.crm5.dynamics.com/main.aspx?pagetype=entitylist&etn=account", "https://cty.crm5.dynamics.com/main.aspx?pagetype=entitylist&etn=account")]
    public void UrlIsNormalizedToSafeForm(string input, string expected)
    {
        var url = BrowserClient.NormalizeUrl(input);
        Assert.Equal(expected, url);
        Assert.False(url.StartsWith('-'));
        Assert.DoesNotContain(url, c => char.IsWhiteSpace(c) || c == '"');
    }

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("chrome://settings")]
    [InlineData("edge://flags")]
    [InlineData("view-source:https://x")]
    [InlineData("vbscript:msgbox")]
    [InlineData("ms-settings:privacy")]
    [InlineData("about:blank --renderer-cmd-prefix=calc")]
    [InlineData("https://x/\r\n--renderer-cmd-prefix=calc")]
    [InlineData("https://x/\0")]
    public void DangerousUrlsAreRejected(string input)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BrowserClient.NormalizeUrl(input));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void LaunchArgumentsKeepUrlAfterSwitchTerminator()
    {
        var dir = @"C:\Users\A B\AppData\Local\ScheduleApp\BrowserProfiles\browser-chrome-Kế \""toán";
        var args = BrowserClient.LaunchArguments(dir, headless: true, " --headless=new  --mute-audio ", "x\" --renderer-cmd-prefix=calc.exe --a=\"");
        Assert.Equal("--remote-debugging-port=0", args[0]);   // cổng ngẫu nhiên, không cố định 9222
        Assert.Equal("--user-data-dir=" + dir, args[1]);
        Assert.Contains("--mute-audio", args);
        Assert.Contains("--window-size=1920,1080", args);
        Assert.Equal("--", args[^2]);
        Assert.Equal("https://x%22%20--renderer-cmd-prefix=calc.exe%20--a=%22", args[^1]);
        Assert.DoesNotContain(args, a => a.StartsWith("--renderer-cmd-prefix", StringComparison.Ordinal));
        Assert.Single(args, a => a.StartsWith("--remote-debugging-port", StringComparison.Ordinal));

        var noUrl = BrowserClient.LaunchArguments(dir, headless: false, "", "  ");
        Assert.DoesNotContain("--", noUrl);
        Assert.DoesNotContain("--headless=new", noUrl);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    private static string[] SplitCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out int count);
        try
        {
            return [.. Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
        }
        finally
        {
            Marshal.FreeHGlobal(argv); // LocalFree — cùng heap tiến trình
        }
    }

    /// <summary>
    /// Tham số đi qua dòng lệnh Windows thật vẫn tách đúng như danh sách: chạy một tiến trình PowerShell chỉ ngủ (tham số sau -File
    /// chỉ là dữ liệu của script, không được thực thi), đọc lại dòng lệnh của nó rồi tách theo quy tắc Windows.
    /// </summary>
    [Fact]
    public void LaunchArgumentsSurviveRealWindowsCommandLine()
    {
        var script = Path.Combine(TestSupport.NewDir(), "ngu.ps1");
        File.WriteAllText(script, "Start-Sleep -Seconds 20");
        var dir = Path.Combine(TestSupport.NewDir(), "browser-chrome-Kế toán x");
        var args = BrowserClient.LaunchArguments(dir, headless: false, "", "x\" --renderer-cmd-prefix=calc.exe \\\"");

        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script]) start.ArgumentList.Add(a);
        foreach (var a in args) start.ArgumentList.Add(a);
        using var proc = Process.Start(start)!;
        try
        {
            string? commandLine = null;
            for (int i = 0; i < 50 && string.IsNullOrEmpty(commandLine); i++)
            {
                commandLine = ProcessNet.CommandLine(proc.Id);
                if (string.IsNullOrEmpty(commandLine)) Thread.Sleep(100);
            }
            Assert.False(string.IsNullOrEmpty(commandLine));
            var argv = SplitCommandLine(commandLine!);
            Assert.Equal(args, argv[^args.Count..]);
            Assert.True(BrowserClient.UsesProfile(commandLine!, dir));
        }
        finally
        {
            try { proc.Kill(); } catch (InvalidOperationException) { }
        }
    }

    // ───────────────────────────── Hồ sơ trình duyệt ─────────────────────────────

    private static bool OnlyCurrentUser(string dir)
    {
        var security = new DirectoryInfo(dir).GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return security.AreAccessRulesProtected && rules.Count > 0 && rules.All(r => r.IdentityReference.Equals(user));
    }

    [Fact]
    public void ProfilesLiveInPrivateLocalFolderNotRoaming()
    {
        // Thư mục dữ liệu kiểm thử (SCHEDULEAPP_DATA_DIR) → hồ sơ ở "BrowserProfiles" bên trong, không đụng %LocalAppData% thật.
        Assert.Equal(Path.Combine(JobStore.DataDir, "BrowserProfiles"), BrowserProfiles.Root);
        var dir = BrowserProfiles.Dir("Chrome", "Riêng tư");
        Assert.Equal(Path.Combine(BrowserProfiles.Root, "browser-chrome-Riêng tư"), dir);
        Assert.True(OnlyCurrentUser(BrowserProfiles.Root));
        Assert.False(OnlyCurrentUser(JobStore.DataDir));   // thư mục cha (trong %TEMP%) rộng hơn → gốc có quyền riêng
    }

    [Fact]
    public void LegacyRoamingProfileIsMigratedOnFirstUse()
    {
        var legacyRoot = TestSupport.NewDir();
        var root = Path.Combine(TestSupport.NewDir(), "Local", "BrowserProfiles");
        var legacy = Path.Combine(legacyRoot, "browser-edge-Tai");
        Directory.CreateDirectory(Path.Combine(legacy, "Default", "Network"));
        File.WriteAllText(Path.Combine(legacy, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"KHOA\"}}");
        File.WriteAllText(Path.Combine(legacy, "Default", "Network", "Cookies"), "cookie");
        File.WriteAllText(Path.Combine(legacy, "lockfile"), "");

        // Trình duyệt đang mở hồ sơ cũ (giữ "lockfile" như Chrome: kèm quyền xóa, cho phép xóa / đổi tên) → tạm dùng chỗ cũ, không đụng gì.
        using (new FileStream(Path.Combine(legacy, "lockfile"), FileMode.Open, FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
        {
            Assert.True(BrowserProfiles.InUse(legacy));
            Assert.Equal(legacy, BrowserProfiles.Locate("browser-edge-Tai", root, legacyRoot));
            Assert.True(File.Exists(Path.Combine(legacy, "Default", "Network", "Cookies")));
            Assert.False(Directory.Exists(Path.Combine(root, "browser-edge-Tai")));
        }
        Assert.True(OnlyCurrentUser(root));

        // Đóng trình duyệt → lần dùng sau được chuyển hẳn sang chỗ mới (chỉ người dùng hiện tại đọc được), chỗ cũ không còn bản sao.
        var target = BrowserProfiles.Locate("browser-edge-Tai", root, legacyRoot);
        Assert.Equal(Path.Combine(root, "browser-edge-Tai"), target);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal("cookie", File.ReadAllText(Path.Combine(target, "Default", "Network", "Cookies")));
        Assert.Contains("KHOA", File.ReadAllText(Path.Combine(target, "Local State")));
        Assert.True(OnlyCurrentUser(target));
        Assert.True(BrowserProfiles.IsCurrentUserOnly(target));

        // Đã chuyển → không làm lại; hồ sơ chưa từng có → chỗ mới.
        Assert.Equal(target, BrowserProfiles.Locate("browser-edge-Tai", root, legacyRoot));
        Assert.Equal(Path.Combine(root, "browser-chrome"), BrowserProfiles.Locate("browser-chrome", root, legacyRoot));
    }

    [Fact]
    public void FailedMigrationIsNotRetriedOnEveryUse()
    {
        var legacyRoot = TestSupport.NewDir();
        var root = Path.Combine(TestSupport.NewDir(), "Local", "BrowserProfiles");
        var legacy = Path.Combine(legacyRoot, "browser-chrome-Mang");
        Directory.CreateDirectory(Path.Combine(legacy, "Default"));
        var cookies = Path.Combine(legacy, "Default", "Cookies");
        File.WriteAllText(cookies, "cookie");

        // Chỉ kiểm tra có hồ sơ (hộp thoại "Sao chép hồ sơ" trên luồng giao diện) → không chuyển gì.
        Assert.True(BrowserProfiles.Exists("browser-chrome-Mang", root, legacyRoot));
        Assert.False(BrowserProfiles.Exists("browser-chrome-Khac", root, legacyRoot));
        Assert.True(Directory.Exists(legacy));
        Assert.False(Directory.Exists(Path.Combine(root, "browser-chrome-Mang")));

        // Một file trong hồ sơ cũ đang bị giữ (không cho xóa / đổi tên, không phải "lockfile") → chuyển lỗi, dùng chỗ cũ.
        using (new FileStream(cookies, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(legacy, BrowserProfiles.Locate("browser-chrome-Mang", root, legacyRoot));

        // Hết lỗi nhưng vẫn trong phiên này → không chép lại ở mỗi lần dùng (chép qua ổ mạng có thể mất vài phút).
        Assert.Equal(legacy, BrowserProfiles.Locate("browser-chrome-Mang", root, legacyRoot));
        Assert.Equal("cookie", File.ReadAllText(cookies));
        Assert.False(Directory.Exists(Path.Combine(root, "browser-chrome-Mang")));
        Assert.Empty(Directory.GetDirectories(root, "*.tmp-*"));
    }

    [Fact]
    public void CrossVolumeFallbackCopiesThenRemovesOldCopy()
    {
        var source = Path.Combine(TestSupport.NewDir(), "browser-chrome-Cu");
        Directory.CreateDirectory(Path.Combine(source, "Default", "Network"));
        File.WriteAllText(Path.Combine(source, "Default", "Network", "Cookies"), "cookie");
        File.WriteAllText(Path.Combine(source, "Local State"), "{}");
        var target = Path.Combine(TestSupport.NewDir(), "browser-chrome-Cu");

        BrowserProfiles.CopyThenDelete(source, target);
        Assert.False(Directory.Exists(source));
        Assert.Equal("cookie", File.ReadAllText(Path.Combine(target, "Default", "Network", "Cookies")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!, "*.tmp-*"));
    }

    // ───────────────────────────── Ô mật khẩu khi ghi macro ─────────────────────────────

    private static List<ActionStep> Record(Func<IntPtr, FieldKind> field, Func<Task<bool?>> probe, params string[] keys)
    {
        using var recorder = new MacroRecorder { FocusedField = field, PasswordProbe = probe };
        long tick = Environment.TickCount64;
        foreach (var key in keys) recorder.AppendText(key, IntPtr.Zero, tick += 50);
        return recorder.Stop();
    }

    private static string Typed(Func<IntPtr, FieldKind> field, Func<Task<bool?>> probe) =>
        Assert.Single(Record(field, probe, "S", "3", "c", "!")).Text;

    [Fact]
    public void PasswordDecidedSynchronouslyNeverStoresLiteral()
    {
        int probes = 0;
        Task<bool?> Probe(bool? result) { probes++; return Task.FromResult(result); }

        // Ô Edit có ES_PASSWORD → biết ngay, không cần hỏi UI Automation.
        Assert.Equal(PasswordPlaceholder, Typed(_ => FieldKind.Password, () => Probe(false)));
        Assert.Equal("S3c!", Typed(_ => FieldKind.Text, () => Probe(true)));
        Assert.Equal(0, probes);

        // Chưa rõ → chỉ khi UI Automation trả lời chắc chắn "không phải mật khẩu" mới lưu chữ thật.
        Assert.Equal("S3c!", Typed(_ => FieldKind.Unknown, () => Probe(false)));
        Assert.Equal(PasswordPlaceholder, Typed(_ => FieldKind.Unknown, () => Probe(true)));
        Assert.Equal(PasswordPlaceholder, Typed(_ => FieldKind.Unknown, () => Probe(null)));
        Assert.Equal(PasswordPlaceholder, Typed(_ => FieldKind.Unknown, () => Task.FromException<bool?>(new InvalidOperationException())));
        Assert.Equal(PasswordPlaceholder, Typed(_ => FieldKind.Unknown, () => throw new COMException()));
        Assert.Equal(3, probes);   // một lần cho mỗi đoạn gõ, không phải mỗi phím
    }

    [Fact]
    public void PendingPasswordCheckIsResolvedBeforeCommit()
    {
        // Kết quả tới chậm (sau khi bấm dừng) vẫn được chờ.
        var late = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Delay(300).ContinueWith(_ => late.SetResult(false));
        Assert.Equal("S3c!", Typed(_ => FieldKind.Unknown, () => late.Task));

        var latePassword = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Delay(300).ContinueWith(_ => latePassword.SetResult(true));
        Assert.Equal(PasswordPlaceholder, Typed(_ => FieldKind.Unknown, () => latePassword.Task));

        // Không bao giờ có kết quả → hết thời gian chờ thì giữ bí mật, không lưu chữ thật.
        var sw = Stopwatch.StartNew();
        Assert.Equal(PasswordPlaceholder, Typed(_ => FieldKind.Unknown, () => new TaskCompletionSource<bool?>().Task));
        Assert.InRange(sw.ElapsedMilliseconds, 0, 15_000);
    }

    [Fact]
    public void MovingIntoPasswordFieldStartsNewStep()
    {
        // Gõ tên đăng nhập rồi tiêu điểm chuyển sang ô mật khẩu (không qua Tab / click) → hai bước, chỉ bước đầu có chữ thật.
        var fields = new Queue<FieldKind>([FieldKind.Text, FieldKind.Text, FieldKind.Password, FieldKind.Password, FieldKind.Text]);
        var steps = Record(_ => fields.Dequeue(), () => Task.FromResult<bool?>(false), "a", "n", "p", "w", "x");
        Assert.Equal(new[] { "an", PasswordPlaceholder, "x" }, steps.Where(s => s.Type == StepType.TypeText).Select(s => s.Text));
        Assert.DoesNotContain(steps, s => s.Text.Contains("pw"));
    }

    [Fact]
    public void MovingFromTextIntoUnknownFieldStartsNewStep()
    {
        // Ô Edit thường rồi tiêu điểm tự chuyển sang ô chưa rõ (ô mật khẩu WPF / trang web, không qua Tab / click):
        // phần gõ sau không được gộp vào bước chữ thường, mà thành bước riêng chờ UI Automation trả lời.
        int probes = 0;
        var fields = new Queue<FieldKind>([FieldKind.Text, FieldKind.Text, FieldKind.Unknown, FieldKind.Unknown, FieldKind.Unknown]);
        var steps = Record(_ => fields.Dequeue(), () => { probes++; return Task.FromResult<bool?>(true); }, "a", "n", "p", "w", "d");
        Assert.Equal(new[] { "an", PasswordPlaceholder }, steps.Where(s => s.Type == StepType.TypeText).Select(s => s.Text));
        Assert.DoesNotContain(steps, s => s.Text.Contains("pwd"));
        Assert.Equal(1, probes);
    }

    [Fact]
    public void RecordedSecretNoteDoesNotClaimEveryPlaceholderIsAPassword()
    {
        var note = ScheduleApp.UI.JobEditorForm.RecordedSecretNote;
        Assert.Contains(PasswordPlaceholder, note);
        Assert.Contains("chưa xác định", note);
        Assert.Contains("kiểm tra lại", note);
        Assert.DoesNotContain("Phát hiện ô mật khẩu", note);
    }

    [Fact]
    public void SettingsNoLongerOfferBrowserPort()
    {
        // Trình duyệt điều khiển tự chọn cổng ngẫu nhiên → không còn ô "cổng 9222" nào để người dùng đổi mà không có tác dụng.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new ScheduleApp.UI.SettingsForm();
                var texts = new List<string>();
                void Walk(Control c)
                {
                    texts.Add(c.Text);
                    foreach (Control child in c.Controls) Walk(child);
                }
                Walk(form);
                Assert.Contains(texts, t => t.Contains("Chế độ an toàn"));   // đã duyệt đúng các ô của tab "Chung"
                Assert.DoesNotContain(texts, t => t.Contains("9222") || t.Contains("remote debugging", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Win32PasswordStyleIsReadFromRealControls()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-20000, -20000), ShowInTaskbar = false };
                var plain = new TextBox();
                var system = new TextBox { UseSystemPasswordChar = true };
                var star = new TextBox { PasswordChar = '*' };
                var rich = new RichTextBox();
                var button = new Button();
                form.Controls.AddRange([plain, system, star, rich, button]);

                Assert.Equal(FieldKind.Text, FieldOf(plain.Handle));
                Assert.Equal(FieldKind.Password, FieldOf(system.Handle));
                Assert.Equal(FieldKind.Password, FieldOf(star.Handle));
                Assert.Equal(FieldKind.Text, FieldOf(rich.Handle));
                Assert.Equal(FieldKind.Unknown, FieldOf(button.Handle));
                Assert.Equal(FieldKind.Unknown, FieldOf(IntPtr.Zero));

                // Đổi sang ô mật khẩu khi đang chạy (EM_SETPASSWORDCHAR) cũng nhận ra ngay.
                plain.PasswordChar = '●';
                Assert.Equal(FieldKind.Password, FieldOf(plain.Handle));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());

        Assert.Equal(FieldKind.Unknown, Classify("Chrome_RenderWidgetHostHWND", 0x20));   // trang web → hỏi UI Automation
        Assert.Equal(FieldKind.Password, Classify("RICHEDIT50W", 0x20));
    }
}
