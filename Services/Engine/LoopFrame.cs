using System.Globalization;
using ScheduleApp.Models;
using ScheduleApp.Services.Data;

namespace ScheduleApp.Services.Engine;

/// <summary>Trạng thái một vòng lặp đang chạy.</summary>
internal sealed class LoopFrame
{
    /// <summary>Giới hạn an toàn cho "Lặp khi" để không treo máy nếu điều kiện luôn đúng.</summary>
    public const int MaxWhileIterations = 100_000;

    private readonly ActionStep _step;
    private readonly List<Dictionary<string, string>>? _items;

    public int Start { get; }
    public int End { get; }

    /// <summary>Số lần đã bắt đầu (1 = đang ở lần đầu).</summary>
    public int Index { get; private set; }

    public int? Total => _step.LoopKind == LoopKind.Count ? _step.Count : _items?.Count;

    private LoopFrame(ActionStep step, int start, int end, List<Dictionary<string, string>>? items)
    {
        _step = step;
        Start = start;
        End = end;
        _items = items;
    }

    /// <summary>Tạo vòng lặp; đọc sẵn dữ liệu (file Excel/CSV, dòng văn bản, danh sách file).</summary>
    /// <param name="expanded">Bước Lặp đã thay {{biến}}.</param>
    /// <param name="original">Bước Lặp gốc (chưa thay biến) — để biết đường dẫn được ghi thẳng trong bước hay lấy từ biến.</param>
    public static LoopFrame Create(ActionStep expanded, int start, int end, ActionStep? original = null) =>
        new(expanded, start, end, expanded.LoopKind switch
        {
            LoopKind.Rows => LoadRows(expanded),
            LoopKind.Lines => LoadLines(expanded, original),
            LoopKind.Files => LoadFiles(expanded),
            _ => null
        });

    /// <summary>Sang lần lặp tiếp theo; gán biến của lần lặp. False nếu đã hết.</summary>
    public async Task<bool> MoveNextAsync(ActionStep original, FlowContext ctx)
    {
        switch (_step.LoopKind)
        {
            case LoopKind.Count:
                if (Index >= Math.Max(0, _step.Count)) return false;
                break;

            case LoopKind.While:
                if (Index >= MaxWhileIterations)
                    throw new InvalidOperationException($"Vòng lặp đã chạy {MaxWhileIterations:N0} lần — dừng để tránh lặp vô hạn.");
                // Điều kiện được đánh giá lại mỗi vòng với giá trị biến mới nhất.
                if (!await ConditionEvaluator.EvaluateAsync(ctx.ExpandStep(original), ctx)) return false;
                break;

            default:
                if (_items == null || Index >= _items.Count) return false;
                foreach (var (k, v) in _items[Index]) ctx.Vars[k] = v;
                break;
        }

        Index++;
        ctx.Vars["loop.index"] = Index.ToString(CultureInfo.InvariantCulture);
        ctx.Vars["loop.count"] = Total?.ToString(CultureInfo.InvariantCulture) ?? "";
        ctx.Vars[_step.LoopVar + ".index"] = ctx.Vars["loop.index"];
        return true;
    }

    private static List<Dictionary<string, string>> LoadRows(ActionStep s)
    {
        var path = Environment.ExpandEnvironmentVariables(s.Target.Trim().Trim('"'));
        if (path.Length == 0) throw new InvalidOperationException("Chưa nhập file Excel / CSV.");
        var table = TabularReader.Read(path, s.Arguments);
        var prefix = s.LoopVar;
        Log.Info($"      Đọc {table.Rows.Count} dòng × {table.Headers.Count} cột: {string.Join(", ", table.Headers.Take(12))}{(table.Headers.Count > 12 ? "…" : "")}");

        var items = new List<Dictionary<string, string>>(table.Rows.Count);
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < table.Headers.Count; c++)
            {
                vars[$"{prefix}.{table.Headers[c]}"] = row[c];
                vars[$"{prefix}.{c + 1}"] = row[c];
            }
            vars[prefix] = string.Join("\t", row);
            // Số dòng thật trong file (như Excel hiển thị) — dùng cho bước "Ghi Excel / CSV" để ghi kết quả vào đúng dòng này.
            vars[$"{prefix}.rowNumber"] = table.RowNumbers[r].ToString(CultureInfo.InvariantCulture);
            items.Add(vars);
        }
        return items;
    }

    /// <summary>
    /// "File hoặc {{biến}}": là đường dẫn tới file có thật → đọc file, ngược lại coi chính nó là nội dung. Đường dẫn mạng (\\máy\..., ổ mạng)
    /// chỉ được mở khi tên máy (ổ đĩa) ghi thẳng trong bước; lấy từ biến (vd nội dung email / API) thì coi là nội dung — chỉ cần kiểm tra
    /// file trên máy lạ là Windows đã tự gửi thông tin đăng nhập (NTLM) tới máy đó.
    /// </summary>
    private static List<Dictionary<string, string>> LoadLines(ActionStep s, ActionStep? original)
    {
        var source = s.Target;
        var path = Environment.ExpandEnvironmentVariables(source.Trim().Trim('"'));
        string text = path.Length > 0 && path.Length < 260 && path.IndexOfAny(['\r', '\n']) < 0 && MayOpen(path, original) && File.Exists(path)
            ? File.ReadAllText(path)
            : source; // không phải file → coi là nội dung (vd {{danhSach}})
        return text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .Select(l => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [s.LoopVar] = l })
            .ToList();
    }

    private static bool MayOpen(string path, ActionStep? original)
    {
        // Bước gốc ghi thẳng đường dẫn, hoặc ít nhất ghi thẳng ổ đĩa / \\máy\thư-mục-chung\ trước {{biến}} → mở được (xem PathGuard).
        if (!IsNetworkPath(path) || (original != null && PathGuard.RootWrittenIn(original.Target, path))) return true;
        Log.Warn($"      \"{path}\" là đường dẫn mạng lấy từ biến — không mở (tránh gửi thông tin đăng nhập Windows tới máy lạ), coi là nội dung văn bản. " +
                 "Muốn đọc file trên máy khác, ghi thẳng \\\\tên-máy\\thư-mục trong bước.");
        return false;
    }

    /// <summary>Đường dẫn đi qua mạng (UNC, \\?\UNC\, \??\UNC\, ổ mạng ánh xạ…) — xem <see cref="RemotePathGate.IsNetworkPath"/>.</summary>
    internal static bool IsNetworkPath(string path) => RemotePathGate.IsNetworkPath(path);

    private static List<Dictionary<string, string>> LoadFiles(ActionStep s)
    {
        var folder = Environment.ExpandEnvironmentVariables(s.Target.Trim().Trim('"'));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Không tìm thấy thư mục \"{folder}\".");
        var patterns = string.IsNullOrWhiteSpace(s.Arguments)
            ? ["*"]
            : s.Arguments.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var prefix = s.LoopVar;
        return patterns.SelectMany(p => Directory.EnumerateFiles(folder, p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [prefix] = f,
                [prefix + ".name"] = Path.GetFileName(f),
                [prefix + ".base"] = Path.GetFileNameWithoutExtension(f),
                [prefix + ".ext"] = Path.GetExtension(f),
                [prefix + ".dir"] = Path.GetDirectoryName(f) ?? ""
            })
            .ToList();
    }
}
