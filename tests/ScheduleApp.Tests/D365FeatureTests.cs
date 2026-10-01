using System.Text;
using ScheduleApp.Automation;
using ScheduleApp.Models;

namespace ScheduleApp.Tests;

/// <summary>Phần không cần trình duyệt của các tính năng D365: TOTP, mô tả bước, so khớp dòng / vai trò / trạng thái nút.</summary>
public class D365FeatureTests
{
    private const string RfcSecretBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"; // "12345678901234567890"

    [Fact]
    public void HotpMatchesRfc4226Vectors()
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        string[] expected = ["755224", "287082", "359152", "969429", "338314", "254676", "287922", "162583", "399871", "520489"];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], Totp.Code(key, i));
    }

    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void TotpMatchesRfc6238Sha1Vectors(long unix, string code)
    {
        Assert.Equal(code, Totp.Code(RfcSecretBase32, DateTimeOffset.FromUnixTimeSeconds(unix), digits: 8));
        Assert.Equal(code[2..], Totp.Code(RfcSecretBase32, DateTimeOffset.FromUnixTimeSeconds(unix))); // 6 chữ số = 6 số cuối
    }

    [Fact]
    public void Base32DecodesAndRejectsInvalidKeys()
    {
        Assert.Equal("12345678901234567890", Encoding.ASCII.GetString(Totp.DecodeBase32(RfcSecretBase32)));
        Assert.Equal([.. "Hello!"u8, 0xDE, 0xAD, 0xBE, 0xEF], Totp.DecodeBase32("jbsw y3dp-ehpk 3pxp"));
        Assert.Throws<FormatException>(() => Totp.DecodeBase32("ABC1"));
        Assert.Throws<FormatException>(() => Totp.DecodeBase32("  "));
        Assert.Equal(30, Totp.SecondsLeft(DateTimeOffset.FromUnixTimeSeconds(60)));
        Assert.Equal(1, Totp.SecondsLeft(DateTimeOffset.FromUnixTimeSeconds(89)));
    }

    [Fact]
    public void EveryActionAndConditionHasANameAndDescription()
    {
        foreach (var a in Enum.GetValues<D365Action>())
        {
            Assert.True(ActionStep.D365ActionNames.ContainsKey(a), a.ToString());
            var s = ActionStep.CreateDefault(StepType.Dynamics);
            s.D365Action = a;
            s.Text = "x";
            Assert.StartsWith("D365: ", s.Describe());
            Assert.True(s.Describe().Length > "D365: ".Length, a.ToString());
        }
        foreach (var c in Enum.GetValues<ConditionKind>())
        {
            Assert.True(ActionStep.ConditionNames.ContainsKey(c), c.ToString());
            var s = ActionStep.CreateDefault(StepType.Assert);
            s.Condition = c;
            Assert.False(string.IsNullOrWhiteSpace(s.DescribeCondition()), c.ToString());
        }
    }

    [Fact]
    public void NewStepsDescribeThemselves()
    {
        ActionStep D(D365Action a, Action<ActionStep> cfg)
        {
            var s = ActionStep.CreateDefault(StepType.Dynamics);
            s.D365Action = a;
            cfg(s);
            return s;
        }
        ActionStep C(ConditionKind k, Action<ActionStep> cfg)
        {
            var s = ActionStep.CreateDefault(StepType.Assert);
            s.Condition = k;
            cfg(s);
            return s;
        }

        Assert.Equal("D365: mở form account [{{id}}] · form \"Bán hàng\"", D(D365Action.OpenForm, s => { s.Text = "account"; s.Arguments = "{{id}}"; s.Form = "Bán hàng"; }).Describe());
        Assert.Equal("D365: subgrid Contacts → mở dòng 1", D(D365Action.SubgridOpenRow, s => s.Text = "Contacts").Describe());
        Assert.Equal("D365: subgrid Contacts dòng 2 · cột emailaddress1 → {{e}}",
            D(D365Action.SubgridGetValue, s => { s.Text = "Contacts"; s.RowRef = "2"; s.Arguments = "emailaddress1"; s.Variable = "e"; }).Describe());
        Assert.Equal("D365: đọc view \"Tài khoản Hà Nội\" của account · tìm \"Contoso\" → {{n}}",
            D(D365Action.ViewQuery, s => { s.Text = "account"; s.Arguments = "Tài khoản Hà Nội"; s.RowRef = "Contoso"; s.Variable = "n"; }).Describe());
        Assert.Equal("D365: mở bản ghi trong view mặc định của account", D(D365Action.ViewOpenRecord, s => s.Text = "account").Describe());
        Assert.Equal("D365: tạo nhanh contact: firstname=Lan, lastname=Phạm",
            D(D365Action.QuickCreate, s => { s.Arguments = "contact"; s.Text = "firstname=Lan\nlastname=Phạm"; }).Describe());
        Assert.Equal("D365: đăng nhập Microsoft \"qa@contoso.vn\" + mã MFA (TOTP)",
            D(D365Action.Login, s => { s.Text = "qa@contoso.vn"; s.Arguments = "{{secret:MatKhau}}"; s.RowRef = "{{secret:Totp}}"; }).Describe());
        Assert.Equal("Kiểm tra: nút \"Assign\" hiện nhưng bị mờ (không bấm được)",
            C(ConditionKind.D365Command, s => { s.Text = "Assign"; s.Arguments = "disabled"; }).Describe());
        Assert.Equal("Kiểm tra: KHÔNG người dùng có vai trò \"System Administrator\"",
            C(ConditionKind.D365UserRole, s => { s.Text = "System Administrator"; s.Negate = true; }).Describe());
        Assert.Equal("Kiểm tra: số dòng subgrid Contacts ≥ 1",
            C(ConditionKind.D365SubgridCount, s => { s.Text = "Contacts"; s.CompareOp = CompareOp.GreaterOrEqual; s.Arguments = "1"; }).Describe());
    }

    [Fact]
    public void MatchingHelpers()
    {
        var row = new D365Client.GridRow(1, "contact", "c1", "Trần Thị B",
            new Dictionary<string, string> { ["emailaddress1"] = "b@contoso.vn", ["parentcustomerid"] = "Công ty Đông Á" });
        Assert.True(D365Client.RowContains(row, "tran thi"));
        Assert.True(D365Client.RowContains(row, "DONG A"));
        Assert.True(D365Client.RowContains(row, "@contoso"));
        Assert.False(D365Client.RowContains(row, "Lê"));

        var user = new D365Client.UserInfo("QA", "u1", ["Salesperson", "Nhân viên CSKH"]);
        Assert.True(D365Client.HasRole(user, "nhan vien cskh"));
        Assert.False(D365Client.HasRole(user, "Nhân viên"));

        var st = new Dictionary<string, string> { ["visible"] = "true", ["enabled"] = "false", ["disabled"] = "true" };
        Assert.True(D365Client.HasCommandState(st, "visible"));
        Assert.True(D365Client.HasCommandState(st, " Disabled "));
        Assert.False(D365Client.HasCommandState(st, "enabled"));
        Assert.Throws<InvalidOperationException>(() => D365Client.HasCommandState(st, "hidden"));
    }

    [Fact]
    public void FormFieldSurvivesSaveAndVariableExpansion()
    {
        var job = new Job { Steps = [TestSupport.S(StepType.Dynamics, s => { s.D365Action = D365Action.OpenForm; s.Text = "account"; s.Form = "{{form}}"; })] };
        var copy = job.Clone();
        Assert.Equal("{{form}}", copy.Steps[0].Form);
        var ctx = new Services.Engine.FlowContext(job, new FakeUi(), Services.Engine.RunOptions.Default, _ => null, CancellationToken.None);
        ctx.Vars["form"] = "Bán hàng";
        Assert.Equal("Bán hàng", ctx.ExpandStep(job.Steps[0]).Form);
    }
}
