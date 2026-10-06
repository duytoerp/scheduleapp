using ScheduleApp.Models;

namespace ScheduleApp.UI;

/// <summary>
/// Cách nhìn "Danh sách" của flow: mỗi bước một dòng (số thứ tự, biểu tượng, mô tả đầy đủ), bước trong Nếu / Lặp thụt vào,
/// bấm ▾ / ▸ để thu gọn / mở khối. Dùng chung dữ liệu, vùng chọn, trạng thái chạy, menu chuột phải và phím tắt với sơ đồ
/// (<see cref="FlowDesigner"/>) — chuyển qua lại giữa các cách nhìn vẫn giữ bước đang chọn.
/// <para>Chế độ <see cref="Tree"/> ("Cây"): đường nối kiểu cây thư mục, nút ⊞ / ⊟; "Không thì" là nút con của Nếu chứa nhánh sai;
/// ẩn dòng Hết Nếu / Hết lặp (cấu trúc đã thể hiện bằng nhánh cây).</para>
/// </summary>
internal sealed class FlowListView : Panel
{
    private static readonly Color HoverBack = Color.FromArgb(243, 246, 250);
    private static readonly Color RunBack = Color.FromArgb(232, 244, 255);
    private static readonly Color FailBack = Color.FromArgb(253, 231, 233);
    private static readonly Color RunColor = Color.FromArgb(0, 120, 212);
    private static readonly Color DoneColor = Color.FromArgb(34, 154, 68);
    private static readonly Color FailColor = Color.FromArgb(196, 43, 28);
    private static readonly Color GuideColor = Color.FromArgb(222, 226, 232);
    private static readonly Color TreeLineColor = Color.FromArgb(176, 182, 190);
    private static readonly Color Muted = Color.FromArgb(110, 114, 122);
    private static readonly Color Disabled = Color.FromArgb(160, 164, 170);

    private const TextFormatFlags OneLine =
        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

    private readonly FlowDesigner _designer;
    /// <summary>Khối Nếu / Lặp đang thu gọn — nhớ theo chính bước đó (thêm / xóa bước khác không làm lệch).</summary>
    private readonly HashSet<ActionStep> _collapsed = new(ReferenceEqualityComparer.Instance);
    private List<int> _rows = [];
    private int[] _treeDepth = [];
    /// <summary>Chế độ cây, theo từng dòng: bit k = kẻ đường dọc đi qua ở cột k; dòng là con cuối của nút cha.</summary>
    private ulong[] _rowLines = [];
    private bool[] _rowLast = [];
    private bool _tree;
    private int _hoverRow = -1;
    private int _dropRow = -1;
    private bool _dropAfter;

    private Font _text = null!, _bold = null!, _small = null!, _icon = null!;

    public FlowListView(FlowDesigner designer)
    {
        _designer = designer;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White;
        AutoScroll = true;
        TabStop = true;
        AllowDrop = true;
        CreateFonts();
        _designer.ViewStateChanged += (_, _) => Rebuild();
        _designer.StepsChanged += (_, _) => Rebuild();
        _designer.SelectionChanged += (_, _) =>
        {
            RevealSelected();
            Invalidate();
        };
        Rebuild();
    }

    private int S(int v) => LogicalToDeviceUnits(v);

    private int RowHeight => S(34);

    private void CreateFonts()
    {
        _text = new Font("Segoe UI", 9.5F);
        _bold = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        _small = new Font("Segoe UI", 8.25F);
        _icon = StepVisuals.CreateIconFont(FontFamily.GenericSansSerif, 10F);
    }

    // ───────────────────────────── Dòng hiển thị ─────────────────────────────

    /// <summary>Vị trí các bước đang hiện (bỏ phần bên trong khối đã thu gọn).</summary>
    internal IReadOnlyList<int> Rows => _rows;

    /// <summary>Hiện dạng cây thay vì danh sách thụt lề.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Tree
    {
        get => _tree;
        set
        {
            if (_tree == value) return;
            _tree = value;
            Rebuild();
            RevealSelected();
        }
    }

    internal static List<int> VisibleRows(IReadOnlyList<ActionStep> steps, FlowStructure fs, Func<ActionStep, bool> collapsed)
    {
        var rows = new List<int>(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            rows.Add(i);
            if (steps[i].Type is StepType.If or StepType.Loop && collapsed(steps[i]) && fs.Match[i] > i) i = fs.Match[i];
        }
        return rows;
    }

