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
/// Xác thực và header của kết nối chỉ gửi tới đúng máy chủ (và cổng) trong URL gốc của kết nối, mật khẩu / token không gửi qua
/// http:// không mã hóa (trừ máy chủ trên chính máy này); chuyển hướng sang máy chủ khác thì bỏ xác thực, không gửi lại nội dung.
/// </summary>
public static class ApiClient
{
    private const int MaxBodyChars = 20_000_000;

    /// <summary>Phản hồi lớn hơn mức này bị từ chối (không đọc cả file khổng lồ vào bộ nhớ).</summary>
    internal const int MaxResponseBytes = 64 * 1024 * 1024;

    private const int MaxRedirects = 10;

    /// <summary>Thời gian chờ khi bước không đặt "Chờ tối đa" — không để flow treo mãi vì một API không trả lời.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    internal static readonly HttpClient Plain = Create(useWindowsAuth: false);
    internal static readonly HttpClient Windows = Create(useWindowsAuth: true);
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> Tokens = new();

    /// <summary>Header mang bí mật: bỏ khi chuyển hướng sang máy chủ khác, che giá trị trong log.</summary>
    private static readonly string[] SecretHeaderWords = ["authorization", "cookie", "key", "token", "secret", "password", "signature"];

    // Tài khoản Windows (NTLM/Kerberos) chỉ có ở client riêng, dùng khi kết nối chọn rõ "Tài khoản Windows" và đúng máy chủ của kết nối.
    // Tự xử lý chuyển hướng (AllowAutoRedirect = false): .NET chỉ bỏ Authorization, vẫn gửi header khóa API sang máy chủ mới.
    private static HttpClient Create(bool useWindowsAuth) =>
        new(new SocketsHttpHandler
        {
            Credentials = useWindowsAuth ? CredentialCache.DefaultCredentials : null,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // mỗi lần gọi tự đặt hạn (thời gian chờ của bước hoặc DefaultTimeout)
            MaxResponseContentBufferSize = MaxResponseBytes,
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
            throw new InvalidOperationException($"URL \"{Log.Redact(fullUrl)}\" không hợp lệ (cần bắt đầu bằng http:// hoặc https://, hoặc chọn kết nối có URL gốc).");

        var stepHeaders = ParseHeaders(headers).ToList();
        var connHeaders = conn == null ? [] : ParseHeaders(Credentials.Reveal(conn.Headers, $"header của kết nối \"{conn.Name}\"")).ToList();
        foreach (var (name, value) in connHeaders.Concat(stepHeaders))
            if (IsSecretHeader(name)) MaskHeaderValue(name, value);

        // Kết nối có xác thực / header riêng → chỉ gắn khi URL đúng máy chủ của kết nối và đi qua kênh mã hóa.
        bool withConn = conn != null && (conn.Auth != ApiAuthType.None || connHeaders.Count > 0);
        if (withConn && !CredentialsAllowed(conn!, uri, out var refused, connHeaders)) throw new InvalidOperationException(refused);

        int limitMs = timeoutMs > 0 ? timeoutMs : (int)DefaultTimeout.TotalMilliseconds;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limitMs);

        var httpMethod = new HttpMethod(method.Trim().ToUpperInvariant());
        bool sendBody = body.Length > 0 && httpMethod != HttpMethod.Get;
        bool stepSecrets = true; // header bí mật do chính bước khai báo — bỏ khi chuyển hướng sang máy chủ khác
        var current = uri;
        try
        {
            for (int hop = 0; ; hop++)
            {
                using var req = new HttpRequestMessage(httpMethod, current);
                string? contentType = null;
                foreach (var (name, value) in (withConn ? connHeaders : []).Concat(stepHeaders.Where(h => stepSecrets || !IsSecretHeader(h.Name))))
                {
                    if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = value; continue; }
                    req.Headers.Remove(name);
                    if (!req.Headers.TryAddWithoutValidation(name, value)) throw new InvalidOperationException($"Header \"{name}\" không hợp lệ.");
                }
                if (withConn) await AuthorizeAsync(req, conn!, timeout.Token);

                if (sendBody)
                {
                    var trimmed = body.TrimStart();
                    contentType ??= trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "application/json" : "text/plain";
                    req.Content = new StringContent(body, Encoding.UTF8);
                    req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType.Contains("charset", StringComparison.OrdinalIgnoreCase) ? contentType : contentType + "; charset=utf-8");
                }

                var client = withConn && conn!.Auth == ApiAuthType.Windows ? Windows : Plain;
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (hop < MaxRedirects && IsRedirect(resp.StatusCode) && resp.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next.Scheme is not ("http" or "https"))
                        throw new InvalidOperationException($"API chuyển hướng tới địa chỉ không hỗ trợ ({next.Scheme}:).");
                    // 303 (và 301/302 với POST, như trình duyệt) → đổi sang GET, bỏ nội dung gửi.
                    if (resp.StatusCode == HttpStatusCode.SeeOther ||
                        (resp.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && httpMethod == HttpMethod.Post))
                    {
                        httpMethod = HttpMethod.Get;
                        sendBody = false;
                    }
                    // 307/308 (và 301/302 với PUT, PATCH…) gửi lại nguyên nội dung — nội dung có thể chứa mật khẩu (POST /login),
                    // nên không gửi lại sang máy chủ khác hay qua http không mã hóa.
                    if (sendBody && (!SameHost(current, next) || Downgraded(current, next)))
                        throw new InvalidOperationException(
                            $"API chuyển hướng ({(int)resp.StatusCode}) sang {next.Host}{(Downgraded(current, next) ? " qua http:// không mã hóa" : "")} và đòi gửi lại nội dung — " +
                            "không gửi lại nội dung (có thể chứa mật khẩu / dữ liệu riêng) sang máy chủ khác. Nếu tin máy chủ đó, hãy gọi thẳng URL mới.");
                    bool keepConn = withConn && CredentialsAllowed(conn!, next, out _, connHeaders);
                    bool keepStep = stepSecrets && SameHost(current, next) && !Downgraded(current, next);
                    if ((withConn && !keepConn) || (stepSecrets && !keepStep && stepHeaders.Any(h => IsSecretHeader(h.Name))))
                        Log.Info($"      ↪ API chuyển hướng sang {next.Host} — không gửi kèm xác thực / header bí mật.");
                    withConn = keepConn;
                    stepSecrets = keepStep;
                    current = next;
                    continue;
                }

