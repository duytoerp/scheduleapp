namespace ScheduleApp.UI;

/// <summary>
/// Duyệt công việc đến từ nguồn khác (Telegram, file nhập): lời giải thích + tóm tắt đầy đủ từng bước (chỉ đọc) + nút Duyệt / Để sau.
/// Duyệt = <see cref="DialogResult.OK"/>.
/// </summary>
internal sealed class ApprovalForm : BaseForm
{
    public ApprovalForm(string title, string intro, string summary, string approveText, string laterText = "Để sau")
    {
        SuspendLayout();
        Text = title + " — ScheduleApp";
        Size = new Size(860, 620);
        MinimumSize = new Size(560, 380);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(12);

        var lbl = new Label
        {
            Text = intro,
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Font = new Font("Segoe UI", 10F),
            ForeColor = Theme.Warning,
            Padding = new Padding(0, 0, 0, 8)
        };
        var text = new TextBox
        {
            Text = summary,
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = true,
            BackColor = Color.White,
            Font = new Font("Consolas", 9.5F)
        };
        var btnApprove = new Button { Text = approveText, AutoSize = true, MinimumSize = new Size(130, 0), DialogResult = DialogResult.OK };
        var btnLater = new Button { Text = laterText, AutoSize = true, MinimumSize = new Size(110, 0), DialogResult = DialogResult.Cancel };
        // Không đặt AcceptButton: duyệt phải là một cú bấm có chủ ý, không phải Enter vô tình.
        CancelButton = btnLater;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([btnLater, btnApprove]);

        Controls.Add(text);
        Controls.Add(lbl);
        Controls.Add(buttons);
        ResumeLayout(true);
        Shown += (_, _) =>
        {
            text.SelectionLength = 0;
            btnLater.Focus();
        };
    }
}
