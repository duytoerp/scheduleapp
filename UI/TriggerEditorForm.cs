using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Soạn một trình kích hoạt (phím tắt, file mới, ứng dụng mở/đóng, máy rảnh…).</summary>
internal sealed class TriggerEditorForm : BaseForm
{
    private static readonly TriggerType[] Types = Enum.GetValues<TriggerType>();

    private readonly JobTrigger _trigger;
    private readonly ComboBox _cboType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Label _lblValue = new() { AutoSize = true, Margin = new Padding(3, 7, 8, 3) };
    private readonly ComboBox _cboValue = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 320 };
    private readonly Button _btnValue = new() { AutoSize = true };
    private readonly Label _lblValue2 = new() { Text = "Chỉ file (vd *.pdf;*.xlsx):", AutoSize = true, Margin = new Padding(3, 7, 8, 3) };
    private readonly TextBox _txtValue2 = new() { Width = 320 };
    private readonly Label _lblMinutes = new() { Text = "Số phút rảnh:", AutoSize = true, Margin = new Padding(3, 7, 8, 3) };
    private readonly NumericUpDown _numMinutes = new() { Minimum = 1, Maximum = 1440, Width = 90 };
    private readonly CheckBox _chkEnabled = new() { Text = "Bật", AutoSize = true };
    private readonly Label _lblHint = new() { AutoSize = true, ForeColor = UiText.Muted, MaximumSize = new Size(520, 0), Margin = new Padding(3, 10, 3, 3) };

    public JobTrigger Trigger => _trigger;

    public TriggerEditorForm(JobTrigger trigger)
    {
        _trigger = trigger;
        SuspendLayout();
        Text = "Kích hoạt công việc";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        foreach (var t in Types) _cboType.Items.Add(JobTrigger.TypeNames[t]);
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        grid.Controls.Add(new Label { Text = "Kích hoạt khi:", AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, 0, 0);
        grid.Controls.Add(_cboType, 1, 0);
        grid.Controls.Add(_lblValue, 0, 1);
        grid.Controls.Add(_cboValue, 1, 1);
        grid.Controls.Add(_btnValue, 2, 1);
        grid.Controls.Add(_lblValue2, 0, 2);
        grid.Controls.Add(_txtValue2, 1, 2);
        grid.Controls.Add(_lblMinutes, 0, 3);
        grid.Controls.Add(_numMinutes, 1, 3);
        grid.Controls.Add(_chkEnabled, 1, 4);
        grid.Controls.Add(_lblHint, 0, 5);
        grid.SetColumnSpan(_lblHint, 3);

        var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Save();
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(grid);
        root.Controls.Add(buttons);
        Controls.Add(root);
        ResumeLayout(true);

        _cboType.SelectedIndexChanged += (_, _) => UpdateUi();
        _btnValue.Click += (_, _) => OnValueButton();
        _cboValue.KeyDown += OnHotkeyKeyDown;

        _cboType.SelectedIndex = Array.IndexOf(Types, trigger.Type);
        _cboValue.Text = trigger.Value;
        _txtValue2.Text = trigger.Value2;
        _numMinutes.Value = Math.Clamp(trigger.Minutes, 1, 1440);
        _chkEnabled.Checked = trigger.Enabled;
    }

    private TriggerType CurrentType => Types[Math.Max(0, _cboType.SelectedIndex)];

    private void UpdateUi()
    {
        var t = CurrentType;
        bool value = t is TriggerType.Hotkey or TriggerType.FileCreated or TriggerType.ProcessStarted or TriggerType.ProcessExited;
        _lblValue.Visible = _cboValue.Visible = value;
        _btnValue.Visible = t is TriggerType.FileCreated or TriggerType.ProcessStarted or TriggerType.ProcessExited;
        _lblValue2.Visible = _txtValue2.Visible = t == TriggerType.FileCreated;
        _lblMinutes.Visible = _numMinutes.Visible = t == TriggerType.Idle;
        _lblValue.Text = t switch
        {
            TriggerType.Hotkey => "Phím tắt (bấm tổ hợp phím):",
            TriggerType.FileCreated => "Thư mục theo dõi:",
            _ => "Tên tiến trình:"
        };
        _btnValue.Text = t == TriggerType.FileCreated ? "Chọn…" : "↻ Danh sách";
        _cboValue.Items.Clear();
        _lblHint.Text = t switch
        {
            TriggerType.Hotkey => "Bấm vào ô rồi nhấn tổ hợp phím, vd Ctrl+Alt+1. Phím tắt hoạt động ở mọi nơi khi ScheduleApp đang chạy.",
            TriggerType.FileCreated => "Chạy mỗi khi có file mới (kể cả file tải về từ trình duyệt). Mỗi file chạy một lần, lần lượt. " +
                                       "Dùng {{trigger.file}} (đường dẫn), {{trigger.name}}, {{trigger.base}} trong flow.",
            TriggerType.ProcessStarted => "Chạy khi ứng dụng vừa được mở (kiểm tra mỗi 2 giây), vd EXCEL, chrome, Teams.",
            TriggerType.ProcessExited => "Chạy khi ứng dụng vừa đóng hết, vd tự sao lưu sau khi đóng phần mềm kế toán.",
            TriggerType.Idle => "Chạy khi không có thao tác chuột/bàn phím trong N phút (vd tự khóa, dọn dẹp, đồng bộ).",
            TriggerType.SessionUnlock => "Chạy mỗi khi bạn mở khóa màn hình Windows (vd mở lại các ứng dụng làm việc).",
            TriggerType.AppStartup => "Chạy khi ScheduleApp khởi động — bật \"Khởi động cùng Windows\" để chạy lúc đăng nhập.",
            _ => ""
        };
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (CurrentType != TriggerType.Hotkey) return;
        e.SuppressKeyPress = true;
        e.Handled = true;
        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        if ((Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0) parts.Add("Win");
        parts.Add(key switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => "NumPad" + (key - Keys.NumPad0),
            _ => key.ToString()
        });
        _cboValue.Text = string.Join("+", parts);
    }

    private void OnValueButton()
    {
        if (CurrentType == TriggerType.FileCreated)
        {
            using var dlg = new FolderBrowserDialog { Description = "Chọn thư mục theo dõi", UseDescriptionForTitle = true, SelectedPath = _cboValue.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _cboValue.Text = dlg.SelectedPath;
            return;
        }
        var text = _cboValue.Text;
        _cboValue.Items.Clear();
        foreach (var p in WindowHelper.GetOpenWindows().Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order())
            _cboValue.Items.Add(p);
        _cboValue.Text = text;
        _cboValue.DroppedDown = true;
    }

    private void Save()
    {
        var t = CurrentType;
        var value = _cboValue.Text.Trim();
        if (_cboValue.Visible && value.Length == 0)
        {
            MessageBox.Show(this, $"Hãy nhập \"{_lblValue.Text.TrimEnd(':')}\".", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (t == TriggerType.Hotkey)
        {
            try { TriggerManager.ParseHotkey(value); }
            catch (FormatException ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        _trigger.Type = t;
        _trigger.Value = _cboValue.Visible ? value : "";
        _trigger.Value2 = _txtValue2.Visible ? _txtValue2.Text.Trim() : "";
        _trigger.Minutes = (int)_numMinutes.Value;
        _trigger.Enabled = _chkEnabled.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}
