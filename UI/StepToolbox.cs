using System.Drawing.Drawing2D;
using ScheduleApp.Models;

namespace ScheduleApp.UI;

/// <summary>Hộp công cụ: danh sách loại thao tác theo nhóm, kéo vào <see cref="FlowDesigner"/> để thêm bước.</summary>
internal sealed class StepToolbox : ListBox
{
    private int _pressIndex = -1;
    private Point _pressPoint;
    private int _hover = -1;
    private Font _iconFont;
    private Font _headerFont;

    /// <summary>Người dùng nhấp đúp một loại thao tác (thêm vào cuối flow).</summary>
    public event Action<StepType>? ItemActivated;

    public StepToolbox()
    {
        DrawMode = DrawMode.OwnerDrawVariable;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        BackColor = Color.White;
        _iconFont = StepVisuals.CreateIconFont(Font, 2f);
        _headerFont = new Font(Font.FontFamily, Font.Size - 0.5f, FontStyle.Bold);
        SetFilter("");
    }

    /// <summary>Bấm một lần là chọn (dùng trong hộp chọn bước khi bấm "+" trên sơ đồ).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SingleClickActivates { get; set; }

    /// <summary>Chỉ hiện các thao tác khớp từ khóa (không phân biệt dấu); nhóm không còn thao tác nào thì ẩn.</summary>
    public void SetFilter(string query)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var (name, types) in StepVisuals.Categories)
        {
            var match = types.Where(t => StepVisuals.Matches(t, name, query)).ToList();
            if (match.Count == 0) continue;
            Items.Add(name);
            foreach (var t in match) Items.Add(t);
        }
        EndUpdate();
        _hover = -1;
    }

    /// <summary>Các thao tác đang hiện (theo thứ tự).</summary>
    public IReadOnlyList<StepType> VisibleTypes => Items.OfType<StepType>().ToList();

    /// <summary>Chọn thao tác đầu tiên đang hiện (khi bấm ↓ từ ô tìm kiếm).</summary>
    public void SelectFirst()
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i] is StepType)
            {
                SelectedIndex = i;
                return;
            }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && SelectedIndex >= 0 && Items[SelectedIndex] is StepType type)
        {
            e.Handled = e.SuppressKeyPress = true;
            ItemActivated?.Invoke(type);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!SingleClickActivates || e.Button != MouseButtons.Left) return;
        int i = IndexFromPoint(e.Location);
        if (i >= 0 && Items[i] is StepType type) ItemActivated?.Invoke(type);
    }

    private int S(int v) => LogicalToDeviceUnits(v);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _iconFont.Dispose();
        _headerFont.Dispose();
        _iconFont = StepVisuals.CreateIconFont(Font, 2f);
        _headerFont = new Font(Font.FontFamily, Font.Size - 0.5f, FontStyle.Bold);
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        base.OnMeasureItem(e);
        e.ItemHeight = S(Items[e.Index] is string ? (e.Index == 0 ? 28 : 36) : 40);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var g = e.Graphics;
        var b = e.Bounds;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, b);

        if (Items[e.Index] is string header)
        {
            var rect = new Rectangle(b.X + S(10), b.Bottom - S(24), b.Width - S(12), S(22));
            TextRenderer.DrawText(g, header.ToUpperInvariant(), _headerFont, rect, Color.FromArgb(110, 110, 110),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        var type = (StepType)Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        if (selected || e.Index == _hover)
        {
            using var path = StepVisuals.RoundRect(Rectangle.Inflate(b, -S(4), -S(2)), S(6));
            using var hb = new SolidBrush(selected ? StepVisuals.Tint(StepVisuals.Accent(type), 0.88f) : Color.FromArgb(242, 242, 242));
            g.FillPath(hb, path);
        }

        var circle = new Rectangle(b.X + S(10), b.Y + (b.Height - S(28)) / 2, S(28), S(28));
        StepVisuals.DrawIcon(g, type, circle, _iconFont, true);
        var textRect = new Rectangle(circle.Right + S(8), b.Y, b.Right - circle.Right - S(10), b.Height);
        TextRenderer.DrawText(g, ActionStep.TypeNames[type], Font, textRect, Color.FromArgb(32, 32, 32),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        // Tiêu đề nhóm không chọn được.
        if (SelectedIndex >= 0 && Items[SelectedIndex] is string)
        {
            SelectedIndex = -1;
            return;
        }
        base.OnSelectedIndexChanged(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = IndexFromPoint(e.Location);
        _pressIndex = e.Button == MouseButtons.Left && i >= 0 && Items[i] is StepType ? i : -1;
        _pressPoint = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_pressIndex >= 0 && e.Button == MouseButtons.Left)
        {
            var dragBox = new Rectangle(
                _pressPoint.X - SystemInformation.DragSize.Width / 2,
                _pressPoint.Y - SystemInformation.DragSize.Height / 2,
                SystemInformation.DragSize.Width, SystemInformation.DragSize.Height);
            if (!dragBox.Contains(e.Location))
            {
                var type = (StepType)Items[_pressIndex];
                _pressIndex = -1;
                var data = new DataObject();
                data.SetData(StepVisuals.StepTypeFormat, type.ToString());
                DoDragDrop(data, DragDropEffects.Copy);
                return;
            }
        }

        int hover = IndexFromPoint(e.Location);
        if (hover >= 0 && Items[hover] is string) hover = -1;
        if (hover != _hover)
        {
            InvalidateItem(_hover);
            _hover = hover;
            InvalidateItem(_hover);
            Cursor = hover >= 0 ? Cursors.SizeAll : Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressIndex = -1;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateItem(_hover);
        _hover = -1;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = IndexFromPoint(e.Location);
        if (!SingleClickActivates && i >= 0 && Items[i] is StepType type) ItemActivated?.Invoke(type);
    }

    private void InvalidateItem(int index)
    {
        if (index >= 0 && index < Items.Count) Invalidate(GetItemRectangle(index));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconFont.Dispose();
            _headerFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