    /// <summary>Số bước bên trong khối (không tính bước đánh dấu Không thì / Hết Nếu / Hết lặp).</summary>
    internal static int InnerCount(IReadOnlyList<ActionStep> steps, FlowStructure fs, int head) =>
        fs.Match[head] > head ? Enumerable.Range(head + 1, fs.Match[head] - head - 1).Count(k => !StepVisuals.IsMarker(steps[k].Type)) : 0;

    /// <summary>"Không thì" ghép đúng với một Nếu (ở dạng cây là nút con của Nếu, chứa nhánh sai).</summary>
    private static bool IsBranch(IReadOnlyList<ActionStep> steps, FlowStructure fs, int i) =>
        steps[i].Type == StepType.Else && !fs.Invalid[i] && fs.Match[i] > i;

    /// <summary>Hết Nếu / Hết lặp đã ghép đúng — dạng cây không hiện (cấu trúc thấy qua nhánh cây).</summary>
    private static bool IsHiddenEnd(IReadOnlyList<ActionStep> steps, FlowStructure fs, int i) =>
        steps[i].Type is StepType.EndIf or StepType.EndLoop && !fs.Invalid[i] && fs.Match[i] >= 0 && fs.Match[i] < i;

    /// <summary>Dòng của dạng cây: bỏ Hết Nếu / Hết lặp, bỏ phần bên trong Nếu / Lặp / Không thì đang thu gọn.</summary>
    internal static List<int> TreeRows(IReadOnlyList<ActionStep> steps, FlowStructure fs, Func<ActionStep, bool> collapsed)
    {
        var rows = new List<int>(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            if (IsHiddenEnd(steps, fs, i)) continue;
            rows.Add(i);
            if (!collapsed(steps[i])) continue;
            if (steps[i].Type is StepType.If or StepType.Loop && fs.Match[i] > i) i = fs.Match[i];
            else if (IsBranch(steps, fs, i)) i = fs.Match[i] - 1;   // tới ngay trước Hết Nếu (dòng ẩn)
        }
        return rows;
    }

