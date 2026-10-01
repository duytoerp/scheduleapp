using System.Globalization;
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

    /// <summary>Tên biến có trong chuỗi (để kiểm tra / gợi ý).</summary>
    public static IEnumerable<string> Names(string template) =>
        Placeholder().Matches(template ?? "").Select(m => m.Groups[1].Value.Trim());

    private string Resolve(string name)
    {
        if (name.StartsWith("secret:", StringComparison.OrdinalIgnoreCase))
        {
            var key = name[7..].Trim();
            var secret = SecretStore.Get(key) ?? throw new InvalidOperationException($"Chưa có bí mật \"{key}\" (thêm trong mục 🔑 Bí mật).");
            Log.Mask(secret);
            return secret;
        }
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(name[4..].Trim()) ?? "";
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
        if (colon > 0 && vars.TryGetValue(name[..colon].Trim(), out var raw))
            return ApplyFormat(raw, name[(colon + 1)..].Trim());

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

    /// <summary>Định dạng giá trị biến: upper, lower, trim, len, số (N0, 0.00…) hoặc ngày (dd/MM/yyyy…).</summary>
    public static string ApplyFormat(string value, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "upper": return value.ToUpper(Vi);
            case "lower": return value.ToLower(Vi);
            case "trim": return value.Trim();
            case "len": return value.Length.ToString(CultureInfo.InvariantCulture);
            case "url": return Uri.EscapeDataString(value);
            case "nodiacritics": return Vision.ScreenOcr.RemoveDiacritics(value);
        }
        if (TryParseNumber(value, out var number)) return number.ToString(format, Vi);
        if (DateTime.TryParse(value, Vi, DateTimeStyles.None, out var d) || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d.ToString(format, Vi);
        return value;
    }

    /// <summary>Đọc số theo dạng chuẩn (1234.5) hoặc kiểu Việt Nam (1.234,5).</summary>
    public static bool TryParseNumber(string s, out double value)
    {
        s = s.Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               || double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, Vi, out value);
    }
}
