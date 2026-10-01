using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.Automation;

/// <summary>
/// Điều khiển Chrome / Edge qua Chrome DevTools Protocol (cổng remote debugging).
/// Trình duyệt phải được mở bằng bước "Mở trình duyệt ở chế độ điều khiển" (hồ sơ riêng của ScheduleApp).
/// Bộ chọn: CSS (#id, .class, input[name=q]), "xpath://button[.='Lưu']" hoặc "text:Đăng nhập".
/// </summary>
internal static class BrowserClient
{
    private const int PollMs = 300;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static int _messageId;

    private static int Port => SettingsStore.Current.BrowserPort;

    /// <summary>Tham số thêm khi mở trình duyệt (kiểm thử dùng " --headless=new").</summary>
    internal static string ExtraLaunchArgs { get; set; } = "";

    /// <summary>Tiến trình trình duyệt mở gần nhất (kiểm thử dùng để dọn dẹp).</summary>
    internal static Process? LastLaunched { get; private set; }
    private static string BaseUrl => $"http://127.0.0.1:{Port}";

    public static async Task ExecuteAsync(ActionStep s, Func<string, string, Task> setVar, CancellationToken ct)
    {
        switch (s.BrowserAction)
        {
            case BrowserAction.Launch:
                await LaunchAsync(s.Target, s.Text, s.Arguments, ct);
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
    private static async Task LaunchAsync(string browser, string url, string profile, CancellationToken ct)
    {
        profile = BrowserProfiles.SafeName(profile);
        var dir = BrowserProfiles.Dir(browser, profile);
        var label = BrowserProfiles.DisplayName(browser) + (profile.Length == 0 ? "" : $" · hồ sơ \"{profile}\"");

        if (await IsAvailableAsync(ct))
        {
            if (BrowserProfiles.InUse(dir) || (profile.Length == 0 && !BrowserProfiles.AllDirs().Any(BrowserProfiles.InUse)))
            {
                // Đúng hồ sơ đang được điều khiển (hoặc trình duyệt do người dùng tự mở ở cổng này) → chỉ mở tab mới.
                await OpenTabAsync(url, ct);
                return;
            }
            // Cổng điều khiển chỉ có một: đóng trình duyệt điều khiển đang mở hồ sơ khác rồi mở hồ sơ được chọn.
            var other = BrowserProfiles.AllDirs().FirstOrDefault(BrowserProfiles.InUse);
            if (other == null)
                throw new InvalidOperationException(
                    $"Cổng điều khiển {Port} đang được một trình duyệt khác dùng. Hãy đóng trình duyệt đó (hoặc đổi cổng trong ⚙ Cài đặt) rồi chạy lại.");
            Log.Info($"      Đóng trình duyệt điều khiển đang mở hồ sơ khác ({Path.GetFileName(other)}) để mở {label}.");
            await CloseBrowserAsync(other, ct);
        }
        else if (BrowserProfiles.InUse(dir))
        {
            // Mở thêm lần nữa chỉ tạo cửa sổ mới trong phiên cũ (không có cổng điều khiển).
            throw new InvalidOperationException(
                $"{label} đang mở nhưng không ở chế độ điều khiển (cổng {Port}). Hãy đóng cửa sổ trình duyệt đó rồi chạy lại.");
        }

        var exe = ResolveBrowser(browser);
        if (!Directory.Exists(dir)) Log.Info($"      Hồ sơ mới ({Path.GetFileName(dir)}) — lần đầu cần đăng nhập các trang web, lần sau được giữ lại.");
        // Chrome 136+ chỉ cho remote debugging với thư mục hồ sơ riêng (không phải hồ sơ mặc định).
        var args = $"--remote-debugging-port={Port} --user-data-dir=\"{dir}\" --no-first-run --no-default-browser-check{ExtraLaunchArgs}";
        if (!string.IsNullOrWhiteSpace(url)) args += " " + NormalizeUrl(url);
        LastLaunched = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });

        var sw = Stopwatch.StartNew();
        while (!await IsAvailableAsync(ct))
        {
            if (sw.ElapsedMilliseconds > 20_000)
                throw new TimeoutException($"Trình duyệt không mở cổng điều khiển {Port}. Nếu {label} đang chạy, hãy đóng hết rồi thử lại.");
            await Task.Delay(500, ct);
        }
        if (!string.IsNullOrWhiteSpace(url)) await WaitReadyAsync("", 30_000, ct);
    }

    private static async Task OpenTabAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}/json/new?{Uri.EscapeDataString(NormalizeUrl(url))}");
        (await Http.SendAsync(req, ct)).EnsureSuccessStatusCode();
        await Task.Delay(500, ct);
        await WaitReadyAsync("", 30_000, ct); // tab mới nằm đầu danh sách
    }

    /// <summary>Đóng trình duyệt đang chiếm cổng điều khiển (lệnh Browser.close) và chờ nó nhả cổng + thư mục hồ sơ.</summary>
    internal static async Task CloseBrowserAsync(string dir, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{BaseUrl}/json/version", ct));
            var wsUrl = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!;
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

        var sw = Stopwatch.StartNew();
        while (await IsAvailableAsync(ct) || BrowserProfiles.InUse(dir))
        {
            if (sw.ElapsedMilliseconds > 15_000)
                throw new TimeoutException($"Trình duyệt điều khiển ({Path.GetFileName(dir)}) không đóng. Hãy tự đóng nó rồi chạy lại.");
            await Task.Delay(300, ct);
        }
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

    private static async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync($"{BaseUrl}/json/version", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        return url.Contains("://") || url.StartsWith("about:") ? url : "https://" + url;
    }

    // ───────────────────────────── Tab & DevTools ─────────────────────────────

    private sealed record TabInfo(string Id, string Title, string Url, string WebSocketUrl);

    private static async Task<List<TabInfo>> ListTabsAsync(CancellationToken ct)
    {
        string json;
        try
        {
            json = await Http.GetStringAsync($"{BaseUrl}/json/list", ct);
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(
                $"Không kết nối được trình duyệt ở cổng {Port}. Thêm bước \"Trình duyệt → Mở trình duyệt ở chế độ điều khiển\" ở đầu flow.");
        }

        var tabs = new List<TabInfo>();
        foreach (var t in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            if (t.GetProperty("type").GetString() != "page" || !t.TryGetProperty("webSocketDebuggerUrl", out var ws)) continue;
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
    internal static Task<bool> IsRunningAsync(CancellationToken ct) => IsAvailableAsync(ct);

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
