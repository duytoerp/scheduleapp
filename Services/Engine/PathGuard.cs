using System.Text.RegularExpressions;
using ScheduleApp.Models;

namespace ScheduleApp.Services.Engine;

/// <summary>
/// Đường dẫn lấy từ {{biến}} (nội dung email, tên file tải về, ô Excel, API…) trong các bước mở / đọc / ghi file không được trỏ tới máy lạ
/// (\\máy\…, \??\UNC\máy\…, file://máy/…): chỉ cần kiểm tra / mở file trên máy đó là Windows đã tự gửi thông tin đăng nhập (mã băm NTLM)
/// tới nó, còn "Mở ứng dụng" thì chạy luôn file trên máy đó. Máy được tin khi bước ghi thẳng \\máy\thư-mục\ (hoặc ổ đĩa) trước phần {{biến}},
/// hoặc tên máy có trong công việc (bước, biến khai báo, biến môi trường kiểm thử) hay do người dùng tự nhập lúc chạy
/// (<see cref="FlowContext.IsTrustedServer"/>). Ổ mạng đã ánh xạ (Z:) là máy người dùng tự chọn — không chặn.
/// Ghi file: đường dẫn sau khi thay biến không được ra ngoài thư mục ghi thẳng trong bước (..\).
/// </summary>
internal static partial class PathGuard
{
    /// <summary>Kiểm các trường đường dẫn của bước vừa thay biến (<paramref name="expanded"/>) so với bước gốc (<paramref name="original"/>).</summary>
    /// <remarks>Phát video kiểm từng dòng lúc thay biến (<see cref="Ensure"/>); "Lặp mỗi dòng văn bản" coi đường dẫn mạng lấy từ biến là nội dung (<see cref="LoopFrame"/>).</remarks>
    public static void Check(ActionStep original, ActionStep expanded, FlowContext ctx)
    {
        switch (original.Type)
        {
            case StepType.LaunchApp:
                Ensure(original.Target, expanded.Target, "Ứng dụng / file cần mở", ctx);
                break;
            case StepType.SetVariable when original.VarSource == VarSource.File:
                Ensure(original.Target, expanded.Target, "File cần đọc", ctx);
                break;
            case StepType.WriteData:
                Ensure(original.Target, expanded.Target, "File cần ghi", ctx);
                EnsureInsideWrittenFolder(original.Target, expanded.Target);
                break;
            case StepType.Loop when original.LoopKind == LoopKind.Rows:
                Ensure(original.Target, expanded.Target, "File Excel / CSV", ctx);
                break;
            case StepType.Loop when original.LoopKind == LoopKind.Files:
                Ensure(original.Target, expanded.Target, "Thư mục", ctx);
                break;
        }
        if (original.HasCondition && original.Condition == ConditionKind.FileExists)
            Ensure(original.Target, expanded.Target, "File / thư mục cần kiểm tra", ctx);
    }

    /// <summary>
    /// Báo lỗi nếu <paramref name="expanded"/> trỏ tới máy khác (UNC / đường dẫn thiết bị / file://máy) mà máy đó không được tin
    /// (xem <see cref="PathGuard"/>). <paramref name="template"/> không có {{biến}} → luôn được (người dùng ghi thẳng).
    /// </summary>
    /// <returns><paramref name="expanded"/> (để dùng ngay trong biểu thức).</returns>
    public static string Ensure(string? template, string? expanded, string what, FlowContext? ctx)
    {
        expanded ??= "";
        if (expanded.Trim().Length == 0 || template == null || !template.Contains("{{", StringComparison.Ordinal)) return expanded;
        if (!RemotePathGate.IsRemote(expanded) || RootWrittenIn(template, expanded)) return expanded;
        var server = ServerOf(expanded);
        if (server != null && ctx?.IsTrustedServer(server) == true) return expanded;
        throw new InvalidOperationException(
            $"{what} \"{expanded.Trim()}\" trỏ tới máy khác" + (server != null ? $" (\"{server}\")" : "") + " lấy từ biến — máy này không có trong công việc " +
            "nên không mở (tránh gửi thông tin đăng nhập Windows tới máy lạ). Muốn dùng file trên máy đó, ghi thẳng \\\\tên-máy\\thư-mục\\ trong bước " +
            "trước phần {{biến}} (vd \\\\nas\\chung\\{{tenFile}}.xlsx) hoặc khai báo đường dẫn trong biến của công việc.");
    }

