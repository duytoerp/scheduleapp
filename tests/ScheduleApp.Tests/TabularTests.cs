using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ScheduleApp.Models;
using ScheduleApp.Services.Data;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

public class TabularReaderTests
{
    [Fact]
    public void Csv()
    {
        var csv = Path.Combine(NewDir(), "data.csv");
        File.WriteAllText(csv, "MaKH;Tên;Ghi chú\n001;\"Lê Thị B\";\"có ; dấu chấm phẩy\"\n\n002;Trần C;\"dòng\nhai\"\n", new UTF8Encoding(true));
        var t = TabularReader.Read(csv);
        Assert.Equal("MaKH|Tên|Ghi chú", string.Join("|", t.Headers));
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("có ; dấu chấm phẩy", t.Rows[0][2]);
        Assert.Equal("dòng\nhai", t.Rows[1][2]);
        Assert.Equal([2, 4], t.RowNumbers); // dòng trống thứ 3 bị bỏ nhưng vẫn giữ số dòng thật
    }

    [Fact]
    public void Xlsx()
    {
        var xlsx = Path.Combine(NewDir(), "data.xlsx");
        MakeXlsx(xlsx);
        var x = TabularReader.Read(xlsx);
        Assert.Equal("Mã|Họ tên|Ngày|Số tiền|Cot5", string.Join("|", x.Headers));
        Assert.Equal(2, x.Rows.Count);
        Assert.Equal("Phạm Văn D", x.Rows[0][1]);
        Assert.Equal("15/03/2026", x.Rows[0][2]);
        Assert.Equal("1500000.25", x.Rows[0][3]);
        Assert.Equal("", x.Rows[1][1]);
        Assert.Equal("ghi chú", x.Rows[1][4]);
        Assert.Equal([2, 4], x.RowNumbers);
        Assert.Equal("Danh sách|Khác", string.Join("|", TabularReader.SheetNames(xlsx)));
        Assert.Equal("Z", TabularReader.Read(xlsx, "khác").Headers[0]);
    }
}

public class TabularWriterTests
{
    private static List<KeyValuePair<string, string>> V(params (string K, string V)[] values) => values.Select(v => new KeyValuePair<string, string>(v.K, v.V)).ToList();

    [Fact]
    public void CsvAppendUpdateAndNewColumn()
    {
        var csv = Path.Combine(NewDir(), "kq.csv");
        File.WriteAllText(csv, "MaKH;Tên\n001;An\n002;Bình\n\n", new UTF8Encoding(true));

        int appended = TabularWriter.Write(csv, null, DataAction.AppendRow, "", V(("MaKH", "003"), ("Tên", "Chi; \"Chí\"")));
        Assert.Equal(4, appended);
        int byKey = TabularWriter.Write(csv, null, DataAction.UpdateRow, "MaKH=002", V(("TrangThai", "Đã nhập")));
        Assert.Equal(3, byKey);
        int byNumber = TabularWriter.Write(csv, null, DataAction.UpdateRow, "2", V(("TrangThai", "Lỗi\ndòng 2")));
        Assert.Equal(2, byNumber);

        var t = TabularReader.Read(csv);
        Assert.Equal("MaKH|Tên|TrangThai", string.Join("|", t.Headers));
        Assert.Equal(3, t.Rows.Count);
        Assert.Equal("Lỗi\ndòng 2", t.Rows[0][2]);
        Assert.Equal("Đã nhập", t.Rows[1][2]);
        Assert.Equal("Chi; \"Chí\"", t.Rows[2][1]);
        Assert.StartsWith("﻿", File.ReadAllText(csv, new UTF8Encoding(false))); // giữ BOM để Excel đọc đúng tiếng Việt
    }

    [Fact]
    public void CsvCreatesNewFile()
    {
        var csv = Path.Combine(NewDir(), "sub", "moi.csv");
        TabularWriter.Write(csv, null, DataAction.AppendRow, "", V(("Ngày", "01/10/2026"), ("Số", "5")));
        TabularWriter.Write(csv, null, DataAction.AppendRow, "", V(("Số", "7")));
        var t = TabularReader.Read(csv);
        Assert.Equal("Ngày|Số", string.Join("|", t.Headers));
        Assert.Equal(["01/10/2026", "5"], t.Rows[0]);
        Assert.Equal(["", "7"], t.Rows[1]);
    }

    [Fact]
    public void UpdateMissingKeyFails()
    {
        var csv = Path.Combine(NewDir(), "a.csv");
        File.WriteAllText(csv, "Ma,Ten\n1,A\n");
        var ex = Assert.Throws<InvalidOperationException>(() => TabularWriter.Write(csv, null, DataAction.UpdateRow, "Ma=9", V(("Ten", "B"))));
        Assert.Contains("Ma = \"9\"", ex.Message);
        Assert.Throws<InvalidOperationException>(() => TabularWriter.Write(csv, null, DataAction.UpdateRow, "1", V(("Ten", "B")))); // dòng tiêu đề
    }

