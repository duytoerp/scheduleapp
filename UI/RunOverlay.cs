using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using ScheduleApp.Native;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>
/// Khung trạng thái nhỏ ở góc phải dưới màn hình trong lúc flow chạy: công việc, bước đang chạy / tổng số bước, thời gian,
/// dòng nhật ký mới nhất và các nút Tạm dừng / Bước tiếp / Chạy tiếp / Dừng.
/// Luôn nằm trên cùng nhưng không lấy focus (phím giả lập vẫn vào đúng ứng dụng đích), không lọt vào ảnh chụp màn hình
/// (tìm theo hình ảnh, ảnh lỗi) và tự dời sang góc khác khi chuột giả lập sắp click vào chỗ nó đang che.
/// </summary>
internal sealed partial class RunOverlay : Form
{
    private enum State { Running, PauseRequested, Paused, Finished }

    private const int LogicalWidth = 390;
    private const int EdgeMargin = 12;

    private static readonly Color Back = Color.FromArgb(32, 32, 32);
    private static readonly Color Fore = Color.FromArgb(242, 242, 242);
    private static readonly Color Dim = Color.FromArgb(165, 165, 165);
    private static readonly Color Track = Color.FromArgb(62, 62, 62);
    private static readonly Color RunColor = Color.FromArgb(76, 194, 255);
    private static readonly Color PauseColor = Color.FromArgb(255, 185, 0);
    private static readonly Color OkColor = Color.FromArgb(108, 203, 95);
    private static readonly Color FailColor = Color.FromArgb(255, 107, 107);
    private static readonly Color StopColor = Color.FromArgb(170, 170, 170);

