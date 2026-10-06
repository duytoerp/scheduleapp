using System.Reflection;
using System.Text.RegularExpressions;
using ScheduleApp.Models;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Services;

/// <summary>
/// Ẩn giá trị bí mật trước khi gửi flow cho AI: mật khẩu / token đã biết (kho 🔑 Bí mật, ⚙ Cài đặt, giá trị đang được che trong log),
/// trường mật khẩu của bước (đăng nhập D365, ô mật khẩu, hỏi giá trị dạng ẩn), header / tham số / khóa JSON có tên kiểu password, token, key.
/// Mỗi giá trị được thay bằng chữ giữ chỗ do <paramref name="hide"/> trả về; chỗ giữ chỗ {{biến}} / {{secret:…}} giữ nguyên.
/// </summary>
internal sealed partial class SecretHider(Func<string, string> hide)
{
    /// <summary>Biến được dùng ở chỗ chứa bí mật (vd mật khẩu đăng nhập = {{matKhau}}) — giá trị của chúng cũng bị ẩn.</summary>
    public HashSet<string> SecretVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Trường chữ của bước (trừ dữ liệu ảnh).</summary>
    internal static readonly PropertyInfo[] TextProperties = [.. typeof(ActionStep).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.Name != nameof(ActionStep.ImageData))];

    /// <summary>Bản sao của bước đã ẩn bí mật (bước gốc không đổi).</summary>
    public ActionStep Step(ActionStep s)
    {
        var copy = s.ShallowCopy();
        var secretFields = SecretFields(s);
        foreach (var p in TextProperties)
        {
            if (p.GetValue(copy) is not string { Length: > 0 } v) continue;
            var hidden = secretFields.Contains(p.Name) ? Whole(v)
                : p.Name == nameof(ActionStep.Headers) ? Text(HeaderLine().Replace(v, m => m.Groups[1].Value + Whole(m.Groups[2].Value)))
                : Text(v);
            if (hidden != v) p.SetValue(copy, hidden);
        }
        return copy;
    }

    /// <summary>Giá trị biến để gửi: biến có tên kiểu mật khẩu / token, hoặc được dùng ở chỗ chứa bí mật → ẩn cả giá trị.</summary>
    public string Variable(VariableDef v) =>
        v.Value.Length > 0 && (SecretName().IsMatch(v.Name) || SecretVariables.Contains(v.Name.Trim())) ? Whole(v.Value) : Text(v.Value);

    /// <summary>Ẩn bí mật đã biết và các cặp tên=giá trị / "tên": "giá trị" / Bearer … trong một đoạn chữ.</summary>
    public string Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var m in Log.Snapshot())
            if (text.Contains(m, StringComparison.Ordinal)) text = text.Replace(m, hide(m));
        text = JsonPair().Replace(text, m => m.Groups[1].Value + Whole(m.Groups[2].Value) + m.Groups[3].Value);
        text = QueryPair().Replace(text, m => m.Groups[1].Value + Whole(m.Groups[2].Value));
        text = AuthScheme().Replace(text, m => m.Groups[1].Value + " " + Whole(m.Groups[2].Value));
        return text;
    }

    /// <summary>
    /// Ẩn cả giá trị. Mọi biến {{…}} trong giá trị được ghi nhớ để ẩn giá trị của biến (vd "Basic {{cred}}" → ẩn giá trị của cred);
    /// giá trị chỉ gồm chỗ giữ chỗ (có thể kèm chữ Bearer / Basic) thì giữ nguyên để AI vẫn đọc được.
    /// </summary>
    private string Whole(string value)
    {
        if (value.Trim().Length == 0) return value;
        foreach (Match t in AnyTemplate().Matches(value))
        {
            var name = t.Groups[1].Value.Trim();
            int colon = name.IndexOf(':');
            if (!name.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)) SecretVariables.Add(colon > 0 ? name[..colon].Trim() : name);
        }
        var rest = AnyTemplate().Replace(value, "").Trim();
        if (rest.Length < value.Trim().Length && (rest.Length == 0 || SchemeOnly().IsMatch(rest))) return value;
        return hide(value);
    }

    /// <summary>Tên (biến, header, khóa JSON) kiểu mật khẩu / token / khóa bí mật — dùng cả khi chạy để che giá trị của biến có tên như vậy.</summary>
    internal static bool IsSecretName(string name) => SecretName().IsMatch(name);

    /// <summary>Trường của bước chắc chắn chứa mật khẩu / khóa bí mật.</summary>
    private static HashSet<string> SecretFields(ActionStep s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        // Gán giá trị cố định cho biến tên kiểu mật khẩu / token (matKhau = "…").
        if (s.Type == StepType.SetVariable && s.VarSource == VarSource.Value && SecretName().IsMatch(s.Variable)) set.Add(nameof(ActionStep.Text));
        if (s.Type == StepType.Dynamics && s.D365Action == D365Action.Login)
        {
            set.Add(nameof(ActionStep.Arguments)); // mật khẩu
            set.Add(nameof(ActionStep.RowRef));    // khóa TOTP
        }
        if (s.Type == StepType.SetVariable && s.VarSource == VarSource.AskUser && s.Force) set.Add(nameof(ActionStep.Arguments));
        // Nhập vào ô mật khẩu (bộ chọn / tên phần tử có chữ password, mật khẩu…).
        if ((s.Type == StepType.SetElementText || (s.Type == StepType.Browser && s.BrowserAction == BrowserAction.SetValue)) && SecretName().IsMatch(s.Text))
            set.Add(nameof(ActionStep.Arguments));
        return set;
    }

    [GeneratedRegex(@"pass|pwd|mat_?khau|mật\s*khẩu|secret|token|api_?key|apikey|(?<![a-z])otp|mfa|credential|private", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"\{\{([^{}]+)\}\}")]
    private static partial Regex AnyTemplate();

    [GeneratedRegex(@"^(?i:bearer|basic)$")]
    private static partial Regex SchemeOnly();

    // Header "Tên: giá trị" có tên kiểu Authorization, x-api-key, Cookie, …-token.
    [GeneratedRegex(@"(?im)^(\s*[\w-]*(?:authorization|cookie|key|token|secret|password|signature)[\w-]*\s*:\s*)(.*?)\s*$")]
    private static partial Regex HeaderLine();

    // Tên khóa JSON / tham số kiểu mật khẩu, token, khóa: password, access_token, …; key, api-key, subscription-key, access_key,
    // subscriptionKey (đuôi key sau dấu _ / - hoặc chữ K hoa — không bắt keyword, monkey).
    private const string SecretKeyName =
        @"(?:[\w-]*(?:pass|pwd|secret|token|api_?key|apikey|mat_?khau|matkhau|credential|authorization)[\w-]*|(?:[\w-]*[_-])?key|[\w-]*(?-i:[a-z0-9]Key))";

    // "password": "…" trong JSON.
    [GeneratedRegex(@"(?i)(""" + SecretKeyName + @"""\s*:\s*"")((?:[^""\\]|\\.)*)("")")]
    private static partial Regex JsonPair();

    // password=… trong URL / dữ liệu form.
    [GeneratedRegex(@"(?i)((?:^|[?&;])(?:" + SecretKeyName + @"|[\w-]*(?:sig|code)[\w-]*)=)([^&\s#""']+)")]
    private static partial Regex QueryPair();

    // Bearer / Basic <chuỗi dài> hoặc Bearer / Basic {{biến}}.
    [GeneratedRegex(@"(?i)\b(Bearer|Basic)\s+(\{\{[^{}]+\}\}|[A-Za-z0-9._~+/=-]{8,})")]
    private static partial Regex AuthScheme();
}
