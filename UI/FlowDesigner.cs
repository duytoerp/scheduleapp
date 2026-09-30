using System.Drawing.Drawing2D;
using ScheduleApp.Models;

namespace ScheduleApp.UI;

/// <summary>
/// Khung thiết kế flow dạng thẻ: Bắt đầu → các bước → Kết thúc.
/// Hỗ trợ kéo thả từ hộp công cụ, kéo thẻ để sắp xếp, thả file từ Explorer, bàn phím và menu chuột phải.
/// </summary>
internal sealed class FlowDesigner : ScrollableControl
{
    private const TextFormatFlags TextFlags =
        TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

    private static readonly Color StartColor = Color.FromArgb(16, 124, 16);
    private static readonly Color EndColor = Color.FromArgb(120, 120, 120);
    private static readonly Color ConnectorColor = Color.FromArgb(160, 166, 176);
    private static readonly Color DropColor = Color.FromArgb(0, 120, 212);

    private List<ActionStep> _steps = [];
    private int _selected = -1;
    private int _hover = -1;
    private int _dropIndex = -1;
    private int _pressIndex = -1;
    private Point _pressPoint;

    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _miToggle;
    private Font _titleFont = null!;
    private Font _iconFont = null!;
    private Font _pillFont = null!;

    public event EventHandler? SelectionChanged;
    public event EventHandler? StepsChanged;

    /// <summary>Yêu cầu mở trình soạn cho bước tại vị trí này.</summary>
    public event Action<int>? EditRequested;

    /// <summary>Người dùng thả một loại thao tác mới vào vị trí này.</summary>
    public event Action<StepType, int>? AddRequested;

    public FlowDesigner()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoScroll = true;
        AllowDrop = true;
        TabStop = true;
        BackColor = Color.FromArgb(243, 245, 249);
        CreateFonts();

