using System.Media;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.UI;

/// <summary>Hộp thoại hỏi người dùng nhập một giá trị khi flow đang chạy (luôn trên cùng).</summary>
internal sealed class InputPromptForm : BaseForm
{
    private readonly TextBox _input = new() { Width = 380 };

    public string Value => _input.Text;

    public InputPromptForm(string title, string message, string defaultValue, bool password)
    {
        SuspendLayout();
        Text = title + " — ScheduleApp";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        _input.Text = defaultValue;
        _input.UseSystemPasswordChar = password;

        var lbl = new Label { Text = message, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 8) };
        var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(lbl);
        layout.Controls.Add(_input);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        ResumeLayout(true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _input.Focus();
        _input.SelectAll();
        SystemSounds.Question.Play();
    }
}

/// <summary>
/// Hộp thoại hỏi với nút chữ tiếng Việt (MessageBox hiện Yes/No theo ngôn ngữ Windows). Mặc định luôn trên cùng giữa màn hình
/// (vd chế độ an toàn hỏi có chạy tiếp flow không); <paramref name="cancel"/> thêm nút thứ ba trả về <see cref="DialogResult.Cancel"/>.
/// </summary>
internal sealed class ConfirmForm : BaseForm
{
    public ConfirmForm(string title, string message, string yes, string no, string? cancel = null, bool topMost = true)
    {
        SuspendLayout();
        Text = title + " — ScheduleApp";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = topMost ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        TopMost = topMost;
        ShowInTaskbar = topMost;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var lbl = new Label { Text = message, AutoSize = true, MaximumSize = new Size(440, 0), Font = new Font("Segoe UI", 10F), Margin = new Padding(0, 0, 0, 12) };
        var btnYes = new Button { Text = yes, AutoSize = true, MinimumSize = new Size(110, 0), DialogResult = DialogResult.Yes };
        var btnNo = new Button { Text = no, AutoSize = true, MinimumSize = new Size(110, 0), DialogResult = DialogResult.No };
        AcceptButton = btnYes;
        CancelButton = btnNo;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        if (cancel != null)
        {
            var btnCancel = new Button { Text = cancel, AutoSize = true, MinimumSize = new Size(110, 0), DialogResult = DialogResult.Cancel };
            CancelButton = btnCancel;
            buttons.Controls.Add(btnCancel);
        }
        buttons.Controls.AddRange([btnNo, btnYes]);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(lbl);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        ResumeLayout(true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        SystemSounds.Exclamation.Play();
    }
}

/// <summary>Thanh gỡ lỗi luôn trên cùng: hiện bước sắp chạy, giá trị biến và các nút Bước tiếp / Chạy tiếp / Dừng.</summary>
internal sealed class DebugToolbar : BaseForm
{
    private readonly TaskCompletionSource<DebugCommand> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<DebugCommand> Result => _result.Task;

    public DebugToolbar(string jobName, string stepText, string reason, IReadOnlyDictionary<string, string> variables)
    {
        SuspendLayout();
        Text = $"Gỡ lỗi — {jobName}";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(620, 300);
        MinimumSize = new Size(480, 200);
        Padding = new Padding(8);
        KeyPreview = true;

        var lblReason = new Label { Text = $"⏸ {reason} — sắp chạy:", AutoSize = true, ForeColor = Color.FromArgb(196, 43, 28), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) };
        var lblStep = new Label { Text = stepText, AutoSize = true, MaximumSize = new Size(580, 0), Font = new Font("Segoe UI", 10F), Margin = new Padding(3, 4, 3, 6) };

        var btnStep = new Button { Text = "⏭ Bước tiếp (F10)", AutoSize = true };
        var btnContinue = new Button { Text = "▶ Chạy tiếp (F5)", AutoSize = true };
        var btnStop = new Button { Text = "■ Dừng (Shift+F5)", AutoSize = true, ForeColor = Color.FromArgb(196, 43, 28) };
        btnStep.Click += (_, _) => Finish(DebugCommand.Step);
        btnContinue.Click += (_, _) => Finish(DebugCommand.Continue);
        btnStop.Click += (_, _) => Finish(DebugCommand.Stop);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([btnStep, btnContinue, btnStop]);

        var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        list.Columns.Add("Biến", 170);
        list.Columns.Add("Giá trị", 380);
        foreach (var (k, v) in variables.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            list.Items.Add(new ListViewItem([k, Log.Redact(v).Replace("\r", "").Replace("\n", " ⏎ ")]));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(lblReason);
        layout.Controls.Add(lblStep);
        layout.Controls.Add(buttons);
        layout.Controls.Add(list);
        Controls.Add(layout);
        ResumeLayout(true);
        AcceptButton = btnStep;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(area.Right - Width - 12, area.Top + 12);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.F10) Finish(DebugCommand.Step);
        else if (e.KeyCode == Keys.F5 && e.Shift) Finish(DebugCommand.Stop);
        else if (e.KeyCode == Keys.F5) Finish(DebugCommand.Continue);
    }

    /// <summary>Chọn lệnh như bấm nút (khung trạng thái flow cũng gọi được).</summary>
    public void Finish(DebugCommand cmd)
    {
        _result.TrySetResult(cmd);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _result.TrySetResult(DebugCommand.Stop);
        base.OnFormClosed(e);
    }
}
