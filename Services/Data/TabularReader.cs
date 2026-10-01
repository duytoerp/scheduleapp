using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace ScheduleApp.Services.Data;

/// <summary>Bảng dữ liệu đọc từ file: dòng đầu là tiêu đề cột.</summary>
/// <param name="RowNumbers">Số dòng thật trong file của từng dòng dữ liệu (như số dòng Excel hiển thị, dòng tiêu đề thường là 1).</param>
public sealed record DataTableResult(List<string> Headers, List<string[]> Rows, List<int> RowNumbers)
{
    /// <summary>Số dòng thật của dòng tiêu đề.</summary>
    public int HeaderRowNumber { get; init; } = 1;
}

/// <summary>
/// Đọc file CSV / Excel (.xlsx) thành bảng — không cần cài Excel hay thư viện ngoài.
/// File đang mở trong Excel vẫn đọc được.
/// </summary>
public static class TabularReader
{
    public static DataTableResult Read(string path, string? sheet = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Không tìm thấy file \"{path}\".");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var raw = ext switch
        {
            ".xlsx" or ".xlsm" => ReadXlsx(path, sheet),
            ".csv" or ".txt" or ".tsv" => ReadCsv(path),
            ".xls" => throw new NotSupportedException("File .xls (Excel 97-2003) chưa được hỗ trợ — hãy lưu lại dạng .xlsx hoặc .csv."),
            _ => throw new NotSupportedException($"Không đọc được định dạng \"{ext}\" — dùng .xlsx hoặc .csv.")
        };

