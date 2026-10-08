using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScheduleApp.Native;

namespace ScheduleApp.Services.Engine;

/// <summary>
/// Thay {{tên}} trong chuỗi bằng giá trị biến. Biến có sẵn:
/// {{today}} {{today-1:dd/MM}} {{now:HH:mm}} {{yesterday}} {{tomorrow}} · {{clipboard}} · {{env:USERNAME}} ·
/// {{secret:Tên}} · {{random:1-100}} · {{guid}} · {{biến:upper|lower|trim|len|N0|…}}.
/// </summary>
public sealed partial class VariableExpander(Dictionary<string, string> vars)
{
    public static readonly CultureInfo Vi = new("vi-VN");

    [GeneratedRegex(@"\{\{\s*([^{}\r\n]+?)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^(today|now|yesterday|tomorrow|time)\s*(?:([+-])\s*(\d+)\s*([dhmMyw]?))?\s*(?::(.*))?$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^random(?::\s*(-?\d+)\s*-\s*(-?\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex RandomPattern();

    public string Expand(string template)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{")) return template;
        return Placeholder().Replace(template, m => Resolve(m.Groups[1].Value.Trim()));
    }

    [GeneratedRegex(@"\{\{\s*secret:([^{}\r\n]+?)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPlaceholder();

    /// <summary>
    /// Chỉ thay {{secret:Tên}} (lấy từ 🔑 Bí mật, che trong nhật ký) — cho biến của môi trường kiểm thử / dòng dữ liệu nạp sẵn trước khi chạy.
    /// Không thay biến khác và không thay tiếp bên trong giá trị bí mật.
    /// </summary>
    public static string ExpandSecrets(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("{{")) return value;
        return SecretPlaceholder().Replace(value, m => ResolveSecret(m.Groups[1].Value.Trim()));
    }

    private static string ResolveSecret(string key)
    {
        var secret = SecretStore.Get(key) ?? throw new InvalidOperationException($"Chưa có bí mật \"{key}\" (thêm trong mục 🔑 Bí mật).");
        Log.Mask(secret);
        return secret;
    }

    /// <summary>Tên biến có trong chuỗi (để kiểm tra / gợi ý).</summary>
    public static IEnumerable<string> Names(string template) =>
        Placeholder().Matches(template ?? "").Select(m => m.Groups[1].Value.Trim());

    // ───────────────────────────── Lệnh cmd ─────────────────────────────

    /// <summary>
    /// Thay {{biến}} trong lệnh của bước Chạy lệnh / Gán biến từ output lệnh, theo vị trí so với dấu nháy kép của cmd — giá trị không bao giờ
    /// đảo trạng thái trong / ngoài dấu nháy: {{x:cmd}} ngoài dấu nháy tự bọc nháy, trong dấu nháy (vd "{{x:cmd}}", "D:\{{x:cmd}}.txt") chỉ bỏ
    /// dấu nháy / xuống dòng của giá trị; {{x:ps}} = chuỗi nháy đơn của PowerShell; {{x}} trần mà giá trị có dấu nháy kép / xuống dòng, hoặc
    /// &amp; | &lt; &gt; ^ khi nằm ngoài dấu nháy → báo lỗi thay vì để dữ liệu chạy thành lệnh khác. (%TÊN% trong giá trị vẫn được cmd thay —
    /// chỉ ra giá trị biến môi trường của máy, không tạo được lệnh.)
    /// </summary>
    public string ExpandCommand(string template)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{")) return template;
        var sb = new StringBuilder(template.Length + 32);
        bool inQuotes = false, escaped = false;
        // Theo dõi đúng cách cmd đọc: " đảo trạng thái, ^ ngoài dấu nháy làm ký tự kế tiếp thành chữ thường (^" không mở dấu nháy).
        void Add(string text)
        {
            foreach (char c in text)
            {
                if (escaped) escaped = false;
                else if (c == '"') inQuotes = !inQuotes;
                else if (c == '^' && !inQuotes) escaped = true;
            }
            sb.Append(text);
        }

        int last = 0;
        foreach (Match m in Placeholder().Matches(template))
        {
            Add(template[last..m.Index]);
            last = m.Index + m.Length;
            var name = m.Groups[1].Value.Trim();
            if (escaped)
                throw new InvalidOperationException($"Dấu ^ đứng ngay trước {{{{{name}}}}} làm mất tác dụng của dấu nháy bảo vệ — bỏ dấu ^ đó.");
            var (baseName, kind) = CommandFormat(name);
            // Giá trị chèn vào không còn dấu nháy kép → không đổi trạng thái nháy.
            sb.Append(kind switch
            {
                "cmd" => inQuotes ? InsideQuotes(Resolve(baseName), beforeQuote: last < template.Length && template[last] == '"') : QuoteForCmd(Resolve(baseName)),
                "ps" => QuoteForPowerShell(Resolve(baseName), inQuotes),
                _ => CheckPlainCommandValue(name, Resolve(name), inQuotes)
            });
        }
        Add(template[last..]);
        return sb.ToString();
    }

    /// <summary>"x:cmd" → ("x", "cmd"), "env:TEMP:ps" → ("env:TEMP", "ps"); không có :cmd / :ps → (tên, null).</summary>
    private static (string Base, string? Kind) CommandFormat(string name)
    {
        int colon = name.LastIndexOf(':');
        if (colon <= 0) return (name, null);
        var format = name[(colon + 1)..].Trim().ToLowerInvariant();
        return format is "cmd" or "ps" ? (name[..colon].Trim(), format) : (name, null);
    }

    /// <summary>{{x:cmd}} nằm trong dấu nháy của người dùng: bỏ dấu nháy / xuống dòng; \ cuối nhân đôi khi ngay sau là dấu nháy đóng.</summary>
    private static string InsideQuotes(string value, bool beforeQuote)
    {
        var s = value.Replace("\"", "").Replace("\r", "").Replace("\n", "");
        return beforeQuote ? s + new string('\\', s.Length - s.TrimEnd('\\').Length) : s;
    }

    /// <summary>
    /// Chuỗi nháy đơn của PowerShell ('…', nháy đơn — cả nháy cong ‘ ’ ‚ ‛ PowerShell cũng coi là nháy đơn — được viết đôi): không thay biến
    /// $x / $(…) bên trong. Bỏ dấu nháy kép / xuống dòng (cmd); ngoài dấu nháy của cmd thì thêm ^ trước &amp; | &lt; &gt; ^ ( ).
    /// </summary>
    private static string QuoteForPowerShell(string value, bool insideCmdQuotes)
    {
        var sb = new StringBuilder(value.Length + 8).Append('\'');
        foreach (char c in value)
        {
            if (c is '"' or '\r' or '\n') continue;
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B') sb.Append(c);
            else if (!insideCmdQuotes && c is '&' or '|' or '<' or '>' or '^' or '(' or ')') sb.Append('^');
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>{{x}} không có :cmd trong lệnh: chỉ cho qua giá trị không thể làm lệch dấu nháy / nối thêm lệnh.</summary>
    private static string CheckPlainCommandValue(string name, string value, bool inQuotes)
    {
        var problems = new List<string>();
        if (value.Contains('"')) problems.Add("dấu nháy kép");
        if (value.IndexOfAny(['\r', '\n']) >= 0) problems.Add("xuống dòng");
        if (!inQuotes && value.IndexOfAny(['&', '|', '<', '>', '^']) >= 0) problems.Add("ký tự & | < > ^ nằm ngoài dấu nháy");
        if (problems.Count == 0) return value;
        var suggest = name.Contains(':') && !name.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ? "biến:cmd" : name + ":cmd";
        throw new InvalidOperationException($"Giá trị của {{{{{name}}}}} có {string.Join(", ", problems)} — trong lệnh cmd có thể chạy thành lệnh khác nên bước không chạy. " +
                                            $"Dùng {{{{{suggest}}}}} (tự bọc dấu nháy an toàn) thay cho {{{{{name}}}}}.");
    }

    /// <summary>
    /// Cảnh báo cho lệnh (khi soạn bước, màn hình duyệt, xem trước AI): {{biến}} dữ liệu không có :cmd / :ps, bí mật nằm trên dòng lệnh.
    /// Biến tự sinh an toàn (ngày giờ, số thứ tự lặp, guid, random, biến môi trường, :len / :count / :url) không bị cảnh báo.
    /// </summary>
    public static List<string> CommandWarnings(string? command)
    {
        var warnings = new List<string>();
        if (string.IsNullOrEmpty(command) || !command.Contains("{{")) return warnings;
        var plain = new List<string>();
        var secrets = new List<string>();
        foreach (Match m in Placeholder().Matches(command))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)) secrets.Add("{{" + name + "}}");
            else if (CommandFormat(name).Kind == null && !IsSafeInCommand(name)) plain.Add("{{" + name + "}}");
        }
        if (plain.Count > 0)
            warnings.Add($"{string.Join(", ", plain.Distinct(StringComparer.OrdinalIgnoreCase))} chưa có :cmd — dữ liệu từ ngoài (tên file, email, Excel, web) " +
                         "nên viết {{biến:cmd}}; giá trị có dấu nháy / xuống dòng / & | < > ^ sẽ làm bước báo lỗi");
        if (secrets.Count > 0)
            warnings.Add($"{string.Join(", ", secrets.Distinct(StringComparer.OrdinalIgnoreCase))} nằm trên dòng lệnh — chương trình khác trên máy đọc được khi lệnh đang chạy");
        return warnings;
    }

    /// <summary>Thêm :cmd cho các {{biến}} bị <see cref="CommandWarnings"/> cảnh báo (không đụng {{secret:…}}, biến an toàn, biến đã có :cmd / :ps).</summary>
    public static string AddCmdFormat(string command) =>
        string.IsNullOrEmpty(command) ? command : Placeholder().Replace(command, m =>
        {
            var name = m.Groups[1].Value.Trim();
            return name.StartsWith("secret:", StringComparison.OrdinalIgnoreCase) || CommandFormat(name).Kind != null || IsSafeInCommand(name)
                ? m.Value
                : "{{" + name + ":cmd}}";
        });

    /// <summary>Biến chỉ ra chữ số / ngày giờ / chữ do máy tạo — để trần trong lệnh không chèn được lệnh.</summary>
    private static bool IsSafeInCommand(string name)
    {
        if (name.StartsWith('=') || name.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            || DatePattern().IsMatch(name) || RandomPattern().IsMatch(name)) return true;
        int colon = name.IndexOf(':');
        var baseName = (colon > 0 ? name[..colon] : name).Trim().ToLowerInvariant();
        var format = colon > 0 ? name[(colon + 1)..].Trim().ToLowerInvariant() : "";
        return format is "len" or "count" or "url"
               || baseName is "guid" or "tab" or "loop.index" or "loop.count" or "lastrow" or "http.status" or "media.played" or "media.seconds"
               || baseName.EndsWith(".index", StringComparison.Ordinal) || baseName.EndsWith(".rownumber", StringComparison.Ordinal);
    }

    private string Resolve(string name)
    {
        // Dữ liệu test ngẫu nhiên ngay trong ô chữ: {{=hoten()}}, {{=random(1, 100)}} — mỗi lần thay ra một giá trị mới.
        if (name.StartsWith('='))
        {
            try { return TestData.Evaluate(name); }
            catch (FormatException ex) { throw new InvalidOperationException(ex.Message, ex); }
        }
        if (name.StartsWith("secret:", StringComparison.OrdinalIgnoreCase))
            return ResolveSecret(name[7..].Trim());
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            // {{env:USERPROFILE:cmd}} — phần sau dấu ':' thứ hai là định dạng.
            var env = name[4..];
            int f = env.IndexOf(':');
            var envValue = Environment.GetEnvironmentVariable((f > 0 ? env[..f] : env).Trim()) ?? "";
            return f > 0 ? ApplyFormat(envValue, env[(f + 1)..].Trim()) : envValue;
        }
        if (name.Equals("clipboard", StringComparison.OrdinalIgnoreCase))
            return ClipboardHelper.TryGetText() ?? "";
        if (name.Equals("guid", StringComparison.OrdinalIgnoreCase))
            return Guid.NewGuid().ToString();
        if (name.Equals("newline", StringComparison.OrdinalIgnoreCase))
            return "\n";
        if (name.Equals("tab", StringComparison.OrdinalIgnoreCase))
            return "\t";

        var rnd = RandomPattern().Match(name);
        if (rnd.Success)
        {
            int lo = rnd.Groups[1].Success ? int.Parse(rnd.Groups[1].Value) : 0;
            int hi = rnd.Groups[2].Success ? int.Parse(rnd.Groups[2].Value) : 100;
            if (hi < lo) (lo, hi) = (hi, lo);
            return Random.Shared.Next(lo, hi + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (vars.TryGetValue(name, out var value)) return value;

        var date = DatePattern().Match(name);
        if (date.Success) return FormatDate(date);

        int colon = name.IndexOf(':');
        if (colon > 0)
        {
            var baseName = name[..colon].Trim();
            var format = name[(colon + 1)..].Trim();
            if (vars.TryGetValue(baseName, out var raw)) return ApplyFormat(raw, format);
            // Biến có sẵn kèm định dạng: {{clipboard:cmd}}, {{guid:upper}}…
            if (baseName.ToLowerInvariant() is "clipboard" or "guid" or "newline" or "tab")
                return ApplyFormat(Resolve(baseName), format);
        }

        throw new InvalidOperationException($"Biến {{{{{name}}}}} chưa được gán giá trị.");
    }

    private static string FormatDate(Match m)
    {
        var kind = m.Groups[1].Value;
        var now = DateTime.Now;
        DateTime value = kind switch
        {
            "today" => now.Date,
            "yesterday" => now.Date.AddDays(-1),
            "tomorrow" => now.Date.AddDays(1),
            _ => now
        };
        if (m.Groups[2].Success)
        {
            int n = int.Parse(m.Groups[3].Value) * (m.Groups[2].Value == "-" ? -1 : 1);
            var unit = m.Groups[4].Value;
            if (unit.Length == 0) unit = kind is "now" or "time" ? "m" : "d";
            value = unit switch
            {
                "d" => value.AddDays(n),
                "w" => value.AddDays(7 * n),
                "h" => value.AddHours(n),
                "m" => value.AddMinutes(n),
                "M" => value.AddMonths(n),
                "y" => value.AddYears(n),
                _ => value
            };
        }
        var format = m.Groups[5].Success && m.Groups[5].Value.Trim().Length > 0
            ? m.Groups[5].Value.Trim()
            : kind switch { "now" => "dd/MM/yyyy HH:mm:ss", "time" => "HH:mm", _ => "dd/MM/yyyy" };
        return value.ToString(format, Vi);
    }

    [GeneratedRegex(@"^(item|join)\((.*)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ListFunction();

    /// <summary>Các phần tử (dòng không rỗng) của biến danh sách.</summary>
    public static List<string> ListItems(string value) =>
        value.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();

    /// <summary>
    /// Định dạng giá trị biến: upper, lower, trim, unquote, len, url, json, cmd (tham số lệnh an toàn), ps (chuỗi PowerShell),
    /// filename (tên file an toàn), số (N0, 0.00…),
    /// ngày (dd/MM/yyyy…), danh sách (count, first, last, item(2), join(, ), sort, unique).
    /// </summary>
    public static string ApplyFormat(string value, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "upper": return value.ToUpper(Vi);
            case "lower": return value.ToLower(Vi);
            case "trim": return value.Trim();
            // Bỏ khoảng trắng và dấu nháy bao quanh (đường dẫn chép bằng "Copy as path" của Explorer).
            case "unquote": return value.Trim().Trim('"', '\'').Trim();
            case "len": return value.Length.ToString(CultureInfo.InvariantCulture);
            case "url": return Uri.EscapeDataString(value);
            case "nodiacritics": return Vision.ScreenOcr.RemoveDiacritics(value);
            // Chuỗi đặt được vào giữa dấu nháy của JSON: "ten": "{{ten:json}}".
            case "json": return System.Text.Json.JsonSerializer.Serialize(value, Models.JsonDefaults.Options)[1..^1];
            // Một tham số an toàn cho lệnh cmd (bước Chạy lệnh): tự bọc dấu nháy, bỏ dấu nháy/xuống dòng trong giá trị
            // để dữ liệu không tin cậy (tên file tải về, nội dung email, ô Excel…) không thoát ra ngoài dấu nháy và
            // chạy như lệnh. Dùng không kèm dấu nháy của mình: move {{tep:cmd}} D:\x.
            case "cmd": return QuoteForCmd(value);
            // Chuỗi nháy đơn của PowerShell (dùng trong powershell -Command "…"): $x / $(…) trong giá trị không chạy.
            case "ps": return QuoteForPowerShell(value, insideCmdQuotes: true);
            // Một tên file an toàn khi ghép vào đường dẫn ("D:\BaoCao\{{ten:filename}}.txt"): không có \ / : … nên không ra thư mục khác.
            case "filename": return SafeFileName(value);
            case "count": return ListItems(value).Count.ToString(CultureInfo.InvariantCulture);
            case "first": return ListItems(value).FirstOrDefault() ?? "";
            case "last": return ListItems(value).LastOrDefault() ?? "";
            case "sort": return string.Join("\n", ListItems(value).Order(StringComparer.Create(Vi, true)));
            case "unique": return string.Join("\n", ListItems(value).Distinct(StringComparer.CurrentCultureIgnoreCase));
        }
        var fn = ListFunction().Match(format);
        if (fn.Success)
        {
            var items = ListItems(value);
            if (fn.Groups[1].Value.Equals("join", StringComparison.OrdinalIgnoreCase)) return string.Join(fn.Groups[2].Value, items);
            // item(1) = phần tử đầu, item(-1) = phần tử cuối.
            if (!int.TryParse(fn.Groups[2].Value.Trim(), out int n) || n == 0) throw new InvalidOperationException($"item({fn.Groups[2].Value}) không hợp lệ — dùng item(1), item(2)… hoặc item(-1).");
            int index = n > 0 ? n - 1 : items.Count + n;
            return index >= 0 && index < items.Count ? items[index] : "";
        }
        if (TryParseNumber(value, out var number)) return number.ToString(format, Vi);
        if (DateTime.TryParse(value, Vi, DateTimeStyles.None, out var d) || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d.ToString(format, Vi);
        return value;
    }

    /// <summary>
    /// Bọc dấu nháy cho một tham số lệnh. Dấu \ ở cuối (ổ đĩa "D:\", thư mục chọn từ hộp thoại) được nhân đôi:
    /// chương trình đọc tham số kiểu C (robocopy, powershell, đa số .exe) hiểu \" là dấu nháy thường → dính tham số sau.
    /// </summary>
    private static string QuoteForCmd(string value)
    {
        var s = value.Replace("\"", "").Replace("\r", "").Replace("\n", "");
        int slashes = s.Length - s.TrimEnd('\\').Length;
        return "\"" + s + new string('\\', slashes) + "\"";
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedFileName();

    /// <summary>
    /// Tên file từ dữ liệu: ký tự không được phép trong tên file (\ / : * ? " &lt; &gt; | và ký tự điều khiển) thành "_", bỏ dấu chấm / khoảng trắng
    /// cuối (Windows tự bỏ), tên rỗng / chỉ có dấu chấm ("..") thành "_", tên thiết bị (CON, NUL, COM1…) thêm "_" phía trước.
    /// </summary>
    internal static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(value.Trim().Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (s.Trim('.').Length == 0) return "_";
        return ReservedFileName().IsMatch(s) ? "_" + s : s;
    }

    /// <summary>Đọc số theo dạng chuẩn (1234.5) hoặc kiểu Việt Nam (1.234,5).</summary>
    public static bool TryParseNumber(string s, out double value)
    {
        s = s.Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               || double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, Vi, out value);
    }
}
