using ScheduleApp.Models;

namespace ScheduleApp.UI;

/// <summary>
/// Hộp chọn thao tác nổi (như bảng "nodes" của n8n): ô tìm kiếm không dấu + danh sách theo nhóm.
/// Mở khi bấm "+" trên dây nối / cuối flow hoặc phím Tab; bấm ra ngoài hay Esc để đóng.
/// </summary>
internal sealed class NodePicker : Form
{
    private readonly TextBox _search;
    private readonly StepToolbox _list;
    private bool _picked;

    /// <summary>Người dùng chọn một thao tác (gọi sau khi hộp đã đóng).</summary>
    public event Action<StepType>? Picked;

    public NodePicker(Control owner, string title)
    {
        int S(int v) => owner.LogicalToDeviceUnits(v);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Font = owner.Font;
        BackColor = Color.FromArgb(200, 204, 212);
        Padding = new Padding(1);
        Size = new Size(S(310), S(440));
        Text = title;                         // tên cửa sổ cho trình đọc màn hình / UI Automation (không có viền nên không hiện)

        var header = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = S(34),
            Padding = new Padding(S(10), 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.White,
            Font = new Font(owner.Font, FontStyle.Bold)
        };
        _search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Tìm thao tác…  (vd: click, excel, nhắc, nếu)", AccessibleName = "Tìm thao tác" };
        var searchHost = new Panel { Dock = DockStyle.Top, Height = _search.PreferredHeight + S(12), Padding = new Padding(S(8), S(4), S(8), S(8)), BackColor = Color.White };
        searchHost.Controls.Add(_search);
        _list = new StepToolbox { Dock = DockStyle.Fill, SingleClickActivates = true };
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        body.Controls.Add(_list);
        body.Controls.Add(searchHost);
        body.Controls.Add(header);
        Controls.Add(body);

        _search.TextChanged += (_, _) => _list.SetFilter(_search.Text);
        _search.KeyDown += (_, e) =>
        {
            // ↑ ↓ đổi mục đang chọn ngay trong ô tìm; Enter thêm mục đang chọn (mặc định là mục khớp nhất).
            if (e.KeyCode is Keys.Down or Keys.Up)
            {
                _list.MoveSelection(e.KeyCode == Keys.Down ? 1 : -1);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter && (_list.SelectedType ?? _list.VisibleTypes.FirstOrDefault()) is { } type && _list.VisibleTypes.Count > 0)
            {
                e.Handled = e.SuppressKeyPress = true;
                Pick(type);
            }
        };
        _list.ItemActivated += Pick;
    }

    /// <summary>Lọc trước (dùng trong kiểm thử).</summary>
    internal StepToolbox List => _list;

    internal TextBox Search => _search;

    private void Pick(StepType type)
    {
        if (_picked) return;
        _picked = true;
        Close();
        Picked?.Invoke(type);
    }

    /// <summary>Hiện hộp tại điểm màn hình, giữ trong vùng làm việc của màn hình đó.</summary>
    public void ShowAt(Control owner, Point screen)
    {
        var area = Screen.FromPoint(screen).WorkingArea;
        int x = Math.Clamp(screen.X, area.Left, Math.Max(area.Left, area.Right - Width));
        int y = Math.Clamp(screen.Y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
        Show(owner.FindForm());
        Activate();
        _search.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Bấm ra ngoài → đóng (form hiện bằng Show nên Close cũng giải phóng luôn).
        if (!_picked && Visible) Close();
    }
}
