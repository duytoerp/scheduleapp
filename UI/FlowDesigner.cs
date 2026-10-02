using System.Drawing.Drawing2D;
using System.Text.Json;
using ScheduleApp.Models;

namespace ScheduleApp.UI;

/// <summary>
/// Khung thiết kế flow dạng thẻ: Bắt đầu → các bước → Kết thúc. Khối Nếu/Lặp được thụt lề.
/// Hỗ trợ kéo thả từ hộp công cụ, kéo thẻ để sắp xếp (cả khối), thả file từ Explorer, bàn phím, menu chuột phải,
/// điểm dừng (F9), sao chép/dán (Ctrl+C/V) và tô sáng bước đang chạy.
/// </summary>
internal sealed class FlowDesigner : ScrollableControl
{
    private const TextFormatFlags TextFlags =
        TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

    private const string ClipboardFormat = "ScheduleApp.Steps";

    private static readonly Color StartColor = Color.FromArgb(16, 124, 16);
    private static readonly Color EndColor = Color.FromArgb(120, 120, 120);
    private static readonly Color ConnectorColor = Color.FromArgb(160, 166, 176);
    private static readonly Color DropColor = Color.FromArgb(0, 120, 212);
    private static readonly Color RunColor = Color.FromArgb(0, 150, 255);
    private static readonly Color ErrorColor = Color.FromArgb(200, 40, 30);
    private static readonly Color BreakpointColor = Color.FromArgb(220, 40, 40);

    private List<ActionStep> _steps = [];
    private FlowStructure _structure = FlowStructure.Build([]);
    private int[] _tops = [];
    private int _selected = -1;
    private int _hover = -1;
    private int _dropIndex = -1;
    private int _pressIndex = -1;
    private int _running = -1;
    private int _failed = -1;
    private Point _pressPoint;

    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _miToggle;
    private readonly ToolStripMenuItem _miBreakpoint;
    private readonly ToolStripMenuItem _miRunFrom;
    private readonly ToolTip _tip = new();
    private readonly Dictionary<ActionStep, (string Data, Bitmap Image)> _thumbs = [];
    private Font _titleFont = null!;
    private Font _iconFont = null!;
    private Font _pillFont = null!;

    public event EventHandler? SelectionChanged;
    public event EventHandler? StepsChanged;

    /// <summary>Yêu cầu mở trình soạn cho bước tại vị trí này.</summary>
    public event Action<int>? EditRequested;

    /// <summary>Người dùng thả một loại thao tác mới vào vị trí này.</summary>
    public event Action<StepType, int>? AddRequested;

    /// <summary>Yêu cầu chạy thử từ bước này.</summary>
    public event Action<int>? RunFromRequested;

    public FlowDesigner()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoScroll = true;
        AllowDrop = true;
        TabStop = true;
        BackColor = Color.FromArgb(243, 245, 249);
        CreateFonts();