        _menu.Items.Add("Sửa…", null, (_, _) => { if (_selected >= 0) EditRequested?.Invoke(_selected); });
        _menu.Items.Add("Nhân bản", null, (_, _) => DuplicateSelected());
        _miToggle = new ToolStripMenuItem("Tắt bước", null, (_, _) => ToggleSelected());
        _menu.Items.Add(_miToggle);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Lên  (Ctrl+↑)", null, (_, _) => MoveSelected(-1));
        _menu.Items.Add("Xuống  (Ctrl+↓)", null, (_, _) => MoveSelected(1));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Xóa  (Delete)", null, (_, _) => DeleteSelected());
    }

    // ───────────────────────────── API ─────────────────────────────

    public int SelectedIndex => _selected;

    public int StepCount => _steps.Count;

    /// <summary>Gắn danh sách bước (sửa trực tiếp trên danh sách này).</summary>
    public void SetSteps(List<ActionStep> steps)
    {
        _steps = steps;
        _selected = Math.Min(_selected, steps.Count - 1);
        RefreshView();
    }

    public void RefreshView()
    {
        AutoScrollMinSize = new Size(0, ContentHeight);
        Invalidate();
    }

    public void SelectStep(int index)
    {
        int newIndex = index >= 0 && index < _steps.Count ? index : -1;
        if (newIndex != _selected)
        {
            _selected = newIndex;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        if (_selected >= 0) EnsureVisible(_selected);
        Invalidate();
    }

    public void InsertStep(int index, ActionStep step)
    {
        index = Math.Clamp(index, 0, _steps.Count);
        _steps.Insert(index, step);
        Changed();
        SelectStep(index);
    }

    public void ReplaceStep(int index, ActionStep step)
    {
        if (index < 0 || index >= _steps.Count) return;
        _steps[index] = step;
        Changed();
        SelectStep(index);
    }

    public void DeleteSelected()
    {
        if (_selected < 0) return;
        int i = _selected;
        _steps.RemoveAt(i);
        _selected = -1;
        Changed();
        SelectStep(Math.Min(i, _steps.Count - 1));
    }

    public void DuplicateSelected()
    {
        if (_selected < 0) return;
        InsertStep(_selected + 1, _steps[_selected].Clone());
    }

    public void ToggleSelected()
    {
        if (_selected < 0) return;
        _steps[_selected].Enabled = !_steps[_selected].Enabled;
        Changed();
    }

    public void MoveSelected(int delta)
    {
        int i = _selected, j = i + delta;
        if (i < 0 || j < 0 || j >= _steps.Count) return;
        (_steps[i], _steps[j]) = (_steps[j], _steps[i]);
        Changed();
        SelectStep(j);
    }

    private void MoveStep(int from, int insertAt)
    {
        if (from < 0 || from >= _steps.Count) return;
        if (insertAt == from || insertAt == from + 1)
        {
            SelectStep(from);
            return;
        }
        var step = _steps[from];
        _steps.RemoveAt(from);
        if (insertAt > from) insertAt--;
        _steps.Insert(insertAt, step);
        Changed();
        SelectStep(insertAt);
    }

    private void Changed()
    {
        AutoScrollMinSize = new Size(0, ContentHeight);
        Invalidate();
        StepsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── Bố cục ─────────────────────────────

    private int S(int v) => LogicalToDeviceUnits(v);
    private int CardH => S(64);
    private int Gap => S(32);
    private int TopMargin => S(16);
    private int PillH => S(30);
    private int EmptyH => S(84);
    private int CardW => Math.Max(S(260), Math.Min(ClientSize.Width - S(40), S(660)));
    private int CardX => Math.Max(S(20), (ClientSize.Width - CardW) / 2);
    private int CenterX => CardX + CardW / 2;
    private int FirstCardY => TopMargin + PillH + Gap;

    private Rectangle CardRect(int i) => new(CardX, FirstCardY + i * (CardH + Gap), CardW, CardH);
    private Rectangle EmptyRect => new(CardX, FirstCardY, CardW, EmptyH);
    private Rectangle StartPill => new(CenterX - S(70), TopMargin, S(140), PillH);

    private Rectangle EndPill
    {
        get
        {
            int y = _steps.Count == 0 ? EmptyRect.Bottom + Gap : FirstCardY + _steps.Count * (CardH + Gap);
            return new Rectangle(CenterX - S(70), y, S(140), PillH);
        }
    }

    private int ContentHeight => EndPill.Bottom + S(24);

    private Point ToContent(Point client) => new(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y);

    private int HitCard(Point client)
    {
        var p = ToContent(client);
        for (int i = 0; i < _steps.Count; i++)
            if (CardRect(i).Contains(p)) return i;
        return -1;
    }

    /// <summary>Vị trí chèn (0..Count) tương ứng với tọa độ chuột.</summary>
    private int DropIndexAt(Point client)
    {
        int y = ToContent(client).Y;
        int index = 0;
        for (int i = 0; i < _steps.Count; i++)
            if (y > CardRect(i).Top + CardH / 2) index = i + 1;
        return index;
    }

    private void EnsureVisible(int i)
    {
        var r = CardRect(i);
        int top = -AutoScrollPosition.Y;
        if (r.Top - Gap < top) ScrollTo(r.Top - Gap);
        else if (r.Bottom + Gap > top + ClientSize.Height) ScrollTo(r.Bottom + Gap - ClientSize.Height);
    }

    private void ScrollTo(int y)
    {
        AutoScrollPosition = new Point(0, Math.Max(0, y));
        Invalidate();
    }

    // ───────────────────────────── Vẽ ─────────────────────────────

    private void CreateFonts()
    {
        _titleFont?.Dispose();
        _iconFont?.Dispose();
        _pillFont?.Dispose();
        _titleFont = new Font(Font.FontFamily, Font.Size + 0.5f, FontStyle.Bold);
        _iconFont = StepVisuals.CreateIconFont(Font, 4f);
        _pillFont = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        CreateFonts();
        RefreshView();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        AutoScrollMinSize = new Size(0, ContentHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

        var start = StartPill;
        DrawPill(g, start, "Bắt đầu", StartColor);
        int prevBottom = start.Bottom;

        if (_steps.Count == 0)
        {
            var empty = EmptyRect;
            DrawConnector(g, prevBottom, empty.Top);
            DrawEmptyHint(g, empty, _dropIndex == 0);
            prevBottom = empty.Bottom;
        }

        for (int i = 0; i < _steps.Count; i++)
        {
            var r = CardRect(i);
            DrawConnector(g, prevBottom, r.Top);
            if (_dropIndex == i) DrawDropIndicator(g, (prevBottom + r.Top) / 2);
            DrawCard(g, i, r);
            prevBottom = r.Bottom;
        }

        var end = EndPill;
        DrawConnector(g, prevBottom, end.Top);
        if (_steps.Count > 0 && _dropIndex == _steps.Count) DrawDropIndicator(g, (prevBottom + end.Top) / 2);
        DrawPill(g, end, "Kết thúc", EndColor);
    }

    private void DrawPill(Graphics g, Rectangle r, string text, Color color)
    {
        using var path = StepVisuals.RoundRect(r, r.Height / 2);
        using (var fill = new SolidBrush(StepVisuals.Tint(color, 0.85f))) g.FillPath(fill, path);
        using (var pen = new Pen(color, S(1))) g.DrawPath(pen, path);
        TextRenderer.DrawText(g, text, _pillFont, r, color, TextFlags | TextFormatFlags.HorizontalCenter);
    }

    private void DrawConnector(Graphics g, int y1, int y2)
    {
        int x = CenterX;
        int arrow = S(5);
        using var pen = new Pen(ConnectorColor, S(2));
        g.DrawLine(pen, x, y1, x, y2 - arrow);
        using var brush = new SolidBrush(ConnectorColor);
        g.FillPolygon(brush, [new Point(x - arrow, y2 - arrow - 1), new Point(x + arrow, y2 - arrow - 1), new Point(x, y2)]);
    }

    private void DrawDropIndicator(Graphics g, int y)
    {
        int left = CardX + S(8), right = CardX + CardW - S(8), dot = S(8);
        using var pen = new Pen(DropColor, S(3));
        g.DrawLine(pen, left, y, right, y);
        using var brush = new SolidBrush(DropColor);
        g.FillEllipse(brush, left - dot / 2, y - dot / 2, dot, dot);
        g.FillEllipse(brush, right - dot / 2, y - dot / 2, dot, dot);
    }

    private void DrawEmptyHint(Graphics g, Rectangle r, bool active)
    {
        using var path = StepVisuals.RoundRect(r, S(8));
        using (var fill = new SolidBrush(active ? StepVisuals.Tint(DropColor, 0.88f) : Color.FromArgb(250, 251, 253))) g.FillPath(fill, path);
        using (var pen = new Pen(active ? DropColor : Color.FromArgb(170, 176, 186), S(2)) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
        TextRenderer.DrawText(g,
            "Kéo một thao tác từ hộp công cụ bên trái và thả vào đây\n(hoặc kéo file .exe / shortcut từ Explorer vào)",
            Font, r, active ? DropColor : Color.FromArgb(110, 110, 110),
            TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak |
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawCard(Graphics g, int i, Rectangle r)
    {
        var step = _steps[i];
        bool selected = i == _selected;
        bool hover = i == _hover && !selected;
        var accent = step.Enabled ? StepVisuals.Accent(step.Type) : Color.FromArgb(160, 160, 160);
        int radius = S(8);

        // Bóng đổ
        var shadowRect = r;
        shadowRect.Offset(0, S(2));
        using (var shadowPath = StepVisuals.RoundRect(shadowRect, radius))
        using (var shadow = new SolidBrush(Color.FromArgb(selected ? 45 : 22, 0, 0, 0)))
            g.FillPath(shadow, shadowPath);

        using var path = StepVisuals.RoundRect(r, radius);
        using (var fill = new SolidBrush(step.Enabled ? Color.White : Color.FromArgb(246, 246, 246))) g.FillPath(fill, path);

        // Dải màu bên trái theo nhóm thao tác
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        using (var strip = new SolidBrush(accent)) g.FillRectangle(strip, r.X, r.Y, S(6), r.Height);
        g.Restore(state);

        var borderColor = selected ? accent : hover ? Color.FromArgb(150, 150, 150) : Color.FromArgb(218, 220, 224);
        using (var pen = new Pen(borderColor, selected ? S(2) : 1)) g.DrawPath(pen, path);

        var circle = new Rectangle(r.X + S(18), r.Y + (r.Height - S(36)) / 2, S(36), S(36));
        StepVisuals.DrawIcon(g, step.Type, circle, _iconFont, step.Enabled, TextFormatFlags.PreserveGraphicsTranslateTransform);

        int textX = circle.Right + S(12);
        int textW = r.Right - textX - S(36);
        var title = $"{i + 1}. {ActionStep.TypeNames[step.Type]}" + (step.Enabled ? "" : "   (đã tắt)");
        TextRenderer.DrawText(g, title, _titleFont, new Rectangle(textX, r.Y + S(9), textW, S(22)),
            step.Enabled ? Color.FromArgb(32, 32, 32) : Color.FromArgb(130, 130, 130), TextFlags);
        TextRenderer.DrawText(g, step.Describe(), Font, new Rectangle(textX, r.Y + S(33), textW, S(22)),
            Color.FromArgb(96, 96, 96), TextFlags);

        // Tay nắm kéo (6 chấm)
        using var dots = new SolidBrush(Color.FromArgb(hover || selected ? 140 : 190, 140, 140, 140));
        int dot = Math.Max(2, S(3)), gx = r.Right - S(22), gy = r.Y + r.Height / 2 - S(8);
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 2; col++)
                g.FillEllipse(dots, gx + col * S(6), gy + row * S(6), dot, dot);
    }

    // ───────────────────────────── Chuột ─────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = HitCard(e.Location);
        if (e.Button is MouseButtons.Left or MouseButtons.Right) SelectStep(i);

        if (e.Button == MouseButtons.Left && i >= 0)
        {
            _pressIndex = i;
            _pressPoint = e.Location;
        }
        else if (e.Button == MouseButtons.Right && i >= 0)
        {
            _miToggle.Text = _steps[i].Enabled ? "Tắt bước  (Space)" : "Bật bước  (Space)";
            _menu.Show(this, e.Location);
        }
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
                int from = _pressIndex;
                _pressIndex = -1;
                var data = new DataObject();
                data.SetData(StepVisuals.StepIndexFormat, from.ToString());
                DoDragDrop(data, DragDropEffects.Move);
                _dropIndex = -1;
                Invalidate();
                return;
            }
        }

        int hover = HitCard(e.Location);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
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
        if (_hover != -1)
        {
            _hover = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = HitCard(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0) EditRequested?.Invoke(i);
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    // ───────────────────────────── Kéo thả ─────────────────────────────

    private static DragDropEffects EffectFor(DragEventArgs e)
    {
        var data = e.Data;
        if (data == null) return DragDropEffects.None;
        if (data.GetDataPresent(StepVisuals.StepIndexFormat)) return DragDropEffects.Move;
        if (data.GetDataPresent(StepVisuals.StepTypeFormat) || data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = EffectFor(e);
        UpdateDropIndex(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = EffectFor(e);
        if (e.Effect != DragDropEffects.None)
        {
            // Tự cuộn khi kéo gần mép trên/dưới.
            var client = PointToClient(new Point(e.X, e.Y));
            if (client.Y < S(30)) ScrollTo(-AutoScrollPosition.Y - S(16));
            else if (client.Y > ClientSize.Height - S(30)) ScrollTo(-AutoScrollPosition.Y + S(16));
        }
        UpdateDropIndex(e);
    }

    private void UpdateDropIndex(DragEventArgs e)
    {
        int index = e.Effect == DragDropEffects.None ? -1 : DropIndexAt(PointToClient(new Point(e.X, e.Y)));
        if (index != _dropIndex)
        {
            _dropIndex = index;
            Invalidate();
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dropIndex = -1;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        int index = _dropIndex >= 0 ? _dropIndex : _steps.Count;
        _dropIndex = -1;
        Invalidate();

        var data = e.Data;
        if (data == null) return;

        if (data.GetDataPresent(StepVisuals.StepIndexFormat))
        {
            if (int.TryParse(data.GetData(StepVisuals.StepIndexFormat) as string, out int from)) MoveStep(from, index);
        }
        else if (data.GetDataPresent(StepVisuals.StepTypeFormat))
        {
            if (Enum.TryParse<StepType>(data.GetData(StepVisuals.StepTypeFormat) as string, out var type))
            {
                // Mở trình soạn sau khi thao tác kéo thả kết thúc hẳn.
                BeginInvoke(new MethodInvoker(() => AddRequested?.Invoke(type, index)));
            }
        }
        else if (data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files)
            {
                var step = ActionStep.CreateDefault(StepType.LaunchApp);
                step.Target = file;
                InsertStep(index++, step);
            }
        }
    }

    // ───────────────────────────── Bàn phím ─────────────────────────────

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up when e.Control: MoveSelected(-1); break;
            case Keys.Down when e.Control: MoveSelected(1); break;
            case Keys.Up: SelectStep(_selected <= 0 ? 0 : _selected - 1); break;
            case Keys.Down: SelectStep(Math.Min(_steps.Count - 1, _selected + 1)); break;
            case Keys.Home: SelectStep(0); break;
            case Keys.End: SelectStep(_steps.Count - 1); break;
            case Keys.Delete: DeleteSelected(); break;
            case Keys.Space: ToggleSelected(); break;
            case Keys.Enter:
                if (_selected >= 0) EditRequested?.Invoke(_selected);
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_selected < 0 && _steps.Count > 0) SelectStep(0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menu.Dispose();
            _titleFont.Dispose();
            _iconFont.Dispose();
            _pillFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
