using System.Drawing.Drawing2D;
using ScheduleApp.Recording;
using ScheduleApp.Vision;

namespace ScheduleApp.UI;

/// <summary>
/// Chụp toàn màn hình rồi cho người dùng kéo chọn một vùng làm hình mẫu (giống công cụ Snipping).
/// </summary>
internal sealed class RegionSelectorForm : Form
{
    private readonly Bitmap _screen;
    private readonly Rectangle _virtual;
    private Point _start;
    private Rectangle _selection;
    private bool _dragging;

    public Bitmap? Result { get; private set; }

    /// <summary>Mức scale (DPI/96) của màn hình chứa vùng vừa chọn ở lần chọn gần nhất.</summary>
    public static double LastScale { get; private set; } = 1.0;

    public RegionSelectorForm()
    {
        _virtual = ScreenCapture.VirtualScreen;
        _screen = ScreenCapture.Capture(_virtual);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _virtual;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
    }

    /// <summary>Mở trình chọn vùng (sau khi dời cửa sổ ứng dụng khỏi màn hình). Trả về null nếu hủy.</summary>
    public static async Task<Bitmap?> SelectAsync(IWin32Window? owner)
    {
        using (ScreenHelper.MoveAppWindowsAway())
        {
            await Task.Delay(250); // chờ màn hình vẽ lại sau khi dời cửa sổ
            using var form = new RegionSelectorForm();
            return form.ShowDialog(owner) == DialogResult.OK ? form.Result : null;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _virtual; // đặt lại phòng khi Windows điều chỉnh theo DPI
        Activate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true; // giữ nguyên kích thước pixel phủ toàn màn hình ảo
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImage(_screen, new Rectangle(0, 0, _screen.Width, _screen.Height), new Rectangle(0, 0, _screen.Width, _screen.Height), GraphicsUnit.Pixel);

        using (var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
        using (var region = new Region(ClientRectangle))
        {
            if (_selection.Width > 0 && _selection.Height > 0) region.Exclude(_selection);
            g.FillRegion(dim, region);
        }

        if (_selection.Width > 0 && _selection.Height > 0)
        {
            using var pen = new Pen(Color.FromArgb(0, 150, 255), 2);
            g.DrawRectangle(pen, _selection);
            DrawLabel(g, $"{_selection.Width} × {_selection.Height}", new Point(_selection.X, Math.Max(0, _selection.Y - 26)));
        }

        // Hướng dẫn ở màn hình chính
        var primary = Screen.PrimaryScreen?.Bounds ?? _virtual;
        DrawLabel(g, "Kéo chuột để chọn vùng hình mẫu (vd: một nút bấm, icon)  ·  Esc để hủy",
            new Point(primary.X - _virtual.X + 20, primary.Y - _virtual.Y + 20));
    }

    private static void DrawLabel(Graphics g, string text, Point at)
    {
        using var font = new Font("Segoe UI", 11F, FontStyle.Bold);
        var size = TextRenderer.MeasureText(text, font);
        var rect = new Rectangle(at, new Size(size.Width + 16, size.Height + 8));
        using (var bg = new SolidBrush(Color.FromArgb(0, 120, 212))) g.FillRectangle(bg, rect);
        TextRenderer.DrawText(g, text, font, rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _start = e.Location;
        _selection = Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _selection = Rectangle.FromLTRB(Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y), Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        if (_selection.Width < 6 || _selection.Height < 6)
        {
            _selection = Rectangle.Empty;
            Invalidate();
            return;
        }
        Result = ScreenCapture.Crop(_screen, _selection);
        LastScale = Native.PowerHelper.ScaleAt(new Point(
            _virtual.X + _selection.X + _selection.Width / 2, _virtual.Y + _selection.Y + _selection.Height / 2));
        DialogResult = DialogResult.OK;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _screen.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Khung viền đỏ nhấp nháy quanh vùng tìm thấy (click xuyên qua, tự đóng).</summary>
internal sealed class HighlightForm : Form
{
    public HighlightForm(Rectangle area)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Bounds = Rectangle.Inflate(area, 4, 4);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x8 /*TOPMOST*/ | 0x80 /*TOOLWINDOW*/ | 0x20 /*TRANSPARENT*/ | 0x08000000 /*NOACTIVATE*/;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(Color.Red, 4);
        e.Graphics.DrawRectangle(pen, 2, 2, Width - 4, Height - 4);
    }

    public static void Flash(Rectangle area, int ms = 1800)
    {
        var form = new HighlightForm(area);
        var timer = new System.Windows.Forms.Timer { Interval = ms };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            form.Close();
            form.Dispose();
        };
        form.Show();
        timer.Start();
    }
}

/// <summary>Thanh nhỏ luôn trên cùng hiển thị khi đang ghi macro.</summary>
internal sealed class RecorderToolbar : BaseForm
{
    private readonly MacroRecorder _recorder;
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(196, 43, 28), Font = new Font("Segoe UI", 10F, FontStyle.Bold), Margin = new Padding(3, 7, 12, 3) };
    private bool _finished;

    /// <summary>true = dừng và lưu, false = hủy.</summary>
    public event Action<bool>? Finished;

    public RecorderToolbar(MacroRecorder recorder)
    {
        _recorder = recorder;

        SuspendLayout();
        Text = "Đang ghi thao tác — ScheduleApp";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(8, 6, 8, 6);

        var btnStop = new Button { Text = "■ Dừng && lưu  (Ctrl+Shift+Q)", AutoSize = true };
        var btnCancel = new Button { Text = "Hủy", AutoSize = true };
        btnStop.Click += (_, _) => Finish(true);
        btnCancel.Click += (_, _) => Finish(false);

        var hint = new Label
        {
            Text = "Thao tác bình thường trên các ứng dụng khác — click, kéo thả, cuộn chuột và phím gõ được ghi lại. " +
                   "Chữ gõ vào ô mật khẩu được thay bằng {{secret:MatKhau}} (không lưu mật khẩu thật).",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = UiText.Muted,
            Margin = new Padding(3, 6, 3, 0)
        };

        var chkElements = new CheckBox
        {
            Text = "Ghi click thành \"Click phần tử UI\" khi nhận diện được (ổn định hơn tọa độ)",
            AutoSize = true,
            Checked = Services.SettingsStore.Current.RecordElements,
            Margin = new Padding(3, 4, 3, 0)
        };
        _recorder.RecordElements = chkElements.Checked;
        chkElements.CheckedChanged += (_, _) =>
        {
            _recorder.RecordElements = chkElements.Checked;
            Services.SettingsStore.Current.RecordElements = chkElements.Checked;
            Services.SettingsStore.Save();
        };

        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange([_status, btnStop, btnCancel]);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(row);
        layout.Controls.Add(chkElements);
        layout.Controls.Add(hint);
        Controls.Add(layout);
        ResumeLayout(true);

        _recorder.Changed += UpdateStatus;
        // Gọi từ hook bàn phím: hoãn để hook trả về ngay.
        _recorder.StopRequested += () => BeginInvoke(new MethodInvoker(() => Finish(true)));
        UpdateStatus();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // NOACTIVATE: bấm nút không cướp focus của ứng dụng đang ghi
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 8);
    }

    private void UpdateStatus()
    {
        _status.Text = $"● Đang ghi — {_recorder.StepCount} bước{(_recorder.IsTyping ? " (đang gõ…)" : "")}";
    }

    private void Finish(bool save)
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke(save);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Finish(false);
        base.OnFormClosing(e);
    }
}
