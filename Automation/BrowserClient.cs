using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;

namespace ScheduleApp.Automation;

/// <summary>
/// Điều khiển Chrome / Edge qua Chrome DevTools Protocol (cổng remote debugging).
/// Trình duyệt phải được mở bằng bước "Mở trình duyệt ở chế độ điều khiển" (hồ sơ riêng của ScheduleApp).
/// Bộ chọn: CSS (#id, .class, input[name=q]), "xpath://button[.='Lưu']" hoặc "text:Đăng nhập".
/// </summary>
/// <remarks>
/// Cổng DevTools không có mật khẩu: ai kết nối được là đọc được mọi trang (cookie, dữ liệu D365…). Vì vậy trình duyệt được mở với
/// cổng ngẫu nhiên (--remote-debugging-port=0, cổng thật đọc từ file DevToolsActivePort trong thư mục hồ sơ) và trước mỗi lần kết nối
/// ScheduleApp kiểm tra cổng đó đúng do tiến trình trình duyệt mình đã mở (hoặc tiến trình con của nó) lắng nghe — chương trình khác
/// chiếm cổng thì từ chối, không gửi lệnh / dữ liệu nào.
/// </remarks>
internal static partial class BrowserClient
{
    private const int PollMs = 300;
    private const string ActivePortFile = "DevToolsActivePort";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static int _messageId;

    /// <summary>Trình duyệt điều khiển đang dùng: cổng DevTools, thư mục hồ sơ và tiến trình trình duyệt (giữ handle để PID không bị dùng lại).</summary>
    private sealed record Session(int Port, string Dir, Process Process);

    private static Session? _session;

    /// <summary>Tham số thêm khi mở trình duyệt (kiểm thử dùng " --headless=new").</summary>
    internal static string ExtraLaunchArgs { get; set; } = "";

    /// <summary>Mở mọi trình duyệt điều khiển ở chế độ ẩn (headless) — đặt bởi tham số dòng lệnh --headless (máy CI).</summary>
    internal static bool ForceHeadless { get; set; }

    /// <summary>Tiến trình trình duyệt mở gần nhất (kiểm thử dùng để dọn dẹp).</summary>
    internal static Process? LastLaunched { get; private set; }

    private static string BaseUrl(Session s) => $"http://127.0.0.1:{s.Port}";

    /// <summary>
    /// Dùng trình duyệt do chính ScheduleApp (kiểm thử) tự mở với cổng <paramref name="port"/> — chỉ cổng do <paramref name="process"/>
    /// lắng nghe mới được kết nối. null = bỏ trình duyệt đang dùng.
    /// </summary>
    internal static void Track(Process? process, int port, string dir = "") =>
        _session = process == null ? null : new Session(port, dir, process);

    public static async Task ExecuteAsync(ActionStep s, Func<string, string, Task> setVar, CancellationToken ct)
    {
        switch (s.BrowserAction)
        {
            case BrowserAction.Launch:
                await LaunchAsync(s.Target, s.Text, s.Arguments, s.Force || ForceHeadless, ct);
                break;

            case BrowserAction.Navigate:
                if (string.IsNullOrWhiteSpace(s.Text)) throw new InvalidOperationException("Chưa nhập địa chỉ URL.");
                await SendAsync(s.Target, "Page.navigate", new { url = NormalizeUrl(s.Text) }, ct);
                await WaitReadyAsync(s.Target, s.DelayMs, ct);
                break;

            case BrowserAction.Click:
                await WaitForAsync(s, ct);
                await EvalAsync(s.Target, Script(s.Text, "el.scrollIntoView({block:'center'}); el.click(); return true;"), ct);
                break;

            case BrowserAction.SetValue:
                await WaitForAsync(s, ct);
                await EvalAsync(s.Target, Script(s.Text, """
                    el.scrollIntoView({block:'center'}); el.focus();
                    if (el.isContentEditable) { el.innerText = VALUE; }
                    else {
                      const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype
                                  : el instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
                      Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, VALUE);
                    }
                    el.dispatchEvent(new Event('input', {bubbles:true}));
                    el.dispatchEvent(new Event('change', {bubbles:true}));
                    el.blur && el.dispatchEvent(new Event('blur', {bubbles:true}));
                    return true;
                    """, s.Arguments), ct);
                break;

            case BrowserAction.ReadText:
            {
                await WaitForAsync(s, ct);
                var value = await EvalAsync(s.Target, Script(s.Text,
                    "return (el.value !== undefined && el.tagName !== 'BUTTON' && el.tagName !== 'LI') ? String(el.value) : (el.innerText || el.textContent || '').trim();"), ct);
                Log.Info($"      Đọc được: \"{Short(value)}\"");
                if (!string.IsNullOrWhiteSpace(s.Variable)) await setVar(s.Variable, value);
                break;
            }

            case BrowserAction.WaitFor:
                await WaitForAsync(s, ct);
                break;

            case BrowserAction.RunScript:
            {
                if (string.IsNullOrWhiteSpace(s.Text)) throw new InvalidOperationException("Chưa nhập JavaScript.");
                var value = await EvalAsync(s.Target, s.Text, ct);
                if (value.Length > 0) Log.Info($"      Kết quả: {Short(value)}");
                if (!string.IsNullOrWhiteSpace(s.Variable)) await setVar(s.Variable, value);
                break;
            }
        }
    }

