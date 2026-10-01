using System.Drawing.Drawing2D;

namespace ScheduleApp.UI;

/// <summary>Màu, nút phẳng và renderer thanh công cụ / menu dùng chung — giao diện kiểu Windows 11.</summary>
internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(0, 103, 192);
    public static readonly Color AccentHover = Color.FromArgb(0, 86, 163);
    public static readonly Color AccentSoft = Color.FromArgb(229, 240, 251);
    public static readonly Color Background = Color.FromArgb(243, 245, 248);
    public static readonly Color Surface = Color.White;
    public static readonly Color NavBackground = Color.FromArgb(235, 238, 243);
    public static readonly Color Border = Color.FromArgb(220, 224, 230);
    public static readonly Color Text = Color.FromArgb(27, 31, 36);
    public static readonly Color Muted = Color.FromArgb(96, 104, 114);
    public static readonly Color Success = Color.FromArgb(16, 124, 16);
    public static readonly Color SuccessSoft = Color.FromArgb(223, 246, 221);
    public static readonly Color Danger = Color.FromArgb(196, 43, 28);
    public static readonly Color DangerSoft = Color.FromArgb(253, 231, 233);
    public static readonly Color Warning = Color.FromArgb(157, 93, 0);

    private static Font? _title, _subtitle, _bold;
    public static Font TitleFont => _title ??= new Font("Segoe UI Semibold", 15F);
    public static Font SubtitleFont => _subtitle ??= new Font("Segoe UI", 9F);
    public static Font BoldFont => _bold ??= new Font("Segoe UI Semibold", 9F);

    /// <summary>Đặt renderer phẳng cho mọi thanh công cụ, menu chuột phải và thanh trạng thái.</summary>
    public static void Install() => ToolStripManager.Renderer = new FlatRenderer();

    /// <summary>Nút phẳng viền mảnh; <paramref name="primary"/> = nút chính nền màu nhấn.</summary>
    public static void StyleButton(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.Cursor = Cursors.Hand;
        if (b.Padding == Padding.Empty) b.Padding = new Padding(8, 2, 8, 2);
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Accent;
            b.FlatAppearance.MouseOverBackColor = AccentHover;
            b.FlatAppearance.MouseDownBackColor = AccentHover;
        }
        else
        {
            b.BackColor = Surface;
            if (b.ForeColor == SystemColors.ControlText) b.ForeColor = Text;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(245, 247, 250);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(233, 236, 240);
        }
    }

    /// <summary>Áp kiểu nút phẳng cho mọi nút trong form (nút OK / AcceptButton thành nút chính).</summary>
    public static void Apply(Form form)
    {
        void Walk(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Button b && b.FlatStyle != FlatStyle.Flat)
                    StyleButton(b, ReferenceEquals(form.AcceptButton, b) || b.Text is "OK" or "Lưu" or "Lưu && đóng");
                Walk(c);
            }
        }
        Walk(form);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Tiêu đề trang: chữ lớn + dòng mô tả.</summary>
    public static Control PageHeader(string title, string subtitle)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(18, 14, 18, 4),
            BackColor = Background
        };
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = TitleFont, ForeColor = Text, Margin = new Padding(0), UseMnemonic = false });
        panel.Controls.Add(new Label { Text = subtitle, AutoSize = true, Font = SubtitleFont, ForeColor = Muted, Margin = new Padding(1, 2, 0, 0), UseMnemonic = false });
        return panel;
    }

    /// <summary>Thanh lệnh phẳng của một trang.</summary>
    public static ToolStrip CommandBar() => new()
    {
        GripStyle = ToolStripGripStyle.Hidden,
        Padding = new Padding(14, 6, 14, 6),
        BackColor = Background,
        Dock = DockStyle.Top,
        ImageScalingSize = new Size(16, 16),
        CanOverflow = true
    };

    public static ToolStripButton CommandButton(string text, EventHandler onClick, bool primary = false, string? tip = null)
    {
        var b = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(0, 0, 4, 0),
            ToolTipText = tip ?? "",
            Tag = primary ? PrimaryTag : null
        };
        if (primary) b.ForeColor = Color.White;
        b.Click += onClick;
        return b;
    }

    /// <summary>Đánh dấu nút thanh công cụ được vẽ nền màu nhấn.</summary>
    public const string PrimaryTag = "primary";

    private sealed class FlatRenderer : ToolStripProfessionalRenderer
    {
        public FlatRenderer() : base(new FlatColors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
            {
                using var pen = new Pen(Border);
                e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            }
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.ToolStrip is ToolStripDropDown ? Surface : e.ToolStrip.BackColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            bool primary = Equals(item.Tag, PrimaryTag);
            var r = new RectangleF(0.5f, 0.5f, item.Width - 1.5f, item.Height - 1.5f);
            Color? fill = primary ? (item.Pressed || item.Selected ? AccentHover : item.Enabled ? Accent : Color.FromArgb(150, 170, 190))
                : item.Pressed ? Color.FromArgb(222, 227, 233)
                : item.Selected || (item is ToolStripButton { Checked: true }) ? Color.FromArgb(229, 233, 238)
                : null;
            if (fill == null) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(r, 4);
            using var brush = new SolidBrush(fill.Value);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => OnRenderButtonBackground(e);

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (Equals(e.Item.Tag, PrimaryTag)) e.TextColor = Color.White;
            else if (!e.Item.Enabled) e.TextColor = Color.FromArgb(160, 166, 173);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            if (e.Vertical)
            {
                int x = e.Item.Width / 2;
                e.Graphics.DrawLine(pen, x, 5, x, e.Item.Height - 5);
            }
            else
            {
                int y = e.Item.Height / 2;
                e.Graphics.DrawLine(pen, 28, y, e.Item.Width - 4, y);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2), 4);
            using var brush = new SolidBrush(AccentSoft);
            e.Graphics.FillPath(brush, path);
        }
    }

    private sealed class FlatColors : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Background;
        public override Color ToolStripGradientMiddle => Background;
        public override Color ToolStripGradientEnd => Background;
        public override Color MenuStripGradientBegin => Background;
        public override Color MenuStripGradientEnd => Background;
        public override Color StatusStripGradientBegin => Surface;
        public override Color StatusStripGradientEnd => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => AccentSoft;
        public override Color ButtonSelectedBorder => Color.Transparent;
        public override Color ButtonPressedBorder => Color.Transparent;
        public override Color ButtonCheckedHighlightBorder => Color.Transparent;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}

