using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Danh sách phiên bản cũ của một công việc: xem các bước của từng bản và chọn bản để khôi phục.</summary>
internal sealed class VersionPickerForm : BaseForm
{
    protected override SizeF ScreenShare => new(0.58f, 0.66f);

    private readonly List<JobVersions.Version> _versions;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Color.White };

    public JobVersions.Version? Selected { get; private set; }

    public VersionPickerForm(List<JobVersions.Version> versions)
    {
        _versions = versions;
        SuspendLayout();
        Text = "Phiên bản cũ";
        Size = new Size(900, 600);
        MinimumSize = new Size(700, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(10);

        _list.Columns.Add("Bản trước lần sửa lúc", 170);
        _list.Columns.Add("Tên", 300);
        _list.Columns.Add("Số bước", 70);
        _list.Columns.Add("Lịch", 260);
        foreach (var v in versions)
            _list.Items.Add(new ListViewItem([v.SavedAt.ToString("HH:mm:ss dd/MM/yyyy"), v.Job.Name, v.Job.Steps.Count.ToString(), v.Job.Schedule.Describe()]) { Tag = v });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_detail);

        var hint = new Label
        {
            Text = "Mỗi lần lưu thay đổi, bản trước đó được giữ lại. Khôi phục sẽ nạp bản cũ vào trình soạn — bấm Lưu để áp dụng, hoặc Hủy để bỏ.",
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = UiText.Muted,
            Padding = new Padding(0, 0, 0, 6)
        };
        var ok = new Button { Text = "Khôi phục bản này", AutoSize = true, MinimumSize = new Size(140, 0) };
        var cancel = new Button { Text = "Đóng", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Choose();
        _list.DoubleClick += (_, _) => Choose();
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);

        Controls.Add(split);
        Controls.Add(buttons);
        Controls.Add(hint);
        ResumeLayout(true);

        _list.SelectedIndexChanged += (_, _) => ShowDetail();
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
    }

    private JobVersions.Version? Current => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as JobVersions.Version : null;

    private void ShowDetail()
    {
        if (Current is not { } v)
        {
            _detail.Text = "";
            return;
        }
        var depth = FlowStructure.Build(v.Job.Steps).Depth;
        var nl = Environment.NewLine;
        _detail.Text = $"{v.Job.Name}  ·  {v.Job.Schedule.Describe()}" + nl + nl +
                       string.Join(nl, v.Job.Steps.Select((s, i) =>
                           $"{i + 1,3}. {new string(' ', Math.Min(depth[i], 6) * 4)}{(s.Enabled ? "" : "(tắt) ")}{ActionStep.TypeNames[s.Type]} — {s.Describe()}"));
    }

    private void Choose()
    {
        if (Current is not { } v) return;
        Selected = v;
        DialogResult = DialogResult.OK;
        Close();
    }
}