        _menu.Items.Add("Sửa…  (Enter)", null, (_, _) => { if (_selected >= 0) EditRequested?.Invoke(_selected); });
        _menu.Items.Add("Nhân bản", null, (_, _) => DuplicateSelected());
        _miToggle = new ToolStripMenuItem("Tắt bước", null, (_, _) => ToggleSelected());
        _menu.Items.Add(_miToggle);
        _miBreakpoint = new ToolStripMenuItem("Điểm dừng  (F9)", null, (_, _) => ToggleBreakpoint());
        _menu.Items.Add(_miBreakpoint);
        _miRunFrom = new ToolStripMenuItem("▶ Chạy thử từ bước này", null, (_, _) => { if (_selected >= 0) RunFromRequested?.Invoke(_selected); });
        _menu.Items.Add(_miRunFrom);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Sao chép  (Ctrl+C)", null, (_, _) => CopySelected());
        _menu.Items.Add("Dán sau bước này  (Ctrl+V)", null, (_, _) => Paste());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Lên  (Ctrl+↑)", null, (_, _) => MoveSelected(-1));
        _menu.Items.Add("Xuống  (Ctrl+↓)", null, (_, _) => MoveSelected(1));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Xóa  (Delete)", null, (_, _) => DeleteSelected());
    }

    // ───────────────────────────── API ─────────────────────────────

    public int SelectedIndex => _selected;

    public int StepCount => _steps.Count;

    public FlowStructure Structure => _structure;

    /// <summary>Gắn danh sách bước (sửa trực tiếp trên danh sách này).</summary>
    public void SetSteps(List<ActionStep> steps)
    {
        _steps = steps;
        _selected = Math.Min(_selected, steps.Count - 1);
        RefreshView();
    }

    public void RefreshView()
    {
        PruneThumbnails();
        _structure = FlowStructure.Build(_steps);
        Relayout();
        Invalidate();
    }

    /// <summary>Tô sáng bước đang chạy (-1 = bỏ tô).</summary>
    public void SetRunning(int index)
    {
        if (_running == index) return;
        _running = index;
        if (index >= 0)
        {
            _failed = -1;
            if (index < _steps.Count) EnsureVisible(index);
        }
        Invalidate();
    }

    /// <summary>Đánh dấu bước lỗi sau khi chạy thử (-1 = bỏ).</summary>
    public void SetFailed(int index)
    {
        _failed = index;
        if (index >= 0 && index < _steps.Count) SelectStep(index);
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

    /// <summary>Phạm vi [đầu, cuối] của khối bắt đầu tại <paramref name="i"/> (chỉ một bước nếu không phải đầu khối).</summary>
    private (int Start, int End) BlockRange(int i)
    {
        if (i >= 0 && i < _steps.Count && _steps[i].Type is StepType.If or StepType.Loop && _structure.Match[i] > i)
            return (i, _structure.Match[i]);
        return (i, i);
    }

    /// <summary>Xóa bước đang chọn. Xóa "Nếu"/"Lặp" thì xóa luôn "Không thì"/"Hết…" tương ứng (giữ các bước bên trong).</summary>
    public void DeleteSelected()
    {
        if (_selected < 0) return;
        int i = _selected;
        var remove = new List<int> { i };
        if (_steps[i].Type is StepType.If or StepType.Loop && _structure.Match[i] > i)
        {
            remove.Add(_structure.Match[i]);
            if (_structure.ElseOf[i] > i) remove.Add(_structure.ElseOf[i]);
        }
        foreach (var r in remove.OrderDescending()) _steps.RemoveAt(r);
        _selected = -1;
        Changed();
        SelectStep(Math.Min(i, _steps.Count - 1));
    }

    /// <summary>Nhân bản bước (hoặc cả khối Nếu/Lặp) đang chọn.</summary>
    public void DuplicateSelected()
    {
        if (_selected < 0) return;
        var (start, end) = BlockRange(_selected);
        var copies = _steps.GetRange(start, end - start + 1).Select(s => s.Clone()).ToList();
        _steps.InsertRange(end + 1, copies);
        Changed();
        SelectStep(end + 1);
    }

    public void ToggleSelected()
    {
        if (_selected < 0) return;
        _steps[_selected].Enabled = !_steps[_selected].Enabled;
        Changed();
    }

    public void ToggleBreakpoint()
    {
        if (_selected < 0 || StepVisuals.IsMarker(_steps[_selected].Type)) return;
        _steps[_selected].Breakpoint = !_steps[_selected].Breakpoint;
        Changed();
    }

    /// <summary>Di chuyển bước (hoặc cả khối) lên/xuống một vị trí — đổi chỗ với bước liền kề.</summary>
    public void MoveSelected(int delta)
    {
        if (_selected < 0) return;
        var (start, end) = BlockRange(_selected);
        if (delta < 0 && start > 0)
        {
            var neighbor = _steps[start - 1];
            _steps.RemoveAt(start - 1);
            _steps.Insert(end, neighbor);
            Changed();
            SelectStep(start - 1);
        }
        else if (delta > 0 && end < _steps.Count - 1)
        {
            var neighbor = _steps[end + 1];
            _steps.RemoveAt(end + 1);
            _steps.Insert(start, neighbor);
            Changed();
            SelectStep(start + 1);
        }
    }

    private void MoveStep(int from, int insertAt)
    {
        if (from < 0 || from >= _steps.Count) return;
        var (start, end) = BlockRange(from);
        if (insertAt >= start && insertAt <= end + 1)
        {
            SelectStep(from);
            return;
        }
        var block = _steps.GetRange(start, end - start + 1);
        _steps.RemoveRange(start, block.Count);
        if (insertAt > end) insertAt -= block.Count;
        _steps.InsertRange(insertAt, block);
        Changed();
        SelectStep(insertAt);
    }

    public void CopySelected()
    {
        if (_selected < 0) return;
        var (start, end) = BlockRange(_selected);
        var json = JsonSerializer.Serialize(_steps.GetRange(start, end - start + 1), JsonDefaults.Options);
        var data = new DataObject();
        data.SetData(ClipboardFormat, json);
        data.SetText(json);
        try { Clipboard.SetDataObject(data, true); } catch (System.Runtime.InteropServices.ExternalException) { }
    }

    public void Paste()
    {
        string? json = null;
        try
        {
            var data = Clipboard.GetDataObject();
            json = data?.GetData(ClipboardFormat) as string;
            if (json == null && data?.GetData(DataFormats.UnicodeText) is string text && text.TrimStart().StartsWith("[")) json = text;
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
        if (json == null) return;

        List<ActionStep>? steps;
        try { steps = JsonSerializer.Deserialize<List<ActionStep>>(json, JsonDefaults.Options); }
        catch (JsonException) { return; }
        if (steps == null || steps.Count == 0) return;

        int index = _selected >= 0 ? BlockRange(_selected).End + 1 : _steps.Count;
        _steps.InsertRange(index, steps);
        Changed();
        SelectStep(index);
    }

    private void Changed()
    {
        _structure = FlowStructure.Build(_steps);
        Relayout();
        Invalidate();
        StepsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── Bố cục ─────────────────────────────

    private int S(int v) => LogicalToDeviceUnits(v);
    private int CardH => S(64);
    private int MarkerH => S(34);
    private int Gap => S(28);
    private int TopMargin => S(16);
    private int PillH => S(30);
    private int EmptyH => S(84);
    private int Indent => S(30);
    private int Gutter => S(26);
    private int BaseW => Math.Max(S(300), Math.Min(ClientSize.Width - S(40) - Gutter, S(700)));
    private int BaseX => Math.Max(S(20) + Gutter, (ClientSize.Width - BaseW) / 2);
    private int CenterX => BaseX + BaseW / 2;
    private int FirstCardY => TopMargin + PillH + Gap;

    private int CardHeight(int i) => StepVisuals.IsMarker(_steps[i].Type) ? MarkerH : CardH;

    private void Relayout()
    {
        _tops = new int[_steps.Count];
        int y = FirstCardY;
        for (int i = 0; i < _steps.Count; i++)
        {
            _tops[i] = y;
            y += CardHeight(i) + Gap;
        }
        AutoScrollMinSize = new Size(0, ContentHeight);
    }

    private int DepthOf(int i) => i < _structure.Depth.Length ? Math.Min(_structure.Depth[i], 8) : 0;

    private Rectangle CardRect(int i)
    {
        int depth = DepthOf(i);
        int x = BaseX + depth * Indent;
        int w = Math.Max(S(180), BaseW - depth * Indent);
        int top = i < _tops.Length ? _tops[i] : FirstCardY;
        return new Rectangle(x, top, w, CardHeight(i));
    }

    private Rectangle EmptyRect => new(BaseX, FirstCardY, BaseW, EmptyH);
    private Rectangle StartPill => new(CenterX - S(70), TopMargin, S(140), PillH);

    private Rectangle EndPill
    {
        get
        {
            int y = _steps.Count == 0 ? EmptyRect.Bottom + Gap : CardRect(_steps.Count - 1).Bottom + Gap;
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

    /// <summary>Bấm vào vùng lề trái của thẻ (để bật/tắt điểm dừng).</summary>
    private int HitGutter(Point client)
    {
        var p = ToContent(client);
        for (int i = 0; i < _steps.Count; i++)
        {
            var r = CardRect(i);
            if (p.Y >= r.Top && p.Y < r.Bottom && p.X >= r.X - Gutter && p.X < r.X) return i;
        }
        return -1;
    }

    /// <summary>Vị trí chèn (0..Count) tương ứng với tọa độ chuột.</summary>
    private int DropIndexAt(Point client)
    {
        int y = ToContent(client).Y;
        int index = 0;
        for (int i = 0; i < _steps.Count; i++)
            if (y > CardRect(i).Top + CardHeight(i) / 2) index = i + 1;
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
        Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

        var start = StartPill;
        DrawPill(g, start, "Bắt đầu", StartColor);
        var prev = new Point(start.X + start.Width / 2, start.Bottom);

        if (_steps.Count == 0)
        {
            var empty = EmptyRect;
            DrawConnector(g, prev, new Point(empty.X + empty.Width / 2, empty.Top));
            DrawEmptyHint(g, empty, _dropIndex == 0);
            prev = new Point(empty.X + empty.Width / 2, empty.Bottom);
        }

        DrawBlockGuides(g);

        for (int i = 0; i < _steps.Count; i++)
        {
            var r = CardRect(i);
            var top = new Point(r.X + S(36), r.Top);
            DrawConnector(g, prev, top);
            if (_dropIndex == i) DrawDropIndicator(g, r, (prev.Y + r.Top) / 2);
            DrawCard(g, i, r);
            prev = new Point(r.X + S(36), r.Bottom);
        }

        var end = EndPill;
        DrawConnector(g, prev, new Point(end.X + end.Width / 2, end.Top));
        if (_steps.Count > 0 && _dropIndex == _steps.Count) DrawDropIndicator(g, CardRect(_steps.Count - 1), (prev.Y + end.Top) / 2);
        DrawPill(g, end, "Kết thúc", EndColor);
    }

    /// <summary>Thanh dọc nhạt bên trái mỗi khối Nếu/Lặp.</summary>
    private void DrawBlockGuides(Graphics g)
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            if (_steps[i].Type is not (StepType.If or StepType.Loop) || _structure.Match[i] <= i) continue;
            var a = CardRect(i);
            var b = CardRect(_structure.Match[i]);
            var color = StepVisuals.Tint(StepVisuals.Accent(_steps[i].Type), _steps[i].Enabled ? 0.55f : 0.8f);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, a.X + S(6), a.Bottom, S(4), b.Top - a.Bottom);
        }
    }

    private void DrawPill(Graphics g, Rectangle r, string text, Color color)
    {
        using var path = StepVisuals.RoundRect(r, r.Height / 2);
        using (var fill = new SolidBrush(StepVisuals.Tint(color, 0.85f))) g.FillPath(fill, path);
        using (var pen = new Pen(color, S(1))) g.DrawPath(pen, path);
        TextRenderer.DrawText(g, text, _pillFont, r, color, TextFlags | TextFormatFlags.HorizontalCenter);
    }

    /// <summary>Mũi tên nối hai thẻ; nếu lệch ngang (thụt lề) thì vẽ dạng gấp khúc.</summary>
    private void DrawConnector(Graphics g, Point from, Point to)
    {
        int arrow = S(5);
        using var pen = new Pen(ConnectorColor, S(2));
        if (Math.Abs(from.X - to.X) < 2)
        {
            g.DrawLine(pen, from.X, from.Y, to.X, to.Y - arrow);
        }
        else
        {
            int mid = (from.Y + to.Y) / 2;
            g.DrawLines(pen, [from, new Point(from.X, mid), new Point(to.X, mid), new Point(to.X, to.Y - arrow)]);
        }
        using var brush = new SolidBrush(ConnectorColor);
        g.FillPolygon(brush, [new Point(to.X - arrow, to.Y - arrow - 1), new Point(to.X + arrow, to.Y - arrow - 1), to]);
    }

    private void DrawDropIndicator(Graphics g, Rectangle card, int y)
    {
        int left = card.X + S(8), right = card.Right - S(8), dot = S(8);
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
        bool invalid = i < _structure.Invalid.Length && _structure.Invalid[i];
        bool running = i == _running;
        bool failed = i == _failed;
        bool marker = StepVisuals.IsMarker(step.Type);
        var accent = step.Enabled ? StepVisuals.Accent(step.Type) : Color.FromArgb(160, 160, 160);
        int radius = S(8);

        if (running || failed)
        {
            var glow = Rectangle.Inflate(r, S(4), S(4));
            using var glowPath = StepVisuals.RoundRect(glow, radius + S(3));
            using var glowBrush = new SolidBrush(Color.FromArgb(70, failed ? ErrorColor : RunColor));
            g.FillPath(glowBrush, glowPath);
        }

        // Bóng đổ
        var shadowRect = r;
        shadowRect.Offset(0, S(2));
        using (var shadowPath = StepVisuals.RoundRect(shadowRect, radius))
        using (var shadow = new SolidBrush(Color.FromArgb(selected ? 45 : 22, 0, 0, 0)))
            g.FillPath(shadow, shadowPath);

        using var path = StepVisuals.RoundRect(r, radius);
        var bg = !step.Enabled ? Color.FromArgb(246, 246, 246) : marker ? StepVisuals.Tint(accent, 0.92f) : Color.White;
        using (var fill = new SolidBrush(bg)) g.FillPath(fill, path);

        // Dải màu bên trái theo nhóm thao tác
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        using (var strip = new SolidBrush(invalid ? ErrorColor : accent)) g.FillRectangle(strip, r.X, r.Y, S(6), r.Height);
        g.Restore(state);

        var borderColor = invalid ? ErrorColor : running ? RunColor : failed ? ErrorColor
            : selected ? accent : hover ? Color.FromArgb(150, 150, 150) : Color.FromArgb(218, 220, 224);
        using (var pen = new Pen(borderColor, selected || invalid || running || failed ? S(2) : 1)) g.DrawPath(pen, path);

        // Điểm dừng
        if (step.Breakpoint)
        {
            int d = S(14);
            using var bp = new SolidBrush(BreakpointColor);
            g.FillEllipse(bp, r.X - Gutter + (Gutter - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        }

        var title = $"{i + 1}. {ActionStep.TypeNames[step.Type]}" + (step.Enabled ? "" : "   (đã tắt)");
        if (marker)
        {
            TextRenderer.DrawText(g, $"{title}   —   {step.Describe()}", Font, new Rectangle(r.X + S(16), r.Y, r.Width - S(24), r.Height),
                invalid ? ErrorColor : Color.FromArgb(70, 70, 70), TextFlags);
            return;
        }

        var circle = new Rectangle(r.X + S(18), r.Y + (r.Height - S(36)) / 2, S(36), S(36));
        StepVisuals.DrawIcon(g, step.Type, circle, _iconFont, step.Enabled, TextFormatFlags.PreserveGraphicsTranslateTransform);

        int textX = circle.Right + S(12);
        int textW = r.Right - textX - S(36);

        // Hình mẫu (click theo hình, click đã ghi…) thu nhỏ ở bên phải — nhìn là biết bước sẽ click vào đâu.
        if (Thumbnail(step) is { } thumb)
        {
            double k = Math.Min(Math.Min((double)S(100) / thumb.Width, (double)(r.Height - S(16)) / thumb.Height), S(100) / 100.0);
            int w = Math.Max(1, (int)(thumb.Width * k)), h = Math.Max(1, (int)(thumb.Height * k));
            var box = new Rectangle(r.Right - S(36) - w, r.Y + (r.Height - h) / 2, w, h);
            var mode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(thumb, box);
            g.InterpolationMode = mode;
            using (var pen = new Pen(Color.FromArgb(200, 204, 210))) g.DrawRectangle(pen, box.X - 1, box.Y - 1, box.Width + 1, box.Height + 1);
            textW -= w + S(12);
        }
        var extras = new List<string>();
        if (step.Retries > 0) extras.Add($"⟳{step.Retries}");
        if (step.OnError == ErrorAction.Continue) extras.Add("bỏ qua lỗi");
        if (step.OnError == ErrorAction.GotoLabel) extras.Add($"lỗi → {step.ErrorLabel}");
        if (invalid) extras.Add("⚠ lỗi cấu trúc");
        if (extras.Count > 0) title += "   [" + string.Join(" · ", extras) + "]";

        TextRenderer.DrawText(g, title, _titleFont, new Rectangle(textX, r.Y + S(9), textW, S(22)),
            invalid ? ErrorColor : step.Enabled ? Color.FromArgb(32, 32, 32) : Color.FromArgb(130, 130, 130), TextFlags);
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

        int gutter = HitGutter(e.Location);
        if (e.Button == MouseButtons.Left && gutter >= 0)
        {
            SelectStep(gutter);
            ToggleBreakpoint();
            return;
        }

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
            _miBreakpoint.Checked = _steps[i].Breakpoint;
            _miBreakpoint.Enabled = !StepVisuals.IsMarker(_steps[i].Type);
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
            Cursor = hover >= 0 ? Cursors.Hand : HitGutter(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
            var tip = hover >= 0 && hover < _structure.Invalid.Length && _structure.Invalid[hover]
                ? string.Join("\n", _structure.Errors.Where(x => x.StartsWith($"Bước {hover + 1}:")))
                : "";
            _tip.SetToolTip(this, tip);
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
            case Keys.F9: ToggleBreakpoint(); break;
            case Keys.C when e.Control: CopySelected(); break;
            case Keys.V when e.Control: Paste(); break;
            case Keys.D when e.Control: DuplicateSelected(); break;
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

    /// <summary>Hình mẫu của bước đã giải mã (giữ lại theo chuỗi ImageData để không giải mã PNG mỗi lần vẽ).</summary>
    private Bitmap? Thumbnail(ActionStep step)
    {
        if (string.IsNullOrEmpty(step.ImageData) || StepVisuals.IsMarker(step.Type)) return null;
        if (_thumbs.TryGetValue(step, out var cached) && ReferenceEquals(cached.Data, step.ImageData)) return cached.Image;
        cached.Image?.Dispose();
        _thumbs.Remove(step);
        try
        {
            var image = Vision.ScreenCapture.FromBase64Png(step.ImageData);
            _thumbs[step] = (step.ImageData, image);
            return image;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }

    private void PruneThumbnails()
    {
        foreach (var step in _thumbs.Keys.Where(s => !_steps.Contains(s)).ToList())
        {
            _thumbs[step].Image.Dispose();
            _thumbs.Remove(step);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var (_, image) in _thumbs.Values) image.Dispose();
            _thumbs.Clear();
            _menu.Dispose();
            _tip.Dispose();
            _titleFont.Dispose();
            _iconFont.Dispose();
            _pillFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
