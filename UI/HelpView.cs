using System.Text;
using ScheduleApp.Vision;

namespace ScheduleApp.UI;

/// <summary>Màn hình chính xử lý các nút "làm ngay" của hướng dẫn (thêm công việc, mở trang Kiểm thử…).</summary>
internal interface IHelpHost
{
    void RunHelpCommand(string command);
}

/// <summary>Trang hướng dẫn: danh sách chủ đề (có ô tìm) bên trái, nội dung bên phải.</summary>
internal sealed class HelpView : UserControl
{
    private readonly IHelpHost? _host;
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "🔍 Tìm trong hướng dẫn…", Margin = new Padding(0), AccessibleName = "Tìm trong hướng dẫn" };
    private readonly ListBox _topics = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        DrawMode = DrawMode.OwnerDrawVariable,
        IntegralHeight = false
    };
    private readonly Label _title = new() { Dock = DockStyle.Top, AutoSize = true, Font = Theme.TitleFont, ForeColor = Theme.Text, UseMnemonic = false };
    private readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = true, ForeColor = Theme.Muted, UseMnemonic = false, Padding = new Padding(0, 4, 0, 8) };
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 10) };
    private readonly RichTextBox _body = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = Theme.Surface,
        DetectUrls = true,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        TabStop = false
    };
    private readonly LinkLabel _prev = new() { AutoSize = true, LinkColor = Theme.Accent, Dock = DockStyle.Left, UseMnemonic = false };
    private readonly LinkLabel _next = new() { AutoSize = true, LinkColor = Theme.Accent, Dock = DockStyle.Right, UseMnemonic = false };
    private readonly Label _noMatch = new() { Text = "Không có chủ đề nào khớp.", Dock = DockStyle.Top, AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(8, 12, 0, 0), Visible = false };
    private Font? _groupFont;
    private HelpTopic? _current;

    public HelpView(IHelpHost? host, bool showHeader)
    {
        _host = host;
        BackColor = Theme.Background;
        Dock = DockStyle.Fill;

        // Danh sách chủ đề
        var left = new Panel { Dock = DockStyle.Left, Width = LogicalToDeviceUnits(280), BackColor = Theme.Surface, Padding = new Padding(10, 10, 6, 10) };
        var searchHost = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(34), Padding = new Padding(0, 0, 4, 8), BackColor = Theme.Surface };
        searchHost.Controls.Add(_search);
        left.Controls.Add(_topics);
        left.Controls.Add(_noMatch);
        left.Controls.Add(searchHost);
        _topics.MeasureItem += (_, e) => e.ItemHeight = _topics.Items[e.Index] is string ? LogicalToDeviceUnits(32) : LogicalToDeviceUnits(30);
        _topics.DrawItem += DrawTopic;
        _topics.SelectedIndexChanged += (_, _) => OnTopicSelected();
        _search.TextChanged += (_, _) => FillTopics(_current?.Id);
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Down && e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            _topics.Focus();
        };

        // Nội dung
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(26, 18, 18, 10) };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(30), BackColor = Theme.Surface, Padding = new Padding(0, 8, 0, 0) };
        footer.Controls.AddRange([_prev, _next]);
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        _prev.LinkClicked += (_, _) => { if (_prev.Tag is HelpTopic t) ShowTopic(t.Id); };
        _next.LinkClicked += (_, _) => { if (_next.Tag is HelpTopic t) ShowTopic(t.Id); };
        _body.LinkClicked += (_, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.LinkText!) { UseShellExecute = true }); }
            catch { /* liên kết hỏng — bỏ qua */ }
        };
        right.Controls.Add(_body);
        right.Controls.Add(_actions);
        right.Controls.Add(_summary);
        right.Controls.Add(_title);
        right.Controls.Add(footer);
        right.Resize += (_, _) => FitLabels(right);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 16), BackColor = Theme.Background };
        var gap = new Panel { Dock = DockStyle.Left, Width = LogicalToDeviceUnits(12), BackColor = Theme.Background };
        body.Controls.Add(right);
        body.Controls.Add(gap);
        body.Controls.Add(left);
        Controls.Add(body);
        if (showHeader)
            Controls.Add(Theme.PageHeader("Hướng dẫn", "Cách dùng ScheduleApp và kiểm thử tự động Dynamics 365 — nhấn F1 ở bất kỳ cửa sổ nào để mở đúng chủ đề"));
        else
            body.Padding = new Padding(12);

        FillTopics(HelpContent.Start);
    }

    /// <summary>Chủ đề đang mở.</summary>
    public string? CurrentTopicId => _current?.Id;

    public void ShowTopic(string? id)
    {
        var topic = HelpContent.Find(id) ?? HelpContent.Topics[0];
        if (!_topics.Items.Contains(topic))
        {
            _search.Text = ""; // chủ đề bị lọc mất — bỏ lọc
            FillTopics(topic.Id);
        }
        _topics.SelectedItem = topic;
    }


    private void FillTopics(string? keepId)
    {
        var query = Normalize(_search.Text);
        // Có từ khóa: xếp chủ đề khớp ở tiêu đề lên trước, rồi tóm tắt, rồi nội dung.
        var topics = HelpContent.Topics.Where(t => query.Length == 0 || Matches(t, query))
            .OrderByDescending(t => query.Length == 0 ? 0 : Relevance(t, query)).ToList();
        _topics.BeginUpdate();
        _topics.Items.Clear();
        string? group = null;
        foreach (var t in topics)
        {
            var header = query.Length == 0 ? t.Group : "Kết quả tìm";
            if (header != group) _topics.Items.Add(group = header);
            _topics.Items.Add(t);
        }
        _topics.EndUpdate();
        _noMatch.Visible = topics.Count == 0;
        var keep = topics.FirstOrDefault(t => t.Id == keepId) ?? topics.FirstOrDefault();
        if (keep != null) _topics.SelectedItem = keep;
    }

    /// <summary>Khớp mọi từ của <paramref name="query"/> (đã bỏ dấu, chữ thường) trong tiêu đề, tóm tắt hoặc nội dung.</summary>
    internal static bool Matches(HelpTopic t, string query)
    {
        var text = Normalize(t.Title + " " + t.Summary + " " + t.Body);
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(text.Contains);
    }

    /// <summary>Độ liên quan: từ khóa nằm trong tiêu đề > tóm tắt > nội dung.</summary>
    internal static int Relevance(HelpTopic t, string query)
    {
        string title = Normalize(t.Title), summary = Normalize(t.Summary);
        int score = 0;
        foreach (var w in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            score += title.Contains(w) ? 10 : summary.Contains(w) ? 4 : 1;
        return score;
    }

    private static string Normalize(string s) => ScreenOcr.RemoveDiacritics(s).Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant().Trim();

    private void OnTopicSelected()
    {
        if (_topics.SelectedItem is string)
        {
            // Dòng tiêu đề nhóm: chuyển sang chủ đề đầu tiên của nhóm.
            int i = _topics.SelectedIndex + 1;
            if (i < _topics.Items.Count) _topics.SelectedIndex = i;
            return;
        }
        if (_topics.SelectedItem is not HelpTopic topic || topic == _current) return;
        _current = topic;
        Render(topic);
    }

    private void Render(HelpTopic topic)
    {
        SuspendLayout();
        _title.Text = topic.Title;
        _summary.Text = topic.Summary;
        _actions.Controls.Clear();
        if (_host != null)
        {
            bool first = true;
            foreach (var a in topic.Actions)
            {
                var b = new Button { Text = a.Text.Replace("&", "&&"), AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(120), 0), Margin = new Padding(0, 0, 8, 0) };
                Theme.StyleButton(b, primary: first);
                var command = a.Command;
                b.Click += (_, _) => _host.RunHelpCommand(command);
                _actions.Controls.Add(b);
                first = false;
            }
        }
        _actions.Visible = _actions.Controls.Count > 0;
        _body.Rtf = HelpRtf.Build(topic.Body);
        _body.SelectionStart = 0;
        _body.ScrollToCaret();

        int index = HelpContent.Topics.ToList().IndexOf(topic);
        var prev = index > 0 ? HelpContent.Topics[index - 1] : null;
        var next = index < HelpContent.Topics.Count - 1 ? HelpContent.Topics[index + 1] : null;
        _prev.Text = prev == null ? "" : "← " + prev.Title;
        _prev.Tag = prev;
        _next.Text = next == null ? "" : next.Title + " →";
        _next.Tag = next;
        FitLabels(_title.Parent!);
        ResumeLayout(true);
        _topics.Invalidate();
    }

    private static void FitLabels(Control right)
    {
        int width = Math.Max(100, right.ClientSize.Width - right.Padding.Horizontal);
        foreach (var l in right.Controls.OfType<Label>()) l.MaximumSize = new Size(width, 0);
    }

    private void DrawTopic(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var g = e.Graphics;
        var item = _topics.Items[e.Index];
        using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, e.Bounds);
        int pad = LogicalToDeviceUnits(10);
        if (item is string group)
        {
            _groupFont ??= new Font(Font.FontFamily, 7.5F, FontStyle.Bold);
            var r = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, e.Bounds.Width - pad, e.Bounds.Height - LogicalToDeviceUnits(4));
            TextRenderer.DrawText(g, group.ToUpperInvariant(), _groupFont, r, Theme.Muted, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            return;
        }
        var topic = (HelpTopic)item;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        if (selected)
        {
            var box = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 6, e.Bounds.Height - 4);
            using var path = Theme.RoundRect(box, LogicalToDeviceUnits(5));
            using var soft = new SolidBrush(Theme.AccentSoft);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillPath(soft, path);
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, box.X, box.Y + LogicalToDeviceUnits(6), LogicalToDeviceUnits(3), box.Height - LogicalToDeviceUnits(12));
        }
        var textRect = new Rectangle(e.Bounds.X + pad + LogicalToDeviceUnits(6), e.Bounds.Y, e.Bounds.Width - pad * 2, e.Bounds.Height);
        TextRenderer.DrawText(g, topic.Title, selected ? Theme.BoldFont : _topics.Font, textRect, selected ? Theme.Accent : Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _groupFont?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Cửa sổ hướng dẫn mở bằng F1 từ các hộp thoại — không chặn hộp thoại, để vừa đọc vừa làm.</summary>
internal sealed class HelpWindow : BaseForm
{
    protected override SizeF ScreenShare => new(0.62f, 0.8f);
    protected override string? LayoutKey => "Help";

    private static HelpWindow? _instance;
    private readonly HelpView _view = new(null, showHeader: false);

    private HelpWindow()
    {
        Text = "Hướng dẫn — ScheduleApp";
        Size = new Size(1000, 700);
        MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        Controls.Add(_view);
    }

    protected override string HelpTopicId => _view.CurrentTopicId ?? HelpContent.Start;

    public static void ShowTopic(string? id)
    {
        // Cửa sổ mở từ trước một hộp thoại modal bị hộp thoại đó vô hiệu hóa — mở cửa sổ mới thay thế.
        if (_instance is { IsDisposed: false, Enabled: false } stale)
        {
            var bounds = stale.Bounds;
            stale.Close();
            _instance = new HelpWindow { StartPosition = FormStartPosition.Manual, Bounds = bounds };
        }
        if (_instance == null || _instance.IsDisposed) _instance = new HelpWindow();
        _instance._view.ShowTopic(id);
        if (!_instance.Visible) _instance.Show();
        if (_instance.WindowState == FormWindowState.Minimized) _instance.WindowState = FormWindowState.Normal;
        _instance.Activate();
    }
}

/// <summary>Chuyển nội dung chủ đề (cú pháp gọn của <see cref="HelpTopic.Body"/>) sang RTF cho RichTextBox.</summary>
internal static class HelpRtf
{
    // Bảng màu: 1 chữ, 2 chữ phụ, 3 nhấn, 4 cảnh báo, 5 thành công, 6 nền mã, 7 lỗi, 8 chữ mã
    private const string Header =
        @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil Segoe UI;}{\f1\fmodern Consolas;}{\f2\fnil Segoe UI Semibold;}}" +
        @"{\colortbl ;\red27\green31\blue36;\red96\green104\blue114;\red0\green103\blue192;\red157\green93\blue0;\red16\green124\blue16;" +
        @"\red236\green239\blue243;\red196\green43\blue28;\red116\green39\blue116;}\uc1";

    private const string Normal = @"\f0\fs20\cf1 ";

    public static string Build(string body)
    {
        var sb = new StringBuilder(Header);
        bool inCode = false;
        foreach (var raw in body.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                inCode = !inCode;
                if (!inCode) sb.Append(@"\pard\sa60\fs8\par");
                continue;
            }
            if (inCode)
            {
                sb.Append(@"\pard\li360\sa0\f1\fs19\cf8\highlight6 ").Append(Escape(line.Length == 0 ? " " : " " + line + " ")).Append(@"\highlight0\par");
                continue;
            }
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("## "))
                sb.Append(@"\pard\sb220\sa80\f2\fs26\cf1 ").Append(Escape(line[3..])).Append(@"\par");
            else if (line.StartsWith("- "))
                sb.Append(@"\pard\li520\fi-260\tx520\sa70\sl276\slmult1\f0\fs20\cf3 \u8226?\tab").Append(Normal).Append(Inline(line[2..])).Append(@"\par");
            else if (NumberPrefix(line) is { } n)
                sb.Append(@"\pard\li520\fi-340\tx520\sa90\sl276\slmult1\f2\fs20\cf3 ").Append(Escape(n)).Append(@"\tab").Append(Normal)
                  .Append(Inline(line[(n.Length + 1)..])).Append(@"\par");
            else if (line.StartsWith("> "))
                sb.Append(@"\pard\li240\sb60\sa100\sl276\slmult1\f2\fs20\cf5 ").Append(Escape("Mẹo  ")).Append(Normal).Append(Inline(line[2..])).Append(@"\par");
            else if (line.StartsWith("! "))
                sb.Append(@"\pard\li240\sb60\sa100\sl276\slmult1\f2\fs20\cf4 ").Append(Escape("Lưu ý  ")).Append(Normal).Append(Inline(line[2..])).Append(@"\par");
            else
                sb.Append(@"\pard\sa120\sl276\slmult1").Append(Normal).Append(Inline(line)).Append(@"\par");
        }
        return sb.Append('}').ToString();
    }

    /// <summary>"12. abc" → "12."; không phải dòng đánh số → null.</summary>
    private static string? NumberPrefix(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        return i > 0 && i < 4 && i + 1 < line.Length && line[i] == '.' && line[i + 1] == ' ' ? line[..(i + 1)] : null;
    }

    /// <summary>Định dạng trong dòng: <c>**đậm**</c>, <c>*nghiêng*</c>, <c>`mã`</c>.</summary>
    internal static string Inline(string s)
    {
        var sb = new StringBuilder();
        bool bold = false, italic = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '`')
            {
                int end = s.IndexOf('`', i + 1);
                if (end > i)
                {
                    sb.Append(@"\f1\fs19\cf8 ").Append(Escape(s[(i + 1)..end])).Append(@"\f0\fs20\cf1 ");
                    i = end;
                    continue;
                }
            }
            if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                bold = !bold;
                sb.Append(bold ? @"\b " : @"\b0 ");
                i++;
                continue;
            }
            if (s[i] == '*' && (italic || (i + 1 < s.Length && s[i + 1] != ' ')))
            {
                italic = !italic;
                sb.Append(italic ? @"\i " : @"\i0 ");
                continue;
            }
            sb.Append(Escape(s[i].ToString()));
        }
        if (bold) sb.Append(@"\b0 ");
        if (italic) sb.Append(@"\i0 ");
        return sb.ToString();
    }

    internal static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\\' or '{' or '}') sb.Append('\\').Append(c);
            else if (c == '\t') sb.Append(@"\tab ");
            else if (c > 127) sb.Append(@"\u").Append((short)c).Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