    /// <summary>
    /// Phần ghi thẳng trước {{biến}} đầu tiên (sau khi thay %BIẾN_MÔI_TRƯỜNG% của máy) chứa đủ gốc của đường dẫn — ổ đĩa "X:" hoặc
    /// "\\máy\thư-mục\" — và <paramref name="path"/> vẫn nằm dưới gốc đó. Không có {{biến}} → true (cả đường dẫn ghi thẳng).
    /// Vd "\\nas\chung\{{tenFile}}.txt" → true với \\nas\chung\a.txt; "{{duongDan}}", "\\{{may}}\chung\a.txt" → false.
    /// </summary>
    internal static bool RootWrittenIn(string template, string path)
    {
        var t = template.Trim().Trim('"');
        int brace = t.IndexOf("{{", StringComparison.Ordinal);
        if (brace < 0) return true;
        var root = RootOf(Environment.ExpandEnvironmentVariables(t[..brace]).Replace('/', '\\'));
        if (root == null) return false;
        try
        {
            return Path.GetFullPath(Clean(path)).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>Gốc đầy đủ ở đầu <paramref name="prefix"/>: "X:" hoặc "\\máy\thư-mục\" (thư mục chung phải kết thúc bằng \); không có → null.</summary>
    private static string? RootOf(string prefix)
    {
        if (prefix.Length >= 2 && char.IsAsciiLetter(prefix[0]) && prefix[1] == ':') return prefix[..2];
        // \\?\…, \\.\… (đường dẫn thiết bị) không coi là gốc ghi thẳng.
        if (!prefix.StartsWith(@"\\", StringComparison.Ordinal) || prefix.Length < 3 || prefix[2] is '?' or '.') return null;
        int server = prefix.IndexOf('\\', 2);
        int share = server > 2 ? prefix.IndexOf('\\', server + 1) : -1;
        return share > server + 1 ? prefix[..(share + 1)] : null;
    }

    /// <summary>
    /// Tên máy trong đường dẫn mạng: \\máy\…, //máy/…, \\?\UNC\máy\…, \\.\UNC\máy\…, \??\UNC\máy\…, file://máy/…; dạng khác
    /// (\\?\GLOBALROOT\…, đường ống \\.\pipe\…) → null (không tin được).
    /// </summary>
    internal static string? ServerOf(string path)
    {
        var raw = path.Trim().Trim('"', '\'');
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.IsUnc && uri.Host.Length > 0 ? uri.Host : null;
        var p = Environment.ExpandEnvironmentVariables(raw).Replace('/', '\\');
        foreach (var prefix in (string[])[@"\\?\UNC\", @"\\.\UNC\", @"\??\UNC\"])
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[prefix.Length..];
        if (!p.StartsWith(@"\\", StringComparison.Ordinal) || p.Length < 3 || p[2] is '?' or '.' or '\\') return null;
        int end = p.IndexOf('\\', 2);
        var server = end < 0 ? p[2..] : p[2..end];
        return server.Length > 0 ? server : null;
    }

    [GeneratedRegex(@"(?:\\\\|file://)([^\\/\s""'{}%*?<>|:]+)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex ServerInText();

    /// <summary>Tên máy của các đường dẫn mạng ghi trong <paramref name="text"/> (\\máy\…, file://máy/…) — bỏ qua phần {{biến}}.</summary>
    internal static IEnumerable<string> ServersIn(string? text) =>
        string.IsNullOrEmpty(text) ? [] : ServerInText().Matches(text).Select(m => m.Groups[1].Value);

    /// <summary>
    /// Ghi file: thư mục ghi thẳng trước {{biến}} (vd "D:\BaoCao\" trong "D:\BaoCao\{{ten}}.txt") — file sau khi thay biến phải nằm trong
    /// thư mục đó, tên lấy từ dữ liệu ("..\..\Windows\…") không được ghi ra chỗ khác.
    /// </summary>
    private static void EnsureInsideWrittenFolder(string template, string expanded)
    {
        var t = template.Trim().Trim('"').Replace('/', '\\');
        int brace = t.IndexOf("{{", StringComparison.Ordinal);
        if (brace <= 0) return;
        int slash = t.LastIndexOf('\\', brace - 1);
        if (slash < 0) return;
        var folder = Environment.ExpandEnvironmentVariables(t[..(slash + 1)]);
        if (RootOf(folder) == null) return; // thư mục tương đối — không có gốc để so
        string dir, full;
        try
        {
            dir = Path.GetFullPath(folder);
            full = Path.GetFullPath(Clean(expanded));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return; // bước ghi tự báo lỗi đường dẫn
        }
        if (!dir.EndsWith('\\')) dir += '\\';
        if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"File cần ghi \"{full}\" nằm ngoài thư mục \"{dir}\" ghi trong bước (tên lấy từ biến có \"..\\\" hoặc đường dẫn khác) — không ghi. " +
                "Tên file lấy từ dữ liệu nên viết {{biến:filename}}.");
    }

    private static string Clean(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
}
