using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Quản lý bí mật (mật khẩu, token…) dùng trong flow bằng {{secret:Tên}}. Giá trị không bao giờ hiển thị lại.</summary>
internal sealed class SecretsForm : BaseForm
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    public SecretsForm()
    {
        SuspendLayout();
        Text = "Bí mật — ScheduleApp";
        Size = new Size(560, 420);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(10);

        var hint = new Label
        {
            Text = "Lưu mật khẩu / token đã mã hóa (Windows DPAPI — chỉ tài khoản Windows của bạn trên máy này đọc được). " +
                   "Dùng trong flow: {{secret:Tên}}, vd bước Gõ văn bản \"{{secret:MatKhauCRM}}\". " +
                   "Bí mật không nằm trong jobs.json và không bị xuất ra file, giá trị được che *** trong nhật ký.",
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = UiText.Muted,
            Padding = new Padding(0, 0, 0, 8)
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Right, WrapContents = false, Padding = new Padding(6, 0, 0, 0) };
        var add = new Button { Text = "＋ Thêm…", AutoSize = true, MinimumSize = new Size(120, 0) };
        var change = new Button { Text = "Đổi giá trị…", AutoSize = true, MinimumSize = new Size(120, 0) };
        var copy = new Button { Text = "Sao chép {{…}}", AutoSize = true, MinimumSize = new Size(120, 0) };
        var del = new Button { Text = "Xóa", AutoSize = true, MinimumSize = new Size(120, 0) };
        var close = new Button { Text = "Đóng", AutoSize = true, MinimumSize = new Size(120, 0), DialogResult = DialogResult.Cancel, Margin = new Padding(3, 20, 3, 3) };
        buttons.Controls.AddRange([add, change, copy, del, close]);
        CancelButton = close;

        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(hint);
        ResumeLayout(true);

        add.Click += (_, _) => Edit(null);
        change.Click += (_, _) => { if (_list.SelectedItem is string n) Edit(n); };
        _list.DoubleClick += (_, _) => { if (_list.SelectedItem is string n) Edit(n); };
        copy.Click += (_, _) => { if (_list.SelectedItem is string n) Clipboard.SetText("{{secret:" + n + "}}"); };
        del.Click += (_, _) =>
        {
            if (_list.SelectedItem is not string n) return;
            if (MessageBox.Show(this, $"Xóa bí mật \"{n}\"? Các flow đang dùng {{{{secret:{n}}}}} sẽ bị lỗi.", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            SecretStore.Remove(n);
            RefreshList();
        };
        RefreshList();
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var n in SecretStore.Names) _list.Items.Add(n);
    }

    private void Edit(string? existing)
    {
        using var dlg = new SecretEditForm(existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SecretStore.Set(dlg.SecretName, dlg.SecretValue);
            Log.Info($"Đã lưu bí mật \"{dlg.SecretName}\".");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không lưu được bí mật: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshList();
    }

    private sealed class SecretEditForm : BaseForm
    {
        private readonly TextBox _name = new() { Width = 260 };
        private readonly TextBox _value = new() { Width = 260, UseSystemPasswordChar = true };
        private readonly TextBox _confirm = new() { Width = 260, UseSystemPasswordChar = true };

        public string SecretName => _name.Text.Trim();
        public string SecretValue => _value.Text;

        public SecretEditForm(string? existing)
        {
            SuspendLayout();
            Text = existing == null ? "Thêm bí mật" : $"Đổi giá trị \"{existing}\"";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            _name.Text = existing ?? "";
            _name.ReadOnly = existing != null;
            var show = new CheckBox { Text = "Hiện ký tự", AutoSize = true };
            show.CheckedChanged += (_, _) => _value.UseSystemPasswordChar = _confirm.UseSystemPasswordChar = !show.Checked;

            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
            grid.Controls.Add(new Label { Text = "Tên (không dấu, vd MatKhauCRM):", AutoSize = true, Margin = new Padding(3, 6, 6, 3) }, 0, 0);
            grid.Controls.Add(_name, 1, 0);
            grid.Controls.Add(new Label { Text = "Giá trị:", AutoSize = true, Margin = new Padding(3, 6, 6, 3) }, 0, 1);
            grid.Controls.Add(_value, 1, 1);
            grid.Controls.Add(new Label { Text = "Nhập lại:", AutoSize = true, Margin = new Padding(3, 6, 6, 3) }, 0, 2);
            grid.Controls.Add(_confirm, 1, 2);
            grid.Controls.Add(show, 1, 3);

            var ok = new Button { Text = "Lưu", AutoSize = true, MinimumSize = new Size(90, 0) };
            var cancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) =>
            {
                if (SecretName.Length == 0 || SecretName.IndexOfAny(['{', '}', ':']) >= 0)
                {
                    MessageBox.Show(this, "Tên không hợp lệ (không chứa { } :).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (_value.Text != _confirm.Text)
                {
                    MessageBox.Show(this, "Hai lần nhập không khớp.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            AcceptButton = ok;
            CancelButton = cancel;
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
            buttons.Controls.AddRange([cancel, ok]);
            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
            root.Controls.Add(grid);
            root.Controls.Add(buttons);
            Controls.Add(root);
            ResumeLayout(true);
        }
    }
}