    /// <summary>Độ sâu ở dạng cây: như danh sách, thêm một bậc cho "Không thì" (con của Nếu) và mọi bước trong nhánh sai.</summary>
    internal static int[] TreeDepths(IReadOnlyList<ActionStep> steps, FlowStructure fs)
    {
        var depth = new int[steps.Count];
        var extra = new int[steps.Count + 1];
        for (int i = 0; i < steps.Count; i++)
            if (IsBranch(steps, fs, i))
            {
                extra[i + 1]++;
                extra[fs.Match[i]]--;
            }
        int add = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            add += extra[i];
            depth[i] = fs.Depth[i] + add + (IsBranch(steps, fs, i) ? 1 : 0);
        }
        return depth;
    }

    private bool IsCollapsed(ActionStep s) => _collapsed.Contains(s);

    /// <summary>Dòng này thu gọn / mở được: đầu khối Nếu / Lặp, ở dạng cây thêm "Không thì".</summary>
    private bool IsHead(int i)
    {
        var steps = _designer.Steps;
        var fs = _designer.Structure;
        return steps[i].Type is StepType.If or StepType.Loop && fs.Match[i] > i || _tree && IsBranch(steps, fs, i);
    }

    /// <summary>Bước cuối thuộc khối bắt đầu ở <paramref name="head"/> (Hết Nếu / Hết lặp; với "Không thì" là bước ngay trước Hết Nếu).</summary>
    private int BlockEnd(int head) =>
        _designer.Steps[head].Type == StepType.Else ? _designer.Structure.Match[head] - 1 : _designer.Structure.Match[head];

    private int DepthOf(int i) => _tree && i < _treeDepth.Length ? _treeDepth[i] : _designer.Structure.Depth[i];

    /// <summary>Dạng cây không có dòng Hết Nếu / Hết lặp → bước đó (đang chọn / đang chạy) hiện ở dòng đầu khối.</summary>
    private int ShownAs(int index)
    {
        if (!_tree || index < 0 || index >= _designer.StepCount) return index;
        return IsHiddenEnd(_designer.Steps, _designer.Structure, index) ? _designer.Structure.Match[index] : index;
    }

    private void Rebuild()
    {
        var steps = _designer.Steps;
        _collapsed.RemoveWhere(s => !steps.Contains(s));
        ComputeRows();
        if (_designer.RunningIndex >= 0) RevealStep(_designer.RunningIndex);
        Invalidate();
    }

    private void ComputeRows()
    {
        var steps = _designer.Steps;
        var fs = _designer.Structure;
        if (_tree)
        {
            _treeDepth = TreeDepths(steps, fs);
            _rows = TreeRows(steps, fs, IsCollapsed);
            ComputeTreeLines();
        }
        else _rows = VisibleRows(steps, fs, IsCollapsed);
        AutoScrollMinSize = new Size(0, _rows.Count * RowHeight + S(8));
    }

    /// <summary>
    /// Đường nối cây, quét ngược từ dòng cuối: cột k có đường dọc đi qua dòng nếu phía dưới còn nút ở độ sâu k + 1
    /// trước khi gặp nút nông hơn hoặc bằng k; dòng là con cuối nếu phía dưới không còn anh em.
    /// </summary>
    private void ComputeTreeLines()
    {
        _rowLines = new ulong[_rows.Count];
        _rowLast = new bool[_rows.Count];
        ulong pending = 0;
        for (int row = _rows.Count - 1; row >= 0; row--)
        {
            int d = Math.Min(DepthOf(_rows[row]), 63);
            ulong shallower = (1UL << d) - 1;   // các cột < d
            _rowLines[row] = pending & shallower;
            pending &= shallower;               // nút ở độ sâu d cắt đường của các cột ≥ d
            if (d > 0)
            {
                _rowLast[row] = (pending & (1UL << (d - 1))) == 0;
                pending |= 1UL << (d - 1);
            }
        }
    }

    /// <summary>Bước nằm trong khối đang thu gọn → mở các khối đó (vd bước đang chạy / bước lỗi / bước vừa chọn).</summary>
    private void RevealStep(int index)
    {
        var steps = _designer.Steps;
        var fs = _designer.Structure;
        if (index < 0 || index >= steps.Count) return;
        index = ShownAs(index);
        bool opened = false;
        for (int h = 0; h < index; h++)
            if (IsHead(h) && BlockEnd(h) >= index && _collapsed.Remove(steps[h])) opened = true;
        if (opened) ComputeRows();
        ScrollToRow(_rows.IndexOf(index));
    }

    private void RevealSelected() => RevealStep(_designer.SelectedIndex);

    private void ScrollToRow(int row)
    {
        if (row < 0 || !IsHandleCreated) return;
        int top = row * RowHeight, y = -AutoScrollPosition.Y;
        if (top < y) AutoScrollPosition = new Point(0, top);
        else if (top + RowHeight > y + ClientSize.Height) AutoScrollPosition = new Point(0, top + RowHeight - ClientSize.Height);
    }

    private Rectangle RowRect(int row) => new(0, row * RowHeight + AutoScrollPosition.Y, ClientSize.Width, RowHeight);

    private int RowAt(Point p)
    {
        int row = (p.Y - AutoScrollPosition.Y) / RowHeight;
        return p.Y - AutoScrollPosition.Y >= 0 && row >= 0 && row < _rows.Count ? row : -1;
    }

    private int IndentX(int depth) => S(46) + depth * S(22);

    /// <summary>Ô ▾ / ▸ (dạng cây: ⊞ / ⊟) của khối ở dòng này (rỗng nếu không phải đầu khối).</summary>
    private Rectangle ToggleRect(int row)
    {
        int i = _rows[row];
        if (!IsHead(i)) return Rectangle.Empty;
        var r = RowRect(row);
        return new Rectangle(IndentX(DepthOf(i)) - S(2), r.Y, S(18), r.Height);
    }

    internal void ToggleBlock(int index, bool? expand = null)
    {
        var steps = _designer.Steps;
        if (index < 0 || index >= steps.Count || !IsHead(index)) return;
        bool collapsed = IsCollapsed(steps[index]);
        bool open = expand ?? collapsed;
        if (open) _collapsed.Remove(steps[index]);
        else _collapsed.Add(steps[index]);
        // Bước đang chọn bị giấu vào khối vừa thu → chọn đầu khối.
        int sel = _designer.SelectedIndex;
        if (!open && sel > index && sel <= BlockEnd(index) && ShownAs(sel) != index) _designer.SelectStep(index);
        Rebuild();
    }

    // ───────────────────────────── Vẽ ─────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var steps = _designer.Steps;
        var fs = _designer.Structure;
        if (steps.Count == 0)
        {
            TextRenderer.DrawText(g, "Chưa có bước nào — kéo thao tác từ hộp công cụ bên trái thả vào đây, hoặc bấm Tab.", _text,
                ClientRectangle, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        int first = Math.Max(0, (e.ClipRectangle.Top - AutoScrollPosition.Y) / RowHeight);
        int last = Math.Min(_rows.Count - 1, (e.ClipRectangle.Bottom - AutoScrollPosition.Y) / RowHeight);
        for (int row = first; row <= last; row++) DrawRow(g, row, steps, fs);

        if (_dropRow >= 0 && _dropRow <= _rows.Count)
        {
            // _dropRow == _rows.Count: thả xuống khoảng trống dưới cùng → cuối flow.
            bool end = _dropRow == _rows.Count;
            var r = RowRect(end ? _rows.Count - 1 : _dropRow);
            int y = _dropAfter || end ? r.Bottom - 1 : r.Top;
            int i = end ? -1 : _rows[_dropRow];
            int depth = end ? 0 : DepthOf(i) + (_dropAfter && IsHead(i) && !IsCollapsed(steps[i]) ? 1 : 0);
            using var pen = new Pen(RunColor, S(2));
            g.DrawLine(pen, IndentX(depth), y, ClientSize.Width - S(8), y);
        }
    }

    private void DrawRow(Graphics g, int row, IReadOnlyList<ActionStep> steps, FlowStructure fs)
    {
        int i = _rows[row];
        var s = steps[i];
        var r = RowRect(row);
        bool selected = i == ShownAs(_designer.SelectedIndex), running = i == ShownAs(_designer.RunningIndex), failed = i == ShownAs(_designer.FailedIndex);
        var back = failed ? FailBack : running ? RunBack : selected ? Theme.AccentSoft : row == _hoverRow ? HoverBack : BackColor;
        using (var b = new SolidBrush(back)) g.FillRectangle(b, r);
        if (running || failed || selected)
            using (var bar = new SolidBrush(failed ? FailColor : running ? RunColor : Theme.Accent)) g.FillRectangle(bar, r.X, r.Y, S(3), r.Height);

        // Số thứ tự + điểm dừng.
        var number = new Rectangle(r.X + S(6), r.Y, S(30), r.Height);
        TextRenderer.DrawText(g, (i + 1).ToString(), _small, number, Muted, OneLine | TextFormatFlags.Right);
        if (s.Breakpoint)
        {
            int d = S(8);
            using var dot = new SolidBrush(FailColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(dot, r.X + S(6), r.Y + (r.Height - d) / 2, d, d);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        }

        int depth = DepthOf(i);
        int x = IndentX(depth);
        bool head = IsHead(i);
        bool collapsed = head && IsCollapsed(s);
        if (_tree) DrawTreeLines(g, row, r, depth, head && !collapsed);
        else
            using (var guide = new Pen(GuideColor))   // đường dọc nối các bước cùng khối
                for (int k = 0; k < depth; k++)
                {
                    int gx = IndentX(k) + S(7);
                    g.DrawLine(guide, gx, r.Top, gx, r.Bottom);
                }

        if (head && _tree) DrawTreeToggle(g, x + S(7), r.Y + r.Height / 2, collapsed);
        else if (head)
            TextRenderer.DrawText(g, collapsed ? "▸" : "▾", _bold, new Rectangle(x - S(2), r.Y, S(18), r.Height), Muted,
                OneLine | TextFormatFlags.HorizontalCenter);
        x += S(18);

        var textColor = fs.Invalid[i] ? FailColor : s.Enabled ? Theme.Text : Disabled;
        if (StepVisuals.IsMarker(s.Type))
        {
            var labelRect = new Rectangle(x, r.Y, r.Right - x - S(8), r.Height);
            if (_tree && IsBranch(steps, fs, i))
            {
                // Dạng cây: "Không thì" là nhánh con của Nếu.
                string hidden = collapsed
                    ? $"   … {Enumerable.Range(i + 1, Math.Max(0, BlockEnd(i) - i)).Count(k => !StepVisuals.IsMarker(steps[k].Type))} bước"
                    : "";
                TextRenderer.DrawText(g, "Không thì (nhánh sai)" + hidden, _bold, labelRect, Muted, OneLine);
                return;
            }
            var label = s.Type switch { StepType.Else => "Không thì", StepType.EndIf => "Hết Nếu", _ => "Hết lặp" };
            TextRenderer.DrawText(g, label, _small, labelRect, fs.Invalid[i] ? FailColor : Muted, OneLine);
            return;
        }

        int icon = S(24);
        StepVisuals.DrawIcon(g, s.Type, new Rectangle(x, r.Y + (r.Height - icon) / 2, icon, icon), _icon, s.Enabled);
        x += icon + S(8);

        // Trạng thái bên phải: đang chạy / đã chạy / lỗi / tắt / số bước đang ẩn.
        string status = failed ? "✖ lỗi" : running ? "● đang chạy" : _designer.IsDone(i) ? "✔" : !s.Enabled ? "(tắt)" : "";
        if (collapsed) status = $"… {InnerCount(steps, fs, i)} bước" + (status.Length > 0 ? "  " + status : "");
        int statusWidth = status.Length > 0 ? TextRenderer.MeasureText(g, status, _small, Size.Empty, TextFormatFlags.NoPadding).Width + S(12) : 0;
        if (status.Length > 0)
            TextRenderer.DrawText(g, status, _small, new Rectangle(r.Right - statusWidth - S(4), r.Y, statusWidth, r.Height),
                failed ? FailColor : running ? RunColor : _designer.IsDone(i) ? DoneColor : Muted, OneLine | TextFormatFlags.Right);

        TextRenderer.DrawText(g, s.Describe(), head ? _bold : _text, new Rectangle(x, r.Y, r.Right - x - statusWidth - S(8), r.Height), textColor, OneLine);
    }

    /// <summary>Đường chấm nối cây: cột cha (├ / └), các cột tổ tiên còn anh em phía dưới (│), xuống con nếu nút đang mở.</summary>
    private void DrawTreeLines(Graphics g, int row, Rectangle r, int depth, bool open)
    {
        using var pen = new Pen(TreeLineColor) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        int mid = r.Y + r.Height / 2;
        ulong lines = row < _rowLines.Length ? _rowLines[row] : 0;
        for (int k = 0; k < depth - 1 && k < 64; k++)
            if ((lines & (1UL << k)) != 0)
            {
                int gx = IndentX(k) + S(7);
                g.DrawLine(pen, gx, r.Top, gx, r.Bottom);
            }
        if (depth > 0)
        {
            int px = IndentX(depth - 1) + S(7);
            bool last = row < _rowLast.Length && _rowLast[row];
            g.DrawLine(pen, px, r.Top, px, last ? mid : r.Bottom);
            g.DrawLine(pen, px, mid, IndentX(depth) + (IsHead(_rows[row]) ? S(2) : S(14)), mid);
        }
        if (open && row + 1 < _rows.Count && DepthOf(_rows[row + 1]) > depth)
        {
            int cx = IndentX(depth) + S(7);
            g.DrawLine(pen, cx, mid, cx, r.Bottom);
        }
    }

    /// <summary>Ô vuông ⊞ / ⊟ kiểu cây thư mục.</summary>
    private void DrawTreeToggle(Graphics g, int cx, int cy, bool collapsed)
    {
        int h = S(5);
        var box = new Rectangle(cx - h, cy - h, 2 * h, 2 * h);
        g.FillRectangle(Brushes.White, box);
        using (var border = new Pen(Muted)) g.DrawRectangle(border, box);
        using var sign = new Pen(Theme.Text);
        g.DrawLine(sign, cx - h + S(2), cy, cx + h - S(2), cy);
        if (collapsed) g.DrawLine(sign, cx, cy - h + S(2), cx, cy + h - S(2));
    }

    // ───────────────────────────── Chuột & bàn phím ─────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int row = RowAt(e.Location);
        if (row < 0) return;
        if (e.Button == MouseButtons.Left && ToggleRect(row).Contains(e.Location))
        {
            ToggleBlock(_rows[row]);
            return;
        }
        _designer.SelectStep(_rows[row]);
        if (e.Button == MouseButtons.Right) _designer.ShowStepMenu(this, e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int row = RowAt(e.Location);
        if (row < 0 || e.Button != MouseButtons.Left || ToggleRect(row).Contains(e.Location)) return;
        var s = _designer.Steps[_rows[row]];
        if (!StepVisuals.IsMarker(s.Type)) _designer.RequestEdit(_rows[row]);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int row = RowAt(e.Location);
        if (row == _hoverRow) return;
        _hoverRow = row;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverRow = -1;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Tab or Keys.Enter or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int row = _rows.IndexOf(ShownAs(_designer.SelectedIndex));
        bool handled = true;
        switch (e.KeyCode)
        {
            case Keys.Up when !e.Control: SelectRow(row < 0 ? 0 : row - 1); break;
            case Keys.Down when !e.Control: SelectRow(row < 0 ? 0 : row + 1); break;
            case Keys.Home: SelectRow(0); break;
            case Keys.End: SelectRow(_rows.Count - 1); break;
            case Keys.PageUp: SelectRow(Math.Max(0, row - ClientSize.Height / RowHeight)); break;
            case Keys.PageDown: SelectRow(row + ClientSize.Height / RowHeight); break;
            case Keys.Left when !e.Control:
            {
                // Thu khối đang mở; đang ở bước bên trong → về đầu khối chứa nó.
                int i = ShownAs(_designer.SelectedIndex);
                if (i < 0) break;
                if (IsHead(i) && !IsCollapsed(_designer.Steps[i])) ToggleBlock(i, expand: false);
                else if (ParentHead(i) is int h and >= 0) _designer.SelectStep(h);
                break;
            }
            case Keys.Right when !e.Control:
                if (_designer.SelectedIndex >= 0) ToggleBlock(_designer.SelectedIndex, expand: true);
                break;
            case Keys.Tab when !e.Control && !e.Alt && !e.Shift:
            {
                var r = row >= 0 ? RowRect(row) : new Rectangle(0, 0, ClientSize.Width, 0);
                _designer.OpenPickerAfterSelected(this, PointToScreen(new Point(IndentX(0) + S(40), Math.Max(0, r.Bottom))));
                break;
            }
            default:
                handled = _designer.HandleEditKey(e);
                break;
        }
        if (handled)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else base.OnKeyDown(e);
    }

    private void SelectRow(int row)
    {
        if (_rows.Count == 0) return;
        _designer.SelectStep(_rows[Math.Clamp(row, 0, _rows.Count - 1)]);
    }

    /// <summary>Đầu khối gần nhất chứa bước <paramref name="index"/> (Nếu / Lặp; dạng cây thêm "Không thì"); -1 nếu ở ngoài cùng.</summary>
    private int ParentHead(int index)
    {
        for (int h = index - 1; h >= 0; h--)
            if (IsHead(h) && BlockEnd(h) >= index) return h;
        return -1;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_designer.SelectedIndex < 0 && _rows.Count > 0) _designer.SelectStep(_rows[0]);
        Invalidate();
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

    // ───────────────────────────── Kéo thả thao tác từ hộp công cụ ─────────────────────────────

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (e.Data?.GetDataPresent(StepVisuals.StepTypeFormat) != true)
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        e.Effect = DragDropEffects.Copy;
        var p = PointToClient(new Point(e.X, e.Y));
        int row = RowAt(p);
        bool after = row < 0 || p.Y - RowRect(row).Top > RowHeight / 2;
        if (row < 0) row = _rows.Count;   // khoảng trống dưới cùng → cuối flow
        if (row != _dropRow || after != _dropAfter)
        {
            _dropRow = row;
            _dropAfter = after;
            Invalidate();
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dropRow = -1;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        int row = _dropRow;
        bool after = _dropAfter;
        _dropRow = -1;
        Invalidate();
        if (!Enum.TryParse<StepType>(e.Data?.GetData(StepVisuals.StepTypeFormat) as string, out var type)) return;
        _designer.RequestAdd(type, InsertIndexFor(row, after));
    }

    /// <summary>Vị trí chèn khi thả trước / sau dòng <paramref name="row"/>: thả sau khối đang thu gọn → sau cả khối.</summary>
    internal int InsertIndexFor(int row, bool after)
    {
        if (row < 0 || row >= _rows.Count) return _designer.StepCount;
        int i = _rows[row];
        if (!after) return i;
        if (IsHead(i) && IsCollapsed(_designer.Steps[i])) return BlockEnd(i) + 1;
        return i + 1;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var f in new[] { _text, _bold, _small, _icon }) f.Dispose();
        base.Dispose(disposing);
    }
}
