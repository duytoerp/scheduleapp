using ScheduleApp.Services.Engine;

namespace ScheduleApp.Tests;

public class ExpanderTests
{
    private static readonly VariableExpander X = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ten"] = "Nguyễn Văn A",
        ["tien"] = "1234567.5",
        ["ngay"] = "2026-10-01",
        ["ds"] = "chuối\nan\n\nbưởi\nan\n",
        ["quote"] = "Nói \"xin chào\"\nrồi đi",
        ["path"] = " \"D:\\Video\\01 giới thiệu.mp4\" "
    });

    [Theory]
    [InlineData("{{ten:upper}}", "NGUYỄN VĂN A")]
    [InlineData("{{tien:N0}}", "1.234.568")]
    [InlineData("{{ngay:dd/MM/yyyy}}", "01/10/2026")]
    [InlineData("{{ten:nodiacritics}}", "Nguyen Van A")]
    [InlineData("Xin chào {{ten}}!", "Xin chào Nguyễn Văn A!")]
    [InlineData("a { b } c", "a { b } c")]
    [InlineData("{{ds:count}}", "4")]
    [InlineData("{{ds:first}}", "chuối")]
    [InlineData("{{ds:last}}", "an")]
    [InlineData("{{ds:item(2)}}", "an")]
    [InlineData("{{ds:item(-2)}}", "bưởi")]
    [InlineData("{{ds:item(9)}}", "")]
    [InlineData("{{ds:join(, )}}", "chuối, an, bưởi, an")]
    [InlineData("{{ds:unique}}", "chuối\nan\nbưởi")]
    [InlineData("{{path:unquote}}", @"D:\Video\01 giới thiệu.mp4")]
    [InlineData("{{quote:json}}", "Nói \\\"xin chào\\\"\\nrồi đi")]
    public void Formats(string template, string expected) => Assert.Equal(expected, X.Expand(template));

    [Fact]
    public void Dates()
    {
        Assert.Equal(DateTime.Today.ToString("dd/MM/yyyy"), X.Expand("{{today}}"));
        Assert.Equal(DateTime.Today.AddDays(-1).ToString("dd/MM/yyyy"), X.Expand("{{today-1:dd/MM/yyyy}}"));
        Assert.Equal(DateTime.Today.AddMonths(1).ToString("MM/yyyy"), X.Expand("{{ today+1M:MM/yyyy }}"));
    }

    [Fact]
    public void SortUsesVietnameseOrder() => Assert.Equal("an\nan\nbưởi\nchuối", X.Expand("{{ds:sort}}"));

    [Fact]
    public void RandomInRange()
    {
        int r = int.Parse(X.Expand("{{random:5-7}}"));
        Assert.InRange(r, 5, 7);
    }

    [Fact]
    public void UnknownVariableThrows() => Assert.Throws<InvalidOperationException>(() => X.Expand("{{khongCo}}"));
}
