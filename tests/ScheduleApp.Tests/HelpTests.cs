using System.Windows.Forms;
using ScheduleApp.UI;

namespace ScheduleApp.Tests;

public class HelpTests
{
    [Fact]
    public void TopicsHaveUniqueIdsAndKnownCommands()
    {
        var topics = HelpContent.Topics;
        Assert.Equal(topics.Count, topics.Select(t => t.Id).Distinct().Count());
        Assert.Equal(HelpContent.Start, topics[0].Id);
        foreach (var t in topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Summary), t.Id);
            Assert.All(t.Actions, a => Assert.Contains(a.Command, HelpContent.Commands));
        }
        // Chủ đề được gọi từ F1 / nút "?" ở các màn hình phải tồn tại.
        foreach (var id in new[] { "start", "flow", "variables", "errors", "d365-overview", "d365-record", "d365-pick", "d365-steps", "assert",
                     "d365-grids", "d365-forms", "d365-login", "environments", "test-folder", "data-driven" })
            Assert.NotNull(HelpContent.Find(id));
        // Các nhóm liền nhau (danh sách chủ đề chia theo nhóm).
        var groups = topics.Select(t => t.Group).ToList();
        Assert.Equal(groups.Distinct().Count(), groups.Where((g, i) => i == 0 || groups[i - 1] != g).Count());
    }

    [Fact]
    public void SearchIgnoresDiacritics()
    {
        string[] Find(string q) => HelpContent.Topics.Where(t => HelpView.Matches(t, q)).Select(t => t.Id).ToArray();
        Assert.Contains("d365-record", Find("ghi kich ban"));
        Assert.Contains("ci", Find("junit ma thoat"));
        Assert.Contains("troubleshoot", Find("cong 9222"));
        Assert.Empty(Find("khongcotukhoanay"));
    }

    [Fact]
    public void InlineFormattingBecomesRtf()
    {
        Assert.Equal(@"a \b b\b0  \i c\i0  d", HelpRtf.Inline("a **b** *c* d"));
        Assert.Contains(@"\f1", HelpRtf.Inline("dùng `{{x}}`"));
        Assert.Contains(@"\{\{x\}\}", HelpRtf.Inline("dùng `{{x}}`"));
        Assert.Equal(@"\u7915?", HelpRtf.Escape("ừ"));
        // Dấu * đứng riêng (vd "`*` mọi kịch bản" đã nằm trong mã, "5 * 3") không thành chữ nghiêng.
        Assert.DoesNotContain(@"\i ", HelpRtf.Inline("5 * 3"));
    }

    [Fact]
    public void EveryTopicRendersInRichTextBox()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var box = new RichTextBox();
                foreach (var t in HelpContent.Topics)
                {
                    box.Rtf = HelpRtf.Build(t.Body);
                    Assert.False(string.IsNullOrWhiteSpace(box.Text), t.Id);
                    Assert.DoesNotContain("**", box.Text.Replace("***", ""));
                    Assert.DoesNotContain("## ", box.Text);
                }
                box.Rtf = HelpRtf.Build("## Tiêu đề\n1. Bước **một** với `{{today}}`\n- Ý *nghiêng*\n> mẹo\n! lưu ý\n```\nScheduleApp.exe --test \"*\"\n```");
                Assert.Contains("Tiêu đề", box.Text);
                Assert.Contains("Bước một với {{today}}", box.Text);
                Assert.Contains("Mẹo", box.Text);
                Assert.Contains("•", box.Text);
                Assert.Contains("ScheduleApp.exe --test \"*\"", box.Text);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
    }
}
