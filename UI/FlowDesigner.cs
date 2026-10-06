using System.Drawing.Drawing2D;
using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>
/// Khung thiết kế flow dạng sơ đồ kiểu n8n: nút vuông nối bằng dây cong từ trái sang phải, khối Nếu tách nhánh đúng/sai,
/// khối Lặp có dây quay về. Kéo thao tác từ hộp công cụ thả lên dây để chèn, bấm "+" trên dây để chọn thao tác,
/// kéo nút sang dây khác để di chuyển (cả khối), kéo nền để cuộn, Ctrl + lăn chuột để thu phóng, bản đồ thu nhỏ ở góc.
/// Thứ tự chạy vẫn là danh sách bước — vị trí trên sơ đồ được tự tính từ thứ tự đó.
/// </summary>
internal sealed class FlowDesigner : Control
{
    private const TextFormatFlags CenterText =
        TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

    private const TextFormatFlags OneLine =
        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    private const string ClipboardFormat = "ScheduleApp.Steps";
    private const float MinZoom = 0.25f, MaxZoom = 2f;

    private static readonly Color CanvasColor = Color.FromArgb(246, 247, 249);
    private static readonly Color DotColor = Color.FromArgb(212, 216, 223);
    private static readonly Color EdgeColor = Color.FromArgb(150, 157, 168);
    private static readonly Color PortColor = Color.FromArgb(120, 127, 138);
    private static readonly Color NodeBorder = Color.FromArgb(196, 201, 209);
    private static readonly Color StartColor = Color.FromArgb(16, 124, 16);
    private static readonly Color HotColor = Color.FromArgb(0, 120, 212);
    private static readonly Color RunColor = Color.FromArgb(0, 150, 255);
    private static readonly Color DoneColor = Color.FromArgb(34, 154, 68);
    private static readonly Color ErrorColor = Color.FromArgb(200, 40, 30);
    private static readonly Color BreakpointColor = Color.FromArgb(220, 40, 40);
    private static readonly Color TrueColor = Color.FromArgb(34, 140, 60);
    private static readonly Color FalseColor = Color.FromArgb(196, 60, 48);
    private static readonly Color TextColor = Color.FromArgb(40, 42, 46);
    private static readonly Color MutedColor = Color.FromArgb(110, 114, 122);

    private List<ActionStep> _steps = [];
    private FlowStructure _structure = FlowStructure.Build([]);
    private FlowGraphLayout _layout = null!;

    // Khung nhìn: điểm màn hình = điểm sơ đồ × _zoom + _pan.
    private float _zoom = 1f;
    private PointF _pan;
    private bool _viewReady;

    private int _selected = -1;
    private int _hover = -1;
    private int _hoverEdge = -1;
    private int _hoverButton = -1;
    private int _dropEdge = -1;
    private int _running = -1;
    private int _failed = -1;
    private readonly HashSet<int> _done = [];

    // Chuột: kéo nút, kéo nền, kéo trên bản đồ thu nhỏ.
    private int _pressNode = -1;
    private Point _pressPoint;
    private int _dragNode = -1;
    private Point _dragPos;
    private bool _panning;
    private bool _panMoved;
    private Point _panMouse;
    private PointF _panStart;
    private bool _minimapDrag;

    private readonly System.Windows.Forms.Timer _spinTimer = new() { Interval = 60 };
    private float _spin;

    private readonly ContextMenuStrip _menu = new();
    private readonly ContextMenuStrip _canvasMenu = new();
    private readonly ToolStripMenuItem _miToggle;
    private readonly ToolStripMenuItem _miBreakpoint;
    private readonly ToolTip _tip = new() { InitialDelay = 400 };
    private string _tipText = "";
    private readonly Dictionary<ActionStep, (string Data, Bitmap Image)> _thumbs = [];
    /// <summary>Ảnh thu nhỏ video của bước "Phát video / nhạc" theo dòng đầu danh sách phát (đã thay biến); Thumb null = đang tải / không có.</summary>
    private readonly Dictionary<ActionStep, (string Line, MediaThumbnails.Thumbnail? Thumb)> _mediaThumbs = [];
    private readonly CancellationTokenSource _mediaCts = new();

    // Font theo mức thu phóng (tạo lại khi đổi) và font cố định cho nút điều khiển.
    private float _fontZoom = -1;
    private Font? _titleFont, _subFont, _smallFont, _glyphFont, _badgeFont, _portFont;
    private Font _uiGlyph = null!, _uiFont = null!;

    public event EventHandler? SelectionChanged;
    public event EventHandler? StepsChanged;

    /// <summary>Yêu cầu mở trình soạn cho bước tại vị trí này.</summary>
    public event Action<int>? EditRequested;

    /// <summary>Người dùng chọn / thả một loại thao tác mới để chèn vào vị trí này.</summary>
    public event Action<StepType, int>? AddRequested;

    /// <summary>Yêu cầu chạy thử từ bước này.</summary>
    public event Action<int>? RunFromRequested;

    /// <summary>
    /// Thay {{biến}} của công việc trong danh sách phát (để hiện ảnh video trên nút "Phát video / nhạc");
    /// ném <see cref="InvalidOperationException"/> khi biến chưa có giá trị. Null = bỏ qua dòng dùng biến.
    /// </summary>
    /// <remarks>
    /// Dòng đã thay được nhớ theo (dòng gốc, hàm thay) — không gọi lại khi vẽ. Biến của công việc đổi thì gán hàm mới
    /// (canvas bỏ các dòng đã nhớ và vẽ lại).
    /// </remarks>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, string>? ExpandMediaLine
    {
        get => _expandMediaLine;
        set
        {
            if (ReferenceEquals(_expandMediaLine, value)) return;
            _expandMediaLine = value;
            _expandedLines.Clear();
            Invalidate();
        }
    }
    private Func<string, string>? _expandMediaLine;

    /// <summary>Dòng đầu danh sách phát đã thay biến, theo dòng gốc (null = không thay được / chỉ biết khi chạy).</summary>
    private readonly Dictionary<ActionStep, (string Raw, string? Expanded)> _expandedLines = [];

