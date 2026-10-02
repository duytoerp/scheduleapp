using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScheduleApp.Services.Engine;

/// <summary>
/// Sinh dữ liệu ngẫu nhiên cho kiểm thử, viết như công thức Excel: giá trị biến bắt đầu bằng "=" (vd <c>=hoten()</c>, <c>=random(1, 100)</c>,
/// <c>=email(cty.vn)</c>) được tính lại ở mỗi lần chạy; dùng trực tiếp trong ô chữ bằng <c>{{=sdt()}}</c>.
/// Dữ liệu tiếng Việt: họ tên, số di động, địa chỉ, công ty…
/// </summary>
public static partial class TestData
{
    public sealed record Function(string Name, string Alias, string Syntax, string Description, string Example);

    /// <summary>Các hàm có sẵn (tên tiếng Việt + tên tiếng Anh tương đương).</summary>
    public static readonly IReadOnlyList<Function> Functions =
    [
        new("hoten", "name", "=hoten()  ·  =hoten(nữ)", "Họ tên người Việt (nam / nữ ngẫu nhiên, hoặc chỉ định)", "=hoten()"),
        new("ho", "lastname", "=ho()", "Họ (Nguyễn, Trần, Lê…)", "=ho()"),
        new("ten", "firstname", "=ten()  ·  =ten(nam)", "Tên (không có họ, tên đệm)", "=ten()"),
        new("email", "email", "=email()  ·  =email(cty.vn)", "Email theo tên không dấu, tên miền mặc định example.com", "=email()"),
        new("sdt", "phone", "=sdt()", "Số di động Việt Nam 10 số (đầu số thật: 03x, 05x, 07x, 08x, 09x)", "=sdt()"),
        new("diachi", "address", "=diachi()", "Địa chỉ: số nhà, đường, quận / huyện, tỉnh / thành", "=diachi()"),
        new("thanhpho", "city", "=thanhpho()", "Tỉnh / thành phố", "=thanhpho()"),
        new("congty", "company", "=congty()", "Tên công ty", "=congty()"),
        new("random", "so", "=random(1, 100)  ·  =random(1, 100, 2)", "Số ngẫu nhiên từ … đến … (tham số 3 = số chữ số thập phân)", "=random(1, 100)"),
        new("chuso", "digits", "=chuso(6)", "Chuỗi N chữ số (có thể bắt đầu bằng 0) — mã, số hợp đồng…", "=chuso(6)"),
        new("chuoi", "text", "=chuoi(8)", "Chuỗi N ký tự chữ hoa + số", "=chuoi(8)"),
        new("chon", "pick", "=chon(Hà Nội; Đà Nẵng; TP. HCM)", "Chọn ngẫu nhiên một giá trị trong danh sách (cách nhau bằng ;)", "=chon(Mới; Đang xử lý; Đã đóng)"),
        new("ngay", "date", "=ngay(-30, 0)  ·  =ngay(1, 90, yyyy-MM-dd)", "Ngày trong khoảng (số ngày so với hôm nay), định dạng tùy chọn", "=ngay(-30, 0)"),
        new("cccd", "idcard", "=cccd()", "Số căn cước 12 số", "=cccd()"),
        new("guid", "uuid", "=guid()", "Mã GUID", "=guid()")
    ];

    [GeneratedRegex(@"^=\s*([\p{L}_][\p{L}\p{N}_]*)\s*(?:\((.*)\))?\s*$", RegexOptions.Singleline)]
    private static partial Regex FormulaPattern();

    /// <summary>Giá trị là công thức sinh dữ liệu (bắt đầu bằng "=" và là một hàm có sẵn).</summary>
    public static bool IsFormula(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var m = FormulaPattern().Match(value.Trim());
        return m.Success && Find(m.Groups[1].Value) != null;
    }

