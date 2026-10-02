using System.Globalization;
using System.Text.RegularExpressions;
using ScheduleApp.Models;
using ScheduleApp.Services.Engine;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

public class TestDataTests
{
    private static string E(string formula, int seed) => TestData.Evaluate(formula, new Random(seed));

    [Fact]
    public void EveryFunctionAndExampleWorks()
    {
        foreach (var f in TestData.Functions)
        {
            Assert.True(TestData.IsFormula(f.Example), f.Example);
            Assert.False(string.IsNullOrWhiteSpace(TestData.Evaluate(f.Example)), f.Example);
            Assert.True(TestData.IsFormula($"={f.Alias}()") || f.Name is "chon", f.Alias);
        }
        Assert.Equal(E("=hoten()", 7), E("=hoten()", 7));               // cùng seed → cùng kết quả
    }

    [Fact]
    public void NumbersStayInRange()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 600; i++)
        {
            var v = int.Parse(E("=random(1, 6)", i));
            Assert.InRange(v, 1, 6);
            seen.Add(v.ToString());
        }
        Assert.Equal(6, seen.Count);                                    // gồm cả hai đầu mút
        Assert.Equal("5", TestData.Evaluate("=random(5, 5)"));
        Assert.InRange(int.Parse(E("=random(10, 1)", 3)), 1, 10);       // đảo khoảng
        var dec = E("=random(1; 2; 2)", 5);
        Assert.Matches(@"^[12]\.\d{2}$", dec);
        Assert.InRange(double.Parse(dec, CultureInfo.InvariantCulture), 1, 2);
        Assert.Matches(@"^\d{6}$", E("=chuso(6)", 1));
        Assert.Matches(@"^[A-Z0-9]{8}$", E("=chuoi(8)", 1));
        Assert.Matches(@"^0\d{11}$", E("=cccd()", 1));
        Assert.True(Guid.TryParse(TestData.Evaluate("=guid()"), out _));
    }

    [Fact]
    public void VietnamesePeopleAndContacts()
    {
        for (int i = 0; i < 200; i++)
        {
            Assert.Matches(@"^0(3[2-9]|5[68]|7[06-9]|8[1-689]|9[01346-8])\d{7}$", E("=sdt()", i));
            Assert.Matches(@"^[a-z]+\.[a-z]+\d{2,3}@example\.com$", E("=email()", i));
            Assert.Equal(3, E("=hoten()", i).Split(' ').Length);
            Assert.DoesNotContain(" Thị ", " " + E("=hoten(nam)", i) + " ");
        }
        Assert.EndsWith("@cty.vn", TestData.Evaluate("=email(@cty.vn)"));
        Assert.Contains(Enumerable.Range(0, 100).Select(i => E("=hoten(nữ)", i)), n => n.Contains(" Thị "));
        Assert.Matches(@"^\d+ .+, .+, .+$", E("=diachi()", 2));
        Assert.StartsWith("Công ty ", E("=congty()", 2));
    }

    [Fact]
    public void PickDatesAndFriendlySyntax()
    {
        string[] options = ["Mới", "Đang xử lý", "Đã đóng"];
        Assert.All(Enumerable.Range(0, 50), i => Assert.Contains(E("=chon(Mới; Đang xử lý; Đã đóng)", i), options));
        Assert.Contains(E("=chon(A, B)", 1), new[] { "A", "B" });       // không có ";" → tách theo ","
        Assert.Contains(E("=chon(Hà Nội, Việt Nam; Đà Nẵng)", 2), new[] { "Hà Nội, Việt Nam", "Đà Nẵng" });

        var d = DateTime.ParseExact(E("=ngay(-30, 0)", 4), "dd/MM/yyyy", CultureInfo.InvariantCulture);
        Assert.InRange(d, DateTime.Today.AddDays(-30), DateTime.Today);
        Assert.Equal(DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"), TestData.Evaluate("=ngay(1, 1, yyyy-MM-dd)"));

        // Tên hàm có dấu, hoa thường, tên tiếng Anh, có khoảng trắng.
        Assert.Equal(3, TestData.Evaluate("=họtên()").Split(' ').Length);
        Assert.Equal("X", TestData.Evaluate(" = Chọn( X ) "));
        Assert.True(TestData.IsFormula("=NAME()"));
        Assert.True(TestData.IsFormula("=sdt"));                        // không cần ()
    }

    [Fact]
    public void NonFormulasAndErrors()
    {
        Assert.False(TestData.IsFormula("Test 123"));
        Assert.False(TestData.IsFormula("=SUM(A1:A3)"));                // công thức Excel → giữ nguyên chữ
        Assert.False(TestData.IsFormula("a=b"));
        Assert.Null(TestData.Preview("Nguyễn Văn A"));
        Assert.Equal(false, TestData.Preview("=hotenn()")?.Ok);
        Assert.Equal(true, TestData.Preview("=sdt()")?.Ok);
        Assert.Throws<FormatException>(() => TestData.Evaluate("=random(a, 3)"));
        Assert.Throws<FormatException>(() => TestData.Evaluate("=chuso(0)"));
        Assert.Throws<FormatException>(() => TestData.Evaluate("=hoten(khác)"));
        Assert.Throws<FormatException>(() => TestData.Evaluate("=chon()"));
    }

    [Fact]
    public void InlineInTextFields()
    {
        var x = new VariableExpander(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        Assert.Matches(@"^KH-\d{4} / 0\d{9}$", x.Expand("KH-{{=chuso(4)}} / {{ =sdt() }}"));
        var ex = Assert.Throws<InvalidOperationException>(() => x.Expand("{{=khongco()}}"));
        Assert.Contains("khongco", ex.Message);
    }

    [Fact]
    public async Task VariablesAreGeneratedOnEveryRun()
    {
        var job = new Job
        {
            Name = "du-lieu-test",
            Variables =
            [
                new VariableDef { Name = "hoTen", Value = "=hoten()" },
                new VariableDef { Name = "so", Value = "=random(7, 7)" },
                new VariableDef { Name = "ma", Value = "KH-{{so}}" },
                new VariableDef { Name = "excel", Value = "=SUM(A1:A3)" }
            ],
            Steps = [SetVar("copy", VarSource.Value, "{{hoTen}}|{{=chuso(3)}}")]
        };
        var (r, c) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, c.Vars["hoTen"].Split(' ').Length);
        Assert.Equal("7", c.Vars["so"]);
        Assert.Equal("KH-7", c.Vars["ma"]);
        Assert.Equal("=SUM(A1:A3)", c.Vars["excel"]);
        Assert.Matches(@"^.+ .+ .+\|\d{3}$", c.Vars["copy"]);
        Assert.StartsWith(c.Vars["hoTen"] + "|", c.Vars["copy"]);        // biến giữ một giá trị trong cả lần chạy

        // Mỗi lần chạy sinh dữ liệu mới.
        var names = new HashSet<string>();
        for (int i = 0; i < 8; i++) names.Add((await RunAsync(job)).C.Vars["hoTen"]);
        Assert.True(names.Count > 1);
    }
}
