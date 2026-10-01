using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>Kết quả một lần gọi API.</summary>
public sealed record HttpResult(int Status, string Body, string ContentType, string Url)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>
/// Gọi API HTTP/REST cho bước "Gọi API": nối URL với kết nối đã khai báo, gắn xác thực
/// (Bearer, Basic, khóa API, tài khoản Windows, Microsoft Entra ID cho Dynamics 365 / Graph, OAuth client credentials).
/// </summary>
public static class ApiClient
{
    private const int MaxBodyChars = 20_000_000;

    private static readonly HttpClient Plain = Create(useWindowsAuth: false);
    private static readonly HttpClient Windows = Create(useWindowsAuth: true);
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> Tokens = new();

    private static HttpClient Create(bool useWindowsAuth) =>
        new(new SocketsHttpHandler
        {
            Credentials = useWindowsAuth ? CredentialCache.DefaultCredentials : null,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "User-Agent", "ScheduleApp" } }
        };

    public static ApiConnection? FindConnection(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : SettingsStore.Current.ApiConnections.FirstOrDefault(c => c.Name.Trim().Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase))
              ?? throw new InvalidOperationException($"Chưa có kết nối API \"{name}\" (thêm trong ⚙ Cài đặt → Kết nối API).");

    public static async Task<HttpResult> SendAsync(string method, string url, ApiConnection? conn, string headers, string body, int timeoutMs, CancellationToken ct)
    {
        var fullUrl = Combine(conn?.BaseUrl ?? "", url);
        if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"URL \"{fullUrl}\" không hợp lệ (cần bắt đầu bằng http:// hoặc https://, hoặc chọn kết nối có URL gốc).");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0) timeout.CancelAfter(timeoutMs);

        using var req = new HttpRequestMessage(new HttpMethod(method.Trim().ToUpperInvariant()), uri);
        string? contentType = null;
        foreach (var (name, value) in ParseHeaders(conn?.Headers).Concat(ParseHeaders(headers)))
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = value; continue; }
            req.Headers.Remove(name);
            if (!req.Headers.TryAddWithoutValidation(name, value)) throw new InvalidOperationException($"Header \"{name}\" không hợp lệ.");
        }
        if (conn != null) await AuthorizeAsync(req, conn, timeout.Token);

        if (body.Length > 0 && req.Method != HttpMethod.Get)
        {
            var trimmed = body.TrimStart();
            contentType ??= trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "application/json" : "text/plain";
            req.Content = new StringContent(body, Encoding.UTF8);
            req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType.Contains("charset", StringComparison.OrdinalIgnoreCase) ? contentType : contentType + "; charset=utf-8");
        }

        var client = conn?.Auth == ApiAuthType.Windows ? Windows : Plain;
        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var text = await resp.Content.ReadAsStringAsync(timeout.Token);
            if (text.Length > MaxBodyChars) text = text[..MaxBodyChars];
            return new HttpResult((int)resp.StatusCode, text, resp.Content.Headers.ContentType?.MediaType ?? "", uri.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"API không phản hồi sau {ActionStep.FormatMs(timeoutMs)} ({uri.Host}).");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"Không kết nối được {uri.Host}: {ex.Message}", ex);
        }
    }

    /// <summary>Nối URL gốc của kết nối với đường dẫn của bước (URL đầy đủ trong bước thì dùng luôn).</summary>
    public static string Combine(string baseUrl, string url)
    {
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        baseUrl = baseUrl.Trim();
        if (baseUrl.Length == 0) return url;
        if (url.Length == 0) return baseUrl;
        return baseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
    }

    public static IEnumerable<(string Name, string Value)> ParseHeaders(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) throw new FormatException($"Header \"{line}\" thiếu dấu : (cần dạng Tên: giá trị).");
            yield return (line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
    }

    private static async Task AuthorizeAsync(HttpRequestMessage req, ApiConnection c, CancellationToken ct)
    {
        string Secret() => Protector.Unprotect(c.Secret);
        switch (c.Auth)
        {
            case ApiAuthType.Bearer:
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret());
                break;
            case ApiAuthType.Basic:
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.User}:{Secret()}")));
                break;
            case ApiAuthType.ApiKey:
                req.Headers.Remove(string.IsNullOrWhiteSpace(c.User) ? "x-api-key" : c.User.Trim());
                req.Headers.TryAddWithoutValidation(string.IsNullOrWhiteSpace(c.User) ? "x-api-key" : c.User.Trim(), Secret());
                break;
            case ApiAuthType.EntraId or ApiAuthType.OAuthClientCredentials:
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(c, ct));
                break;
        }
    }

    /// <summary>Lấy access token (client credentials), dùng lại tới khi gần hết hạn.</summary>
    public static async Task<string> GetTokenAsync(ApiConnection c, CancellationToken ct)
    {
        string tokenUrl, scope = c.Scope.Trim();
        if (c.Auth == ApiAuthType.EntraId)
        {
            if (string.IsNullOrWhiteSpace(c.TenantId)) throw new InvalidOperationException($"Kết nối \"{c.Name}\": chưa nhập Tenant ID.");
            tokenUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(c.TenantId.Trim())}/oauth2/v2.0/token";
            if (scope.Length == 0)
            {
                // Dynamics 365 / Dataverse: scope = https://<org>.crm.dynamics.com/.default
                if (!Uri.TryCreate(c.BaseUrl.Trim(), UriKind.Absolute, out var b)) throw new InvalidOperationException($"Kết nối \"{c.Name}\": cần URL gốc hoặc Scope.");
                scope = b.GetLeftPart(UriPartial.Authority) + "/.default";
            }
        }
        else
        {
            tokenUrl = c.TokenUrl.Trim();
            if (tokenUrl.Length == 0) throw new InvalidOperationException($"Kết nối \"{c.Name}\": chưa nhập URL lấy token.");
        }
        if (string.IsNullOrWhiteSpace(c.User)) throw new InvalidOperationException($"Kết nối \"{c.Name}\": chưa nhập Client ID.");

        var key = $"{tokenUrl}|{c.User.Trim()}|{scope}|{c.Secret.GetHashCode()}";
        if (Tokens.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow.AddMinutes(2)) return cached.Token;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = c.User.Trim(),
            ["client_secret"] = Protector.Unprotect(c.Secret)
        };
        if (scope.Length > 0) form["scope"] = scope;
        using var resp = await Plain.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string detail = text;
            try
            {
                using var err = JsonDocument.Parse(text);
                if (err.RootElement.TryGetProperty("error_description", out var d)) detail = d.GetString() ?? text;
            }
            catch (JsonException) { }
            throw new InvalidOperationException($"Không lấy được token ({(int)resp.StatusCode}): {Short(detail)}");
        }
        using var doc = JsonDocument.Parse(text);
        var token = doc.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Phản hồi lấy token không có access_token.");
        int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int s) ? s : 3600;
        Tokens[key] = (token, DateTime.UtcNow.AddSeconds(expiresIn));
        Log.Mask(token);
        return token;
    }

    /// <summary>Thử kết nối (lấy token nếu cần rồi GET URL gốc) — dùng cho nút "Thử" trong Cài đặt.</summary>
    public static async Task<string> TestAsync(ApiConnection c, CancellationToken ct)
    {
        var r = await SendAsync("GET", "", c, "", "", 30_000, ct);
        return $"{r.Status} {(HttpStatusCode)r.Status} — {Short(r.Body.Replace("\r", "").Replace("\n", " "))}";
    }

    public static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
