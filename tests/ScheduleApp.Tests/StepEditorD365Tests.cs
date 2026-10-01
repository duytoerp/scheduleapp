using System.Reflection;
using System.Windows.Forms;
using ScheduleApp.Models;
using ScheduleApp.UI;

namespace ScheduleApp.Tests;

/// <summary>Form soạn bước giữ đúng các ô mới của bước Dynamics 365 / Kiểm tra khi mở rồi lưu lại (không mất dữ liệu).</summary>
public class StepEditorD365Tests
{
    private static ActionStep RoundTrip(ActionStep step)
    {
        ActionStep? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                // BuildStep đọc Visible của các ô → form phải đang hiện (đặt ngoài màn hình).
                using var f = new StepEditorForm(step.Clone()) { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-20000, -20000) };
                f.Show();
                Application.DoEvents();
                var build = typeof(StepEditorForm).GetMethod("BuildStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
                result = (ActionStep)build.Invoke(f, null)!;
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
        return result!;
    }

    private static ActionStep D(D365Action a, Action<ActionStep> cfg)
    {
        var s = ActionStep.CreateDefault(StepType.Dynamics);
        s.D365Action = a;
        cfg(s);
        return s;
    }

    [Fact]
    public void NewDynamicsFieldsSurviveTheEditor()
    {
        var open = RoundTrip(D(D365Action.OpenForm, s => { s.Text = "account"; s.Arguments = "{{id}}"; s.Form = "Bán hàng"; }));
        Assert.Equal(("account", "{{id}}", "Bán hàng", ""), (open.Text, open.Arguments, open.Form, open.RowRef));

        var cell = RoundTrip(D(D365Action.SubgridGetValue, s => { s.Text = "Contacts"; s.Arguments = "emailaddress1"; s.RowRef = "2"; s.Variable = "email"; }));
        Assert.Equal(("Contacts", "emailaddress1", "2", "email", ""), (cell.Text, cell.Arguments, cell.RowRef, cell.Variable, cell.Form));

        var view = RoundTrip(D(D365Action.ViewQuery, s => { s.Text = "account"; s.Arguments = "Tài khoản Hà Nội"; s.RowRef = "Contoso"; s.Variable = "n"; }));
        Assert.Equal(("account", "Tài khoản Hà Nội", "Contoso", "n"), (view.Text, view.Arguments, view.RowRef, view.Variable));

        var qc = RoundTrip(D(D365Action.QuickCreate, s => { s.Arguments = "contact"; s.Text = "firstname=Lan\nlastname=Phạm"; s.Variable = "id"; }));
        Assert.Equal(("contact", "firstname=Lan\nlastname=Phạm", "id"), (qc.Arguments, qc.Text, qc.Variable));

        var login = RoundTrip(D(D365Action.Login, s => { s.Text = "qa@contoso.vn"; s.Arguments = "{{secret:MatKhau}}"; s.RowRef = "{{secret:Totp}}"; }));
        Assert.Equal(("qa@contoso.vn", "{{secret:MatKhau}}", "{{secret:Totp}}"), (login.Text, login.Arguments, login.RowRef));

        var user = RoundTrip(D(D365Action.GetUser, s => { s.Variable = "u"; s.Text = "bỏ"; }));
        Assert.Equal(("u", ""), (user.Variable, user.Text)); // không có ô văn bản

        // Bước khác không giữ ô phụ của D365
        var set = RoundTrip(D(D365Action.SetField, s => { s.Text = "name"; s.Arguments = "A"; s.RowRef = "x"; s.Form = "y"; }));
        Assert.Equal(("", ""), (set.RowRef, set.Form));
    }

    [Fact]
    public void NewConditionsSurviveTheEditor()
    {
        ActionStep C(ConditionKind k, Action<ActionStep> cfg)
        {
            var s = ActionStep.CreateDefault(StepType.Assert);
            s.Condition = k;
            cfg(s);
            return s;
        }
        var cmd = RoundTrip(C(ConditionKind.D365Command, s => { s.Text = "Assign"; s.Arguments = "disabled"; s.Negate = true; }));
        Assert.Equal(("Assign", "disabled", true), (cmd.Text, cmd.Arguments, cmd.Negate));
        var count = RoundTrip(C(ConditionKind.D365SubgridCount, s => { s.Text = "Contacts"; s.CompareOp = CompareOp.GreaterOrEqual; s.Arguments = "2"; }));
        Assert.Equal(("Contacts", CompareOp.GreaterOrEqual, "2"), (count.Text, count.CompareOp, count.Arguments));
        var form = RoundTrip(C(ConditionKind.D365CurrentForm, s => { s.Text = "bỏ"; s.Arguments = "Bán hàng"; }));
        Assert.Equal(("", "Bán hàng"), (form.Text, form.Arguments));
        var role = RoundTrip(C(ConditionKind.D365UserRole, s => s.Text = "Salesperson"));
        Assert.Equal("Salesperson", role.Text);
    }
}
