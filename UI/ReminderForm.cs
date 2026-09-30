using System.Media;

namespace ScheduleApp.UI;

/// <summary>Cửa sổ nhắc nhở hiện ở góc phải dưới màn hình, luôn nằm trên cùng.</summary>
internal sealed class ReminderForm : BaseForm
{
    private readonly bool _requireConfirm;

    public ReminderForm(string title, string message, bool requireConfirm)
    {
        _requireConfirm = requireConfirm;

        SuspendLayout();
        Text = "Nhắc nhở — ScheduleApp";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        Padding = new Padding(16, 12, 16, 12);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill
        };

        var lblTitle = new Label
        {
            Text = "⏰  " + title,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 84, 153),
            Margin = new Padding(0, 0, 0, 8)
        };
        var lblMessage = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            MinimumSize = new Size(320, 0),
            Font = new Font("Segoe UI", 10F),
            Margin = new Padding(0, 0, 0, 12)
        };
        var lblTime = new Label
        {
            Text = DateTime.Now.ToString("HH:mm:ss dd/MM/yyyy") + (requireConfirm ? "  •  Flow đang tạm dừng chờ bạn xác nhận" : ""),
            AutoSize = true,
            ForeColor = UiText.Muted,
            Margin = new Padding(0, 0, 0, 10)
        };
        var btnOk = new Button
        {
            Text = requireConfirm ? "Đã hiểu — tiếp tục flow" : "Đã hiểu",
            AutoSize = true,
            Padding = new Padding(10, 3, 10, 3),
            Anchor = AnchorStyles.Right
        };
        btnOk.Click += (_, _) => Close();
        AcceptButton = btnOk;

        layout.Controls.Add(lblTitle);
        if (!string.IsNullOrWhiteSpace(message)) layout.Controls.Add(lblMessage);
        layout.Controls.Add(lblTime);
        layout.Controls.Add(btnOk);
        Controls.Add(layout);
        ResumeLayout(true);
    }

    protected override bool ShowWithoutActivation => !_requireConfirm;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        int stackOffset = Application.OpenForms.OfType<ReminderForm>().Count(f => f != this && f.Visible) * 30;
        Location = new Point(area.Right - Width - 12 - stackOffset, area.Bottom - Height - 12 - stackOffset);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SystemSounds.Exclamation.Play();
    }
}