    /// <summary>Xem thử cho giao diện: (true, giá trị mẫu) · (false, lỗi) · null nếu không phải công thức.</summary>
    public static (bool Ok, string Text)? Preview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !FormulaPattern().IsMatch(value.Trim())) return null;
        try { return (true, Evaluate(value)); }
        catch (FormatException ex) { return (false, ex.Message); }
    }

    private static Function? Find(string name) => Functions.FirstOrDefault(f =>
        f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || f.Alias.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        RemoveDiacritics(f.Name).Equals(RemoveDiacritics(name), StringComparison.OrdinalIgnoreCase));

    /// <summary>Tính công thức, vd "=hoten()" → "Nguyễn Thị Lan". Sai cú pháp / hàm lạ → FormatException.</summary>
    public static string Evaluate(string formula, Random? random = null)
    {
        var rnd = random ?? Random.Shared;
        var m = FormulaPattern().Match(formula.Trim());
        if (!m.Success) throw new FormatException($"\"{formula}\" không phải công thức — viết dạng =hoten(), =random(1, 100)…");
        var fn = Find(m.Groups[1].Value)
                 ?? throw new FormatException($"Không có hàm \"{m.Groups[1].Value}\". Các hàm: {string.Join(", ", Functions.Select(f => f.Name))}.");
        var args = SplitArgs(m.Groups[2].Value);

        return fn.Name switch
        {
            "hoten" => FullName(rnd, Gender(args, rnd)),
            "ho" => Pick(rnd, Surnames),
            "ten" => Pick(rnd, Gender(args, rnd) ? MaleNames : FemaleNames),
            "email" => Email(rnd, args.Count > 0 && args[0].Length > 0 ? args[0].TrimStart('@') : "example.com"),
            "sdt" => Pick(rnd, PhonePrefixes) + Digits(rnd, 7),
            "diachi" => Address(rnd),
            "thanhpho" => Pick(rnd, Cities).Name,
            "congty" => $"Công ty {Pick(rnd, CompanyTypes)} {Pick(rnd, CompanyNames)}{Pick(rnd, CompanyFields)}",
            "random" => Number(rnd, args),
            "chuso" => Digits(rnd, Count(args, 6)),
            "chuoi" => new string(Enumerable.Range(0, Count(args, 8)).Select(_ => Alphabet[rnd.Next(Alphabet.Length)]).ToArray()),
            "chon" => args.Count > 0 ? Pick(rnd, args) : throw new FormatException("=chon(...) cần ít nhất một giá trị, vd =chon(A; B; C)."),
            "ngay" => Date(rnd, args),
            "cccd" => "0" + rnd.Next(1, 97).ToString("00") + rnd.Next(0, 4) + Digits(rnd, 8),
            "guid" => Guid.NewGuid().ToString(),
            _ => throw new FormatException($"Không có hàm \"{fn.Name}\".")
        };
    }

    /// <summary>Tham số cách nhau bằng ";" (hoặc "," khi không có ";").</summary>
    private static List<string> SplitArgs(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var sep = text.Contains(';') ? ';' : ',';
        return [.. text.Split(sep).Select(a => a.Trim())];
    }

    private static int Count(List<string> args, int fallback)
    {
        if (args.Count == 0 || args[0].Length == 0) return fallback;
        return int.TryParse(args[0], out var n) && n is > 0 and <= 1000 ? n : throw new FormatException($"Độ dài \"{args[0]}\" phải là số từ 1 tới 1000.");
    }

    /// <summary>true = nam. "nam"/"male"/"m" hoặc "nữ"/"nu"/"female"/"f"; không chỉ định = ngẫu nhiên.</summary>
    private static bool Gender(List<string> args, Random rnd)
    {
        var g = args.Count > 0 ? RemoveDiacritics(args[0]).ToLowerInvariant() : "";
        return g switch
        {
            "nam" or "male" or "m" => true,
            "nu" or "female" or "f" => false,
            "" => rnd.Next(2) == 0,
            _ => throw new FormatException($"Giới tính \"{args[0]}\" không hợp lệ — dùng nam hoặc nữ.")
        };
    }

    private static double ParseNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException($"\"{s}\" không phải số.");

    private static string Number(Random rnd, List<string> args)
    {
        double lo = args.Count > 0 && args[0].Length > 0 ? ParseNumber(args[0]) : 0;
        double hi = args.Count > 1 ? ParseNumber(args[1]) : 100;
        if (hi < lo) (lo, hi) = (hi, lo);
        int decimals = args.Count > 2 ? (int)ParseNumber(args[2]) : 0;
        if (decimals <= 0) return ((long)Math.Ceiling(lo) + (long)Math.Floor(rnd.NextDouble() * (Math.Floor(hi) - Math.Ceiling(lo) + 1))).ToString(CultureInfo.InvariantCulture);
        decimals = Math.Min(decimals, 6);
        var value = Math.Round(lo + rnd.NextDouble() * (hi - lo), decimals);
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    private static string Date(Random rnd, List<string> args)
    {
        int from = args.Count > 0 && args[0].Length > 0 ? (int)ParseNumber(args[0]) : -365;
        int to = args.Count > 1 && args[1].Length > 0 ? (int)ParseNumber(args[1]) : 0;
        if (to < from) (from, to) = (to, from);
        var format = args.Count > 2 && args[2].Length > 0 ? args[2] : "dd/MM/yyyy";
        return DateTime.Today.AddDays(rnd.Next(from, to + 1)).ToString(format, CultureInfo.InvariantCulture);
    }

    private static string Digits(Random rnd, int n)
    {
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append((char)('0' + rnd.Next(10)));
        return sb.ToString();
    }

    private static T Pick<T>(Random rnd, IReadOnlyList<T> items) => items[rnd.Next(items.Count)];

    private static string FullName(Random rnd, bool male) =>
        $"{Pick(rnd, Surnames)} {Pick(rnd, male ? MaleMiddle : FemaleMiddle)} {Pick(rnd, male ? MaleNames : FemaleNames)}";

    private static string Email(Random rnd, string domain)
    {
        bool male = rnd.Next(2) == 0;
        var first = RemoveDiacritics(Pick(rnd, male ? MaleNames : FemaleNames)).ToLowerInvariant();
        var last = RemoveDiacritics(Pick(rnd, Surnames)).ToLowerInvariant();
        return $"{first}.{last}{rnd.Next(10, 1000)}@{domain}";
    }

    private static string Address(Random rnd)
    {
        var city = Pick(rnd, Cities);
        return $"{rnd.Next(1, 300)} {Pick(rnd, Streets)}, {Pick(rnd, city.Districts)}, {city.Name}";
    }

    public static string RemoveDiacritics(string s)
    {
        var normalized = s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static readonly string[] Surnames =
        ["Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan", "Vũ", "Võ", "Đặng", "Bùi", "Đỗ", "Hồ", "Ngô", "Dương", "Lý", "Trương", "Đinh"];
    private static readonly string[] MaleMiddle = ["Văn", "Hữu", "Đức", "Minh", "Quốc", "Thành", "Công", "Gia", "Hoàng", "Anh"];
    private static readonly string[] FemaleMiddle = ["Thị", "Ngọc", "Thu", "Thanh", "Mai", "Kim", "Phương", "Bảo", "Minh", "Hoài"];
    private static readonly string[] MaleNames =
        ["An", "Bình", "Cường", "Dũng", "Đạt", "Hải", "Hiếu", "Hùng", "Huy", "Khoa", "Khánh", "Long", "Minh", "Nam", "Phúc", "Quang", "Sơn", "Tài", "Thắng", "Trung", "Tuấn", "Việt"];
    private static readonly string[] FemaleNames =
        ["Anh", "Chi", "Dung", "Giang", "Hà", "Hạnh", "Hoa", "Hương", "Lan", "Linh", "Loan", "Mai", "My", "Ngân", "Nhung", "Phương", "Quỳnh", "Thảo", "Trang", "Uyên", "Vy", "Yến"];
    private static readonly string[] PhonePrefixes =
        ["032", "033", "034", "035", "036", "037", "038", "039", "056", "058", "070", "076", "077", "078", "079",
         "081", "082", "083", "084", "085", "086", "088", "089", "090", "091", "093", "094", "096", "097", "098"];
    private static readonly string[] Streets =
        ["Lê Lợi", "Nguyễn Huệ", "Trần Hưng Đạo", "Hai Bà Trưng", "Lý Thường Kiệt", "Điện Biên Phủ", "Cách Mạng Tháng Tám", "Võ Văn Tần",
         "Nguyễn Trãi", "Lê Duẩn", "Phan Đình Phùng", "Nguyễn Văn Cừ", "Hoàng Diệu", "Pasteur"];
    private static readonly (string Name, string[] Districts)[] Cities =
    [
        ("TP. Hồ Chí Minh", ["Quận 1", "Quận 3", "Quận 7", "Quận 10", "Bình Thạnh", "Phú Nhuận", "Tân Bình", "TP. Thủ Đức"]),
        ("Hà Nội", ["Ba Đình", "Hoàn Kiếm", "Cầu Giấy", "Đống Đa", "Hai Bà Trưng", "Thanh Xuân", "Tây Hồ"]),
        ("Đà Nẵng", ["Hải Châu", "Sơn Trà", "Thanh Khê", "Ngũ Hành Sơn"]),
        ("Hải Phòng", ["Lê Chân", "Ngô Quyền", "Hồng Bàng"]),
        ("Cần Thơ", ["Ninh Kiều", "Bình Thủy", "Cái Răng"]),
        ("Bình Dương", ["Thủ Dầu Một", "Dĩ An", "Thuận An"]),
        ("Đồng Nai", ["Biên Hòa", "Long Thành"])
    ];
    private static readonly string[] CompanyTypes = ["TNHH", "Cổ phần", "TNHH MTV"];
    private static readonly string[] CompanyNames =
        ["An Phát", "Minh Long", "Thành Đạt", "Hưng Thịnh", "Việt Tiến", "Sao Mai", "Phú Gia", "Đại Dương", "Hoàng Gia", "Bình Minh", "Tân Phú", "Kim Long"];
    private static readonly string[] CompanyFields = ["", " Thương mại", " Dịch vụ", " Công nghệ", " Xây dựng", " Logistics"];
}