/// <summary>Mục điều hướng ở thanh bên trái: icon + chữ, mục đang chọn có nền trắng và vạch màu nhấn.</summary>
internal sealed class NavButton : Control
{
    private bool _hover;
    private bool _selected;

    public NavButton(string glyph, string text)
    {
        Glyph = glyph;
        Text = text;
        Height = 40;
        Dock = DockStyle.Top;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    public string Glyph { get; }

    /// <summary>Số đếm nhỏ bên phải (vd số kịch bản không đạt); trống = không hiện.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Badge { get; set; } = "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BadgeColor { get; set; } = Theme.Danger;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.NavBackground);
        var box = new RectangleF(LogicalToDeviceUnits(6), 2, Width - LogicalToDeviceUnits(12), Height - 4);
        if (_selected || _hover)
        {
            using var path = Theme.RoundRect(box, LogicalToDeviceUnits(5));
            using var brush = new SolidBrush(_selected ? Theme.Surface : Color.FromArgb(226, 230, 236));
            g.FillPath(brush, path);
        }
        if (_selected)
        {
            using var bar = new SolidBrush(Theme.Accent);
            float h = Height * 0.42f;
            using var path = Theme.RoundRect(new RectangleF(box.X, (Height - h) / 2, LogicalToDeviceUnits(3), h), 1.5f);
            g.FillPath(bar, path);
        }
        using (var icon = StepVisuals.CreateIconFont(Font, 2.5f))
            TextRenderer.DrawText(g, Glyph, icon, new Rectangle(LogicalToDeviceUnits(16), 0, LogicalToDeviceUnits(26), Height),
                _selected ? Theme.Accent : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        using (var font = new Font(Font, _selected ? FontStyle.Bold : FontStyle.Regular))
            TextRenderer.DrawText(g, Text, font, new Rectangle(LogicalToDeviceUnits(48), 0, Width - LogicalToDeviceUnits(84), Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Badge.Length > 0)
        {
            using var f = new Font(Font.FontFamily, Font.Size - 1, FontStyle.Bold);
            var size = TextRenderer.MeasureText(Badge, f);
            var pill = new RectangleF(Width - LogicalToDeviceUnits(16) - size.Width - 6, (Height - size.Height) / 2f, size.Width + 6, size.Height);
            using var path = Theme.RoundRect(pill, pill.Height / 2);
            using var brush = new SolidBrush(BadgeColor);
            g.FillPath(brush, path);
            TextRenderer.DrawText(g, Badge, f, Rectangle.Round(pill), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Round(box));
    }
}

/// <summary>Thẻ số liệu: nhãn nhỏ + con số lớn (vd "Kịch bản đạt 3 / 4").</summary>
internal sealed class StatCard : Control
{
    private string _value = "—";
    private Color _valueColor = Theme.Text;

    public StatCard(string caption)
    {
        Caption = caption;
        DoubleBuffered = true;
        Size = new Size(170, 72);
        Margin = new Padding(0, 0, 12, 0);
    }

    public string Caption { get; }

    public void SetValue(string value, Color? color = null)
    {
        _value = value;
        _valueColor = color ?? Theme.Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundRect(r, LogicalToDeviceUnits(8)))
        {
            using var fill = new SolidBrush(Theme.Surface);
            using var pen = new Pen(Theme.Border);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        int pad = LogicalToDeviceUnits(12);
        TextRenderer.DrawText(g, Caption, Font, new Point(pad, LogicalToDeviceUnits(9)), Theme.Muted);
        using var big = new Font("Segoe UI Semibold", 16F);
        TextRenderer.DrawText(g, _value, big, new Point(pad - 2, LogicalToDeviceUnits(29)), _valueColor);
    }
}
