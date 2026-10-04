using System.Diagnostics;
using System.Drawing.Drawing2D;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Sửa danh sách phát dạng văn bản (mỗi dòng một file / thư mục, dòng # là ghi chú) theo thứ tự mục phát.</summary>
internal static class PlaylistText
{
    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Dòng là một mục phát (giống <see cref="ActionStep.MediaLines"/>: không trống, không phải ghi chú #).</summary>
    private static bool IsItem(string line)
    {
        var t = line.Trim();
        return t.Length > 0 && !t.StartsWith('#');
    }

    private static List<int> ItemLines(string[] lines) => Enumerable.Range(0, lines.Length).Where(i => IsItem(lines[i])).ToList();

    /// <summary>Chuyển mục thứ <paramref name="from"/> tới vị trí <paramref name="to"/> (tính trên danh sách sau khi chuyển); dòng ghi chú, dòng trống giữ chỗ.</summary>
    public static string Move(string text, int from, int to)
    {
        var lines = Lines(text);
        var slots = ItemLines(lines);
        if (from < 0 || from >= slots.Count) return text;
        to = Math.Clamp(to, 0, slots.Count - 1);
        if (from == to) return text;
        var items = slots.Select(i => lines[i]).ToList();
        var moved = items[from];
        items.RemoveAt(from);
        items.Insert(to, moved);
        for (int k = 0; k < slots.Count; k++) lines[slots[k]] = items[k];
        return string.Join("\r\n", lines);
    }

    /// <summary>Bỏ mục thứ <paramref name="item"/> (cả dòng) khỏi danh sách.</summary>
    public static string Remove(string text, int item)
    {
        var lines = Lines(text).ToList();
        var slots = ItemLines([.. lines]);
        if (item < 0 || item >= slots.Count) return text;
        lines.RemoveAt(slots[item]);
        return string.Join("\r\n", lines);
    }

    /// <summary>Thêm file / thư mục vào cuối danh sách, mỗi đường dẫn một dòng.</summary>
    public static string Append(string text, IEnumerable<string> paths)
    {
        var existing = text.Replace("\r\n", "\n").TrimEnd('\n', '\r');
        return string.Join("\r\n", (existing.Length > 0 ? existing.Split('\n') : []).Concat(paths));
    }

    /// <summary>File video / nhạc và thư mục trong dữ liệu kéo thả (vd từ Explorer), theo thứ tự tên.</summary>
    public static string[] DroppedPaths(IDataObject? data) =>
        data?.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.Where(p => Directory.Exists(p) || (File.Exists(p) && MediaPlayback.Extensions.Contains(Path.GetExtension(p))))
                .Order(MediaPlayback.NameOrder).ToArray()
            : [];
}

/// <summary>
/// Dải ảnh thu nhỏ của danh sách phát (như Explorer): số thứ tự phát, khung hình đầu, thời lượng, tên file; mục lỗi / dùng biến hiện rõ.
/// Kéo một ảnh để đổi thứ tự, nhấp đúp để phát thử, chuột phải để sắp xếp / bỏ; kéo file video từ Explorer vào để thêm.
/// Ô văn bản vẫn là nơi lưu danh sách — dải chỉ báo thao tác qua sự kiện, theo thứ tự mục (<see cref="MediaInfo.Entry.Item"/>).
/// </summary>
internal sealed class MediaStrip : Control
{
    private const int MaxTiles = 40;
    private const int ThumbRequest = 256;

    private static readonly Color TileBack = Color.FromArgb(236, 236, 238);
    private static readonly Color VariableBack = Color.FromArgb(234, 241, 251);
    private static readonly Color ProblemBack = Color.FromArgb(253, 231, 233);
    private static readonly Color ProblemFore = Color.FromArgb(196, 43, 28);
    private static readonly Color Accent = StepVisuals.Accent(StepType.PlayMedia);

    /// <param name="Number">Thứ tự phát (1, 2…); 0 = mục không phát được.</param>
    /// <param name="More">Ô "+N mục nữa" khi danh sách dài hơn <see cref="MaxTiles"/>.</param>
    private sealed record Tile(MediaInfo.Entry Entry, int Number, int More = 0);

    private readonly List<Tile> _tiles = [];
    private readonly Dictionary<string, MediaThumbnails.Thumbnail?> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Ảnh đang tải, mỗi file một lần hủy riêng — file bị bỏ khỏi danh sách thì hủy ngay, không giữ chỗ tải của file mới.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _loads = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly HScrollBar _scroll = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly ToolTip _tip = new() { InitialDelay = 400, AutoPopDelay = 15000 };
    private readonly ContextMenuStrip _menu = new();
    /// <summary>Tên đã rút gọn vừa hai dòng dưới ô (theo tên đầy đủ) — tính lại khi đổi font / DPI.</summary>
    private readonly Dictionary<string, string> _fittedNames = [];
    // Font riêng tính theo font của control (đã theo DPI của màn hình) — tạo lại khi font / DPI đổi.
    private Font _badgeFont = null!, _numberFont = null!, _smallFont = null!, _glyphFont = null!;

    private int _items;            // số dòng (mục) của danh sách
    /// <summary>Số file của từng mục trong cả danh sách (thư mục nhiều file; không chỉ các ô đang hiện).</summary>
    private Dictionary<int, int> _lineFiles = [];
    /// <summary>Mục cần chọn khi danh sách mới tới (sau khi chuyển / bỏ mục); -1 = giữ ô đang chọn.</summary>
    private int _followItem = -1;
    private int _hover = -1, _selected = -1;
    private int _press = -1;       // ô đang nhấn chuột trái (có thể thành kéo)
    private Point _pressAt;
    private bool _dragging, _dropHighlight;
    private int _dropBefore = -1;  // kéo đổi thứ tự: chèn trước ô này (= số ô → cuối danh sách)
    private string _empty = "";
    private string? _tipText;

    /// <summary>Nhấp đúp / Enter / "Phát thử": phát riêng file này.</summary>
    public event Action<string>? PlayRequested;

    /// <summary>Đổi thứ tự: mục thứ from chuyển tới vị trí to (tính sau khi chuyển).</summary>
    public event Action<int, int>? MoveRequested;

    /// <summary>Bỏ mục (cả dòng) khỏi danh sách.</summary>
    public event Action<int>? RemoveRequested;

    /// <summary>Kéo thả file video / thư mục từ Explorer vào dải.</summary>
    public event Action<string[]>? FilesDropped;

    public MediaStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Width = 620;
        CreateFonts();
        UpdateMetrics();
        BackColor = Color.White;
        TabStop = true;
        AllowDrop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Ảnh thu nhỏ của danh sách phát";
        _scroll.Scroll += (_, _) => Invalidate();
        _scroll.ValueChanged += (_, _) => Invalidate();
        Controls.Add(_scroll);
        SetPlan(null);
    }

    // ───────────────────────────── dữ liệu ─────────────────────────────

    /// <summary>Hiện danh sách phát đã phân tích (null = danh sách trống) và tải ảnh thu nhỏ còn thiếu ở nền.</summary>
    public void SetPlan(MediaInfo.Plan? plan, string? emptyText = null)
    {
        _tiles.Clear();
        _fittedNames.Clear();
        _empty = emptyText ?? "Chưa có file — bấm \"＋ Thêm file…\" hoặc kéo thả video từ Explorer vào đây.";
        var entries = plan?.Entries ?? [];
        int number = 0;
        foreach (var e in entries.Take(MaxTiles)) _tiles.Add(new Tile(e, e.Path != null ? ++number : 0));
        if (entries.Count > MaxTiles) _tiles.Add(new Tile(entries[MaxTiles], 0, entries.Count - MaxTiles));
        _items = entries.Count == 0 ? 0 : entries.Max(e => e.Item) + 1;
        _lineFiles = entries.Where(e => e.Path != null).GroupBy(e => e.Item).ToDictionary(g => g.Key, g => g.Count());
        _hover = -1;
        // Vừa chuyển / bỏ mục → chọn theo mục (ảnh vừa chuyển, hoặc ảnh kề ảnh vừa bỏ), không theo số thứ tự ô cũ.
        int follow = _followItem >= 0 ? _tiles.FindIndex(t => t.More == 0 && t.Entry.Item == _followItem) : -1;
        _followItem = -1;
        _selected = follow >= 0 ? follow : Math.Min(_selected, RealCount - 1);
        _press = -1;
        _dragging = false;
        _dropBefore = -1;
        AccessibleDescription = entries.Count == 0 ? _empty
            : string.Join("; ", _tiles.Where(t => t.More == 0).Select(t => t.Entry.Path != null
                ? $"{t.Number}. {Path.GetFileName(t.Entry.Path)}" + (t.Entry.Duration is { } d ? $" ({ActionStep.FormatDuration(d)})" : "")
                : $"{t.Entry.Line}: {t.Entry.Problem}"));

        // Giữ ảnh của các file vẫn còn trong danh sách, bỏ ảnh không dùng nữa; ảnh đang tải của file đã bỏ thì hủy.
        var paths = ShownPaths();
        foreach (var stale in _thumbs.Keys.Where(p => !paths.Contains(p)).ToList())
        {
            _thumbs[stale]?.Dispose();
            _thumbs.Remove(stale);
        }
        foreach (var stale in _loads.Keys.Where(p => !paths.Contains(p)).ToList())
        {
            _loads[stale].Cancel();
            _loads.Remove(stale);
        }
        foreach (var p in paths)
        {
            if (_thumbs.ContainsKey(p) || _loads.ContainsKey(p)) continue;
            var load = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _loads[p] = load;
            _ = LoadThumbnailAsync(p, load);
        }
        UpdateScroll();
        if (follow >= 0) EnsureVisible(follow);
        Invalidate();
    }

    private HashSet<string> ShownPaths() =>
        _tiles.Where(t => t.More == 0).Select(t => t.Entry.Path).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task LoadThumbnailAsync(string path, CancellationTokenSource load)
    {
        MediaThumbnails.Thumbnail? thumb = null;
        try { thumb = await MediaThumbnails.GetAsync(path, ThumbRequest, load.Token); }
        catch (OperationCanceledException) { }
        bool cancelled = load.IsCancellationRequested;
        if (_loads.TryGetValue(path, out var current) && current == load) _loads.Remove(path);
        load.Dispose();
        // Đã đóng / file đã bị bỏ khỏi danh sách trong lúc tải → không giữ ảnh.
        if (cancelled || IsDisposed || !ShownPaths().Contains(path))
        {
            thumb?.Dispose();
            return;
        }
        if (_thumbs.TryGetValue(path, out var old)) old?.Dispose();
        _thumbs[path] = thumb;
        Invalidate();
    }

    /// <summary>Số ảnh thu nhỏ đang tải (cho kiểm thử).</summary>
    internal int PendingThumbnails => _loads.Count;

    /// <summary>Số ô (không tính ô "+N mục nữa").</summary>
    internal int RealCount => _tiles.Count(t => t.More == 0);

    /// <summary>Ảnh thu nhỏ đã tải của ô thứ <paramref name="tile"/> (null = chưa có / không có).</summary>
    internal MediaThumbnails.Thumbnail? ThumbnailOf(int tile) =>
        tile >= 0 && tile < _tiles.Count && _tiles[tile].Entry.Path is { } p && _thumbs.TryGetValue(p, out var t) ? t : null;

    /// <summary>Ô đang chọn (-1 = chưa chọn).</summary>
    internal int SelectedTile => _selected;

    /// <summary>Tên như hiện dưới ô (đã rút gọn vừa hai dòng) — cho kiểm thử.</summary>
    internal string Caption(string name)
    {
        using var g = CreateGraphics();
        return FitName(g, name, NameRect(0).Size);
    }

    /// <summary>Chiều cao vùng tên dưới ô và khoảng còn lại cho thanh cuộn — cho kiểm thử.</summary>
    internal (int NameBottom, int ScrollTop) CaptionLayout() => (NameRect(0).Bottom, Height - _scroll.Height);

    /// <summary>Mô tả ô (cho kiểm thử, trình đọc màn hình): "1. Video 01.mp4 · 0:19".</summary>
    internal string TileText(int tile)
    {
        var t = _tiles[tile];
        if (t.More > 0) return $"+{t.More} mục nữa";
        return t.Entry.Path != null
            ? $"{t.Number}. {Path.GetFileName(t.Entry.Path)}" + (t.Entry.Duration is { } d ? " · " + ActionStep.FormatDuration(d) : "")
            : $"{t.Entry.Line} — {t.Entry.Problem}";
    }

    // ───────────────────────────── bố cục ─────────────────────────────

    // Kích thước thiết kế ở 96 DPI, nhân theo DPI thật của control (form không tự scale control thêm sau khi dựng).
    private float K => DeviceDpi / 96f;
    private int S(int logical) => (int)Math.Round(logical * K);
    private int Pad => S(6);
    private int Box => S(112);
    private int Gap => S(10);
    private int Step => Box + Gap;
    private int Offset => _scroll.Visible ? _scroll.Value : 0;
    private int ContentWidth => _tiles.Count == 0 ? 0 : Pad * 2 + _tiles.Count * Step - Gap;
    /// <summary>Hai dòng tên file theo font thật (font tính theo DPI có thể cao hơn tỉ lệ thiết kế).</summary>
    private int NameHeight => Math.Max(S(32), Font.Height * 2 + S(2));

    internal Rectangle BoxRect(int tile) => new(Pad + tile * Step - Offset, Pad, Box, Box);
    private Rectangle NameRect(int tile) => new(Pad + tile * Step - Offset - S(4), Pad + Box + S(3), Box + S(8), NameHeight);

    /// <summary>Chiều cao đủ cho ô ảnh, hai dòng tên và thanh cuộn (thanh cuộn không che tên).</summary>
    private void UpdateMetrics()
    {
        _scroll.Height = SystemInformation.GetHorizontalScrollBarHeightForDpi(DeviceDpi);
        Height = Pad + Box + S(3) + NameHeight + S(4) + _scroll.Height;
        _fittedNames.Clear();
    }

    private void CreateFonts()
    {
        foreach (var f in new[] { _badgeFont, _numberFont, _smallFont, _glyphFont }) f?.Dispose();
        // Cùng tỉ lệ với font 9pt khi thiết kế: 8pt đậm, 8.5pt đậm, 8pt, icon 20pt.
        float size = Font.Size;
        _badgeFont = new Font(Font.FontFamily, size * 8F / 9F, FontStyle.Bold);
        _numberFont = new Font(Font.FontFamily, size * 8.5F / 9F, FontStyle.Bold);
        _smallFont = new Font(Font.FontFamily, size * 8F / 9F);
        _glyphFont = StepVisuals.CreateIconFont(Font, size * 11F / 9F);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        CreateFonts();
        UpdateMetrics();
        UpdateScroll();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // DPI thật của màn hình chứa cửa sổ chỉ biết khi đã có handle.
        UpdateMetrics();
        UpdateScroll();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        CreateFonts();
        UpdateMetrics();
        UpdateScroll();
        Invalidate();
    }

    private void UpdateScroll()
    {
        int content = ContentWidth, view = ClientSize.Width;
        bool need = content > view && view > 0;
        _scroll.Visible = need;
        if (!need) return;
        _scroll.Minimum = 0;
        _scroll.Maximum = content - 1;
        _scroll.LargeChange = Math.Max(1, view);
        _scroll.SmallChange = Math.Max(1, Step);
        _scroll.Value = Math.Clamp(_scroll.Value, 0, Math.Max(0, content - view));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    /// <summary>Kích thước mong muốn: không ép cột của bảng bố cục rộng ra — dải trải theo ô chứa nó.</summary>
    public override Size GetPreferredSize(Size proposedSize) => new(LogicalToDeviceUnits(300), Height);

    private int HitTile(Point p)
    {
        for (int i = 0; i < _tiles.Count; i++)
            if (Rectangle.Union(BoxRect(i), NameRect(i)).Contains(p)) return i;
        return -1;
    }

    private void EnsureVisible(int tile)
    {
        if (!_scroll.Visible || tile < 0) return;
        var r = BoxRect(tile);
        int max = Math.Max(0, ContentWidth - ClientSize.Width);
        if (r.Left < Pad) _scroll.Value = Math.Clamp(_scroll.Value + r.Left - Pad, 0, max);
        else if (r.Right > ClientSize.Width - Pad) _scroll.Value = Math.Clamp(_scroll.Value + r.Right - ClientSize.Width + Pad, 0, max);
    }

    // ───────────────────────────── vẽ ─────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_tiles.Count == 0)
        {
            var r = new Rectangle(Pad, Pad, ClientSize.Width - Pad * 2, Box);
            using (var pen = new Pen(Color.FromArgb(190, 190, 196)) { DashStyle = DashStyle.Dash })
            using (var path = StepVisuals.RoundRect(r, S(6)))
                g.DrawPath(pen, path);
            TextRenderer.DrawText(g, _empty, Font, Rectangle.Inflate(r, -S(12), 0), UiText.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        for (int i = 0; i < _tiles.Count; i++)
        {
            var box = BoxRect(i);
            if (box.Right < 0 || box.Left > ClientSize.Width) continue;
            DrawTile(g, i, box);
        }
        if (_dragging && _dropBefore >= 0)
        {
            int x = Pad + _dropBefore * Step - Gap / 2 - Offset;
            using var bar = new SolidBrush(Accent);
            g.FillRectangle(bar, x - S(2), Pad - S(2), S(4), Box + S(4));
        }
        if (_dropHighlight)
        {
            using var pen = new Pen(Accent, S(2)) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, S(1), S(1), ClientSize.Width - S(3), ClientSize.Height - S(3) - (_scroll.Visible ? _scroll.Height : 0));
        }
    }

    private void DrawTile(Graphics g, int i, Rectangle box)
    {
        var t = _tiles[i];
        var e = t.Entry;
        bool variable = e.Problem == MediaInfo.VariableProblem;
        using var path = StepVisuals.RoundRect(box, S(6));
        using (var back = new SolidBrush(t.More > 0 || e.Path != null ? TileBack : variable ? VariableBack : ProblemBack)) g.FillPath(back, path);

        string name;
        if (t.More > 0)
        {
            TextRenderer.DrawText(g, $"+{t.More}", Theme.TitleFont, box, UiText.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            name = "mục nữa";
        }
        else if (e.Path is { } file)
        {
            var image = Rectangle.Inflate(box, -S(3), -S(3));
            var thumb = _thumbs.GetValueOrDefault(file);
            if (thumb != null && !thumb.IsIcon)
            {
                image = Fit(thumb.Image.Size, image);
                var mode = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(thumb.Image, image);
                g.InterpolationMode = mode;
                using var border = new Pen(Color.FromArgb(40, 0, 0, 0));
                g.DrawRectangle(border, image.X, image.Y, image.Width - 1, image.Height - 1);
            }
            else if (thumb != null)
            {
                // Không có ảnh thu nhỏ (vd file nhạc không có ảnh bìa): biểu tượng của loại file như Explorer.
                int side = Math.Min(S(56), Math.Min(thumb.Image.Width, thumb.Image.Height) * 2);
                g.DrawImage(thumb.Image, new Rectangle(box.X + (box.Width - side) / 2, box.Y + (box.Height - side) / 2, side, side));
            }
            else
            {
                bool audio = Path.GetExtension(file).ToLowerInvariant() is ".mp3" or ".wav" or ".wma" or ".m4a" or ".aac" or ".flac";
                TextRenderer.DrawText(g, StepVisuals.UiGlyph(audio ? "" : "", audio ? "♪" : "▶"), _glyphFont, box,
                    Color.FromArgb(150, 150, 156), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (_loads.ContainsKey(file))
                {
                    int h = Math.Max(S(18), _smallFont.Height + S(2));
                    TextRenderer.DrawText(g, "đang tải…", _smallFont, new Rectangle(box.X, box.Bottom - h - S(4), box.Width, h), UiText.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                }
            }
            if (e.Duration is { } d) DrawBadge(g, ActionStep.FormatDuration(d), thumb != null && !thumb.IsIcon ? image : box);
            DrawNumber(g, t.Number, box);
            name = Path.GetFileName(file);
        }
        else
        {
            var inner = Rectangle.Inflate(box, -S(6), -S(8));
            var glyph = variable ? "{x}" : "✖";
            var top = new Rectangle(inner.X, inner.Y, inner.Width, inner.Height / 2);
            TextRenderer.DrawText(g, glyph, Theme.TitleFont, top, variable ? Theme.Accent : ProblemFore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
            var bottom = new Rectangle(inner.X, inner.Y + inner.Height / 2 + S(4), inner.Width, inner.Height / 2 - S(4));
            TextRenderer.DrawText(g, e.Problem ?? "", _smallFont, bottom, variable ? Color.FromArgb(30, 70, 120) : ProblemFore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            name = e.Line;
        }

        bool selected = i == _selected && Focused;
        if (selected || i == _hover || (_dragging && i == _press))
        {
            using var pen = new Pen(selected || (_dragging && i == _press) ? Accent : StepVisuals.Tint(Accent, 0.45f), S(2));
            g.DrawPath(pen, path);
        }
        var nameRect = NameRect(i);
        TextRenderer.DrawText(g, FitName(g, name, nameRect.Size), Font, nameRect, ForeColor, NameFlags | TextFormatFlags.EndEllipsis);
    }

    // TextBoxControl: không vẽ nửa dòng cuối bị cắt.
    private const TextFormatFlags NameFlags =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

    /// <summary>
    /// Tên vừa khung hai dòng; dài hơn thì bỏ bớt ở giữa ("Bài giảng môn…phần 1.mp4") — giữ phần cuối và đuôi file
    /// vì hai file cùng thư mục thường chỉ khác nhau ở cuối tên.
    /// </summary>
    private string FitName(Graphics g, string name, Size area)
    {
        if (_fittedNames.TryGetValue(name, out var cached)) return cached;
        bool Fits(string s)
        {
            var size = TextRenderer.MeasureText(g, s, Font, new Size(area.Width, int.MaxValue), NameFlags);
            return size.Height <= area.Height && size.Width <= area.Width;
        }
        var result = name;
        if (!Fits(name))
        {
            // Giữ đuôi file + vài ký tự trước nó, phần đầu dài nhất còn vừa (tìm nhị phân).
            int tailLength = Math.Min(name.Length / 2, Path.GetExtension(name).Length + 8);
            var tail = name[^tailLength..];
            var head = name[..^tailLength];
            int lo = 0, hi = head.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Fits(head[..mid].TrimEnd() + "…" + tail)) lo = mid;
                else hi = mid - 1;
            }
            result = head[..lo].TrimEnd() + "…" + tail;
        }
        _fittedNames[name] = result;
        return result;
    }

    /// <summary>Thời lượng ở góc phải dưới khung hình (chữ trắng trên nền tối, như trình phát video).</summary>
    private void DrawBadge(Graphics g, string text, Rectangle image)
    {
        var size = TextRenderer.MeasureText(g, text, _badgeFont, Size.Empty, TextFormatFlags.NoPadding);
        var r = new Rectangle(image.Right - size.Width - S(10), image.Bottom - size.Height - S(8), size.Width + S(6), size.Height + S(2));
        using (var path = StepVisuals.RoundRect(r, S(3)))
        using (var back = new SolidBrush(Color.FromArgb(185, 0, 0, 0)))
            g.FillPath(back, path);
        TextRenderer.DrawText(g, text, _badgeFont, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    /// <summary>Thứ tự phát ở góc trái trên.</summary>
    private void DrawNumber(Graphics g, int number, Rectangle box)
    {
        var r = new Rectangle(box.X + S(5), box.Y + S(5), S(22), S(22));
        using (var back = new SolidBrush(Accent)) g.FillEllipse(back, r);
        using (var ring = new Pen(Color.White, Math.Max(1, S(1)))) g.DrawEllipse(ring, r);
        TextRenderer.DrawText(g, number.ToString(System.Globalization.CultureInfo.InvariantCulture), _numberFont, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    /// <summary>Khung giữ tỉ lệ của ảnh, nằm giữa <paramref name="area"/>.</summary>
    internal static Rectangle Fit(Size image, Rectangle area)
    {
        if (image.Width <= 0 || image.Height <= 0) return area;
        double k = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
        int w = Math.Max(1, (int)Math.Round(image.Width * k)), h = Math.Max(1, (int)Math.Round(image.Height * k));
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    // ───────────────────────────── chuột / bàn phím ─────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int hit = HitTile(e.Location);
        if (hit < 0 || _tiles[hit].More > 0) return;
        _selected = hit;
        if (e.Button == MouseButtons.Left)
        {
            _press = hit;
            _pressAt = e.Location;
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // Đang giữ chuột trái từ lúc nhấn trên một ô (thả chuột / mất capture sẽ xóa _press).
        if (_press >= 0)
        {
            var drag = SystemInformation.DragSize;
            if (!_dragging && _items > 1 && (Math.Abs(e.X - _pressAt.X) > drag.Width / 2 || Math.Abs(e.Y - _pressAt.Y) > drag.Height / 2))
                _dragging = true;
            if (_dragging)
            {
                // Kéo sát mép → cuộn theo.
                if (_scroll.Visible && (e.X < S(24) || e.X > ClientSize.Width - S(24)))
                    _scroll.Value = Math.Clamp(_scroll.Value + (e.X < S(24) ? -S(16) : S(16)), 0, Math.Max(0, ContentWidth - ClientSize.Width));
                _dropBefore = DropBoundary(e.Location);
                Invalidate();
                return;
            }
        }
        int hit = HitTile(e.Location);
        if (hit == _hover) return;
        _hover = hit;
        UpdateTip();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging && _press >= 0)
        {
            int from = _tiles[_press].Entry.Item;
            // Thả xa ngoài dải = hủy kéo.
            int to = TargetIndex(from, DropBoundary(e.Location));
            _dragging = false;
            _press = -1;
            _dropBefore = -1;
            Invalidate();
            if (to != from) RequestMove(from, to);
            return;
        }
        _press = -1;
        if (e.Button == MouseButtons.Right)
        {
            int hit = HitTile(e.Location);
            if (hit >= 0 && _tiles[hit].More == 0) ShowMenu(hit, e.Location);
        }
    }

    /// <summary>Mất capture chuột giữa chừng (vd Alt+Tab khi đang kéo) → hủy kéo.</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || (_press < 0 && !_dragging)) return;
        _press = -1;
        _dragging = false;
        _dropBefore = -1;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int hit = HitTile(e.Location);
        if (e.Button == MouseButtons.Left && hit >= 0 && _tiles[hit].More == 0 && _tiles[hit].Entry.Path is { } p) PlayRequested?.Invoke(p);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        UpdateTip();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_scroll.Visible) return;
        _scroll.Value = Math.Clamp(_scroll.Value - Math.Sign(e.Delta) * Step, 0, Math.Max(0, ContentWidth - ClientSize.Width));
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_selected < 0 && RealCount > 0) _selected = 0;
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    /// <summary>Đang nhấn giữ / kéo một ô — Esc hủy kéo (không để form coi là bấm "Hủy").</summary>
    private bool Pressing => _press >= 0 || _dragging;

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End ||
        ((keyData & Keys.KeyCode) == Keys.Escape && Pressing) || base.IsInputKey(keyData);

    private void CancelDrag()
    {
        _press = -1;
        _dragging = false;
        _dropBefore = -1;
        Capture = false;
        Invalidate();
    }

    /// <summary>Báo đổi thứ tự; danh sách mới tới thì ô chọn theo mục vừa chuyển.</summary>
    private void RequestMove(int from, int to)
    {
        _followItem = to;
        MoveRequested?.Invoke(from, to);
    }

    /// <summary>Báo bỏ mục; danh sách mới tới thì chọn ảnh kề đó (mục sau, hết thì mục trước).</summary>
    private void RequestRemove(int item)
    {
        _followItem = Math.Min(item, _items - 2);
        RemoveRequested?.Invoke(item);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && Pressing)
        {
            CancelDrag();
            e.Handled = true;
            return;
        }
        if (_selected < 0 || _selected >= RealCount) return;
        var entry = _tiles[_selected].Entry;
        switch (e.KeyCode)
        {
            case Keys.Left when e.Control:
                if (entry.Item > 0) RequestMove(entry.Item, entry.Item - 1);
                break;
            case Keys.Right when e.Control:
                if (entry.Item < _items - 1) RequestMove(entry.Item, entry.Item + 1);
                break;
            case Keys.Left or Keys.Right or Keys.Home or Keys.End:
                _selected = e.KeyCode switch
                {
                    Keys.Home => 0,
                    Keys.End => RealCount - 1,
                    Keys.Left => Math.Max(0, _selected - 1),
                    _ => Math.Min(RealCount - 1, _selected + 1)
                };
                EnsureVisible(_selected);
                Invalidate();
                break;
            case Keys.Delete:
                RequestRemove(entry.Item);
                break;
            case Keys.Enter or Keys.Space:
                if (entry.Path != null) PlayRequested?.Invoke(entry.Path);
                break;
            case Keys.Apps:
            case Keys.F10 when e.Shift:
                var box = BoxRect(_selected);
                ShowMenu(_selected, new Point(box.X + box.Width / 2, box.Y + box.Height / 2));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Ranh giới giữa các ô gần <paramref name="x"/> nhất: 0 = trước ô đầu, số ô = sau ô cuối.</summary>
    private int BoundaryAt(int x) => Math.Clamp((int)Math.Round((x + Offset - Pad + Gap / 2.0) / Step), 0, RealCount);

    /// <summary>
    /// Chỗ thả khi kéo ô đang nhấn tới <paramref name="p"/>: ranh giới gần nhất, dời ra sau nhóm ảnh của cùng một thư mục
    /// (thả giữa thư mục = thả sau thư mục) để vạch chèn vẽ đúng chỗ mục sẽ tới; -1 = không đổi gì (ngay chỗ cũ / thả xa ngoài dải).
    /// </summary>
    private int DropBoundary(Point p)
    {
        if (_press < 0 || !Rectangle.Inflate(ClientRectangle, S(24), S(24)).Contains(p)) return -1;
        int b = BoundaryAt(p.X);
        while (b > 0 && b < RealCount && _tiles[b - 1].Entry.Item == _tiles[b].Entry.Item) b++;
        int from = _tiles[_press].Entry.Item;
        return TargetIndex(from, b) == from ? -1 : b;
    }

    /// <summary>
    /// Vị trí mới (tính sau khi chuyển) của mục <paramref name="from"/> khi thả ở ranh giới <paramref name="boundary"/>.
    /// Thả giữa các ô của cùng một thư mục = thả sau thư mục đó.
    /// </summary>
    internal int TargetIndex(int from, int boundary)
    {
        int real = RealCount;
        if (boundary < 0) return from;
        int before;
        if (boundary >= real) before = real == 0 ? 0 : _tiles[real - 1].Entry.Item + 1;
        else if (boundary > 0 && _tiles[boundary - 1].Entry.Item == _tiles[boundary].Entry.Item) before = _tiles[boundary].Entry.Item + 1;
        else before = _tiles[boundary].Entry.Item;
        // Danh sách dài hơn số ô hiện: thả cuối = sau mục cuối cùng đang hiện.
        int to = before > from ? before - 1 : before;
        return Math.Clamp(to, 0, Math.Max(0, _items - 1));
    }

    private void UpdateTip()
    {
        string? text = null;
        if (_hover >= 0 && _hover < _tiles.Count && _tiles[_hover].More == 0)
        {
            var e = _tiles[_hover].Entry;
            if (e.Path != null)
            {
                var parts = new List<string>();
                if (e.Duration is { } d) parts.Add("thời lượng " + ActionStep.FormatDuration(d));
                try { parts.Add($"{new FileInfo(e.Path).Length / 1048576.0:0.#} MB"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                bool folder = FilesOfItem(e.Item) > 1 || IsFolderLine(e.Line);
                if (folder) parts.Add($"từ thư mục \"{FolderName(e)}\"");
                text = e.Path + (parts.Count > 0 ? Environment.NewLine + string.Join(" · ", parts) : "") + Environment.NewLine +
                       "Nhấp đúp: phát thử · Kéo: đổi thứ tự · Chuột phải: thêm lựa chọn" +
                       (folder ? Environment.NewLine + $"Delete: bỏ cả dòng thư mục ({FilesOfItem(e.Item)} file)" : "");
            }
            else text = $"{e.Line}{Environment.NewLine}{e.Problem}";
        }
        if (text == _tipText) return;
        _tipText = text;
        _tip.SetToolTip(this, text);
    }

    private void ShowMenu(int tile, Point at)
    {
        var e = _tiles[tile].Entry;
        _menu.Items.Clear();
        if (e.Path is { } file)
        {
            _menu.Items.Add("▶ Phát thử file này", null, (_, _) => PlayRequested?.Invoke(file));
            _menu.Items.Add("Mở thư mục chứa file", null, (_, _) => OpenFolder(file));
            _menu.Items.Add(new ToolStripSeparator());
        }
        var earlier = _menu.Items.Add("← Chuyển lên trước", null, (_, _) => RequestMove(e.Item, e.Item - 1));
        earlier.Enabled = e.Item > 0;
        var later = _menu.Items.Add("→ Chuyển ra sau", null, (_, _) => RequestMove(e.Item, e.Item + 1));
        later.Enabled = e.Item < _items - 1;
        _menu.Items.Add(RemoveText(e), null, (_, _) => RequestRemove(e.Item));
        _menu.Show(this, at);
    }

    /// <summary>Số file của mục <paramref name="item"/> trong cả danh sách (không chỉ các ô đang hiện).</summary>
    internal int FilesOfItem(int item) => _lineFiles.GetValueOrDefault(item);

    /// <summary>Chữ của lựa chọn "Bỏ": ảnh thuộc dòng thư mục thì nói rõ bỏ cả dòng thư mục (mọi file trong đó).</summary>
    internal string RemoveText(MediaInfo.Entry e) =>
        e.Path != null && (FilesOfItem(e.Item) > 1 || IsFolderLine(e.Line))
            ? $"✕ Bỏ cả dòng thư mục \"{FolderName(e)}\" khỏi danh sách ({FilesOfItem(e.Item)} file)"
            : "✕ Bỏ khỏi danh sách";

    private static string ExpandLine(string line) => Environment.ExpandEnvironmentVariables(line.Trim().Trim('"', '\'').Trim());

    private static bool IsFolderLine(string line)
    {
        try { return Directory.Exists(ExpandLine(line)); }
        catch (ArgumentException) { return false; }
    }

    private static string FolderName(MediaInfo.Entry e) =>
        Path.GetFileName(Path.GetDirectoryName(e.Path!) ?? "") is { Length: > 0 } name ? name : ExpandLine(e.Line);

    private static void OpenFolder(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Warn("Không mở được thư mục: " + ex.Message); }
    }

    // ───────────────────────────── kéo thả từ Explorer ─────────────────────────────

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        bool ok = PlaylistText.DroppedPaths(e.Data).Length > 0;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (_dropHighlight == ok) return;
        _dropHighlight = ok;
        Invalidate();
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dropHighlight = false;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        _dropHighlight = false;
        Invalidate();
        var paths = PlaylistText.DroppedPaths(e.Data);
        if (paths.Length > 0) FilesDropped?.Invoke(paths);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _loads.Clear();   // ảnh đang tải tự hủy khi quay về (đã hủy theo _cts)
            foreach (var t in _thumbs.Values) t?.Dispose();
            _thumbs.Clear();
            _tip.Dispose();
            _menu.Dispose();
            _badgeFont.Dispose();
            _numberFont.Dispose();
            _smallFont.Dispose();
            _glyphFont.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