    [Fact]
    public void XlsxAppendUpdateKeepsOtherContent()
    {
        var xlsx = Path.Combine(NewDir(), "data.xlsx");
        MakeXlsx(xlsx);

        int appended = TabularWriter.Write(xlsx, null, DataAction.AppendRow, "", V(("Mã", "3"), ("Họ tên", "Võ Thị Ê"), ("TrangThai", "Mới"), ("Mã đơn", "007")));
        Assert.Equal(5, appended);
        int updated = TabularWriter.Write(xlsx, "Danh sách", DataAction.UpdateRow, "Họ tên=phạm văn d", V(("TrangThai", "Đã nhập"), ("Số tiền", "99")));
        Assert.Equal(2, updated);
        TabularWriter.Write(xlsx, null, DataAction.UpdateRow, "4", V(("Số tiền", "12.5"))); // ghi đè ô có công thức

        var x = TabularReader.Read(xlsx);
        Assert.Equal("Mã|Họ tên|Ngày|Số tiền|Cot5|TrangThai|Mã đơn", string.Join("|", x.Headers));
        Assert.Equal(3, x.Rows.Count);
        Assert.Equal("Phạm Văn D", x.Rows[0][1]);
        Assert.Equal("15/03/2026", x.Rows[0][2]);   // định dạng ngày (style) giữ nguyên
        Assert.Equal("99", x.Rows[0][3]);
        Assert.Equal("Đã nhập", x.Rows[0][5]);
        Assert.Equal("12.5", x.Rows[1][3]);
        Assert.Equal("ghi chú", x.Rows[1][4]);
        Assert.Equal("Võ Thị Ê", x.Rows[2][1]);
        Assert.Equal("007", x.Rows[2][6]);         // số có 0 đầu giữ dạng chữ
        Assert.Equal("Z", TabularReader.Read(xlsx, "Khác").Headers[0]);

        using var zip = ZipFile.OpenRead(xlsx);
        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal("A1:G5", (string?)sheet.Root!.Element(ns + "dimension")!.Attribute("ref"));
        // Ô số được ghi dạng số, không phải chữ
        var d2 = sheet.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "D2");
        Assert.Null(d2.Attribute("t"));
        Assert.Empty(sheet.Descendants(ns + "f")); // công thức ở D4 đã bị thay bằng giá trị
        // Thứ tự ô trong dòng phải tăng dần (Excel báo lỗi file nếu sai)
        foreach (var row in sheet.Descendants(ns + "row"))
        {
            var cols = row.Elements(ns + "c").Select(c => (string)c.Attribute("r")!).Select(r => TabularReader.ColumnIndex(r)!.Value).ToList();
            Assert.Equal(cols.Order().ToList(), cols);
        }
        Assert.Equal([1, 2, 4, 5], sheet.Descendants(ns + "row").Select(r => (int)r.Attribute("r")!).ToList());
    }

    [Fact]
    public void XlsxCreatesNewWorkbook()
    {
        var xlsx = Path.Combine(NewDir(), "moi.xlsx");
        TabularWriter.Write(xlsx, "Kết quả", DataAction.AppendRow, "", V(("Mã", "A1"), ("Tiền", "1500")));
        TabularWriter.Write(xlsx, "Kết quả", DataAction.AppendRow, "", V(("Mã", "A2"), ("Tiền", "-2.75")));
        Assert.Equal(["Kết quả"], TabularReader.SheetNames(xlsx));
        var x = TabularReader.Read(xlsx);
        Assert.Equal("Mã|Tiền", string.Join("|", x.Headers));
        Assert.Equal(["A1", "1500"], x.Rows[0]);
        Assert.Equal(["A2", "-2.75"], x.Rows[1]);
    }

    [Fact]
    public void XlsxLockedFileGivesClearError()
    {
        var xlsx = Path.Combine(NewDir(), "khoa.xlsx");
        MakeXlsx(xlsx);
        using var excelLike = new FileStream(xlsx, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var ex = Assert.Throws<IOException>(() => TabularWriter.Write(xlsx, null, DataAction.AppendRow, "", V(("Mã", "9"))));
        Assert.Contains("đang được mở", ex.Message);
    }

    [Fact]
    public void ParseAssignments()
    {
        var pairs = TabularWriter.ParseAssignments("TrangThai = Xong\n# ghi chú\n\nCông thức=a=b", s => s.ToUpperInvariant());
        Assert.Equal([new("TRANGTHAI", "XONG"), new("CÔNG THỨC", "A=B")], pairs);
        Assert.Throws<FormatException>(() => TabularWriter.ParseAssignments("thiếu dấu bằng", s => s));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void ColumnNames(int index, string name) => Assert.Equal(name, TabularWriter.ColumnName(index));
}