        // Bỏ dòng trống hoàn toàn (giữ số dòng thật để ghi ngược lại đúng chỗ).
        var numbers = raw.Select((r, i) => (Row: r, Number: i + 1)).Where(x => x.Row.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        raw = numbers.Select(x => x.Row).ToList();
        if (raw.Count == 0) return new DataTableResult([], [], []);

        int width = raw.Max(r => r.Length);
        var headers = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < width; c++)
        {
            var h = c < raw[0].Length ? raw[0][c].Trim() : "";
            if (h.Length == 0) h = "Cot" + (c + 1);
            var unique = h;
            for (int k = 2; !used.Add(unique); k++) unique = $"{h}_{k}";
            headers.Add(unique);
        }
        var rows = raw.Skip(1).Select(r => r.Length == width ? r : [.. r, .. Enumerable.Repeat("", width - r.Length)]).ToList();
        return new DataTableResult(headers, rows, numbers.Skip(1).Select(x => x.Number).ToList()) { HeaderRowNumber = numbers[0].Number };
    }

    /// <summary>Đọc toàn bộ ô của file CSV (không bỏ dòng trống) và dấu phân cách — dùng khi ghi lại file.</summary>
    internal static (List<string[]> Rows, char Separator) ReadCsvRaw(string path)
    {
        var rows = ReadCsv(path, out char sep);
        return (rows, sep);
    }

    // ───────────────────────────── CSV ─────────────────────────────

    private static List<string[]> ReadCsv(string path) => ReadCsv(path, out _);

    private static List<string[]> ReadCsv(string path, out char sep)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();

        sep = DetectSeparator(text, Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase));
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }
            if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add([.. row]);
                row.Clear();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add([.. row]);
        }
        return rows;
    }

    /// <summary>Chọn dấu phân cách xuất hiện nhiều nhất ở dòng đầu (ngoài dấu nháy): , ; hoặc Tab.</summary>
    private static char DetectSeparator(string text, bool tsv)
    {
        if (tsv) return '\t';
        int comma = 0, semi = 0, tab = 0;
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted)
            {
                if (c is '\r' or '\n') break;
                if (c == ',') comma++;
                else if (c == ';') semi++;
                else if (c == '\t') tab++;
            }
        }
        return tab > comma && tab > semi ? '\t' : semi > comma ? ';' : ',';
    }

    // ───────────────────────────── XLSX ─────────────────────────────

    internal static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    internal static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Tên các sheet trong file Excel (để chọn trong trình soạn).</summary>
    public static List<string> SheetNames(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        return Load(zip, "xl/workbook.xml")?.Descendants(Main + "sheet").Select(s => (string?)s.Attribute("name") ?? "").ToList() ?? [];
    }

    private static List<string[]> ReadXlsx(string path, string? sheetName)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var sheetPath = SheetPath(zip, sheetName);
        var sheetXml = Load(zip, sheetPath) ?? throw new InvalidDataException($"Thiếu {sheetPath} trong file Excel.");

        var shared = Load(zip, "xl/sharedStrings.xml")?.Root?.Elements(Main + "si").Select(TextOf).ToList() ?? [];
        var dateStyles = DateStyleIndexes(Load(zip, "xl/styles.xml"));

        var rows = new List<string[]>();
        foreach (var rowEl in sheetXml.Descendants(Main + "row"))
        {
            int rowNumber = (int?)rowEl.Attribute("r") ?? rows.Count + 1;
            while (rows.Count < rowNumber - 1) rows.Add([]); // giữ đúng vị trí khi có dòng bị bỏ trống
            var cells = new List<string>();
            foreach (var c in rowEl.Elements(Main + "c"))
            {
                int col = ColumnIndex((string?)c.Attribute("r")) ?? cells.Count;
                while (cells.Count < col) cells.Add("");
                cells.Add(CellValue(c, shared, dateStyles));
            }
            rows.Add([.. cells]);
        }
        return rows;
    }

    /// <summary>Đường dẫn trong file zip của sheet (trống = sheet đầu tiên).</summary>
    internal static string SheetPath(ZipArchive zip, string? sheetName)
    {
        var workbook = Load(zip, "xl/workbook.xml") ?? throw new InvalidDataException("File Excel không hợp lệ (thiếu workbook.xml).");
        var sheets = workbook.Descendants(Main + "sheet").ToList();
        if (sheets.Count == 0) throw new InvalidDataException("File Excel không có sheet nào.");

        var sheet = string.IsNullOrWhiteSpace(sheetName)
            ? sheets[0]
            : sheets.FirstOrDefault(s => string.Equals((string?)s.Attribute("name"), sheetName.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException(
                  $"Không có sheet \"{sheetName}\". Các sheet: {string.Join(", ", sheets.Select(s => (string?)s.Attribute("name")))}.");

        var relId = (string?)sheet.Attribute(Rel + "id");
        var rels = Load(zip, "xl/_rels/workbook.xml.rels");
        var target = rels?.Descendants(PkgRel + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == relId)?.Attribute("Target")?.Value
                     ?? throw new InvalidDataException("Không tìm thấy dữ liệu của sheet.");
        return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
    }

    internal static XDocument? Load(ZipArchive zip, string entryPath)
    {
        var entry = zip.GetEntry(entryPath) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    /// <summary>Chữ của một ô chuỗi (bỏ phần phiên âm rPh).</summary>
    internal static string TextOf(XElement si) =>
        string.Concat(si.Descendants(Main + "t").Where(t => t.Parent?.Name != Main + "rPh").Select(t => t.Value));

    internal static string CellValue(XElement c, List<string> shared, HashSet<int> dateStyles)
    {
        var type = (string?)c.Attribute("t");
        var v = c.Element(Main + "v")?.Value;
        switch (type)
        {
            case "s":
                return int.TryParse(v, out int idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
            case "inlineStr":
                return c.Element(Main + "is") is { } inl ? TextOf(inl) : "";
            case "str":
            case "e":
                return v ?? "";
            case "b":
                return v == "1" ? "TRUE" : "FALSE";
        }
        if (string.IsNullOrEmpty(v)) return "";
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return v;

        int style = (int?)c.Attribute("s") ?? 0;
        if (dateStyles.Contains(style) && number is > 0 and < 2_958_466)
        {
            var date = DateTime.FromOADate(number);
            return date.TimeOfDay == TimeSpan.Zero ? date.ToString("dd/MM/yyyy")
                 : number < 1 ? date.ToString("HH:mm:ss")
                 : date.ToString("dd/MM/yyyy HH:mm");
        }
        // Làm tròn sai số dấu phẩy động (0.1+0.2) nhưng giữ đủ chữ số.
        return Math.Round(number, 10).ToString("G15", CultureInfo.InvariantCulture);
    }

    /// <summary>Các chỉ số kiểu ô (cellXfs) có định dạng ngày/giờ.</summary>
    internal static HashSet<int> DateStyleIndexes(XDocument? styles)
    {
        var result = new HashSet<int>();
        if (styles?.Root == null) return result;
        var custom = styles.Root.Element(Main + "numFmts")?.Elements(Main + "numFmt")
            .ToDictionary(n => (int?)n.Attribute("numFmtId") ?? -1, n => (string?)n.Attribute("formatCode") ?? "") ?? [];
        var xfs = styles.Root.Element(Main + "cellXfs")?.Elements(Main + "xf").ToList() ?? [];
        for (int i = 0; i < xfs.Count; i++)
        {
            int id = (int?)xfs[i].Attribute("numFmtId") ?? 0;
            bool isDate = id is >= 14 and <= 22 or >= 45 and <= 47
                          || (custom.TryGetValue(id, out var code) && LooksLikeDate(code));
            if (isDate) result.Add(i);
        }
        return result;
    }

    private static bool LooksLikeDate(string code)
    {
        var sb = new StringBuilder();
        bool inQuote = false, inBracket = false;
        foreach (char ch in code)
        {
            if (ch == '"') inQuote = !inQuote;
            else if (ch == '[') inBracket = true;
            else if (ch == ']') inBracket = false;
            else if (!inQuote && !inBracket) sb.Append(char.ToLowerInvariant(ch));
        }
        var s = sb.ToString();
        return s.IndexOfAny(['d', 'y']) >= 0 || (s.Contains('m') && (s.Contains('h') || s.Contains('s')));
    }

    /// <summary>"C12" → 2 (cột tính từ 0).</summary>
    internal static int? ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        int col = 0, i = 0;
        for (; i < cellRef.Length && char.IsLetter(cellRef[i]); i++)
            col = col * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1);
        return i == 0 ? null : col - 1;
    }
}