    /// <summary>Biến có sẵn đổi giá trị theo từng lần chạy: ngày giờ, số ngẫu nhiên, guid (cùng quy tắc với MediaInfo).</summary>
    private static readonly System.Text.RegularExpressions.Regex PerRunVariable = new(
        @"^(random(\s*:.*)?|guid|(today|now|time|yesterday|tomorrow)(\s*[-+:].*)?)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Dòng chỉ biết giá trị khi chạy, hoặc thay biến tốn kém / mỗi lần một khác: clipboard, bí mật, công thức {{=…}},
    /// ngày giờ, số ngẫu nhiên, guid. Canvas không thay các dòng này (hiện biểu tượng thường).
    /// </summary>
    internal static bool IsRuntimeOnlyMediaLine(string line) =>
        line.Contains("{{clipboard", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("{{secret:", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("{{=", StringComparison.Ordinal) ||
        ScheduleApp.Services.Engine.VariableExpander.Names(line).Any(n => PerRunVariable.IsMatch(n) ||
            n.StartsWith("clipboard", StringComparison.OrdinalIgnoreCase) || n.StartsWith("secret:", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith('='));

    public FlowDesigner()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AllowDrop = true;
        TabStop = true;
        BackColor = CanvasColor;
        CreateFixedFonts();
        Rebuild();

        _menu.Items.Add("Sửa…  (Enter)", null, (_, _) => { if (_selected >= 0) EditRequested?.Invoke(_selected); });
        _menu.Items.Add("Nhân bản  (Ctrl+D)", null, (_, _) => DuplicateSelected());
        _miToggle = new ToolStripMenuItem("Tắt bước", null, (_, _) => ToggleSelected());
        _menu.Items.Add(_miToggle);
        _miBreakpoint = new ToolStripMenuItem("Điểm dừng  (F9)", null, (_, _) => ToggleBreakpoint());
        _menu.Items.Add(_miBreakpoint);
        _menu.Items.Add("▶ Chạy thử từ bước này", null, (_, _) => { if (_selected >= 0) RunFromRequested?.Invoke(_selected); });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Thêm bước sau bước này…  (Tab)", null, (_, _) => OpenPickerAfterSelected());
        _menu.Items.Add("Sao chép  (Ctrl+C)", null, (_, _) => CopySelected());
        _menu.Items.Add("Dán sau bước này  (Ctrl+V)", null, (_, _) => Paste());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Lên trước  (Ctrl+←)", null, (_, _) => MoveSelected(-1));
        _menu.Items.Add("Xuống sau  (Ctrl+→)", null, (_, _) => MoveSelected(1));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Xóa  (Delete)", null, (_, _) => DeleteSelected());

        _canvasMenu.Items.Add("Thêm bước vào cuối…  (Tab)", null, (_, _) => OpenPicker(StubEdge, ToScreenPoint(StubEdge.Mid)));
        _canvasMenu.Items.Add("Dán vào cuối  (Ctrl+V)", null, (_, _) => { SelectStep(-1); Paste(); });
        _canvasMenu.Items.Add(new ToolStripSeparator());
        _canvasMenu.Items.Add("Vừa khung  (1)", null, (_, _) => ZoomToFit());
        _canvasMenu.Items.Add("Thu phóng 100%  (0)", null, (_, _) => ResetZoom());

        _spinTimer.Tick += (_, _) =>
        {
            _spin = (_spin + 24) % 360;
            if (_layout.IsVisible(_running)) Invalidate(Rectangle.Inflate(ToScreen(_layout.Bounds[_running]!.Value), S(12), S(12)));
        };
    }

    // ───────────────────────────── API ─────────────────────────────

    public int SelectedIndex => _selected;

    public int StepCount => _steps.Count;

    // ── Cho cách nhìn khác (danh sách thụt lề) dùng chung dữ liệu, vùng chọn, menu và phím tắt của sơ đồ ──

    /// <summary>Danh sách bước đang hiển thị.</summary>
    internal IReadOnlyList<ActionStep> Steps => _steps;

    /// <summary>Bước đang chạy (-1 = không), bước lỗi của lần chạy thử (-1 = không), bước đã chạy xong.</summary>
    internal int RunningIndex => _running;
    internal int FailedIndex => _failed;
    internal bool IsDone(int index) => _done.Contains(index);

    /// <summary>Danh sách bước / trạng thái chạy vừa đổi — cách nhìn khác cần vẽ lại.</summary>
    internal event EventHandler? ViewStateChanged;

    private void NotifyViewState() => ViewStateChanged?.Invoke(this, EventArgs.Empty);

    internal void RequestEdit(int index)
    {
        if (index >= 0 && index < _steps.Count) EditRequested?.Invoke(index);
    }

    internal void RequestAdd(StepType type, int index) => AddRequested?.Invoke(type, Math.Clamp(index, 0, _steps.Count));

    /// <summary>Menu chuột phải của bước đang chọn, mở trên <paramref name="owner"/> (cách nhìn khác).</summary>
    internal void ShowStepMenu(Control owner, Point location)
    {
        if (_selected < 0 || _selected >= _steps.Count) return;
        _miToggle.Text = _steps[_selected].Enabled ? "Tắt bước  (Space)" : "Bật bước  (Space)";
        _miBreakpoint.Checked = _steps[_selected].Breakpoint;
        _miBreakpoint.Enabled = !StepVisuals.IsMarker(_steps[_selected].Type);
        _menu.Show(owner, location);
    }

    public FlowStructure Structure => _structure;

    internal FlowGraphLayout Graph => _layout;

    private string _startSubtitle = "theo lịch, kích hoạt hoặc chạy tay";

    /// <summary>Dòng mô tả dưới nút Bắt đầu (lịch chạy / trình kích hoạt của công việc).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string StartSubtitle
    {
        get => _startSubtitle;
        set
        {
            if (_startSubtitle == value) return;
            _startSubtitle = value;
            Invalidate();
        }
    }

    internal float Zoom => _zoom;

    internal PointF Pan => _pan;

    /// <summary>Gắn danh sách bước (sửa trực tiếp trên danh sách này).</summary>
    public void SetSteps(List<ActionStep> steps)
    {
        _steps = steps;
        _selected = Math.Min(_selected, steps.Count - 1);
        _done.Clear();
        RefreshView();
    }

    public void RefreshView()
    {
        PruneThumbnails();
        Rebuild();
        Invalidate();
    }

    /// <summary>Tô sáng bước đang chạy (-1 = bỏ tô). Bước chạy trước đó được đánh dấu đã chạy xong (dấu ✓ xanh).</summary>
    public void SetRunning(int index)
    {
        if (_running == index) return;
        if (_running >= 0) _done.Add(_running);
        _running = index;
        if (index >= 0)
        {
            _failed = -1;
            if (index < _steps.Count) EnsureVisible(index);
            _spinTimer.Start();
        }
        else _spinTimer.Stop();
        Invalidate();
        NotifyViewState();
    }

    /// <summary>Đánh dấu bước lỗi sau khi chạy thử (-1 = bỏ).</summary>
    public void SetFailed(int index)
    {
        _failed = index;
        if (index >= 0)
        {
            _done.Remove(index);
            if (index < _steps.Count) SelectStep(index);
        }
        Invalidate();
        NotifyViewState();
    }

    /// <summary>Xóa trạng thái lần chạy trước (đang chạy, đã chạy, lỗi) — gọi trước khi chạy thử.</summary>
    public void ResetRunState()
    {
        _done.Clear();
        _running = -1;
        _failed = -1;
        _spinTimer.Stop();
        Invalidate();
        NotifyViewState();
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

    /// <summary>Di chuyển bước (hoặc cả khối) lên trước/xuống sau một vị trí — đổi chỗ với bước liền kề.</summary>
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

    /// <summary>
    /// Chuyển bước (hoặc cả khối) <paramref name="from"/> vào dây <paramref name="edge"/>.
    /// Dây nhánh "sai" chưa có "Không thì" thì tạo "Không thì" trước. Trả về false nếu không đổi gì.
    /// </summary>
    internal bool MoveToEdge(int from, GraphEdge edge)
    {
        if (from < 0 || from >= _steps.Count) return false;
        var (start, end) = BlockRange(from);
        int index = edge.InsertIndex;
        if (index > start && index <= end) return false;                                    // thả vào trong chính khối đó
        if (!edge.NeedsElse && (index == start || index == end + 1)) return false;           // đúng chỗ cũ

        var block = _steps.GetRange(start, end - start + 1);
        var anchor = index < _steps.Count ? _steps[index] : null;
        _steps.RemoveRange(start, block.Count);
        int at = anchor == null ? _steps.Count : _steps.FindIndex(s => ReferenceEquals(s, anchor));
        if (edge.NeedsElse) _steps.Insert(at++, ActionStep.CreateDefault(StepType.Else));
        _steps.InsertRange(at, block);
        Changed();
        SelectStep(at);
        return true;
    }

    /// <summary>Thêm thao tác <paramref name="type"/> vào dây <paramref name="edge"/> (mở trình soạn bước qua <see cref="AddRequested"/>).</summary>
    internal void AddViaEdge(StepType type, GraphEdge edge)
    {
        int index = Math.Clamp(edge.InsertIndex, 0, _steps.Count);
        if (edge.NeedsElse && type == StepType.Else)
        {
            InsertStep(index, ActionStep.CreateDefault(StepType.Else));
            return;
        }
        ActionStep? addedElse = null;
        if (edge.NeedsElse)
        {
            // Tạm chèn "Không thì" để bước mới vào nhánh sai; bỏ ra nếu người dùng hủy trình soạn.
            addedElse = ActionStep.CreateDefault(StepType.Else);
            _steps.Insert(index++, addedElse);
            Rebuild();
        }
        int before = _steps.Count;
        AddRequested?.Invoke(type, index);
        if (addedElse != null && _steps.Count == before)
        {
            _steps.Remove(addedElse);
            Rebuild();
            Invalidate();
        }
    }

    /// <summary>Chèn các bước (thả file, dán) vào dây.</summary>
    private void InsertViaEdge(GraphEdge edge, IReadOnlyList<ActionStep> steps)
    {
        if (steps.Count == 0) return;
        int index = Math.Clamp(edge.InsertIndex, 0, _steps.Count);
        if (edge.NeedsElse) _steps.Insert(index++, ActionStep.CreateDefault(StepType.Else));
        _steps.InsertRange(index, steps);
        Changed();
        SelectStep(index);
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
        Rebuild();
        KeepContentInView();
        Invalidate();
        StepsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sau khi thêm / xóa / di chuyển bước: flow còn nhỏ thì giữ cả sơ đồ trong khung (cuộn vừa đủ, hoặc thu nhỏ tới 75%)
    /// để không phải đi tìm bước mới hay nút "+" cuối; flow lớn thì giữ nguyên khung nhìn (đã có bản đồ thu nhỏ).
    /// </summary>
    private void KeepContentInView()
    {
        if (!_viewReady || ClientSize.Width <= S(100)) return;
        var b = _layout.ContentBounds;
        b.Inflate(S(30), S(30));
        float w = ClientSize.Width, h = ClientSize.Height - S(50);
        if (b.Width * _zoom > w || b.Height * _zoom > h)
        {
            float fit = Math.Min(w / b.Width, h / b.Height);
            if (fit < 0.75f) return;
            _zoom = Math.Min(_zoom, fit);
        }
        var r = new RectangleF(b.X * _zoom + _pan.X, b.Y * _zoom + _pan.Y, b.Width * _zoom, b.Height * _zoom);
        float dx = r.Left < 0 ? -r.Left : r.Right > w ? w - r.Right : 0;
        float dy = r.Top < 0 ? -r.Top : r.Bottom > h ? h - r.Bottom : 0;
        _pan = new PointF(_pan.X + dx, _pan.Y + dy);
    }

    private void Rebuild()
    {
        _structure = FlowStructure.Build(_steps);
        _layout = FlowGraphLayout.Build(_steps, _structure, FlowGraphLayout.Metrics.Scaled(S));
        _hover = _hoverEdge = _dropEdge = -1;
        NotifyViewState();
    }

    // ───────────────────────────── Khung nhìn ─────────────────────────────

    private int S(int v) => LogicalToDeviceUnits(v);

    private PointF ToScreenPoint(PointF p) => new(p.X * _zoom + _pan.X, p.Y * _zoom + _pan.Y);

    private Rectangle ToScreen(RectangleF r)
    {
        var p = ToScreenPoint(r.Location);
        return Rectangle.Round(new RectangleF(p.X, p.Y, r.Width * _zoom, r.Height * _zoom));
    }

    private PointF ToContent(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    /// <summary>Thu phóng quanh điểm <paramref name="anchor"/> trên màn hình (điểm đó đứng yên).</summary>
    internal void SetZoom(float zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var c = ToContent(anchor);
        _zoom = zoom;
        _pan = new PointF(anchor.X - c.X * zoom, anchor.Y - c.Y * zoom);
        Invalidate();
    }

    private Point ViewCenter => new(ClientSize.Width / 2, ClientSize.Height / 2);

    public void ZoomIn() => SetZoom(_zoom * 1.2f, ViewCenter);

    public void ZoomOut() => SetZoom(_zoom / 1.2f, ViewCenter);

    public void ResetZoom() => SetZoom(1f, ViewCenter);

    /// <summary>
    /// Thu phóng cho cả sơ đồ vừa khung (tối đa 100%, không nhỏ hơn <paramref name="minZoom"/>);
    /// vẫn không vừa thì canh trái từ nút Bắt đầu.
    /// </summary>
    public void ZoomToFit(float minZoom = MinZoom)
    {
        var b = _layout.ContentBounds;
        b.Inflate(S(30), S(30));
        float w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height - S(50));
        _zoom = Math.Clamp(Math.Min(w / b.Width, h / b.Height), minZoom, 1f);
        float x = b.Width * _zoom <= w ? (w - b.Width * _zoom) / 2 - b.X * _zoom : -b.X * _zoom;
        float y = b.Height * _zoom <= h ? (h - b.Height * _zoom) / 2 - b.Y * _zoom : -b.Y * _zoom;
        _pan = new PointF(x, y);
        _viewReady = true;
        Invalidate();
    }

    private void PanBy(float dx, float dy)
    {
        _pan = new PointF(_pan.X + dx, _pan.Y + dy);
        Invalidate();
    }

    /// <summary>Cuộn ít nhất để nút (kèm chữ bên dưới) nằm trong khung.</summary>
    private void EnsureVisible(int i)
    {
        if (!_layout.IsVisible(i) || ClientSize.Width <= 0) return;
        if (!_viewReady) return;
        var r = ToScreen(_layout.Bounds[i]!.Value);
        r = Rectangle.FromLTRB(r.Left - S(20), r.Top - S(44), r.Right + S(20), r.Bottom + (int)(_layout.Size.Label * _zoom));
        // Bước cuối: giữ cả nút "+" phía sau trong khung để thêm bước tiếp ngay.
        if (i == _steps.Count - 1) r = Rectangle.Union(r, Rectangle.Inflate(ToScreen(_layout.StubBounds), S(16), 0));
        int m = S(12);
        float dx = r.Left < m ? m - r.Left : r.Right > ClientSize.Width - m ? ClientSize.Width - m - r.Right : 0;
        float dy = r.Top < m ? m - r.Top : r.Bottom > ClientSize.Height - m ? ClientSize.Height - m - r.Bottom : 0;
        if (dx != 0 || dy != 0) PanBy(dx, dy);
    }

    // ───────────────────────────── Dò chuột ─────────────────────────────

    private GraphEdge StubEdge => _layout.Edges.First(e => e.Kind == GraphEdgeKind.Stub);

    /// <summary>Nút (bước hoặc nút gộp) dưới chuột, -1 nếu không có.</summary>
    private int NodeAt(Point p)
    {
        var c = ToContent(p);
        for (int i = _steps.Count - 1; i >= 0; i--)
            if (_layout.Bounds[i] is { } r && r.Contains(c)) return i;
        return -1;
    }

    private bool StartAt(Point p) => _layout.StartBounds.Contains(ToContent(p));

    private bool StubAt(Point p)
    {
        var r = ToScreen(_layout.StubBounds);
        r.Inflate(S(4), S(4));
        return r.Contains(p);
    }

    private Rectangle PlusRect(GraphEdge edge)
    {
        var m = ToScreenPoint(edge.Mid);
        int d = Math.Max(S(20), (int)(S(24) * _zoom));
        return new Rectangle((int)(m.X - d / 2f), (int)(m.Y - d / 2f), d, d);
    }

    /// <summary>Dây gần chuột (trong vài điểm ảnh) hoặc nút "+" của dây đang di chuột.</summary>
    private int EdgeAt(Point p)
    {
        if (_hoverEdge >= 0 && _hoverEdge < _layout.Edges.Count && PlusRect(_layout.Edges[_hoverEdge]).Contains(p)) return _hoverEdge;
        var c = ToContent(p);
        float limit = S(8) / _zoom, best = float.MaxValue;
        int found = -1;
        for (int k = 0; k < _layout.Edges.Count; k++)
        {
            var e = _layout.Edges[k];
            if (e.Kind == GraphEdgeKind.Stub) continue;
            float d = FlowGraphLayout.DistanceTo(e, c);
            if (d <= limit && d < best)
            {
                best = d;
                found = k;
            }
        }
        return found;
    }

    /// <summary>Dây gần nhất để thả (không giới hạn khoảng cách); bỏ qua các dây nằm trong khối đang kéo.</summary>
    internal int NearestEdge(PointF content, int dragging = -1)
    {
        var (start, end) = dragging >= 0 ? BlockRange(dragging) : (-1, -2);
        if (_layout.StubBounds.Contains(content)) return _layout.Edges.FindIndex(e => e.Kind == GraphEdgeKind.Stub);
        float best = float.MaxValue;
        int found = -1;
        for (int k = 0; k < _layout.Edges.Count; k++)
        {
            var e = _layout.Edges[k];
            if (dragging >= 0 && e.InsertIndex > start && e.InsertIndex <= end) continue;
            float d = Math.Min(FlowGraphLayout.DistanceTo(e, content), FlowGraphLayout.Distance(e.Mid, content));
            if (d < best)
            {
                best = d;
                found = k;
            }
        }
        return found;
    }

    // Thanh công cụ nổi phía trên nút đang di chuột (như n8n): chạy từ đây · bật/tắt · xóa · thêm.
    private static readonly (string Glyph, string Fallback, string Tip)[] ToolbarButtons =
    [
        ("", "▶", "Chạy thử từ bước này"),
        ("", "⏻", "Bật / tắt bước (Space)"),
        ("", "✕", "Xóa bước (Delete)"),
        ("", "⋯", "Thêm…")
    ];

    private bool ShowsToolbar(int i) =>
        i >= 0 && _layout.IsStepNode(i) && _zoom >= 0.45f && _dragNode < 0 && !_panning;

    private Rectangle ToolbarRect(int i)
    {
        var r = ToScreen(_layout.Bounds[i]!.Value);
        int b = S(26), gap = S(2), w = ToolbarButtons.Length * b + (ToolbarButtons.Length - 1) * gap + S(6);
        return new Rectangle(r.X + r.Width / 2 - w / 2, r.Top - b - S(12), w, b + S(6));
    }

    private Rectangle ToolbarButton(int i, int k)
    {
        var t = ToolbarRect(i);
        int b = S(26), gap = S(2);
        return new Rectangle(t.X + S(3) + k * (b + gap), t.Y + S(3), b, b);
    }

    private int ToolbarButtonAt(Point p)
    {
        if (!ShowsToolbar(_hover)) return -1;
        for (int k = 0; k < ToolbarButtons.Length; k++)
            if (ToolbarButton(_hover, k).Contains(p)) return k;
        return -1;
    }

    /// <summary>Vùng giữ trạng thái di chuột của nút: cả nút, thanh công cụ và khoảng trống giữa hai thứ.</summary>
    private bool InHoverZone(int i, Point p)
    {
        if (!ShowsToolbar(i)) return false;
        var r = ToScreen(_layout.Bounds[i]!.Value);
        return Rectangle.Union(r, ToolbarRect(i)).Contains(p);
    }

    // Nút thu phóng ở góc trái dưới.
    private static readonly (string Glyph, string Fallback, string Tip)[] ZoomButtons =
    [
        ("", "⤢", "Vừa khung (phím 1)"),
        ("", "+", "Phóng to (Ctrl + lăn chuột)"),
        ("", "−", "Thu nhỏ (Ctrl + lăn chuột)"),
        ("", "", "Về 100% (phím 0)")
    ];

    private Rectangle ZoomButton(int k)
    {
        int b = S(32), gap = S(6), y = ClientSize.Height - S(12) - b, x = S(12);
        for (int j = 0; j < k; j++) x += b + gap;
        return new Rectangle(x, y, k == 3 ? S(58) : b, b);
    }

    private int ZoomButtonAt(Point p)
    {
        for (int k = 0; k < ZoomButtons.Length; k++)
            if (ZoomButton(k).Contains(p)) return k;
        return -1;
    }

    private void ClickZoomButton(int k)
    {
        switch (k)
        {
            case 0: ZoomToFit(); break;
            case 1: ZoomIn(); break;
            case 2: ZoomOut(); break;
            default: ResetZoom(); break;
        }
    }

    // Bản đồ thu nhỏ ở góc phải dưới — chỉ hiện khi sơ đồ lớn hơn khung.
    private bool MinimapVisible
    {
        get
        {
            if (_steps.Count == 0 || ClientSize.Width < S(420) || ClientSize.Height < S(260)) return false;
            var b = ToScreen(_layout.ContentBounds);
            return b.Left < 0 || b.Top < 0 || b.Right > ClientSize.Width || b.Bottom > ClientSize.Height;
        }
    }

    private Rectangle MinimapRect => new(ClientSize.Width - S(172), ClientSize.Height - S(112), S(160), S(100));

    private (float Scale, PointF Offset) MinimapTransform()
    {
        var m = Rectangle.Inflate(MinimapRect, -S(6), -S(6));
        var b = _layout.ContentBounds;
        float k = Math.Min(m.Width / b.Width, m.Height / b.Height);
        return (k, new PointF(m.X + (m.Width - b.Width * k) / 2 - b.X * k, m.Y + (m.Height - b.Height * k) / 2 - b.Y * k));
    }

    /// <summary>Đưa điểm trên bản đồ thu nhỏ về giữa khung.</summary>
    private void CenterOnMinimap(Point p)
    {
        var (k, o) = MinimapTransform();
        var c = new PointF((p.X - o.X) / k, (p.Y - o.Y) / k);
        _pan = new PointF(ClientSize.Width / 2f - c.X * _zoom, ClientSize.Height / 2f - c.Y * _zoom);
        Invalidate();
    }

    // ───────────────────────────── Vẽ ─────────────────────────────

    private void CreateFixedFonts()
    {
        _uiGlyph?.Dispose();
        _uiFont?.Dispose();
        _uiGlyph = StepVisuals.CreateIconFont(Font.FontFamily, Font.Size + 1.5f);
        _uiFont = new Font(Font.FontFamily, Font.Size - 0.5f, FontStyle.Bold);
    }

    private void DisposeZoomFonts()
    {
        foreach (var f in new[] { _titleFont, _subFont, _smallFont, _glyphFont, _badgeFont, _portFont }) f?.Dispose();
        _titleFont = _subFont = _smallFont = _glyphFont = _badgeFont = _portFont = null;
        _fontZoom = -1;
    }

    private void EnsureFonts()
    {
        if (Math.Abs(_fontZoom - _zoom) < 0.001f && _titleFont != null) return;
        DisposeZoomFonts();
        float z = _zoom, size = Font.Size;
        _titleFont = new Font(Font.FontFamily, Math.Max(1f, (size + 0.5f) * z), FontStyle.Bold);
        _subFont = new Font(Font.FontFamily, Math.Max(1f, (size - 0.5f) * z));
        _smallFont = new Font(Font.FontFamily, Math.Max(1f, (size - 1.5f) * z));
        _glyphFont = StepVisuals.CreateIconFont(Font.FontFamily, (size + 13f) * z);
        _badgeFont = StepVisuals.CreateIconFont(Font.FontFamily, (size - 1f) * z);
        _portFont = new Font(Font.FontFamily, Math.Max(1f, size * z), FontStyle.Bold);
        _fontZoom = z;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        CreateFixedFonts();
        DisposeZoomFonts();
        RefreshView();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        CreateFixedFonts();
        DisposeZoomFonts();
        RefreshView();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RefreshView();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Lần đầu: vừa khung nhưng không nhỏ quá 70% để chữ còn đọc được (bản đồ thu nhỏ giúp đi tới phần còn lại).
        if (!_viewReady && ClientSize.Width > S(100)) ZoomToFit(0.7f);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        EnsureFonts();

        DrawGrid(g, e.ClipRectangle);
        foreach (var j in _layout.Jumps) DrawJump(g, j);
        for (int k = 0; k < _layout.Edges.Count; k++)
            DrawEdge(g, _layout.Edges[k], k == _hoverEdge || k == _dropEdge);

        var view = Rectangle.Inflate(ClientRectangle, S(120), S(120));
        DrawStart(g);
        for (int i = 0; i < _steps.Count; i++)
        {
            if (_layout.Bounds[i] is not { } b) continue;
            var r = ToScreen(b);
            if (!view.IntersectsWith(r)) continue;
            if (_layout.IsMerge[i]) DrawMerge(g, i, r);
            else DrawNode(g, i, r);
        }
        DrawStub(g);

        // Nhánh Nếu / thân lặp còn trống: nút "+" luôn hiện để biết thêm bước vào đâu.
        for (int k = 0; k < _layout.Edges.Count; k++)
            if (_layout.Edges[k].IsSlot && k != _hoverEdge && k != _dropEdge) DrawSlotPlus(g, PlusRect(_layout.Edges[k]));

        int plus = _dropEdge >= 0 ? _dropEdge : _hoverEdge;
        if (plus >= 0 && plus < _layout.Edges.Count && _layout.Edges[plus].Kind != GraphEdgeKind.Stub)
            DrawPlus(g, PlusRect(_layout.Edges[plus]), _dropEdge >= 0);

        if (ShowsToolbar(_hover)) DrawToolbar(g, _hover);
        if (_dragNode >= 0) DrawGhost(g);
        DrawZoomControls(g);
        if (MinimapVisible) DrawMinimap(g);
    }

    private void DrawGrid(Graphics g, Rectangle clip)
    {
        float step = S(20) * _zoom;
        while (step < 9) step *= 2;
        float x0 = _pan.X % step, y0 = _pan.Y % step;
        if (x0 < 0) x0 += step;
        if (y0 < 0) y0 += step;
        float dot = Math.Max(1.2f, 1.6f * _zoom);
        using var brush = new SolidBrush(DotColor);
        for (float x = x0; x < clip.Right; x += step)
        {
            if (x < clip.Left - step) continue;
            for (float y = y0; y < clip.Bottom; y += step)
                if (y >= clip.Top - step) g.FillRectangle(brush, x - dot / 2, y - dot / 2, dot, dot);
        }
    }

    private PointF[] ScreenPoints(PointF[] pts) => pts.Select(ToScreenPoint).ToArray();

    private void DrawEdge(Graphics g, GraphEdge edge, bool hot)
    {
        var pts = ScreenPoints(edge.Points);
        using var pen = new Pen(hot ? HotColor : EdgeColor, Math.Max(1.5f, (hot ? 3f : 2f) * _zoom))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawLines(pen, pts);
        if (edge.Kind == GraphEdgeKind.Stub) return;

        // Mũi tên ở đầu vào.
        var tip = pts[^1];
        var before = pts[^2];
        for (int k = pts.Length - 2; k >= 0 && FlowGraphLayout.Distance(pts[k], tip) < 3; k--) before = pts[k];
        float len = FlowGraphLayout.Distance(before, tip);
        if (len < 0.01f) return;
        float ux = (tip.X - before.X) / len, uy = (tip.Y - before.Y) / len, a = Math.Max(4f, 7f * _zoom), w = a * 0.6f;
        var head = new PointF[]
        {
            tip,
            new(tip.X - ux * a - uy * w, tip.Y - uy * a + ux * w),
            new(tip.X - ux * a + uy * w, tip.Y - uy * a - ux * w)
        };
        using var brush = new SolidBrush(pen.Color);
        g.FillPolygon(brush, head);
    }

    /// <summary>Dây nét đứt cong phía trên: "Nhảy tới nhãn" (tím) hoặc "khi lỗi nhảy tới nhãn" (đỏ).</summary>
    private void DrawJump(Graphics g, GraphJump j)
    {
        var a = _layout.Bounds[j.From]!.Value;
        var b = _layout.Bounds[j.To]!.Value;
        var p1 = ToScreenPoint(new PointF(a.X + a.Width / 2, a.Top));
        var p2 = ToScreenPoint(new PointF(b.X + b.Width / 2, b.Top));
        float top = ToScreenPoint(new PointF(0, _layout.JumpTop(j))).Y;
        var color = j.OnError ? Color.FromArgb(200, ErrorColor) : Color.FromArgb(170, 120, 70, 200);
        using var pen = new Pen(color, Math.Max(1.2f, 1.6f * _zoom)) { DashStyle = DashStyle.Dash, EndCap = LineCap.ArrowAnchor };
        g.DrawBezier(pen, p1, new PointF(p1.X, top), new PointF(p2.X, top), p2);
    }

    private void DrawPlus(Graphics g, Rectangle r, bool active)
    {
        using var path = StepVisuals.RoundRect(r, S(5));
        using (var fill = new SolidBrush(active ? HotColor : Color.White)) g.FillPath(fill, path);
        using (var pen = new Pen(active ? HotColor : Color.FromArgb(120, 127, 138), S(1))) g.DrawPath(pen, path);
        DrawCross(g, r, active ? Color.White : TextColor);
    }

    private void DrawSlotPlus(Graphics g, Rectangle r)
    {
        using var path = StepVisuals.RoundRect(r, S(5));
        using (var fill = new SolidBrush(Color.White)) g.FillPath(fill, path);
        using (var pen = new Pen(Color.FromArgb(160, 166, 176), S(1)) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
        DrawCross(g, r, MutedColor);
    }

    private void DrawCross(Graphics g, Rectangle r, Color color)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, h = r.Width * 0.27f;
        using var pen = new Pen(color, Math.Max(1.5f, r.Width / 11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - h, cy, cx + h, cy);
        g.DrawLine(pen, cx, cy - h, cx, cy + h);
    }

    private void DrawStart(Graphics g)
    {
        var r = ToScreen(_layout.StartBounds);
        int big = r.Height / 2, small = Math.Max(2, (int)(8 * _zoom));
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, big * 2 - 1, big * 2 - 1, 90, 180);
        path.AddArc(r.Right - small * 2, r.Y, small * 2, small * 2, 270, 90);
        path.AddArc(r.Right - small * 2, r.Bottom - small * 2, small * 2, small * 2, 0, 90);
        path.CloseFigure();
        DrawShadow(g, path);
        using (var fill = new SolidBrush(Color.White)) g.FillPath(fill, path);
        using (var pen = new Pen(NodeBorder, 1)) g.DrawPath(pen, path);
        TextRenderer.DrawText(g, StepVisuals.UiGlyph("", "▶"), _glyphFont, r, StartColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        DrawOutPort(g, new PointF(r.Right, r.Y + r.Height / 2f));
        DrawLabels(g, r, "Bắt đầu", _startSubtitle, null, TextColor);
    }

    private void DrawStub(Graphics g)
    {
        var r = ToScreen(_layout.StubBounds);
        bool active = _dropEdge >= 0 && _dropEdge < _layout.Edges.Count && _layout.Edges[_dropEdge].Kind == GraphEdgeKind.Stub;
        if (_steps.Count == 0)
        {
            using var path = StepVisuals.RoundRect(r, Math.Max(3, (int)(8 * _zoom)));
            using (var fill = new SolidBrush(active ? StepVisuals.Tint(HotColor, 0.88f) : Color.White)) g.FillPath(fill, path);
            using (var pen = new Pen(active ? HotColor : Color.FromArgb(150, 156, 166), Math.Max(1.5f, 2 * _zoom)) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
            DrawCross(g, Rectangle.Inflate(r, -r.Width / 4, -r.Height / 4), active ? HotColor : MutedColor);
            DrawLabels(g, r, "Thêm bước đầu tiên", "Bấm +, nhấn Tab hoặc kéo thao tác từ hộp công cụ vào đây", null, TextColor);
            return;
        }
        bool hot = active || StubAt(PointToClient(MousePosition)) && _dragNode < 0;
        using (var path = StepVisuals.RoundRect(r, Math.Max(2, (int)(5 * _zoom))))
        {
            using (var fill = new SolidBrush(active ? HotColor : Color.White)) g.FillPath(fill, path);
            using var pen = new Pen(hot ? HotColor : Color.FromArgb(150, 156, 166), Math.Max(1f, 1.5f * _zoom));
            g.DrawPath(pen, path);
        }
        DrawCross(g, r, active ? Color.White : hot ? HotColor : MutedColor);
    }

    private void DrawShadow(Graphics g, GraphicsPath path)
    {
        using var shadow = (GraphicsPath)path.Clone();
        using var m = new Matrix();
        m.Translate(0, Math.Max(1, 2 * _zoom));
        shadow.Transform(m);
        using var brush = new SolidBrush(Color.FromArgb(24, 0, 0, 0));
        g.FillPath(brush, shadow);
    }

    private void DrawOutPort(Graphics g, PointF p)
    {
        float d = Math.Max(5, 11 * _zoom);
        using var brush = new SolidBrush(PortColor);
        g.FillEllipse(brush, p.X - d / 2, p.Y - d / 2, d, d);
    }

    private void DrawInPort(Graphics g, Rectangle r)
    {
        float w = Math.Max(3, 6 * _zoom), h = Math.Max(6, 16 * _zoom);
        using var brush = new SolidBrush(PortColor);
        g.FillRectangle(brush, r.X - w / 2, r.Y + r.Height / 2f - h / 2, w, h);
    }

    /// <summary>Tên thao tác (đậm) + mô tả (xám) + ghi chú (thử lại, xử lý lỗi) dưới nút.</summary>
    private void DrawLabels(Graphics g, Rectangle node, string title, string subtitle, string? extra, Color titleColor)
    {
        if (_titleFont!.Size < 3.5f) return;
        int w = (int)(_layout.Size.LabelWidth * _zoom);
        int x = node.X + node.Width / 2 - w / 2, y = node.Bottom + (int)(6 * _zoom);
        int bottom = node.Bottom + (int)(_layout.Size.Label * _zoom);
        int titleLine = TextRenderer.MeasureText(g, "Ag", _titleFont, Size.Empty, TextFormatFlags.NoPadding).Height;
        int titleH = Math.Min(TextRenderer.MeasureText(g, title, _titleFont, new Size(w, int.MaxValue), CenterText).Height, titleLine * 2);
        TextRenderer.DrawText(g, title, _titleFont, new Rectangle(x, y, w, titleH), titleColor, CenterText);
        y += titleH + (int)(2 * _zoom);
        if (_zoom < 0.45f) return;

        int subLine = TextRenderer.MeasureText(g, "Ag", _subFont, Size.Empty, TextFormatFlags.NoPadding).Height;
        int extraH = string.IsNullOrEmpty(extra) ? 0 : subLine;
        int subH = Math.Min(Math.Min(TextRenderer.MeasureText(g, subtitle, _subFont, new Size(w, int.MaxValue), CenterText).Height, subLine * 2),
            Math.Max(0, bottom - y - extraH));
        if (subH >= subLine)
        {
            TextRenderer.DrawText(g, subtitle, _subFont, new Rectangle(x, y, w, subH), MutedColor, CenterText);
            y += subH;
        }
        if (extraH > 0 && y + extraH <= bottom + subLine / 2)
            TextRenderer.DrawText(g, extra, _smallFont, new Rectangle(x, y, w, extraH), Color.FromArgb(160, 100, 0), CenterText);
    }

    private void DrawNode(Graphics g, int i, Rectangle r)
    {
        var step = _steps[i];
        bool selected = i == _selected, hover = i == _hover;
        bool invalid = i < _structure.Invalid.Length && _structure.Invalid[i];
        bool running = i == _running, failed = i == _failed, done = _done.Contains(i) && !running && !failed;
        var accent = step.Enabled ? StepVisuals.Accent(step.Type) : Color.FromArgb(160, 160, 160);
        int radius = Math.Max(3, (int)(8 * _zoom));
        float z = _zoom;

        using var path = StepVisuals.RoundRect(r, radius);
        if (selected || running || failed)
        {
            var halo = Rectangle.Inflate(r, (int)Math.Max(3, 5 * z), (int)Math.Max(3, 5 * z));
            using var haloPath = StepVisuals.RoundRect(halo, radius + (int)(5 * z));
            using var haloBrush = new SolidBrush(Color.FromArgb(failed ? 60 : 50, failed ? ErrorColor : running ? RunColor : HotColor));
            g.FillPath(haloBrush, haloPath);
        }
        DrawShadow(g, path);
        using (var fill = new SolidBrush(step.Enabled ? Color.White : Color.FromArgb(243, 243, 244))) g.FillPath(fill, path);

        // Biểu tượng hoặc hình mẫu (click theo hình…) ở giữa nút.
        var inner = Rectangle.Inflate(r, -(int)(12 * z), -(int)(12 * z));
        if (Thumbnail(step) is { } thumb && inner.Width > 4)
        {
            double k = Math.Min((double)inner.Width / thumb.Width, (double)inner.Height / thumb.Height);
            k = Math.Min(k, 2 * z);
            int w = Math.Max(1, (int)(thumb.Width * k)), h = Math.Max(1, (int)(thumb.Height * k));
            var box = new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            var mode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(thumb, box);
            g.InterpolationMode = mode;
            using var pen = new Pen(Color.FromArgb(200, 204, 210));
            g.DrawRectangle(pen, box.X - 1, box.Y - 1, box.Width + 1, box.Height + 1);
            if (step.Type == StepType.PlayMedia) DrawPlayBadge(g, box, accent, z);
        }
        else
        {
            TextRenderer.DrawText(g, StepVisuals.IconText(step.Type), _glyphFont, r, accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        // Dải màu nhóm thao tác ở cạnh trái.
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        using (var strip = new SolidBrush(invalid ? ErrorColor : accent)) g.FillRectangle(strip, r.X, r.Y, Math.Max(2, 4 * z), r.Height);
        g.Restore(state);

        if (!step.Enabled)
        {
            using var strike = new Pen(Color.FromArgb(150, 150, 150), Math.Max(1, 1.5f * z));
            g.DrawLine(strike, r.X + radius, r.Bottom - radius, r.Right - radius, r.Y + radius);
        }

        var border = invalid || failed ? ErrorColor : running ? RunColor : done ? DoneColor : selected ? HotColor
            : hover ? Color.FromArgb(130, 136, 146) : NodeBorder;
        bool thick = invalid || failed || running || done || selected;
        using (var pen = new Pen(border, thick ? Math.Max(1.5f, 2.2f * z) : 1)) g.DrawPath(pen, path);

        if (running)
        {
            var ring = Rectangle.Inflate(r, (int)(5 * z), (int)(5 * z));
            using var arc = new Pen(RunColor, Math.Max(2, 3 * z)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, ring, _spin, 70);
            g.DrawArc(arc, ring, _spin + 180, 70);
        }

        // Số thứ tự bước (góc trái trên).
        if (z >= 0.55f)
            TextRenderer.DrawText(g, (i + 1).ToString(), _smallFont, new Rectangle(r.X + (int)(7 * z), r.Y + (int)(4 * z), r.Width / 2, (int)(16 * z)),
                MutedColor, OneLine);

        // Huy hiệu trạng thái (góc phải dưới).
        string? badge = failed || invalid ? StepVisuals.UiGlyph("", "!") : done ? StepVisuals.UiGlyph("", "✓") : null;
        if (badge != null && z >= 0.4f)
        {
            var color = failed || invalid ? ErrorColor : DoneColor;
            TextRenderer.DrawText(g, badge, _badgeFont, new Rectangle(r.Right - (int)(24 * z), r.Bottom - (int)(22 * z), (int)(20 * z), (int)(18 * z)),
                color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        if (step.Breakpoint)
        {
            float d = Math.Max(8, 14 * z);
            using var bp = new SolidBrush(BreakpointColor);
            using var ring = new Pen(Color.White, Math.Max(1, 2 * z));
            g.FillEllipse(bp, r.X - d / 2, r.Y - d / 2, d, d);
            g.DrawEllipse(ring, r.X - d / 2, r.Y - d / 2, d, d);
        }

        DrawInPort(g, r);
        float cy = r.Y + r.Height / 2f;
        if (_layout.IsIfBlock(i))
        {
            DrawPortLabel(g, new PointF(r.Right, cy - r.Height / 4f), FlowGraphLayout.TruePort, TrueColor);
            DrawPortLabel(g, new PointF(r.Right, cy + r.Height / 4f), FlowGraphLayout.FalsePort, FalseColor);
        }
        else if (_layout.IsLoopBlock(i))
        {
            DrawPortLabel(g, new PointF(r.Right, cy - r.Height / 4f), FlowGraphLayout.DonePort, MutedColor);
            DrawPortLabel(g, new PointF(r.Right, cy + r.Height / 4f), FlowGraphLayout.LoopPort, StepVisuals.Accent(StepType.Loop));
        }
        else DrawOutPort(g, new PointF(r.Right, cy));

        var title = ActionStep.TypeNames[step.Type] + (step.Enabled ? "" : " (đã tắt)");
        var extras = new List<string>();
        if (step.Retries > 0) extras.Add($"thử lại {step.Retries} lần");
        if (step.OnError == ErrorAction.Continue) extras.Add("bỏ qua lỗi");
        if (step.OnError == ErrorAction.GotoLabel) extras.Add($"lỗi → {step.ErrorLabel}");
        if (invalid) extras.Add("lỗi cấu trúc");
        DrawLabels(g, r, title, step.Describe(), extras.Count > 0 ? string.Join(" · ", extras) : null,
            invalid ? ErrorColor : step.Enabled ? TextColor : MutedColor);
    }

    private void DrawPortLabel(Graphics g, PointF port, string text, Color color)
    {
        DrawOutPort(g, port);
        if (_zoom < 0.5f) return;
        var size = TextRenderer.MeasureText(g, text, _portFont, Size.Empty, TextFormatFlags.NoPadding);
        var rect = new Rectangle((int)(port.X + 8 * _zoom), (int)(port.Y - size.Height - 2 * _zoom), size.Width + 2, size.Height);
        TextRenderer.DrawText(g, text, _portFont, rect, color, OneLine);
    }

    /// <summary>Nút gộp tròn của "Hết Nếu".</summary>
    private void DrawMerge(Graphics g, int i, Rectangle r)
    {
        bool invalid = i < _structure.Invalid.Length && _structure.Invalid[i];
        var color = invalid ? ErrorColor : i == _selected ? HotColor : i == _hover ? Color.FromArgb(120, 126, 136) : NodeBorder;
        using (var fill = new SolidBrush(Color.White)) g.FillEllipse(fill, r);
        using (var pen = new Pen(color, Math.Max(1.2f, (i == _selected ? 2.2f : 1.5f) * _zoom))) g.DrawEllipse(pen, r);
        var dot = Rectangle.Inflate(r, -r.Width / 3, -r.Height / 3);
        using var brush = new SolidBrush(StepVisuals.Accent(StepType.If));
        g.FillEllipse(brush, dot);
    }

    private void DrawToolbar(Graphics g, int i)
    {
        var t = ToolbarRect(i);
        using (var path = StepVisuals.RoundRect(t, S(6)))
        {
            DrawShadow(g, path);
            using var fill = new SolidBrush(Color.White);
            g.FillPath(fill, path);
            using var pen = new Pen(NodeBorder);
            g.DrawPath(pen, path);
        }
        for (int k = 0; k < ToolbarButtons.Length; k++)
        {
            var b = ToolbarButton(i, k);
            if (k == _hoverButton)
            {
                using var path = StepVisuals.RoundRect(b, S(4));
                using var hb = new SolidBrush(Color.FromArgb(236, 238, 242));
                g.FillPath(hb, path);
            }
            var glyph = ToolbarButtons[k];
            var color = k == 2 && k == _hoverButton ? ErrorColor : k == 1 && !_steps[i].Enabled ? HotColor : TextColor;
            TextRenderer.DrawText(g, StepVisuals.UiGlyph(glyph.Glyph, glyph.Fallback), _uiGlyph, b, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    private void DrawGhost(Graphics g)
    {
        if (_dragNode < 0 || _dragNode >= _steps.Count) return;
        int n = (int)(_layout.Size.Node * _zoom);
        var r = new Rectangle(_dragPos.X - n / 2, _dragPos.Y - n / 2, n, n);
        var step = _steps[_dragNode];
        using var path = StepVisuals.RoundRect(r, Math.Max(3, (int)(8 * _zoom)));
        using (var fill = new SolidBrush(Color.FromArgb(215, 255, 255, 255))) g.FillPath(fill, path);
        using (var pen = new Pen(HotColor, Math.Max(1.5f, 2 * _zoom)) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
        TextRenderer.DrawText(g, StepVisuals.IconText(step.Type), _glyphFont, r, StepVisuals.Accent(step.Type),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var (start, end) = BlockRange(_dragNode);
        if (end > start)
        {
            var note = new Rectangle(r.X - n / 2, r.Bottom + S(4), n * 2, S(18));
            TextRenderer.DrawText(g, $"cả khối ({end - start + 1} bước)", _uiFont, note, HotColor, OneLine | TextFormatFlags.HorizontalCenter);
        }
    }

    private void DrawZoomControls(Graphics g)
    {
        var mouse = PointToClient(MousePosition);
        for (int k = 0; k < ZoomButtons.Length; k++)
        {
            var b = ZoomButton(k);
            using var path = StepVisuals.RoundRect(b, S(6));
            using (var fill = new SolidBrush(b.Contains(mouse) ? Color.FromArgb(236, 238, 242) : Color.White)) g.FillPath(fill, path);
            using (var pen = new Pen(NodeBorder)) g.DrawPath(pen, path);
            bool text = k == 3;
            TextRenderer.DrawText(g, text ? $"{Math.Round(_zoom * 100)}%" : StepVisuals.UiGlyph(ZoomButtons[k].Glyph, ZoomButtons[k].Fallback),
                text ? _uiFont : _uiGlyph, b, TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    private void DrawMinimap(Graphics g)
    {
        var m = MinimapRect;
        using (var path = StepVisuals.RoundRect(m, S(6)))
        {
            using var fill = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            g.FillPath(fill, path);
            using var pen = new Pen(NodeBorder);
            g.DrawPath(pen, path);
        }
        var (k, o) = MinimapTransform();
        RectangleF Map(RectangleF r) => new(r.X * k + o.X, r.Y * k + o.Y, Math.Max(2, r.Width * k), Math.Max(2, r.Height * k));

        var state = g.Save();
        g.SetClip(m);
        using (var start = new SolidBrush(StepVisuals.Tint(StartColor, 0.4f))) g.FillRectangle(start, Map(_layout.StartBounds));
        for (int i = 0; i < _steps.Count; i++)
        {
            if (_layout.Bounds[i] is not { } b) continue;
            var color = i == _running ? RunColor : i == _failed ? ErrorColor : i == _selected ? HotColor
                : StepVisuals.Tint(StepVisuals.Accent(_steps[i].Type), _layout.IsMerge[i] ? 0.6f : 0.3f);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, Map(b));
        }
        var tl = ToContent(Point.Empty);
        var br = ToContent(new Point(ClientSize.Width, ClientSize.Height));
        var view = Map(RectangleF.FromLTRB(tl.X, tl.Y, br.X, br.Y));
        using (var vf = new SolidBrush(Color.FromArgb(30, HotColor))) g.FillRectangle(vf, view);
        using (var vp = new Pen(HotColor, S(1))) g.DrawRectangle(vp, view.X, view.Y, view.Width, view.Height);
        g.Restore(state);
    }

    // ───────────────────────────── Chuột ─────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.Button == MouseButtons.Middle)
        {
            StartPan(e.Location);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            if (ZoomButtonAt(e.Location) is int zb and >= 0)
            {
                ClickZoomButton(zb);
                return;
            }
            if (MinimapVisible && MinimapRect.Contains(e.Location))
            {
                _minimapDrag = true;
                Capture = true;
                CenterOnMinimap(e.Location);
                return;
            }
            if (ToolbarButtonAt(e.Location) is int tb and >= 0)
            {
                ClickToolbar(_hover, tb, e.Location);
                return;
            }
        }

        int i = NodeAt(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            if (i >= 0)
            {
                SelectStep(i);
                _miToggle.Text = _steps[i].Enabled ? "Tắt bước  (Space)" : "Bật bước  (Space)";
                _miBreakpoint.Checked = _steps[i].Breakpoint;
                _miBreakpoint.Enabled = !StepVisuals.IsMarker(_steps[i].Type);
                _menu.Show(this, e.Location);
            }
            else _canvasMenu.Show(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (i >= 0)
        {
            SelectStep(i);
            if (_layout.IsStepNode(i))
            {
                _pressNode = i;
                _pressPoint = e.Location;
            }
            return;
        }
        if (StubAt(e.Location))
        {
            OpenPicker(StubEdge, new Point(ToScreen(_layout.StubBounds).Right + S(8), ToScreen(_layout.StubBounds).Top));
            return;
        }
        // Bấm vào nút "+" hoặc bất kỳ chỗ nào trên dây (kể cả "+" luôn hiện của nhánh trống) → chọn thao tác chèn vào đó.
        int edge = EdgeAt(e.Location);
        if (edge < 0) edge = _layout.Edges.FindIndex(x => x.IsSlot && PlusRect(x).Contains(e.Location));
        if (edge >= 0)
        {
            var plus = PlusRect(_layout.Edges[edge]);
            OpenPicker(_layout.Edges[edge], new Point(plus.Right + S(8), plus.Top));
            return;
        }
        StartPan(e.Location);
    }

    private void StartPan(Point p)
    {
        _panning = true;
        _panMoved = false;
        _panMouse = p;
        _panStart = _pan;
        Capture = true;
        Cursor = Cursors.SizeAll;
    }

    private void ClickToolbar(int i, int button, Point p)
    {
        SelectStep(i);
        switch (button)
        {
            case 0: RunFromRequested?.Invoke(i); break;
            case 1: ToggleSelected(); break;
            case 2: DeleteSelected(); break;
            default:
                _miToggle.Text = _steps[i].Enabled ? "Tắt bước  (Space)" : "Bật bước  (Space)";
                _miBreakpoint.Checked = _steps[i].Breakpoint;
                _miBreakpoint.Enabled = !StepVisuals.IsMarker(_steps[i].Type);
                _menu.Show(this, p);
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_minimapDrag)
        {
            CenterOnMinimap(e.Location);
            return;
        }
        if (_panning)
        {
            int dx = e.X - _panMouse.X, dy = e.Y - _panMouse.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 2) _panMoved = true;
            _pan = new PointF(_panStart.X + dx, _panStart.Y + dy);
            Invalidate();
            return;
        }

        if (_pressNode >= 0 && e.Button == MouseButtons.Left)
        {
            var dragBox = new Rectangle(
                _pressPoint.X - SystemInformation.DragSize.Width / 2,
                _pressPoint.Y - SystemInformation.DragSize.Height / 2,
                SystemInformation.DragSize.Width, SystemInformation.DragSize.Height);
            if (!dragBox.Contains(e.Location))
            {
                _dragNode = _pressNode;
                _pressNode = -1;
                _hover = -1;
                Capture = true;
                Cursor = Cursors.SizeAll;
            }
        }

        if (_dragNode >= 0)
        {
            _dragPos = e.Location;
            AutoPanNear(e.Location);
            _dropEdge = NearestEdge(ToContent(e.Location), _dragNode);
            Invalidate();
            return;
        }

        UpdateHover(e.Location);
    }

    /// <summary>Kéo gần mép khung thì tự cuộn theo.</summary>
    private void AutoPanNear(Point p)
    {
        int m = S(28), v = S(14);
        float dx = p.X < m ? v : p.X > ClientSize.Width - m ? -v : 0;
        float dy = p.Y < m ? v : p.Y > ClientSize.Height - m ? -v : 0;
        if (dx != 0 || dy != 0) PanBy(dx, dy);
    }

    private void UpdateHover(Point p)
    {
        int button = ToolbarButtonAt(p);
        int hover = button >= 0 || InHoverZone(_hover, p) && NodeAt(p) is var n && (n < 0 || n == _hover) ? _hover : NodeAt(p);
        int edge = hover < 0 ? EdgeAt(p) : -1;
        bool stub = hover < 0 && edge < 0 && StubAt(p);
        int zoomButton = ZoomButtonAt(p);

        if (hover != _hover || edge != _hoverEdge || button != _hoverButton)
        {
            _hover = hover;
            _hoverEdge = edge;
            _hoverButton = button;
            Invalidate();
        }
        else if (zoomButton >= 0 || stub) Invalidate();

        Cursor = button >= 0 || zoomButton >= 0 || stub || edge >= 0 ? Cursors.Hand
            : hover >= 0 ? (_layout.IsStepNode(hover) ? Cursors.SizeAll : Cursors.Hand)
            : MinimapVisible && MinimapRect.Contains(p) ? Cursors.Hand : Cursors.Default;

        string tip = button >= 0 ? ToolbarButtons[button].Tip
            : zoomButton >= 0 ? ZoomButtons[zoomButton].Tip
            : stub ? "Thêm bước vào cuối flow"
            : edge >= 0 ? (_layout.Edges[edge].NeedsElse ? "Thêm bước vào nhánh \"sai\" (tự tạo \"Không thì\")" : "Bấm + để thêm bước vào đây")
            : hover >= 0 ? NodeTip(hover)
            : "";
        if (tip != _tipText)
        {
            _tipText = tip;
            _tip.SetToolTip(this, tip);
        }
    }

    private string NodeTip(int i)
    {
        var errors = _structure.Errors.Where(x => x.StartsWith($"Bước {i + 1}:")).ToList();
        if (errors.Count > 0) return string.Join("\n", errors);
        if (_layout.IsMerge[i]) return "Hết Nếu — hai nhánh gộp lại, chạy tiếp các bước sau";
        return $"{i + 1}. {ActionStep.TypeNames[_steps[i].Type]}\n{_steps[i].Describe()}\nNhấp đúp để sửa · kéo thả lên dây khác để di chuyển";
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragNode >= 0)
        {
            int from = _dragNode, target = _dropEdge;
            _dragNode = -1;
            _dropEdge = -1;
            Capture = false;
            if (target >= 0 && target < _layout.Edges.Count) MoveToEdge(from, _layout.Edges[target]);
            Invalidate();
        }
        if (_panning)
        {
            _panning = false;
            Capture = false;
            if (!_panMoved && e.Button == MouseButtons.Left) SelectStep(-1);
        }
        _minimapDrag = false;
        _pressNode = -1;
        UpdateHover(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1 || _hoverEdge != -1 || _hoverButton != -1)
        {
            _hover = _hoverEdge = _hoverButton = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || ToolbarButtonAt(e.Location) >= 0) return;
        int i = NodeAt(e.Location);
        if (i >= 0 && _layout.IsStepNode(i)) EditRequested?.Invoke(i);
        else if (i < 0 && StartAt(e.Location)) OpenPicker(_layout.Edges.First(x => x.InsertIndex == 0), new Point(e.X + S(8), e.Y));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0) SetZoom(_zoom * (e.Delta > 0 ? 1.12f : 1 / 1.12f), e.Location);
        else if ((ModifierKeys & Keys.Shift) != 0) PanBy(e.Delta * 0.6f, 0);
        else PanBy(0, e.Delta * 0.6f);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEHWHEEL = 0x020E;
        if (m.Msg == WM_MOUSEHWHEEL)
        {
            int delta = (short)((long)m.WParam >> 16 & 0xFFFF);
            PanBy(-delta * 0.6f, 0);
            m.Result = 1;
            return;
        }
        base.WndProc(ref m);
    }

    // ───────────────────────────── Chọn thao tác ─────────────────────────────

    private void OpenPicker(GraphEdge edge, Point clientAnchor) => ShowPicker(edge, this, PointToScreen(clientAnchor));

    private void ShowPicker(GraphEdge edge, Control owner, Point screen)
    {
        if (AddRequested == null) return;
        var picker = new NodePicker(this, edge.NeedsElse ? "Thêm bước vào nhánh \"sai\"" : edge.Kind == GraphEdgeKind.Stub ? "Thêm bước vào cuối" : "Thêm bước vào đây");
        picker.Picked += type => BeginInvoke(new MethodInvoker(() => AddViaEdge(type, edge)));
        picker.ShowAt(owner, screen);
    }

    /// <summary>Tab ở cách nhìn khác: chọn thao tác để chèn sau bước (khối) đang chọn, hộp chọn mở tại <paramref name="screen"/>.</summary>
    internal void OpenPickerAfterSelected(Control owner, Point screen) => ShowPicker(EdgeAfterSelected(), owner, screen);

    /// <summary>Dây ngay sau bước (hoặc cả khối Nếu / Lặp) đang chọn; chưa chọn gì → dây cuối flow.</summary>
    private GraphEdge EdgeAfterSelected()
    {
        if (_selected < 0) return StubEdge;
        int index = BlockRange(_selected).End + 1;
        return _layout.Edges.FirstOrDefault(x => x.InsertIndex == index && !x.NeedsElse && x.Kind != GraphEdgeKind.LoopBack)
               ?? _layout.Edges.FirstOrDefault(x => x.InsertIndex == index && !x.NeedsElse) ?? StubEdge;
    }

    private void OpenPicker(GraphEdge edge, PointF clientAnchor) => OpenPicker(edge, Point.Round(clientAnchor));

    /// <summary>Tab: chọn thao tác để chèn ngay sau bước (hoặc khối) đang chọn; chưa chọn gì thì thêm vào cuối.</summary>
    private void OpenPickerAfterSelected()
    {
        var edge = EdgeAfterSelected();
        var p = ToScreenPoint(edge.Mid);
        if (!ClientRectangle.Contains(Point.Round(p))) p = new PointF(ClientSize.Width / 3f, ClientSize.Height / 4f);
        OpenPicker(edge, new PointF(p.X + S(14), p.Y));
    }

    // ───────────────────────────── Kéo thả từ ngoài ─────────────────────────────

    private static DragDropEffects EffectFor(DragEventArgs e)
    {
        var data = e.Data;
        if (data == null) return DragDropEffects.None;
        if (data.GetDataPresent(StepVisuals.StepTypeFormat) || data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = EffectFor(e);
        UpdateDropEdge(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = EffectFor(e);
        if (e.Effect != DragDropEffects.None) AutoPanNear(PointToClient(new Point(e.X, e.Y)));
        UpdateDropEdge(e);
    }

    private void UpdateDropEdge(DragEventArgs e)
    {
        int edge = e.Effect == DragDropEffects.None ? -1 : NearestEdge(ToContent(PointToClient(new Point(e.X, e.Y))));
        if (edge != _dropEdge)
        {
            _dropEdge = edge;
            Invalidate();
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dropEdge = -1;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        int target = _dropEdge >= 0 ? _dropEdge : _layout.Edges.FindIndex(x => x.Kind == GraphEdgeKind.Stub);
        _dropEdge = -1;
        Invalidate();
        var edge = _layout.Edges[target];

        var data = e.Data;
        if (data == null) return;
        if (data.GetDataPresent(StepVisuals.StepTypeFormat))
        {
            if (Enum.TryParse<StepType>(data.GetData(StepVisuals.StepTypeFormat) as string, out var type))
            {
                // Mở trình soạn sau khi thao tác kéo thả kết thúc hẳn.
                BeginInvoke(new MethodInvoker(() => AddViaEdge(type, edge)));
            }
        }
        else if (data.GetData(DataFormats.FileDrop) is string[] files)
        {
            InsertViaEdge(edge, files.Select(file =>
            {
                var step = ActionStep.CreateDefault(StepType.LaunchApp);
                step.Target = file;
                return step;
            }).ToList());
        }
    }

    // ───────────────────────────── Bàn phím ─────────────────────────────

    // Tab (không kèm phím khác) mở hộp chọn thao tác như n8n; Shift+Tab vẫn chuyển ô. Esc chỉ giữ lại khi đang kéo nút.
    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter or Keys.Space
        || keyData == Keys.Tab || keyData == Keys.Escape && _dragNode >= 0
        || base.IsInputKey(keyData);

    /// <summary>Bước hiện trên sơ đồ liền trước / liền sau bước đang chọn (bỏ qua "Không thì", "Hết lặp" đã ẩn).</summary>
    private int NeighborVisible(int delta)
    {
        if (_steps.Count == 0) return -1;
        int i = _selected < 0 ? (delta > 0 ? -1 : _steps.Count) : _selected;
        for (int k = i + delta; k >= 0 && k < _steps.Count; k += delta)
            if (_layout.IsVisible(k)) return k;
        return _selected;
    }

    /// <summary>
    /// Phím tắt sửa flow dùng chung với cách nhìn khác (Delete, Space, F9, Ctrl+C/V/D, Ctrl+↑/↓, Tab, Enter) — không gồm di chuyển
    /// vùng chọn / thu phóng (mỗi cách nhìn tự xử lý). True nếu đã xử lý.
    /// </summary>
    internal bool HandleEditKey(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up or Keys.Left when e.Control: MoveSelected(-1); return true;
            case Keys.Down or Keys.Right when e.Control: MoveSelected(1); return true;
            case Keys.Delete: DeleteSelected(); return true;
            case Keys.Space: ToggleSelected(); return true;
            case Keys.F9: ToggleBreakpoint(); return true;
            case Keys.C when e.Control: CopySelected(); return true;
            case Keys.V when e.Control: Paste(); return true;
            case Keys.D when e.Control: DuplicateSelected(); return true;
            case Keys.Tab when !e.Control && !e.Alt && !e.Shift: OpenPickerAfterSelected(); return true;
            case Keys.Enter:
                if (_selected >= 0 && !StepVisuals.IsMarker(_steps[_selected].Type)) EditRequested?.Invoke(_selected);
                return true;
            default: return false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up or Keys.Left when e.Control: MoveSelected(-1); break;
            case Keys.Down or Keys.Right when e.Control: MoveSelected(1); break;
            case Keys.Up or Keys.Left: SelectStep(NeighborVisible(-1)); break;
            case Keys.Down or Keys.Right: SelectStep(NeighborVisible(1)); break;
            case Keys.Home: SelectStep(0); break;
            case Keys.End: SelectStep(_steps.Count - 1); break;
            case Keys.Delete: DeleteSelected(); break;
            case Keys.Space: ToggleSelected(); break;
            case Keys.F9: ToggleBreakpoint(); break;
            case Keys.C when e.Control: CopySelected(); break;
            case Keys.V when e.Control: Paste(); break;
            case Keys.D when e.Control: DuplicateSelected(); break;
            case Keys.Oemplus or Keys.Add when e.Control: ZoomIn(); break;
            case Keys.OemMinus or Keys.Subtract when e.Control: ZoomOut(); break;
            case Keys.D0 or Keys.NumPad0 when !e.Shift && !e.Alt: ResetZoom(); break;
            case Keys.D1 or Keys.NumPad1 when !e.Control && !e.Shift && !e.Alt: ZoomToFit(); break;
            case Keys.Tab when !e.Control && !e.Alt && !e.Shift: OpenPickerAfterSelected(); break;
            case Keys.Escape when _dragNode >= 0:
                _dragNode = _dropEdge = -1;
                Capture = false;
                Invalidate();
                break;
            case Keys.Enter:
                if (_selected >= 0 && _layout.IsStepNode(_selected)) EditRequested?.Invoke(_selected);
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

    /// <summary>Nút ▶ nhỏ ở góc ảnh video — vẫn nhận ra đây là bước phát video.</summary>
    private static void DrawPlayBadge(Graphics g, Rectangle box, Color accent, float z)
    {
        int d = Math.Max(10, (int)(18 * z));
        var c = new Rectangle(box.Right - d + (int)(4 * z), box.Bottom - d + (int)(4 * z), d, d);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(accent)) g.FillEllipse(fill, c);
        using (var ring = new Pen(Color.White, Math.Max(1, 1.5f * z))) g.DrawEllipse(ring, c);
        using (var white = new SolidBrush(Color.White))
            g.FillPolygon(white, [
                new PointF(c.X + c.Width * 0.40f, c.Y + c.Height * 0.28f),
                new PointF(c.X + c.Width * 0.40f, c.Y + c.Height * 0.72f),
                new PointF(c.X + c.Width * 0.74f, c.Y + c.Height * 0.50f)
            ]);
        g.SmoothingMode = smoothing;
    }

    /// <summary>Ảnh thu nhỏ của file video đầu tiên trong danh sách phát (tải ở nền, vẽ lại khi có); null = chưa có / không có.</summary>
    internal Bitmap? MediaThumbnail(ActionStep step)
    {
        var line = FirstMediaLine(step);
        if (_mediaThumbs.TryGetValue(step, out var cached))
        {
            if (cached.Line == line) return cached.Thumb is { IsIcon: false } t ? t.Image : null;
            cached.Thumb?.Dispose();
            _mediaThumbs.Remove(step);
        }
        if (line == null) return null;
        _mediaThumbs[step] = (line, null);
        _ = LoadMediaThumbnailAsync(step, line);
        return null;
    }

    /// <summary>
    /// Dòng đầu của danh sách phát đã thay biến; null = danh sách trống / biến chưa có giá trị / chỉ biết khi chạy.
    /// Gọi khi vẽ nên chỉ thay biến khi dòng gốc hoặc hàm thay đổi (nhớ kết quả theo bước).
    /// </summary>
    internal string? FirstMediaLine(ActionStep step)
    {
        var first = step.MediaLines.FirstOrDefault();
        if (first == null || !first.Contains("{{")) return first;
        if (_expandedLines.TryGetValue(step, out var cached) && cached.Raw == first) return cached.Expanded;
        string? expanded = null;
        if (_expandMediaLine != null && !IsRuntimeOnlyMediaLine(first))
        {
            try
            {
                var value = _expandMediaLine(first);
                expanded = value.Contains("{{") ? null : value;
            }
            catch (Exception ex)
            {
                // Biến chưa có giá trị, định dạng ngày sai, số quá lớn… → hiện biểu tượng thường, không làm hỏng việc vẽ.
                System.Diagnostics.Debug.WriteLine($"FlowDesigner media line \"{first}\": {ex.Message}");
            }
        }
        _expandedLines[step] = (first, expanded);
        return expanded;
    }

    private async Task LoadMediaThumbnailAsync(ActionStep step, string line)
    {
        var ct = _mediaCts.Token;
        MediaThumbnails.Thumbnail? thumb = null;
        try
        {
            thumb = await Task.Run(async () =>
            {
                try
                {
                    // Dòng là thư mục → file đầu tiên theo tên (đúng thứ tự phát).
                    var (files, _) = MediaPlayback.Resolve([line]);
                    return files.Count > 0 ? await MediaThumbnails.GetAsync(files[0], 256, ct) : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return null;
                }
            }, ct);
        }
        catch (OperationCanceledException) { }
        // Trong lúc tải: nút đã bị xóa / đổi danh sách phát / canvas đã đóng → bỏ ảnh.
        if (IsDisposed || !_mediaThumbs.TryGetValue(step, out var current) || current.Line != line || current.Thumb != null)
        {
            thumb?.Dispose();
            return;
        }
        _mediaThumbs[step] = (line, thumb);
        if (thumb is { IsIcon: false }) Invalidate();
    }

    /// <summary>Hình mẫu của bước đã giải mã (giữ lại theo chuỗi ImageData để không giải mã PNG mỗi lần vẽ).</summary>
    private Bitmap? Thumbnail(ActionStep step)
    {
        if (step.Type == StepType.PlayMedia) return MediaThumbnail(step);
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
        foreach (var step in _mediaThumbs.Keys.Where(s => !_steps.Contains(s)).ToList())
        {
            _mediaThumbs[step].Thumb?.Dispose();
            _mediaThumbs.Remove(step);
        }
        foreach (var step in _expandedLines.Keys.Where(s => !_steps.Contains(s)).ToList()) _expandedLines.Remove(step);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var (_, image) in _thumbs.Values) image.Dispose();
            _thumbs.Clear();
            _mediaCts.Cancel();
            _mediaCts.Dispose();
            foreach (var (_, thumb) in _mediaThumbs.Values) thumb?.Dispose();
            _mediaThumbs.Clear();
            _spinTimer.Dispose();
            _menu.Dispose();
            _canvasMenu.Dispose();
            _tip.Dispose();
            DisposeZoomFonts();
            _uiGlyph.Dispose();
            _uiFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