    // ───────────────────────────── Mở trình duyệt ─────────────────────────────

    /// <param name="profile">Hồ sơ của ScheduleApp (trống = mặc định) — mỗi hồ sơ giữ đăng nhập riêng.</param>
    private static async Task LaunchAsync(string browser, string url, string profile, bool headless, CancellationToken ct)
    {
        profile = BrowserProfiles.SafeName(profile);
        // Lần đầu có thể phải chuyển hồ sơ cũ (chép qua ổ mạng) → chạy nền, dừng flow là hủy được.
        var dir = await Task.Run(() => BrowserProfiles.Dir(browser, profile, ct), ct);
        var label = BrowserProfiles.DisplayName(browser) + (profile.Length == 0 ? "" : $" · hồ sơ \"{profile}\"");

        if (Current() is { } current)
        {
            if (await IsAvailableAsync(current, ct, throwIfForeign: true))
            {
                if (SamePath(current.Dir, dir))
                {
                    // Đúng hồ sơ đang được điều khiển → chỉ mở tab mới.
                    await OpenTabAsync(current, url, ct);
                    return;
                }
                // Mỗi lúc chỉ điều khiển một trình duyệt: đóng trình duyệt đang mở hồ sơ khác rồi mở hồ sơ được chọn.
                Log.Info($"      Đóng trình duyệt điều khiển đang mở hồ sơ khác ({Path.GetFileName(current.Dir)}) để mở {label}.");
                await CloseBrowserAsync(current.Dir, ct);
            }
            else if (_session == current) _session = null;
        }

        if (BrowserProfiles.InUse(dir))
        {
            // Trình duyệt ScheduleApp mở từ trước (vd trước khi khởi động lại ScheduleApp) vẫn giữ hồ sơ → dùng tiếp nếu xác minh được.
            if (TryReattach(dir) is { } previous && await IsAvailableAsync(previous, ct, throwIfForeign: true))
            {
                _session = previous;
                await OpenTabAsync(previous, url, ct);
                return;
            }
            // Mở thêm lần nữa chỉ tạo cửa sổ mới trong phiên cũ (không có cổng điều khiển).
            throw new InvalidOperationException(
                $"{label} đang mở nhưng không ở chế độ điều khiển. Hãy đóng cửa sổ trình duyệt đó rồi chạy lại " +
                "(trình duyệt chạy ẩn không có cửa sổ: đóng trong Trình quản lý tác vụ — Task Manager).");
        }

        var exe = ResolveBrowser(browser);
        if (!Directory.Exists(dir)) Log.Info($"      Hồ sơ mới ({Path.GetFileName(dir)}) — lần đầu cần đăng nhập các trang web, lần sau được giữ lại.");
        // File cổng của lần mở trước (trình duyệt bị tắt đột ngột) → xóa để không đọc nhầm cổng cũ.
        try { File.Delete(Path.Combine(dir, ActivePortFile)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // Truyền từng tham số riêng (không ghép chuỗi) → URL / thư mục có dấu nháy, khoảng trắng không thành tham số khác.
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in LaunchArguments(dir, headless, ExtraLaunchArgs, url)) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Không mở được {label}.");
        LastLaunched = process;

        var sw = Stopwatch.StartNew();
        long exitedAt = -1;
        while (true)
        {
            // Tiến trình vừa mở đã thoát nhưng trình duyệt có thể vẫn chạy: Chrome / Edge có bản cập nhật chờ (new_chrome.exe) tự mở lại
            // bản mới, trình mở bản portable chạy trình duyệt thật là tiến trình khác → nhận tiến trình đang lắng nghe cổng ghi trong
            // DevToolsActivePort nếu dòng lệnh của nó mở đúng thư mục hồ sơ này.
            bool exited = process.HasExited;
            var session = exited ? TryReattach(dir) : ReadActivePort(dir) is int port ? new Session(port, dir, process) : null;
            if (session != null && await IsAvailableAsync(session, ct, throwIfForeign: true))
            {
                _session = session;
                LastLaunched = session.Process;
                break;
            }
            if (exited && exitedAt < 0) exitedAt = sw.ElapsedMilliseconds;
            // Thoát hẳn (không tiến trình nào giữ hồ sơ sau vài giây) hoặc hết thời gian chờ → báo lỗi.
            if (exited && (!BrowserProfiles.InUse(dir) && sw.ElapsedMilliseconds - exitedAt > 5_000 || sw.ElapsedMilliseconds > 20_000))
                throw new InvalidOperationException($"{label} đóng ngay sau khi mở (mã {process.ExitCode}). Nếu {label} đang chạy với hồ sơ này, hãy đóng hết rồi thử lại.");
            if (sw.ElapsedMilliseconds > 20_000)
                throw new TimeoutException($"Trình duyệt không mở cổng điều khiển. Nếu {label} đang chạy, hãy đóng hết rồi thử lại.");
            await Task.Delay(300, ct);
        }
        if (!string.IsNullOrWhiteSpace(url)) await WaitReadyAsync("", 30_000, ct);
    }

    /// <summary>
    /// Tham số dòng lệnh mở trình duyệt, mỗi phần tử một tham số: cổng 0 = trình duyệt tự chọn cổng trống và ghi vào DevToolsActivePort;
    /// Chrome 136+ chỉ cho remote debugging với thư mục hồ sơ riêng (không phải hồ sơ mặc định). URL đã chuẩn hóa đứng sau "--"
    /// nên không bao giờ bị hiểu là tham số của trình duyệt.
    /// </summary>
    internal static List<string> LaunchArguments(string dir, bool headless, string extraArgs, string url)
    {
        List<string> args = ["--remote-debugging-port=0", "--user-data-dir=" + dir, "--no-first-run", "--no-default-browser-check"];
        args.AddRange(extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        // Ẩn: không hiện cửa sổ, vẫn chụp ảnh được qua DevTools. Đặt kích thước như màn hình Full HD để giao diện (thanh lệnh D365…) không bị thu gọn.
        if (headless) args.AddRange(["--headless=new", "--window-size=1920,1080"]);
        if (!string.IsNullOrWhiteSpace(url)) args.AddRange(["--", NormalizeUrl(url)]);
        return args;
    }

    private static async Task OpenTabAsync(Session s, string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl(s)}/json/new?{Uri.EscapeDataString(NormalizeUrl(url))}");
        (await Http.SendAsync(req, ct)).EnsureSuccessStatusCode();
        await Task.Delay(500, ct);
        await WaitReadyAsync("", 30_000, ct); // tab mới nằm đầu danh sách
    }

    /// <summary>Đóng trình duyệt điều khiển đang mở thư mục hồ sơ <paramref name="dir"/> (lệnh Browser.close) và chờ nó nhả cổng + thư mục hồ sơ.</summary>
    internal static async Task CloseBrowserAsync(string dir, CancellationToken ct)
    {
        var s = _session is { } cur && SamePath(cur.Dir, dir) ? cur : TryReattach(dir);
        if (s == null)
        {
            if (!BrowserProfiles.InUse(dir)) return;
            throw new InvalidOperationException(
                $"Trình duyệt đang mở hồ sơ {Path.GetFileName(dir)} không ở chế độ điều khiển của ScheduleApp — hãy tự đóng nó rồi chạy lại.");
        }

        var (owner, pid) = Probe(s);
        if (owner == Ownership.Foreign) throw ForeignListener(s, pid);
        if (owner == Ownership.Trusted)
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{BaseUrl(s)}/json/version", ct));
                var wsUrl = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!;
                if (!IsLocalDevToolsUrl(wsUrl, s.Port)) throw new InvalidOperationException("Địa chỉ điều khiển trình duyệt không hợp lệ: " + wsUrl);
                using var ws = new ClientWebSocket();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await ws.ConnectAsync(new Uri(wsUrl), timeout.Token);
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { id = Interlocked.Increment(ref _messageId), method = "Browser.close" });
                await ws.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or OperationCanceledException or KeyNotFoundException && !ct.IsCancellationRequested)
            {
                Log.Warn("      Không gửi được lệnh đóng trình duyệt: " + ex.Message);
            }
        }

        var sw = Stopwatch.StartNew();
        while (Probe(s).Result == Ownership.Trusted || BrowserProfiles.InUse(dir))
        {
            if (sw.ElapsedMilliseconds > 15_000)
                throw new TimeoutException($"Trình duyệt điều khiển ({Path.GetFileName(dir)}) không đóng. Hãy tự đóng nó rồi chạy lại.");
            await Task.Delay(300, ct);
        }
        if (_session == s) _session = null;
    }

    private static string ResolveBrowser(string browser)
    {
        var b = browser.Trim().Trim('"');
        if (b.Length > 0 && File.Exists(b)) return b;
        bool edge = BrowserProfiles.Key(b) == "edge";
        var exeName = edge ? "msedge.exe" : "chrome.exe";

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            if (key?.GetValue(null) is string path && File.Exists(path)) return path;
        }
        var candidates = edge
            ? new[] { @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe" }
            : [@"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", @"%LocalAppData%\Google\Chrome\Application\chrome.exe"];
        foreach (var c in candidates)
        {
            var p = Environment.ExpandEnvironmentVariables(c);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException($"Không tìm thấy {(edge ? "Microsoft Edge" : "Google Chrome")} trên máy.");
    }

    /// <summary>Cổng trả lời và đúng do trình duyệt của phiên lắng nghe; cổng của chương trình khác → false (hoặc báo lỗi khi <paramref name="throwIfForeign"/>).</summary>
    private static async Task<bool> IsAvailableAsync(Session s, CancellationToken ct, bool throwIfForeign = false)
    {
        var (owner, pid) = Probe(s);
        if (owner == Ownership.Foreign)
        {
            if (throwIfForeign) throw ForeignListener(s, pid);
            Log.Warn("      " + ForeignListener(s, pid).Message);
            return false;
        }
        if (owner == Ownership.NoListener) return false;
        try
        {
            using var resp = await Http.GetAsync($"{BaseUrl(s)}/json/version", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // ───────────────────────────── Địa chỉ trang ─────────────────────────────

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "file", "about", "chrome", "edge" };

    /// <summary>Giao thức ở đầu địa chỉ ("javascript:", "data:", "http:"…) — "localhost:8080" là tên máy kèm cổng, không phải giao thức.</summary>
    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.\-]*):(?!\d)")]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"^about:[a-zA-Z0-9\-]+$")]
    private static partial Regex AboutPattern();

    /// <summary>Trang có sẵn của trình duyệt: chrome://downloads, edge://settings/privacy…</summary>
    [GeneratedRegex(@"^(chrome|edge)://[a-zA-Z0-9\-]+(/[^\s]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserPagePattern();

    /// <summary>
    /// Địa chỉ mở / chuyển trang: chỉ http, https, file trên máy này, about: (vd about:blank) và trang có sẵn của trình duyệt
    /// (chrome://, edge://); không ghi giao thức → https://.
    /// Khoảng trắng và dấu nháy được mã hóa %XX, ký tự điều khiển bị từ chối — kết quả luôn bắt đầu bằng giao thức, không bao giờ bằng "-".
    /// </summary>
    internal static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.Any(char.IsControl)) throw new InvalidOperationException("Địa chỉ URL có ký tự điều khiển (xuống dòng, tab…) — hãy nhập lại.");
        var scheme = SchemePattern().Match(url);
        var name = scheme.Groups[1].Value.ToLowerInvariant();
        if (!scheme.Success) url = "https://" + url;
        else if (!AllowedSchemes.Contains(name) ||
                 name == "about" && !AboutPattern().IsMatch(url) ||
                 name is "chrome" or "edge" && !BrowserPagePattern().IsMatch(url))
            throw new InvalidOperationException(
                $"Không mở được địa chỉ \"{Short(url)}\": chỉ hỗ trợ http://, https://, file://, about:blank và trang của trình duyệt (chrome://, edge://).");
        else if (name == "file" && !IsLocalFileUrl(url))
            // file://máy/thư mục mở thư mục chia sẻ qua mạng (SMB) — Windows tự gửi thông tin đăng nhập (NTLM) tới máy đó.
            throw new InvalidOperationException($"Không mở được địa chỉ \"{Short(url)}\": chỉ mở file trên máy này (file:///C:/…), không mở thư mục mạng.");

        var sb = new StringBuilder(url.Length);
        foreach (var c in url)
        {
            if (c is '"' or '\'' or '<' or '>' or '`' || char.IsWhiteSpace(c)) sb.Append(Uri.EscapeDataString(c.ToString()));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Địa chỉ file:// trỏ tới ổ đĩa của máy này: không có tên máy (hoặc localhost) và đường dẫn không phải \\máy\thư mục.</summary>
    private static bool IsLocalFileUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        if (uri.Host.Length > 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        // Phần đường dẫn (đã giải mã %5C…) phải bắt đầu bằng ổ đĩa của máy này: C:/…
        return LocalDrivePath().IsMatch(Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'));
    }

    [GeneratedRegex(@"^[a-zA-Z]:([\\/]|$)")]
    private static partial Regex LocalDrivePath();

    // ───────────────────────────── Xác minh cổng điều khiển ─────────────────────────────

    internal enum Ownership { Trusted, NoListener, Foreign }

    /// <summary>
    /// Cổng <paramref name="port"/> (127.0.0.1) có đúng do tiến trình <paramref name="rootPid"/> hoặc tiến trình con / cháu của nó lắng nghe không,
    /// theo ảnh chụp bảng cổng TCP và cây tiến trình. Một tiến trình lạ cùng lắng nghe cổng đó (vd 0.0.0.0) → Foreign kèm PID của nó.
    /// </summary>
    internal static (Ownership Result, int Pid) CheckOwner(int port, IEnumerable<ProcessNet.Listener> listeners,
        IReadOnlyDictionary<int, int> parents, int rootPid)
    {
        var owners = listeners.Where(l => l.Port == port && ReachesLoopback(l.Address)).Select(l => l.Pid).Distinct().ToList();
        if (owners.Count == 0) return (Ownership.NoListener, 0);
        foreach (var pid in owners)
            if (!IsSelfOrDescendant(pid, rootPid, parents)) return (Ownership.Foreign, pid);
        return (Ownership.Trusted, owners[0]);
    }

    /// <summary>Kết nối tới 127.0.0.1 có thể tới socket này: 127.0.0.1, 0.0.0.0, [::] (dual-stack) hoặc [::ffff:127.0.0.1].</summary>
    private static bool ReachesLoopback(IPAddress a) =>
        a.Equals(IPAddress.Loopback) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) ||
        a.IsIPv4MappedToIPv6 && (a.MapToIPv4().Equals(IPAddress.Loopback) || a.MapToIPv4().Equals(IPAddress.Any));

    private static bool IsSelfOrDescendant(int pid, int rootPid, IReadOnlyDictionary<int, int> parents)
    {
        var seen = new HashSet<int>();
        while (seen.Add(pid))
        {
            if (pid == rootPid) return true;
            if (!parents.TryGetValue(pid, out var parent) || parent <= 0) return false;
            pid = parent;
        }
        return false; // vòng lặp (PID bị dùng lại)
    }

    private static (Ownership Result, int Pid) Probe(Session s)
    {
        try
        {
            if (s.Process.HasExited) return (Ownership.NoListener, 0);
            return CheckOwner(s.Port, ProcessNet.TcpListeners(), ProcessNet.ParentMap(), s.Process.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Log.Warn("      Không kiểm tra được cổng điều khiển trình duyệt: " + ex.Message);
            return (Ownership.NoListener, 0);
        }
    }

    private static InvalidOperationException ForeignListener(Session s, int pid)
    {
        if (_session == s) _session = null;
        return new InvalidOperationException(
            $"Cổng điều khiển {s.Port} đang do một chương trình khác (PID {pid}) lắng nghe, không phải trình duyệt ScheduleApp đã mở — " +
            "ScheduleApp không kết nối để tránh lộ dữ liệu trang web. Hãy chạy lại bước \"Trình duyệt → Mở trình duyệt ở chế độ điều khiển\".");
    }

    /// <summary>Nội dung file DevToolsActivePort: dòng 1 là cổng, dòng 2 là đường dẫn "/devtools/browser/…" (null = chưa ghi xong / sai dạng).</summary>
    internal static int? ParseActivePort(string content)
    {
        var lines = content.Replace("\r", "").Split('\n');
        if (lines.Length < 2 || !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal)) return null;
        return int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535 ? port : null;
    }

    private static int? ReadActivePort(string dir)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(dir, ActivePortFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return ParseActivePort(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("""(?<q>")?--user-data-dir=(?:"(?<v>[^"]*)"|(?(q)(?<v>[^"]*)|(?<v>[^\s"]+)))""", RegexOptions.IgnoreCase)]
    private static partial Regex UserDataDirPattern();

    /// <summary>Dòng lệnh trình duyệt có mở đúng thư mục hồ sơ <paramref name="dir"/> (--user-data-dir) không.</summary>
    internal static bool UsesProfile(string commandLine, string dir) =>
        UserDataDirPattern().Matches(commandLine).Any(m => SamePath(m.Groups["v"].Value, dir));

    private static bool SamePath(string a, string b)
    {
        if (a.Trim().Length == 0 || b.Trim().Length == 0) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim()).TrimEnd('\\', '/'), Path.GetFullPath(b.Trim()).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [GeneratedRegex("""(?:^|[\s"])--remote-debugging-port=(?<p>\d{1,5})(?=[\s"]|$)""", RegexOptions.IgnoreCase)]
    private static partial Regex DebugPortPattern();

    /// <summary>Cổng cố định trong dòng lệnh trình duyệt (--remote-debugging-port=N, N &gt; 0); cổng 0 (ngẫu nhiên), không có hoặc nhiều giá trị khác nhau → null.</summary>
    internal static int? CommandLinePort(string commandLine)
    {
        var ports = DebugPortPattern().Matches(commandLine)
            .Select(m => int.TryParse(m.Groups["p"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : -1).Distinct().ToList();
        return ports.Count == 1 && ports[0] is > 0 and <= 65535 ? ports[0] : null;
    }

    /// <summary>
    /// Trình duyệt do ScheduleApp mở trong lần chạy trước vẫn giữ hồ sơ <paramref name="dir"/>: chỉ dùng tiếp khi đúng một tiến trình lắng nghe
    /// cổng ghi trong DevToolsActivePort và dòng lệnh của nó mở chính thư mục hồ sơ này.
    /// </summary>
    private static Session? TryReattach(string dir)
    {
        if (!BrowserProfiles.InUse(dir)) return null;
        try
        {
            var listeners = ProcessNet.TcpListeners().Where(l => ReachesLoopback(l.Address)).ToList();
            if (ReadActivePort(dir) is int port)
            {
                var owners = listeners.Where(l => l.Port == port).Select(l => l.Pid).Distinct().ToList();
                if (owners.Count != 1 || ProcessNet.CommandLine(owners[0]) is not { } commandLine || !UsesProfile(commandLine, dir)) return null;
                return new Session(port, dir, Process.GetProcessById(owners[0]));
            }
            return ReattachFixedPort(dir, listeners);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Trình duyệt do phiên bản cũ của ScheduleApp mở với cổng cố định (vd 9222) không có file DevToolsActivePort: dùng tiếp khi tiến trình
    /// lắng nghe đúng cổng ghi trong dòng lệnh của chính nó (--remote-debugging-port=N) và dòng lệnh mở đúng thư mục hồ sơ này, không chương trình
    /// lạ nào cùng lắng nghe cổng đó. Trình duyệt này đóng thì lần sau mở lại bằng cổng ngẫu nhiên như thường.
    /// </summary>
    private static Session? ReattachFixedPort(string dir, List<ProcessNet.Listener> listeners)
    {
        foreach (var pid in listeners.Select(l => l.Pid).Distinct())
        {
            if (ProcessNet.CommandLine(pid) is not { } commandLine || !UsesProfile(commandLine, dir) || CommandLinePort(commandLine) is not int port) continue;
            if (!listeners.Any(l => l.Pid == pid && l.Port == port)) continue;
            if (CheckOwner(port, listeners, ProcessNet.ParentMap(), pid).Result != Ownership.Trusted) return null;
            Log.Info($"      Dùng tiếp trình duyệt điều khiển do phiên bản trước mở ở cổng {port} ({Path.GetFileName(dir)}).");
            return new Session(port, dir, Process.GetProcessById(pid));
        }
        return null;
    }

    /// <summary>Trình duyệt điều khiển hiện tại; chưa có thì tìm trình duyệt ScheduleApp mở từ trước còn giữ hồ sơ.</summary>
    private static Session? Current()
    {
        if (_session is { } s) return s;
        foreach (var dir in BrowserProfiles.AllDirs().Where(BrowserProfiles.InUse))
            if (TryReattach(dir) is { } found) return _session = found;
        return null;
    }

    /// <summary>Địa chỉ WebSocket của tab phải trỏ về đúng cổng điều khiển trên máy này.</summary>
    internal static bool IsLocalDevToolsUrl(string url, int port) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "ws" && uri.Port == port &&
        (uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    // ───────────────────────────── Tab & DevTools ─────────────────────────────

    private sealed record TabInfo(string Id, string Title, string Url, string WebSocketUrl);

    private static async Task<List<TabInfo>> ListTabsAsync(CancellationToken ct)
    {
        var s = Current() ?? throw new InvalidOperationException(
            "Chưa có trình duyệt điều khiển nào đang mở. Thêm bước \"Trình duyệt → Mở trình duyệt ở chế độ điều khiển\" ở đầu flow.");
        var (owner, pid) = Probe(s);
        if (owner == Ownership.Foreign) throw ForeignListener(s, pid);
        string json;
        try
        {
            if (owner == Ownership.NoListener) throw new HttpRequestException();
            json = await Http.GetStringAsync($"{BaseUrl(s)}/json/list", ct);
        }
        catch (HttpRequestException)
        {
            if (_session == s) _session = null;
            throw new InvalidOperationException(
                $"Không kết nối được trình duyệt ở cổng {s.Port}. Thêm bước \"Trình duyệt → Mở trình duyệt ở chế độ điều khiển\" ở đầu flow.");
        }

        var tabs = new List<TabInfo>();
        foreach (var t in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            if (t.GetProperty("type").GetString() != "page" || !t.TryGetProperty("webSocketDebuggerUrl", out var ws)) continue;
            if (!IsLocalDevToolsUrl(ws.GetString() ?? "", s.Port)) continue;
            tabs.Add(new TabInfo(t.GetProperty("id").GetString() ?? "", t.GetProperty("title").GetString() ?? "",
                t.GetProperty("url").GetString() ?? "", ws.GetString() ?? ""));
        }
        return tabs;
    }

    /// <summary>
    /// Tab cần điều khiển: <paramref name="query"/> nếu có, ngược lại tab đầu tiên có URL chứa một trong <paramref name="preferred"/>
    /// (vd "main.aspx" của Dynamics 365), không có thì tab đầu tiên.
    /// </summary>
    internal static async Task<string> PreferTabAsync(string query, string[] preferred, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(query)) return query;
        var tabs = await ListTabsAsync(ct);
        foreach (var p in preferred)
            if (tabs.Any(t => t.Url.Contains(p, StringComparison.OrdinalIgnoreCase))) return p;
        return "";
    }

    /// <summary>Trình duyệt điều khiển đang mở (cổng remote debugging trả lời).</summary>
    internal static Task<bool> IsRunningAsync(CancellationToken ct) =>
        Current() is { } s ? IsAvailableAsync(s, ct) : Task.FromResult(false);

    /// <summary>Chụp ảnh nội dung tab (PNG) — chụp được cả khi cửa sổ trình duyệt bị che hoặc chạy headless.</summary>
    internal static async Task<byte[]> CaptureScreenshotAsync(string tabQuery, CancellationToken ct)
    {
        var result = await SendAsync(tabQuery, "Page.captureScreenshot", new { format = "png" }, ct);
        return Convert.FromBase64String(result.GetProperty("data").GetString() ?? "");
    }

    /// <summary>Phần tử có trên trang không; chờ tối đa <paramref name="timeoutMs"/> (0 = kiểm tra một lần).</summary>
    internal static async Task<bool> ExistsAsync(string tabQuery, string selector, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(selector)) throw new InvalidOperationException("Chưa nhập bộ chọn phần tử (CSS selector).");
        var probe = $$"""
            (() => { {{FindFunction}} const el = __find({{JsonSerializer.Serialize(selector)}}); return !!el && (el.offsetParent !== null || getComputedStyle(el).position === 'fixed'); })()
            """;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await EvalAsync(tabQuery, probe, ct) == "true") return true;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(PollMs, ct);
        }
    }

    private static async Task<TabInfo> FindTabAsync(string query, CancellationToken ct)
    {
        var tabs = await ListTabsAsync(ct);
        var tab = PickTab(tabs.Select(t => (t.Url, t.Title)).ToList(), query);
        if (tab < 0)
            throw new InvalidOperationException(tabs.Count == 0 ? "Trình duyệt không có tab nào đang mở." : $"Không có tab nào có URL/tiêu đề chứa \"{query.Trim()}\".");
        return tabs[tab];
    }

    /// <summary>
    /// Chọn tab theo một phần URL/tiêu đề (trống = tab đầu tiên). Trang web thật được ưu tiên hơn tab mới và trang nội bộ
    /// (edge://sync-confirmation-dialog, chrome://…, tiện ích) — Edge liệt kê cả các hộp thoại ẩn này như một "page", có khi đứng đầu danh sách.
    /// </summary>
    internal static int PickTab(IReadOnlyList<(string Url, string Title)> tabs, string query)
    {
        var q = query.Trim();
        var candidates = Enumerable.Range(0, tabs.Count);
        if (q.Length > 0)
        {
            var byUrl = candidates.Where(i => tabs[i].Url.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            candidates = byUrl.Count > 0 ? byUrl : candidates.Where(i => tabs[i].Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return candidates.OrderBy(i => TabRank(tabs[i].Url)).DefaultIfEmpty(-1).First();
    }

    private static int TabRank(string url)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(url, @"^(chrome|edge)://(newtab|new-tab-page|ntp)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return 1;
        if (System.Text.RegularExpressions.Regex.IsMatch(url, @"^(chrome|edge|chrome-untrusted|devtools|chrome-extension|extension|chrome-search)://", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return 2;
        return 0;
    }

    /// <summary>Gửi một lệnh DevTools tới tab và trả về phần "result".</summary>
    private static async Task<JsonElement> SendAsync(string tabQuery, string method, object parameters, CancellationToken ct, int timeoutMs = 30_000)
    {
        var tab = await FindTabAsync(tabQuery, ct);
        using var ws = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(5_000, timeoutMs));
        await ws.ConnectAsync(new Uri(tab.WebSocketUrl), timeout.Token);

        int id = Interlocked.Increment(ref _messageId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, timeout.Token);
                if (r.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Trình duyệt đã đóng kết nối.");
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var rid) || rid.GetInt32() != id) continue; // sự kiện khác
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException("Lỗi trình duyệt: " + err.GetProperty("message").GetString());
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (WebSocketException) { }
            return root.GetProperty("result").Clone();
        }
    }

    /// <summary>Chạy JavaScript trong tab, trả kết quả dạng chuỗi (đối tượng → JSON).</summary>
    public static async Task<string> EvalAsync(string tabQuery, string expression, CancellationToken ct, int timeoutMs = 30_000)
    {
        var result = await SendAsync(tabQuery, "Runtime.evaluate",
            new { expression, awaitPromise = true, returnByValue = true, userGesture = true }, ct, timeoutMs);
        if (result.TryGetProperty("exceptionDetails", out var ex))
        {
            var msg = ex.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var d)
                ? d.GetString()
                : ex.GetProperty("text").GetString();
            throw new InvalidOperationException("Lỗi JavaScript: " + msg?.Split('\n')[0]);
        }
        var value = result.GetProperty("result");
        if (!value.TryGetProperty("value", out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => v.GetRawText()
        };
    }

    /// <summary>Hàm tìm phần tử theo bộ chọn (CSS / xpath: / text:) rồi chạy <paramref name="body"/> với biến el.</summary>
    private static string Script(string selector, string body, string? value = null) => $$"""
        (() => {
          const SEL = {{JsonSerializer.Serialize(selector)}};
          const VALUE = {{JsonSerializer.Serialize(value ?? "")}};
          {{FindFunction}}
          const el = __find(SEL);
          if (!el) throw new Error('Không tìm thấy phần tử ' + SEL);
          {{body}}
        })()
        """;

    private const string FindFunction = """
        function __find(sel) {
          if (sel.startsWith('xpath:'))
            return document.evaluate(sel.slice(6), document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;
          if (sel.startsWith('text:')) {
            const t = sel.slice(5).trim().toLowerCase();
            const all = document.querySelectorAll('a,button,input[type=button],input[type=submit],[role=button],[role=menuitem],[role=tab],label,span,div,td,li,h1,h2,h3');
            let partial = null;
            for (const e of all) {
              if (e.offsetParent === null && getComputedStyle(e).position !== 'fixed') continue;
              const s = (e.innerText || e.value || '').trim().toLowerCase();
              if (s === t) return e;
              if (!partial && s.includes(t) && e.children.length === 0) partial = e;
            }
            return partial;
          }
          return document.querySelector(sel);
        }
        """;

    private static async Task WaitForAsync(ActionStep s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Text)) throw new InvalidOperationException("Chưa nhập bộ chọn phần tử (CSS selector).");
        var probe = $$"""
            (() => { {{FindFunction}} return !!__find({{JsonSerializer.Serialize(s.Text)}}); })()
            """;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await EvalAsync(s.Target, probe, ct) == "true") return;
            if (sw.ElapsedMilliseconds >= s.DelayMs)
                throw new TimeoutException($"Không thấy phần tử \"{s.Text}\" trên trang sau {ActionStep.FormatMs(s.DelayMs)}.");
            await Task.Delay(PollMs, ct);
        }
    }

    private static async Task WaitReadyAsync(string tabQuery, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(300, ct);
        while (sw.ElapsedMilliseconds < Math.Max(timeoutMs, 5000))
        {
            try
            {
                if (await EvalAsync(tabQuery, "document.readyState", ct) == "complete") return;
            }
            catch (InvalidOperationException) { /* trang đang chuyển — thử lại */ }
            await Task.Delay(PollMs, ct);
        }
        Log.Warn("      Trang chưa tải xong hoàn toàn — vẫn chạy tiếp.");
    }

    private static string Short(string s) => s.Length > 120 ? s[..120] + "…" : s.Replace("\n", " ");
}