    private readonly Font _small = new("Segoe UI", 8.25F, FontStyle.Bold);
    private readonly Font _title = new("Segoe UI", 10.5F, FontStyle.Bold);
    private readonly Font _bold = new("Segoe UI", 9F, FontStyle.Bold);
    private readonly Font _text = new("Segoe UI", 9.5F);
    private readonly Font _detailFont = new("Segoe UI", 8.25F);

    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, BackColor = Back };
    private readonly Button _btnPause = HudButton("⏸ Tạm dừng");
    private readonly Button _btnStep = HudButton("⏭ Bước tiếp");
    private readonly Button _btnContinue = HudButton("▶ Chạy tiếp");
    private readonly Button _btnStop = HudButton("■ Dừng", Color.FromArgb(196, 43, 28));
    private readonly Button _btnClose = HudButton("✕ Đóng");
    private readonly Button _btnHide = HudButton("— Ẩn");
    private readonly Label _hint = new() { Text = "Ctrl+Shift+Q = dừng", AutoSize = true, ForeColor = Dim, BackColor = Back };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly ToolTip _tips = new() { ShowAlways = true };

    private readonly object _boundsLock = new();
    private Rectangle _avoidBounds;     // vùng đang che (rỗng khi ẩn) — đọc từ luồng của flow
    private Point? _home;               // vị trí người dùng kéo tới; null = góc phải dưới

    private RunProgress? _run;
    private State _state;
    private string _pausedStep = "", _pausedReason = "";
    private string _detail = "";
    private bool _detailWarn;
    private DateTime _finishedAt;
    /// <summary>
    /// Người dùng đã bấm "Ẩn" — không hiện lại tới khi hết flow đang chạy / đang chờ (<see cref="EndDismissal"/>):
    /// một bộ kiểm thử, kiểm thử theo dữ liệu hay lần chạy lại xếp hàng từng kịch bản / dòng, mỗi cái là một lần chạy riêng.
    /// </summary>
    private bool _dismissed;

    public event Action? PauseClicked, StepClicked, ContinueClicked, StopClicked;

    public RunOverlay()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Back;
        ForeColor = Fore;
        DoubleBuffered = true;
        Text = "ScheduleApp — flow đang chạy";
        Icon = AppIcon.Get();

        _btnPause.Click += (_, _) => PauseClicked?.Invoke();
        _btnStep.Click += (_, _) => StepClicked?.Invoke();
        _btnContinue.Click += (_, _) => ContinueClicked?.Invoke();
        _btnStop.Click += (_, _) => StopClicked?.Invoke();
        _btnClose.Click += (_, _) => Hide();
        _btnHide.Click += (_, _) => DismissRun();
        _tips.SetToolTip(_btnStop, "Dừng flow ngay (Ctrl+Shift+Q)");
        _tips.SetToolTip(_btnHide, "Ẩn khung cho lần chạy này (cả bộ kiểm thử / các flow đang chờ) — flow vẫn chạy tiếp. Tắt hẳn: chuột phải biểu tượng ScheduleApp ở khay.");
        _buttons.Padding = new Padding(S(EdgeMargin) - 3, 0, S(8), S(8));
        _hint.Margin = new Padding(S(6), S(8), 0, 0);
        _buttons.Controls.AddRange([_btnPause, _btnStep, _btnContinue, _btnStop, _btnHide, _btnClose, _hint]);
        _buttons.MouseDown += (_, e) => DragStart(e);
        Controls.Add(_buttons);
        ResumeLayout(false);

        _timer.Tick += (_, _) => Tick();
        ApplyState();
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    private static Button HudButton(string text, Color? back = null)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = back ?? Color.FromArgb(58, 58, 58),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(3, 3, 3, 0),
            Padding = new Padding(4, 0, 4, 0),
            Cursor = Cursors.Hand,
            TabStop = false,
            UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderColor = back is { } c ? ControlPaint.Light(c, 0.2f) : Color.FromArgb(88, 88, 88);
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(b.BackColor, 0.25f);
        return b;
    }

    // ───────────────────────────── trạng thái (luồng UI) ─────────────────────────────

    /// <summary>Flow vừa bắt đầu / sang bước mới / kết thúc.</summary>
    public void ShowProgress(RunProgress p)
    {
        bool newRun = Apply(p);
        if (IsDismissed) return;
        if (newRun && p.Ok == null) MoveTo(Home());
        if (!Visible) Show();
        _timer.Start();
    }

    /// <summary>Cập nhật nội dung (chưa hiện). True nếu là lần chạy mới.</summary>
    internal bool Apply(RunProgress p)
    {
        bool newRun = _run == null || _run.JobId != p.JobId || _run.Started != p.Started;
        if (!newRun && p.Ok == null && p.Step != _run!.Step) _detail = "";   // dòng chi tiết thuộc bước trước
        _run = p;
        if (p.Ok != null)
        {
            _state = State.Finished;
            _finishedAt = DateTime.Now;
        }
        else if (newRun || _state == State.Finished)
        {
            _state = State.Running;
            _detail = "";
            _detailWarn = false;
            _pausedStep = _pausedReason = "";
        }
        ApplyState();
        return newRun;
    }

    /// <summary>Người dùng đã bấm "Ẩn" — flow vẫn chạy tiếp; khung hiện lại ở lần chạy sau khi đã hết flow đang chạy / đang chờ.</summary>
    internal bool IsDismissed => _dismissed;

    internal void DismissRun()
    {
        if (_run != null) _dismissed = true;
        Hide();
    }

    /// <summary>Không còn flow nào đang chạy / đang chờ — lần chạy sau lại hiện khung (bỏ "Ẩn").</summary>
    internal void EndDismissal() => _dismissed = false;

    /// <summary>Flow đang chờ người dùng ở bước <paramref name="stepText"/> (null = đã chạy tiếp).</summary>
    public void SetPaused(string? reason, string? stepText)
    {
        if (_run == null || _state == State.Finished) return;
        if (reason != null)
        {
            _state = State.Paused;
            _pausedReason = reason;
            _pausedStep = StepNumber().Replace(stepText ?? "", "");
        }
        else if (_state == State.Paused) _state = State.Running;
        ApplyState();
    }

    /// <summary>Đã gửi yêu cầu tạm dừng — chờ bước đang chạy xong.</summary>
    public void MarkPauseRequested()
    {
        if (_state != State.Running) return;
        _state = State.PauseRequested;
        ApplyState();
    }

    /// <summary>Dòng nhật ký mới (dạng "HH:mm:ss [INFO] …") — hiện dòng chi tiết mới nhất của bước đang chạy.</summary>
    public void AddLog(string line)
    {
        if (_run == null || _state == State.Finished) return;
        var m = LogLine().Match(line);
        if (!m.Success) return;
        var text = m.Groups[2].Value.Trim();
        // Dòng "[3/12] mô tả bước" trùng với phần bước đang chạy.
        if (text.Length == 0 || StepLogLine().IsMatch(text) || text.StartsWith("▶ Bắt đầu", StringComparison.Ordinal)) return;
        _detail = text.Replace("\r", "").Replace("\n", " ⏎ ");
        _detailWarn = m.Groups[1].Value != "INFO";
        Invalidate();
    }

    [GeneratedRegex(@"^\d\d:\d\d:\d\d \[([^\]]+)\] (.*)$", RegexOptions.Singleline)]
    private static partial Regex LogLine();

    [GeneratedRegex(@"^\[\d+/\d+\] ")]
    private static partial Regex StepLogLine();

    [GeneratedRegex(@"^\d+\.\s*")]
    private static partial Regex StepNumber();

    private void ApplyState()
    {
        SuspendLayout();
        _buttons.SuspendLayout();
        _btnPause.Visible = _state is State.Running or State.PauseRequested;
        _btnPause.Enabled = _state == State.Running;
        _btnPause.Text = _state == State.PauseRequested ? "⏸ Chờ bước đang chạy…" : "⏸ Tạm dừng";
        _btnStep.Visible = _btnContinue.Visible = _state == State.Paused;
        _btnStop.Visible = _state != State.Finished;
        _btnClose.Visible = _state == State.Finished;
        _btnHide.Visible = _state is State.Running or State.PauseRequested;
        // Gợi ý hiện cùng các nút lúc đang chạy: Tạm dừng, Dừng, Ẩn.
        _hint.Visible = _state is State.Running or State.PauseRequested && HintFits(_btnPause, _btnStop, _btnHide);
        _buttons.ResumeLayout(true);
        ResumeLayout(true);
        Size = new Size(S(LogicalWidth), ContentHeight() + _buttons.PreferredSize.Height);
        Invalidate();
    }

    /// <summary>Dòng gợi ý phím tắt vừa chỗ còn lại bên phải các nút (không thì chỉ còn trong tooltip của nút Dừng).</summary>
    private bool HintFits(params Button[] shown)
    {
        int used = _buttons.Padding.Horizontal + _hint.Margin.Horizontal + _hint.PreferredWidth
                   + shown.Sum(b => b.GetPreferredSize(Size.Empty).Width + b.Margin.Horizontal);
        return used <= S(LogicalWidth);
    }

    private void Tick()
    {
        if (_state == State.Finished)
        {
            // Kết quả hiện thêm một lúc (lỗi lâu hơn) rồi tự ẩn; đang rê chuột trên khung thì chưa ẩn.
            var keep = TimeSpan.FromSeconds(_run?.Ok == true ? 6 : 20);
            if (DateTime.Now - _finishedAt > keep && !Bounds.Contains(Cursor.Position))
            {
                Hide();
                return;
            }
        }
        Invalidate();
    }

    // ───────────────────────────── vẽ ─────────────────────────────

    private (Color Color, string Label) Header()
    {
        if (_run == null) return (RunColor, "");
        return _state switch
        {
            State.Paused => (PauseColor, $"⏸ {(_pausedReason == "Tạm dừng" ? "TẠM DỪNG" : "TẠM DỪNG · " + _pausedReason.ToUpperInvariant())} — CHỜ BẠN"),
            State.PauseRequested => (PauseColor, "⏸ SẼ TẠM DỪNG TRƯỚC BƯỚC KẾ TIẾP"),
            State.Finished when _run.Ok == true => (OkColor, "✔ HOÀN THÀNH"),
            State.Finished when _run.Message == "Đã dừng" => (StopColor, "■ ĐÃ DỪNG"),
            State.Finished => (FailColor, "✖ KHÔNG HOÀN THÀNH"),
            _ => (RunColor, _run.IsTest ? "● ĐANG CHẠY THỬ" : "● ĐANG CHẠY")
        };
    }

    /// <summary>Dòng "Bước 3/12 · 0:05" và nội dung bước (hoặc kết quả khi đã xong).</summary>
    private (string StepLine, string Text) Body()
    {
        var r = _run!;
        switch (_state)
        {
            case State.Finished:
                var total = Elapsed(_finishedAt - r.Started);
                if (r.Ok == true) return ($"{r.Total} bước · {total}", "Đã chạy xong tất cả các bước.");
                return (r.FailedStep > 0 ? $"Lỗi ở bước {r.FailedStep}/{r.Total} · {total}" : total, r.Message);
            case State.Paused:
                return (r.Step >= 0 ? $"Sắp chạy bước {r.Step + 1}/{r.Total}" : "Sắp chạy", _pausedStep);
            default:
                if (r.Step < 0) return ("Đang chuẩn bị…", "");
                return ($"Bước {r.Step + 1}/{r.Total} · {Elapsed(DateTime.Now - r.StepStarted)}", r.StepText);
        }
    }

    private double Fraction()
    {
        var r = _run!;
        if (r.Total <= 0) return 0;
        if (_state == State.Finished && r.Ok == true) return 1;
        if (_state == State.Finished && r.FailedStep > 0) return (r.FailedStep - 1) / (double)r.Total;
        return Math.Clamp(r.Step, 0, r.Total) / (double)r.Total;
    }

    internal string HeaderText => Header().Label;
    internal (string StepLine, string Text) BodyText => Body();
    internal string Detail => _detail;

    internal static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private int ContentHeight() =>
        S(12) + _small.Height + S(2) + _title.Height + S(6) + _bold.Height + S(2) + _text.Height * 2 + S(6) + S(4) + S(6) + _detailFont.Height + S(6);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_run == null) return;
        var g = e.Graphics;
        var (color, label) = Header();
        var (stepLine, text) = Body();
        int pad = S(EdgeMargin) + S(4), width = ClientSize.Width - pad - S(EdgeMargin);
        const TextFormatFlags one = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

        using (var stripe = new SolidBrush(color)) g.FillRectangle(stripe, 0, 0, S(4), ClientSize.Height);

        int y = S(12);
        var time = Elapsed((_state == State.Finished ? _finishedAt : DateTime.Now) - _run.Started);
        var timeSize = TextRenderer.MeasureText(g, time, _small, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, time, _small, new Rectangle(pad + width - timeSize.Width, y, timeSize.Width, _small.Height), Dim, one | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, label, _small, new Rectangle(pad, y, width - timeSize.Width - S(8), _small.Height), color, one | TextFormatFlags.NoPadding);
        y += _small.Height + S(2);

        TextRenderer.DrawText(g, _run.JobName, _title, new Rectangle(pad, y, width, _title.Height), Fore, one | TextFormatFlags.NoPadding);
        y += _title.Height + S(6);

        TextRenderer.DrawText(g, stepLine, _bold, new Rectangle(pad, y, width, _bold.Height), _state == State.Running ? RunColor : color, one | TextFormatFlags.NoPadding);
        y += _bold.Height + S(2);

        TextRenderer.DrawText(g, text, _text, new Rectangle(pad, y, width, _text.Height * 2), _state == State.Finished && _run.Ok != true ? FailColor : Fore,
            TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
        y += _text.Height * 2 + S(6);

        using (var track = new SolidBrush(Track)) g.FillRectangle(track, pad, y, width, S(4));
        int done = (int)Math.Round(width * Fraction());
        using (var fill = new SolidBrush(color)) g.FillRectangle(fill, pad, y, done, S(4));
        y += S(4) + S(6);

        if (_state != State.Finished && _detail.Length > 0)
            TextRenderer.DrawText(g, _detail, _detailFont, new Rectangle(pad, y, width, _detailFont.Height), _detailWarn ? PauseColor : Dim, one | TextFormatFlags.NoPadding);
    }

    // ───────────────────────────── cửa sổ ─────────────────────────────

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x8 /*TOPMOST*/ | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Không xuất hiện trong ảnh chụp màn hình (tìm theo hình ảnh, ảnh lỗi, báo cáo kiểm thử) — Windows 10 2004 trở lên.
        Win32.SetWindowDisplayAffinity(Handle, 0x11 /*WDA_EXCLUDEFROMCAPTURE*/);
        int round = 3; // DWMWCP_ROUNDSMALL (Windows 11)
        Win32.DwmSetWindowAttribute(Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, sizeof(int));
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        // Tạo sẵn cửa sổ của mọi nút (kể cả nút đang ẩn): nút ẩn được tạo muộn lúc hiện lần đầu sẽ bị WinForms đổi thứ tự trong hàng.
        foreach (Control c in _buttons.Controls) _ = c.Handle;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        DragStart(e);
    }

    /// <summary>Kéo khung tới chỗ khác — lần chạy sau vẫn hiện ở chỗ đó.</summary>
    private void DragStart(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Win32.ReleaseCapture();
        Win32.SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, IntPtr.Zero);
        _home = Location;
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        UpdateAvoidBounds();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateAvoidBounds();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _timer.Stop();
        UpdateAvoidBounds();
    }

    private void UpdateAvoidBounds()
    {
        lock (_boundsLock) _avoidBounds = Visible ? Bounds : Rectangle.Empty;
    }

    private Point Home()
    {
        if (_home is Point p && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(p, Size)))) return p;
        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        return new Point(area.Right - Width - S(EdgeMargin), area.Bottom - Height - S(EdgeMargin));
    }

    private void MoveTo(Point p)
    {
        if (Location != p) Location = p;
    }

    /// <summary>
    /// Chuột giả lập sắp tới <paramref name="p"/> (gọi từ luồng của flow): nếu khung đang che chỗ đó thì dời sang góc khác
    /// và chờ dời xong rồi mới cho click.
    /// </summary>
    public void Avoid(Point p)
    {
        Rectangle covered;
        lock (_boundsLock) covered = _avoidBounds;
        if (covered.IsEmpty) return;
        covered.Inflate(EdgeMargin, EdgeMargin);
        if (!covered.Contains(p)) return;

        if (!InvokeRequired)
        {
            MoveAway(p);
            return;
        }
        var done = new ManualResetEventSlim();
        try
        {
            BeginInvoke(new MethodInvoker(() =>
            {
                try { MoveAway(p); }
                finally { done.Set(); }
            }));
        }
        catch (InvalidOperationException) { return; }
        done.Wait(1000);
    }

    private void MoveAway(Point p)
    {
        if (IsDisposed || !Visible) return;
        var home = Home();
        var area = Screen.FromPoint(new Point(home.X + Width / 2, home.Y + Height / 2)).WorkingArea;
        MoveTo(Place(area, Size, home, p, S(EdgeMargin)));
    }

    /// <summary>Vị trí đầu tiên (chỗ quen thuộc, rồi các góc phải dưới → trái dưới → trái trên → phải trên) không che điểm sắp click.</summary>
    internal static Point Place(Rectangle area, Size size, Point home, Point avoid, int margin)
    {
        Point[] candidates =
        [
            home,
            new(area.Right - size.Width - margin, area.Bottom - size.Height - margin),
            new(area.Left + margin, area.Bottom - size.Height - margin),
            new(area.Left + margin, area.Top + margin),
            new(area.Right - size.Width - margin, area.Top + margin)
        ];
        foreach (var c in candidates)
        {
            var r = new Rectangle(c, size);
            r.Inflate(EdgeMargin, EdgeMargin);
            if (!r.Contains(avoid)) return c;
        }
        return home;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            foreach (var f in new[] { _small, _title, _bold, _text, _detailFont }) f.Dispose();
        }
        base.Dispose(disposing);
    }
}