                var text = await ReadLimitedAsync(resp.Content, timeout.Token);
                if (text.Length > MaxBodyChars) text = text[..MaxBodyChars];
                return new HttpResult((int)resp.StatusCode, text, resp.Content.Headers.ContentType?.MediaType ?? "", current.ToString());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"API không phản hồi sau {ActionStep.FormatMs(limitMs)} ({current.Host}).");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"Không kết nối được {current.Host}: {Log.Redact(ex.Message)}", ex);
        }
    }

    /// <summary>
    /// Có được gắn xác thực / header của kết nối vào yêu cầu tới <paramref name="uri"/> không: phải đúng máy chủ và cổng trong URL gốc
    /// của kết nối (127.0.0.1 và localhost là hai máy chủ khác nhau — so đúng tên), và không gửi bí mật qua http:// không mã hóa
    /// (trừ máy chủ trên chính máy này). Tài khoản Windows (NTLM/Kerberos — không gửi mật khẩu) và kết nối chỉ có header thường
    /// được qua http (vd Dynamics 365 on-premises http://crm.contoso.local/).
    /// </summary>
    /// <param name="headers">Header của kết nối đã giải mã; null = chưa giải mã — có header thì coi như có bí mật.</param>
    internal static bool CredentialsAllowed(ApiConnection c, Uri uri, out string reason, IReadOnlyCollection<(string Name, string Value)>? headers = null)
    {
        if (!Uri.TryCreate(c.BaseUrl.Trim(), UriKind.Absolute, out var b) || b.Scheme is not ("http" or "https"))
        {
            reason = $"Kết nối \"{c.Name}\" chưa có URL gốc nên không biết được gửi mật khẩu / token tới máy chủ nào — nhập URL gốc trong ⚙ Cài đặt → Kết nối API.";
            return false;
        }
        if (!SameHost(b, uri))
        {
            reason = $"URL \"{uri.Host}\" khác máy chủ của kết nối \"{c.Name}\" ({b.Host}) — không gửi mật khẩu / token / header của kết nối sang máy chủ khác. " +
                     "Dùng đường dẫn tương đối, hoặc tạo kết nối riêng cho máy chủ đó.";
            return false;
        }
        if (uri.Scheme == "http" && !NotificationService.IsLoopback(uri.Host) && SendsSecrets(c, headers) &&
            !(c.Auth == ApiAuthType.Windows && IsIntranetHost(uri.Host)))
        {
            reason = $"Không gửi mật khẩu / token của kết nối \"{c.Name}\" qua http:// (không mã hóa) tới {uri.Host} — dùng https://.";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// Kết nối có gửi bí mật đi không: mật khẩu / token / khóa API / OAuth, tài khoản Windows (NTLM qua http có thể bị chuyển tiếp
    /// để đăng nhập thay bạn — chỉ cho phép với máy nội bộ, xem <see cref="IsIntranetHost"/>), hoặc header kiểu Authorization, x-api-key…
    /// </summary>
    private static bool SendsSecrets(ApiConnection c, IReadOnlyCollection<(string Name, string Value)>? headers) =>
        c.Auth != ApiAuthType.None ||
        (headers == null ? c.Headers.Trim().Length > 0 : headers.Any(h => IsSecretHeader(h.Name)));

    /// <summary>
    /// Máy trong mạng nội bộ (như vùng Intranet của Windows): tên một chữ (crmserver), tên miền nội bộ (.local, .lan, .internal, .corp,
    /// .intranet, .home.arpa) hoặc địa chỉ IP riêng (10.x, 172.16–31.x, 192.168.x, fc00::/7). Tên miền Internet (crm.contoso.com) thì không.
    /// </summary>
    internal static bool IsIntranetHost(string host)
    {
        host = host.Trim().TrimEnd('.').Trim('[', ']');
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            var b = ip.MapToIPv4().GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !ip.IsIPv4MappedToIPv6)
                return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
        }
        if (!host.Contains('.')) return host.Length > 0;
        return new[] { ".local", ".lan", ".internal", ".corp", ".intranet", ".home.arpa" }
            .Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Cùng máy chủ và cùng cổng (Uri.Port đã điền cổng mặc định 80 / 443). http → https cổng mặc định của cùng máy chủ vẫn tính là
    /// cùng máy chủ (nâng lên kênh mã hóa); hạ https → http đã kiểm riêng ở <see cref="Downgraded"/>.
    /// </summary>
    private static bool SameHost(Uri a, Uri b) =>
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && (a.Port == b.Port || (a.IsDefaultPort && b.IsDefaultPort));

    /// <summary>Chuyển từ https sang http (không mã hóa) tới máy khác máy này.</summary>
    private static bool Downgraded(Uri from, Uri to) => from.Scheme == "https" && to.Scheme == "http" && !NotificationService.IsLoopback(to.Host);

    private static bool IsRedirect(HttpStatusCode s) => s is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    internal static bool IsSecretHeader(string name) =>
        SecretHeaderWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Che giá trị header bí mật, cả phần token sau "Bearer " / "Basic ". Tên header chỉ là đoán (có chữ key / token…) nên giá trị
    /// ngắn không che (<see cref="Log.MaskGuessed"/>), header phiên bản (X-Api-Key-Version: 2024-01-01) cũng không.
    /// </summary>
    private static void MaskHeaderValue(string name, string value)
    {
        if (name.EndsWith("version", StringComparison.OrdinalIgnoreCase)) return;
        Log.MaskGuessed(value);
        int space = value.IndexOf(' ');
        if (space > 0) Log.MaskGuessed(value[(space + 1)..]);
    }

    /// <summary>Đọc nội dung phản hồi, từ chối khi lớn hơn <see cref="MaxResponseBytes"/>.</summary>
    internal static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        static InvalidOperationException TooLarge() =>
            new($"Phản hồi của API quá lớn (hơn {MaxResponseBytes / (1024 * 1024)} MB) — hãy lọc bớt ($select, $top…).");
        if (content.Headers.ContentLength > MaxResponseBytes) throw TooLarge();
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxResponseBytes) throw TooLarge();
            buffer.Write(chunk, 0, n);
        }
        // Giải mã chữ theo charset của phản hồi (như ReadAsStringAsync).
        using var copy = new ByteArrayContent(buffer.ToArray());
        if (content.Headers.ContentType != null) copy.Headers.ContentType = content.Headers.ContentType;
        return await copy.ReadAsStringAsync(ct);
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
        int number = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int colon = line.IndexOf(':');
            // Không nhắc lại nội dung dòng: thường là "Authorization Bearer <token>" thiếu dấu hai chấm.
            if (colon <= 0) throw new FormatException($"Dòng header thứ {number} thiếu dấu : (cần dạng Tên: giá trị).");
            yield return (line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
    }

    private static async Task AuthorizeAsync(HttpRequestMessage req, ApiConnection c, CancellationToken ct)
    {
        string Secret() => Credentials.Reveal(c.Secret, $"mật khẩu / token của kết nối \"{c.Name}\"");
        switch (c.Auth)
        {
            case ApiAuthType.Bearer:
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret());
                break;
            case ApiAuthType.Basic:
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.User}:{Secret()}"));
                Log.Mask(basic);
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
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
            if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out var t) || t.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"Kết nối \"{c.Name}\": URL lấy token không hợp lệ.");
            if (t.Scheme == "http" && !NotificationService.IsLoopback(t.Host))
                throw new InvalidOperationException($"Kết nối \"{c.Name}\": không gửi client secret qua http:// (không mã hóa) tới {t.Host} — dùng https://.");
        }
        if (string.IsNullOrWhiteSpace(c.User)) throw new InvalidOperationException($"Kết nối \"{c.Name}\": chưa nhập Client ID.");

        var key = $"{tokenUrl}|{c.User.Trim()}|{scope}|{c.Secret.GetHashCode()}";
        if (Tokens.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow.AddMinutes(2)) return cached.Token;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = c.User.Trim(),
            ["client_secret"] = Credentials.Reveal(c.Secret, $"client secret của kết nối \"{c.Name}\"")
        };
        if (scope.Length > 0) form["scope"] = scope;
        using var resp = await Plain.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        var text = await ReadLimitedAsync(resp.Content, ct);
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

    /// <summary>Rút gọn để hiện trong thông báo lỗi — che bí mật TRƯỚC khi cắt (cắt trước thì nửa bí mật còn lại không che được).</summary>
    public static string Short(string s)
    {
        s = Log.Redact(s);
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}
