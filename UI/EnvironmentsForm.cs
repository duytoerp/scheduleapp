using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>
/// Quản lý môi trường kiểm thử (Dev / Test / UAT…): mỗi môi trường là một bộ biến (vd d365Url, tài khoản test) ghi đè biến
/// cùng tên của kịch bản khi chạy ở môi trường đó.
/// </summary>
internal sealed class EnvironmentsForm : BaseForm
{
    private readonly List<TestEnvironment> _envs;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly DataGridView _vars = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        RowHeadersWidth = 28,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };
    private int _shown = -1;

    protected override string HelpTopicId => "environments";

    /// <summary>Danh sách môi trường sau khi sửa (khi bấm Lưu).</summary>
    public IReadOnlyList<TestEnvironment> Environments => _envs;

    public EnvironmentsForm(IEnumerable<TestEnvironment> environments)
    {
        _envs = environments.Select(e => new TestEnvironment
        {
            Name = e.Name,
            Variables = e.Variables.Select(v => new VariableDef { Name = v.Name, Value = v.Value }).ToList()
        }).ToList();

        Text = "Môi trường kiểm thử";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 520);
        MinimumSize = new Size(640, 380);
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(12);

        _vars.Columns.Add("Name", "Tên biến");
        _vars.Columns.Add("Value", "Giá trị (dùng được {{secret:Tên}})");
        _vars.Columns[0].FillWeight = 35;

        var left = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(0, 0, 10, 0) };
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        var add = new Button { Text = "＋ Thêm", AutoSize = true };
        var copy = new Button { Text = "Nhân bản", AutoSize = true };
        var rename = new Button { Text = "Đổi tên", AutoSize = true };
        var remove = new Button { Text = "Xóa", AutoSize = true };
        leftButtons.Controls.AddRange([add, copy, rename, remove]);
        left.Controls.Add(_list);
        left.Controls.Add(leftButtons);
        left.Controls.Add(new Label { Text = "Môi trường", Dock = DockStyle.Top, Height = 24, Font = Theme.BoldFont });

        var hint = new Label
        {
            Text = "Biến của môi trường ghi đè biến cùng tên của kịch bản (tab Biến), vd d365Url, taiKhoanTest. Chọn môi trường trên trang " +
                   "Kiểm thử, hoặc khi chạy dòng lệnh: --env \"Tên\". Mật khẩu nên để trong 🔑 Bí mật rồi ghi {{secret:Tên}}. " +
                   "Biến {{env.name}} = tên môi trường đang chạy.",
            Dock = DockStyle.Bottom,
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Theme.Muted,
            Padding = new Padding(0, 8, 0, 0)
        };
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_vars);
        right.Controls.Add(hint);
        right.Controls.Add(new Label { Text = "Biến", Dock = DockStyle.Top, Height = 24, Font = Theme.BoldFont });

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var cancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "Lưu", AutoSize = true, MinimumSize = new Size(90, 0) };
        Theme.StyleButton(ok, primary: true);
        bottom.Controls.AddRange([cancel, ok, HelpLink()]);
        CancelButton = cancel;

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(bottom);

        add.Click += (_, _) => AddEnvironment(null);
        copy.Click += (_, _) => { if (_shown >= 0) AddEnvironment(_envs[_shown]); };
        rename.Click += (_, _) => Rename();
        remove.Click += (_, _) =>
        {
            if (_shown < 0) return;
            var env = _envs[_shown];
            if (MessageBox.Show(this, $"Xóa môi trường \"{env.Name}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _envs.RemoveAt(_shown);
            _shown = -1;
            FillList(Math.Min(_list.SelectedIndex, _envs.Count - 1));
        };
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.DoubleClick += (_, _) => Rename();
        ok.Click += (_, _) =>
        {
            Commit();
            if (Validate(out var error)) { DialogResult = DialogResult.OK; Close(); }
            else MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        FillList(_envs.Count > 0 ? 0 : -1);
    }

    private void FillList(int select)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _envs) _list.Items.Add(e.Name);
        _list.EndUpdate();
        _list.SelectedIndex = select;
        if (select < 0) ShowSelected();
    }

    private void ShowSelected()
    {
        Commit();
        _shown = _list.SelectedIndex;
        _vars.Rows.Clear();
        _vars.Enabled = _shown >= 0;
        if (_shown < 0) return;
        foreach (var v in _envs[_shown].Variables) _vars.Rows.Add(v.Name, v.Value);
    }

    /// <summary>Ghi các biến đang sửa vào môi trường đang hiện.</summary>
    private void Commit()
    {
        if (_shown < 0 || _shown >= _envs.Count) return;
        _vars.EndEdit();
        _envs[_shown].Variables = _vars.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
            .Select(r => new VariableDef { Name = (r.Cells[0].Value as string ?? "").Trim(), Value = r.Cells[1].Value as string ?? "" })
            .Where(v => v.Name.Length > 0).ToList();
    }

    private void AddEnvironment(TestEnvironment? from)
    {
        Commit();
        var name = Prompt("Tên môi trường mới (vd Dev, Test, UAT):", from == null ? "" : from.Name + " (bản sao)");
        if (name == null) return;
        var env = new TestEnvironment
        {
            Name = name,
            Variables = from?.Variables.Select(v => new VariableDef { Name = v.Name, Value = v.Value }).ToList()
                        ?? [new VariableDef { Name = "d365Url", Value = "https://<org>.crm5.dynamics.com/main.aspx?appid=…" }]
        };
        _envs.Add(env);
        _shown = -1;
        FillList(_envs.Count - 1);
    }

    private void Rename()
    {
        if (_shown < 0) return;
        var name = Prompt("Tên môi trường:", _envs[_shown].Name, _shown);
        if (name == null) return;
        _envs[_shown].Name = name;
        _list.Items[_shown] = name;
    }

    /// <summary>Hỏi tên (không trống, không trùng tên môi trường khác); null nếu hủy.</summary>
    private string? Prompt(string message, string value, int self = -1)
    {
        while (true)
        {
            var name = InputBox.Show(this, Text, message, value)?.Trim();
            if (name == null) return null;
            if (name.Length == 0) continue;
            if (_envs.Where((_, i) => i != self).Any(e => e.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            {
                MessageBox.Show(this, $"Đã có môi trường \"{name}\".", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                value = name;
                continue;
            }
            return name;
        }
    }

    private bool Validate(out string error)
    {
        foreach (var e in _envs)
        {
            var dup = e.Variables.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null)
            {
                error = $"Môi trường \"{e.Name}\" có biến \"{dup.Key}\" khai báo hai lần.";
                return false;
            }
        }
        error = "";
        return true;
    }
}

/// <summary>Hỏi nhập một dòng chữ (dùng hộp thoại nhập của flow, nhưng không luôn-trên-cùng).</summary>
internal static class InputBox
{
    public static string? Show(IWin32Window owner, string title, string message, string value)
    {
        using var f = new InputPromptForm(title, message, value, false) { StartPosition = FormStartPosition.CenterParent, TopMost = false };
        return f.ShowDialog(owner) == DialogResult.OK ? f.Value : null;
    }
}
