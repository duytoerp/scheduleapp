using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ScheduleApp.Models;

namespace ScheduleApp.Services.Data;

/// <summary>
/// Ghi dữ liệu vào file CSV / Excel (.xlsx) — không cần cài Excel. Thêm dòng mới hoặc sửa ô của một dòng có sẵn
/// (theo số dòng hoặc theo cột khóa), giữ nguyên định dạng, công thức và các sheet khác. Cột chưa có được thêm vào cuối dòng tiêu đề.
/// </summary>
public static partial class TabularWriter
{
    private static readonly XNamespace Main = TabularReader.Main;
    private static readonly XNamespace Rel = TabularReader.Rel;
    private static readonly XNamespace PkgRel = TabularReader.PkgRel;
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>Ghi các ô; trả về số dòng (như Excel hiển thị) đã ghi.</summary>
    /// <param name="rowRef">Sửa dòng: số dòng (vd 5) hoặc "Cột=giá trị" (dòng đầu tiên có cột đó bằng giá trị).</param>
    /// <param name="values">(tên cột, giá trị) theo thứ tự.</param>
    public static int Write(string path, string? sheet, DataAction action, string rowRef, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        if (values.Count == 0) throw new InvalidOperationException("Chưa nhập ô nào cần ghi (mỗi dòng dạng Cột=giá trị).");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".xlsx" or ".xlsm" => WriteXlsx(path, sheet, action, rowRef, values),
                ".csv" or ".txt" or ".tsv" => WriteCsv(path, action, rowRef, values),
                ".xls" => throw new NotSupportedException("File .xls (Excel 97-2003) chưa được hỗ trợ — hãy lưu lại dạng .xlsx hoặc .csv."),
                _ => throw new NotSupportedException($"Không ghi được định dạng \"{ext}\" — dùng .xlsx hoặc .csv.")
            };
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException && IsLocked(ex))
        {
            throw new IOException($"File \"{Path.GetFileName(path)}\" đang được mở (thường là trong Excel — Excel khóa file khi mở). " +
                                  "Đóng file rồi chạy lại, hoặc bật \"Thử lại\" cho bước này.", ex);
        }
    }

    private static bool IsLocked(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33; // sharing / lock violation

    /// <summary>Tách nội dung "Cột=giá trị" (mỗi dòng một ô). Dòng trống và dòng bắt đầu bằng # được bỏ qua.</summary>
    public static List<KeyValuePair<string, string>> ParseAssignments(string text, Func<string, string> expand)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Dòng \"{line}\" thiếu dấu = (cần dạng TênCột=giá trị).");
            result.Add(new(expand(line[..eq].Trim()), expand(line[(eq + 1)..].Trim())));
        }
        return result;
    }

    // ───────────────────────────── CSV ─────────────────────────────

    private static int WriteCsv(string path, DataAction action, string rowRef, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        List<string[]> rows;
        char sep;
        bool bom = true;
        if (File.Exists(path))
        {
            (rows, sep) = TabularReader.ReadCsvRaw(path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[3];
            bom = fs.Read(head, 0, 3) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF || fs.Length == 0;
        }
        else
        {
            if (action == DataAction.UpdateRow) throw new FileNotFoundException($"Không tìm thấy file \"{path}\".");
            rows = [];
            sep = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        }

        var table = rows.Select(r => r.ToList()).ToList();
        int header = table.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (header < 0)
        {
            // File trống: tạo dòng tiêu đề từ các cột cần ghi.
            table.Clear();
            table.Add([]);
            header = 0;
        }
        var headers = table[header];
        // Cột mới đặt sau cột cuối cùng có dữ liệu (kể cả cột không có tiêu đề) để không ghi đè dữ liệu.
        int nextNew = table.Max(r => r.Count);
        int Column(string name)
        {
            int i = FindHeader(headers, name);
            if (i >= 0) return i;
            while (headers.Count < nextNew) headers.Add("");
            headers.Add(name);
            nextNew = headers.Count;
            return headers.Count - 1;
        }

        int target;
        if (action == DataAction.AppendRow)
        {
            // Dòng tiêu đề vừa tạo (file trống) chưa có chữ nhưng vẫn phải giữ.
            int last = Math.Max(header, table.FindLastIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c))));
            table.RemoveRange(last + 1, table.Count - last - 1); // bỏ dòng trống cuối file
            table.Add([]);
            target = table.Count - 1;
        }
        else
        {
            target = FindCsvRow(table, header, rowRef);
        }

        foreach (var (name, value) in values)
        {
            int col = Column(name);
            var row = table[target];
            while (row.Count <= col) row.Add("");
            row[col] = value;
        }

        var sb = new StringBuilder();
        foreach (var r in table) sb.Append(string.Join(sep, r.Select(c => CsvQuote(c, sep)))).Append("\r\n");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(bom));
        return target + 1;
    }

    private static int FindCsvRow(List<List<string>> table, int header, string rowRef)
    {
        var (number, keyColumn, keyValue) = ParseRowRef(rowRef);
        if (number is int n)
        {
            if (n - 1 <= header || n > table.Count) throw new InvalidOperationException($"Dòng {n} không có dữ liệu (file có {table.Count} dòng, tiêu đề ở dòng {header + 1}).");
            return n - 1;
        }
        int col = FindHeader(table[header], keyColumn!);
        if (col < 0) throw new InvalidOperationException($"Không có cột \"{keyColumn}\". Các cột: {string.Join(", ", table[header])}.");
        for (int i = header + 1; i < table.Count; i++)
            if (col < table[i].Count && SameValue(table[i][col], keyValue!)) return i;
        throw new InvalidOperationException($"Không có dòng nào có {keyColumn} = \"{keyValue}\".");
    }

    private static string CsvQuote(string value, char sep) =>
        value.IndexOfAny([sep, '"', '\r', '\n']) >= 0 || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ───────────────────────────── Dòng / cột ─────────────────────────────

    /// <summary>"12" → dòng 12; "MaKH=KH001" → tìm theo cột khóa.</summary>
    private static (int? Number, string? KeyColumn, string? KeyValue) ParseRowRef(string rowRef)
    {
        var r = rowRef.Trim();
        if (r.Length == 0) throw new InvalidOperationException("Chưa nhập dòng cần sửa (số dòng, vd {{row.rowNumber}}, hoặc Cột=giá trị).");
        if (int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            if (n < 1) throw new InvalidOperationException($"Số dòng \"{r}\" không hợp lệ.");
            return (n, null, null);
        }
        int eq = r.IndexOf('=');
        if (eq <= 0) throw new InvalidOperationException($"Dòng cần sửa \"{r}\" không hợp lệ — nhập số dòng hoặc Cột=giá trị.");
        return (null, r[..eq].Trim(), r[(eq + 1)..].Trim());
    }

    [GeneratedRegex(@"^Cot(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedHeader();

    /// <summary>Vị trí cột theo tiêu đề (không phân biệt hoa thường); "Cot5" = cột thứ 5 không có tiêu đề (như khi đọc).</summary>
    private static int FindHeader(IList<string> headers, string name)
    {
        name = name.Trim();
        for (int i = 0; i < headers.Count; i++)
            if (string.Equals(headers[i].Trim(), name, StringComparison.CurrentCultureIgnoreCase)) return i;
        var m = GeneratedHeader().Match(name);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && (n > headers.Count || headers[n - 1].Trim().Length == 0)) return n - 1;
        return -1;
    }

    private static bool SameValue(string cell, string value) =>
        string.Equals(cell.Trim(), value.Trim(), StringComparison.CurrentCultureIgnoreCase);

    // ───────────────────────────── XLSX ─────────────────────────────

    private static int WriteXlsx(string path, string? sheetName, DataAction action, string rowRef, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        if (!File.Exists(path))
        {
            if (action == DataAction.UpdateRow) throw new FileNotFoundException($"Không tìm thấy file \"{path}\".");
            CreateWorkbook(path, string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName.Trim());
        }

        // FileShare.None: không ghi đè khi Excel đang mở file (Excel giữ khóa) → báo lỗi rõ ràng thay vì làm hỏng file.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Update);

        var sheetPath = TabularReader.SheetPath(zip, sheetName);
        var doc = TabularReader.Load(zip, sheetPath) ?? throw new InvalidDataException($"Thiếu {sheetPath} trong file Excel.");
        var sheetData = doc.Root?.Element(Main + "sheetData") ?? throw new InvalidDataException("Sheet không có dữ liệu (sheetData).");
        var shared = TabularReader.Load(zip, "xl/sharedStrings.xml")?.Root?.Elements(Main + "si").Select(TabularReader.TextOf).ToList() ?? [];
        var dateStyles = TabularReader.DateStyleIndexes(TabularReader.Load(zip, "xl/styles.xml"));

        // Số dòng → phần tử <row>, chỉ lấy dòng có dữ liệu thật để tìm tiêu đề / dòng cuối.
        var rows = new SortedDictionary<int, XElement>();
        int implicitRow = 0;
        foreach (var r in sheetData.Elements(Main + "row"))
        {
            int n = (int?)r.Attribute("r") ?? implicitRow + 1;
            implicitRow = n;
            rows[n] = r;
        }
        string Text(XElement row, int col) =>
            row.Elements(Main + "c").FirstOrDefault(c => CellColumn(c) == col) is { } cell ? TabularReader.CellValue(cell, shared, dateStyles) : "";
        bool HasData(XElement row) => row.Elements(Main + "c").Any(c => TabularReader.CellValue(c, shared, dateStyles).Trim().Length > 0);

        int headerRow = rows.FirstOrDefault(kv => HasData(kv.Value)).Key;
        var headers = new List<string>();
        if (headerRow > 0)
        {
            int maxCol = rows[headerRow].Elements(Main + "c").Select(CellColumn).DefaultIfEmpty(-1).Max();
            for (int c = 0; c <= maxCol; c++) headers.Add(Text(rows[headerRow], c));
        }
        else
        {
            headerRow = 1;
        }

        int target;
        if (action == DataAction.AppendRow)
        {
            int last = rows.Where(kv => HasData(kv.Value)).Select(kv => kv.Key).DefaultIfEmpty(0).Max();
            // Sheet trống: dòng 1 là tiêu đề (tạo từ tên cột), dữ liệu bắt đầu ở dòng 2.
            target = headers.Count == 0 ? headerRow + 1 : last + 1;
        }
        else
        {
            var (number, keyColumn, keyValue) = ParseRowRef(rowRef);
            if (number is int n)
            {
                if (n <= headerRow) throw new InvalidOperationException($"Dòng {n} là dòng tiêu đề hoặc nằm trên tiêu đề (tiêu đề ở dòng {headerRow}).");
                target = n;
            }
            else
            {
                int col = FindHeader(headers, keyColumn!);
                if (col < 0) throw new InvalidOperationException($"Không có cột \"{keyColumn}\". Các cột: {string.Join(", ", headers.Where(h => h.Length > 0))}.");
                target = rows.Where(kv => kv.Key > headerRow && SameValue(Text(kv.Value, col), keyValue!)).Select(kv => kv.Key).FirstOrDefault();
                if (target == 0) throw new InvalidOperationException($"Không có dòng nào có {keyColumn} = \"{keyValue}\".");
            }
        }

        bool removedFormula = false;
        int maxWrittenCol = 0;
        // Cột mới đặt sau cột cuối cùng có dữ liệu (kể cả cột không có tiêu đề) để không ghi đè dữ liệu.
        int nextNew = Math.Max(headers.Count, rows.Values.SelectMany(r => r.Elements(Main + "c")).Select(CellColumn).DefaultIfEmpty(-1).Max() + 1);
        foreach (var (name, value) in values)
        {
            int col = FindHeader(headers, name);
            if (col < 0)
            {
                col = nextNew++;
                while (headers.Count < col) headers.Add("");
                headers.Add(name);
                SetCell(GetRow(sheetData, rows, headerRow), headerRow, col, name, ref removedFormula, forceText: true);
            }
            SetCell(GetRow(sheetData, rows, target), target, col, value, ref removedFormula, forceText: false);
            maxWrittenCol = Math.Max(maxWrittenCol, col);
        }

        UpdateDimension(doc, target, maxWrittenCol);
        if (action == DataAction.AppendRow) ExtendTables(zip, sheetPath, target);
        Save(zip, sheetPath, doc);
        if (removedFormula) RemoveCalcChain(zip);
        return target;
    }

    /// <summary>Cột (từ 0) của ô theo thuộc tính r="C5".</summary>
    private static int CellColumn(XElement c) => TabularReader.ColumnIndex((string?)c.Attribute("r")) ?? -1;

    private static XElement GetRow(XElement sheetData, SortedDictionary<int, XElement> rows, int number)
    {
        if (rows.TryGetValue(number, out var row)) return row;
        row = new XElement(Main + "row", new XAttribute("r", number));
        // Chèn đúng thứ tự dòng.
        var after = rows.Where(kv => kv.Key < number).Select(kv => kv.Value).LastOrDefault();
        if (after != null) after.AddAfterSelf(row);
        else sheetData.AddFirst(row);
        rows[number] = row;
        return row;
    }

    [GeneratedRegex(@"^-?(0|[1-9]\d{0,14})(\.\d{1,15})?$")]
    private static partial Regex PlainNumber();

    private static void SetCell(XElement row, int rowNumber, int col, string value, ref bool removedFormula, bool forceText)
    {
        row.Attribute("spans")?.Remove(); // gợi ý tối ưu của Excel — bỏ để không bị sai sau khi thêm ô
        var reference = ColumnName(col) + rowNumber.ToString(CultureInfo.InvariantCulture);
        var cell = row.Elements(Main + "c").FirstOrDefault(c => CellColumn(c) == col);
        if (cell == null)
        {
            cell = new XElement(Main + "c", new XAttribute("r", reference));
            var next = row.Elements(Main + "c").FirstOrDefault(c => CellColumn(c) > col);
            if (next != null) next.AddBeforeSelf(cell);
            else row.Add(cell);
        }
        if (cell.Element(Main + "f") != null) removedFormula = true;
        cell.Elements().Remove();       // giữ thuộc tính s (định dạng ô)
        cell.Attribute("t")?.Remove();

        // Số "thuần" (không có số 0 ở đầu như mã 001) ghi dạng số để Excel tính toán được; còn lại ghi chữ.
        if (!forceText && PlainNumber().IsMatch(value))
        {
            cell.Add(new XElement(Main + "v", value));
        }
        else if (value.Length > 0)
        {
            cell.SetAttributeValue("t", "inlineStr");
            var t = new XElement(Main + "t", value.Replace("\r\n", "\n"));
            if (value.Length != value.Trim().Length || value.Contains('\n')) t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            cell.Add(new XElement(Main + "is", t));
        }
    }

    /// <summary>0 → A, 26 → AA.</summary>
    public static string ColumnName(int index)
    {
        var sb = new StringBuilder();
        for (int n = index + 1; n > 0; n = (n - 1) / 26) sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }

    private static void UpdateDimension(XDocument doc, int row, int col)
    {
        var dim = doc.Root?.Element(Main + "dimension");
        if (dim == null) return;
        var parts = ((string?)dim.Attribute("ref") ?? "A1").Split(':');
        var (r1, c1) = ParseRef(parts[0]);
        var (r2, c2) = parts.Length > 1 ? ParseRef(parts[1]) : (r1, c1);
        dim.SetAttributeValue("ref", $"{ColumnName(Math.Min(c1, col))}{Math.Min(r1, row)}:{ColumnName(Math.Max(c2, col))}{Math.Max(r2, row)}");
    }

    private static (int Row, int Col) ParseRef(string cellRef)
    {
        int col = TabularReader.ColumnIndex(cellRef) ?? 0;
        var digits = new string(cellRef.SkipWhile(char.IsLetter).ToArray());
        return (int.TryParse(digits, out int r) ? r : 1, col);
    }

    /// <summary>Bảng (Format as Table) kết thúc ngay trên dòng vừa thêm → nới bảng xuống để dòng mới thuộc bảng.</summary>
    private static void ExtendTables(ZipArchive zip, string sheetPath, int newRow)
    {
        var relsPath = Path.GetDirectoryName(sheetPath)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(sheetPath) + ".rels";
        var rels = TabularReader.Load(zip, relsPath);
        if (rels == null) return;
        foreach (var rel in rels.Descendants(PkgRel + "Relationship").Where(r => ((string?)r.Attribute("Type") ?? "").EndsWith("/table")))
        {
            var target = (string?)rel.Attribute("Target") ?? "";
            var tablePath = target.StartsWith('/') ? target.TrimStart('/') : NormalizePath(Path.GetDirectoryName(sheetPath)!.Replace('\\', '/') + "/" + target);
            var table = TabularReader.Load(zip, tablePath);
            if (table?.Root == null) continue;
            var parts = ((string?)table.Root.Attribute("ref") ?? "").Split(':');
            if (parts.Length != 2) continue;
            var (r1, c1) = ParseRef(parts[0]);
            var (r2, c2) = ParseRef(parts[1]);
            if (r2 != newRow - 1) continue; // cột mới nằm ngoài bảng vẫn được ghi, chỉ không thuộc bảng
            var newRef = $"{ColumnName(c1)}{r1}:{ColumnName(c2)}{newRow}";
            table.Root.SetAttributeValue("ref", newRef);
            table.Root.Element(Main + "autoFilter")?.SetAttributeValue("ref", newRef);
            Save(zip, tablePath, table);
        }
    }

    private static string NormalizePath(string path)
    {
        var stack = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else if (part.Length > 0 && part != ".") stack.Add(part);
        }
        return string.Join("/", stack);
    }

    /// <summary>Đã ghi đè ô có công thức → xóa calcChain để Excel tự dựng lại (tránh báo file lỗi).</summary>
    private static void RemoveCalcChain(ZipArchive zip)
    {
        zip.GetEntry("xl/calcChain.xml")?.Delete();
        var rels = TabularReader.Load(zip, "xl/_rels/workbook.xml.rels");
        if (rels != null)
        {
            rels.Descendants(PkgRel + "Relationship").Where(r => ((string?)r.Attribute("Target") ?? "").EndsWith("calcChain.xml")).Remove();
            Save(zip, "xl/_rels/workbook.xml.rels", rels);
        }
        var types = TabularReader.Load(zip, "[Content_Types].xml");
        if (types != null)
        {
            types.Descendants(ContentTypes + "Override").Where(o => ((string?)o.Attribute("PartName") ?? "").EndsWith("calcChain.xml")).Remove();
            Save(zip, "[Content_Types].xml", types);
        }
    }

    private static void Save(ZipArchive zip, string entryPath, XDocument doc)
    {
        var name = zip.GetEntry(entryPath)?.FullName
                   ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(entryPath, StringComparison.OrdinalIgnoreCase))?.FullName
                   ?? entryPath;
        zip.GetEntry(name)?.Delete();
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        doc.Save(writer, SaveOptions.DisableFormatting);
    }

    /// <summary>Tạo file .xlsx tối thiểu với một sheet trống.</summary>
    private static void CreateWorkbook(string path, string sheetName)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, XDocument doc)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            doc.Save(w, SaveOptions.DisableFormatting);
        }
        const string officeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string mainType = "application/vnd.openxmlformats-officedocument.spreadsheetml";
        Add("[Content_Types].xml", new XDocument(new XElement(ContentTypes + "Types",
            new XElement(ContentTypes + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ContentTypes + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(ContentTypes + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", mainType + ".sheet.main+xml")),
            new XElement(ContentTypes + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"), new XAttribute("ContentType", mainType + ".worksheet+xml")))));
        Add("_rels/.rels", new XDocument(new XElement(PkgRel + "Relationships",
            new XElement(PkgRel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", officeRel + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml")))));
        Add("xl/workbook.xml", new XDocument(new XElement(Main + "workbook", new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
            new XElement(Main + "sheets", new XElement(Main + "sheet", new XAttribute("name", sheetName), new XAttribute("sheetId", 1), new XAttribute(Rel + "id", "rId1"))))));
        Add("xl/_rels/workbook.xml.rels", new XDocument(new XElement(PkgRel + "Relationships",
            new XElement(PkgRel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", officeRel + "/worksheet"), new XAttribute("Target", "worksheets/sheet1.xml")))));
        Add("xl/worksheets/sheet1.xml", new XDocument(new XElement(Main + "worksheet", new XElement(Main + "sheetData"))));
    }
}
