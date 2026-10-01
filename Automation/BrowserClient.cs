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
    private static string BaseUrl => $"http://127.0.0.1:{Port}";

    public static async Task ExecuteAsync(ActionStep s, Func<string, string, Task> setVar, CancellationToken ct)
    {
        switch (s.BrowserAction)
        {
            case BrowserAction.Launch:
                await LaunchAsync(s.Target, s.Text, ct);
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

    private static async Task LaunchAsync(string browser, string url, CancellationToken ct)
    {
        if (await IsAvailableAsync(ct))
        {
            // Đã có trình duyệt đang điều khiển → chỉ mở tab mới.
            if (!string.IsNullOrWhiteSpace(url))
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}/json/new?{Uri.EscapeDataString(NormalizeUrl(url))}");
                (await Http.SendAsync(req, ct)).EnsureSuccessStatusCode();
                await Task.Delay(500, ct);
                await WaitReadyAsync("", 30_000, ct); // tab mới nằm đầu danh sách
            }
            return;
        }

        var (exe, name) = ResolveBrowser(browser);
        // Chrome 136+ chỉ cho remote debugging với thư mục hồ sơ riêng (không phải hồ sơ mặc định).
        var profile = Path.Combine(JobStore.DataDir, "browser-" + name);
        var args = $"--remote-debugging-port={Port} --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check";
        if (!string.IsNullOrWhiteSpace(url)) args += " " + NormalizeUrl(url);
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });

        var sw = Stopwatch.StartNew();
        while (!await IsAvailableAsync(ct))
        {
            if (sw.ElapsedMilliseconds > 20_000)
                throw new TimeoutException($"Trình duyệt không mở cổng điều khiển {Port}. Nếu {name} đang chạy với cùng hồ sơ, hãy đóng hết rồi thử lại.");
            await Task.Delay(500, ct);
        }
        if (!string.IsNullOrWhiteSpace(url)) await WaitReadyAsync("", 30_000, ct);
    }

    private static (string Exe, string Name) ResolveBrowser(string browser)
    {
        var b = browser.Trim().Trim('"');
        if (b.Length > 0 && File.Exists(b)) return (b, Path.GetFileNameWithoutExtension(b).ToLowerInvariant());
        bool edge = b.Contains("edge", StringComparison.OrdinalIgnoreCase);
        var exeName = edge ? "msedge.exe" : "chrome.exe";

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            if (key?.GetValue(null) is string path && File.Exists(path)) return (path, edge ? "edge" : "chrome");
        }
        var candidates = edge
            ? new[] { @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe" }
            : [@"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", @"%LocalAppData%\Google\Chrome\Application\chrome.exe"];
        foreach (var c in candidates)
        {
            var p = Environment.ExpandEnvironmentVariables(c);
            if (File.Exists(p)) return (p, edge ? "edge" : "chrome");
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

    private static async Task<TabInfo> FindTabAsync(string query, CancellationToken ct)
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
        if (tabs.Count == 0) throw new InvalidOperationException("Trình duyệt không có tab nào đang mở.");
        if (string.IsNullOrWhiteSpace(query)) return tabs[0];

        var q = query.Trim();
        return tabs.FirstOrDefault(t => t.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
               ?? tabs.FirstOrDefault(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"Không có tab nào có URL/tiêu đề chứa \"{q}\".");
    }

    /// <summary>Gửi một lệnh DevTools tới tab và trả về phần "result".</summary>
    private static async Task<JsonElement> SendAsync(string tabQuery, string method, object parameters, CancellationToken ct)
    {
        var tab = await FindTabAsync(tabQuery, ct);
        using var ws = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
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
    public static async Task<string> EvalAsync(string tabQuery, string expression, CancellationToken ct)
    {
        var result = await SendAsync(tabQuery, "Runtime.evaluate",
            new { expression, awaitPromise = true, returnByValue = true, userGesture = true }, ct);
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
