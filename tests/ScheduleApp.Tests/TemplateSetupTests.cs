using ScheduleApp.Models;
using ScheduleApp.Services;
using Kind = ScheduleApp.Services.TemplateSetup.ItemKind;

namespace ScheduleApp.Tests;

public class TemplateSetupTests
{
    private static List<Job> Sample(string file)
    {
        var name = typeof(Job).Assembly.GetManifestResourceNames().Single(n => n.EndsWith(file));
        using var stream = typeof(Job).Assembly.GetManifestResourceStream(name)!;
        return JobStore.ImportJson(new StreamReader(stream).ReadToEnd());
    }

    private static Job Named(List<Job> jobs, string prefix) => jobs.Single(j => j.Name.StartsWith(prefix));

    [Fact]
    public void D365ScenarioIncludesSharedOpenJobVariablesAndSecrets()
    {
        var all = Sample("ScheduleApp-mau-kiem-thu-d365.json");
        var jobs = TemplateSetup.WithDependencies(Named(all, "C2"), all);
        Assert.Contains(jobs, j => j.Name.StartsWith("C1"));            // C2 gọi C1 "Mở Dynamics 365"

        var items = TemplateSetup.Collect(jobs, remember: false);
        var url = items.Single(i => i.Kind == Kind.Variable && i.Key == "d365Url");
        Assert.True(url.NeedsInput);                                   // tenorg + appid toàn số 0
        Assert.Contains("appid", url.Description);
        Assert.True(items.Single(i => i.Key == "taiKhoanTest").NeedsInput);
        Assert.Contains(items, i => i.Kind == Kind.Secret && i.Key == "MatKhauTest" && !i.Exists);
        Assert.Contains(items, i => i.Kind == Kind.Secret && i.Key == "TotpTest");
        Assert.Equal(Kind.Variable, items[0].Kind);                    // biến trước, bí mật sau
    }

    [Fact]
    public void SharedVariableAppliedToEveryJobAndRemembered()
    {
        var all = Sample("ScheduleApp-mau-nang-cao.json");
        var a2 = Named(all, "A2");
        var jobs = TemplateSetup.WithDependencies(a2, all);              // A2 gọi A1 đăng nhập
        var items = TemplateSetup.Collect(jobs, remember: true);

        var tenant = items.Single(i => i.Kind == Kind.Variable && i.Key == "tenant");
        Assert.Equal(2, tenant.Jobs.Count);                             // một ô cho cả A1 và A2
        Assert.DoesNotContain(items, i => i.Key == "dem");              // bộ đếm do flow tự gán → không hỏi
        var excel = items.Single(i => i.Kind == Kind.Path && i.Key.EndsWith("khach-hang.xlsx"));
        Assert.False(excel.IsFolder);
        var secrets = items.Where(i => i.Kind == Kind.Secret).Select(i => i.Key).ToList();
        Assert.Contains("D365User", secrets);
        Assert.Contains("D365Pass", secrets);

        int changes = TemplateSetup.Apply(new Dictionary<TemplateSetup.Item, string>
        {
            [tenant] = " contoso ",
            [excel] = @"D:\DuLieu\khach.xlsx",
            [items.Single(i => i.Key == "D365Pass")] = "mật-khẩu-thử",
            [items.Single(i => i.Key == "D365User")] = ""                // trống = giữ nguyên
        });
        Assert.True(changes >= 4, changes.ToString());
        Assert.All(jobs, j => Assert.Equal("contoso", j.Variables.Single(v => v.Name == "tenant").Value));
        Assert.Contains(a2.Steps, s => s.Target == @"D:\DuLieu\khach.xlsx");
        Assert.DoesNotContain(a2.Steps, s => s.Target.EndsWith("khach-hang.xlsx"));
        Assert.Equal("mật-khẩu-thử", SecretStore.Get("D365Pass"));
        Assert.False(SecretStore.Contains("D365User"));

        // Thêm mẫu khác dùng cùng biến → điền sẵn giá trị đã nhập.
        var b1 = Named(Sample("ScheduleApp-mau-tich-hop.json"), "B1");
        var again = TemplateSetup.Collect([b1], remember: true).Single(i => i.Key == "tenant");
        Assert.Equal("contoso", again.Suggested);
        Assert.False(again.NeedsInput);
        Assert.Null(TemplateSetup.Collect([b1], remember: false).Single(i => i.Key == "tenant").Suggested);
    }

    [Fact]
    public void FolderTriggersConnectionsAndMixedValues()
    {
        var all = Sample("ScheduleApp-mau-nang-cao.json");
        var a4 = Named(all, "A4");
        var downloads = TemplateSetup.Collect([a4], false).Single(i => i.Kind == Kind.Path && i.Key.EndsWith("Downloads"));
        Assert.True(downloads.IsFolder);
        TemplateSetup.Apply(new Dictionary<TemplateSetup.Item, string> { [downloads] = @"E:\Tai ve" });
        Assert.Equal(@"E:\Tai ve", a4.Triggers.Single(t => t.Type == TriggerType.FileCreated).Value);

        var b2 = Named(Sample("ScheduleApp-mau-tich-hop.json"), "B2");
        var conn = TemplateSetup.Collect([b2], false).Single(i => i.Kind == Kind.Connection);
        Assert.Equal("Dynamics365", conn.Key);
        Assert.True(conn.NeedsInput);

        // Hai công việc đặt giá trị khác nhau: không sửa thì giữ nguyên từng giá trị.
        var j1 = new Job { Name = "J1", Variables = [new VariableDef { Name = "thuMuc", Value = "a" }] };
        var j2 = new Job { Name = "J2", Variables = [new VariableDef { Name = "THUMUC", Value = "b" }] };
        var mixed = TemplateSetup.Collect([j1, j2], false).Single();
        Assert.True(mixed.Mixed);
        Assert.Equal(0, TemplateSetup.Apply(new Dictionary<TemplateSetup.Item, string> { [mixed] = "a" }));
        Assert.Equal("b", j2.Variables[0].Value);
        TemplateSetup.Apply(new Dictionary<TemplateSetup.Item, string> { [mixed] = "c" });
        Assert.Equal("c", j1.Variables[0].Value);
        Assert.Equal("c", j2.Variables[0].Value);
    }
}
