using System.Media;
using ScheduleApp.Native;

namespace ScheduleApp.UI;

/// <summary>
/// Cửa sổ nhắc nhở hiện ở góc phải dưới màn hình, luôn nằm trên cùng. Trong lúc có flow đang thao tác chuột/phím (<see cref="SetShield"/>
/// — vd công việc khác chạy khi flow này đã nhường lượt chờ bấm OK), nhắc nhở được che chắn để flow đó không bấm nhầm / thấy nhầm nó:
/// bỏ qua chuột/phím giả lập (kể cả Enter của nút mặc định), tự dời khỏi chỗ chuột giả lập sắp click, không lọt vào ảnh chụp màn hình,
/// không giữ quyền nhận bàn phím (cửa sổ mới hiện không lấy focus).
/// </summary>
internal sealed class ReminderForm : BaseForm
{
    private const int EdgeMargin = 12;

    private static readonly object OpenLock = new();
    private static readonly List<ReminderForm> OpenForms = [];
    private static volatile bool _shielded;

    [ThreadStatic] private static bool _filterInstalled;

    /// <summary>Thông điệp chuột/phím đang xử lý do phần mềm giả lập (SendInput…) chứ không phải người bấm. Kiểm thử thay được.</summary>
    internal static Func<bool> IsInjectedInput { get; set; } = Win32.IsInjectedInput;

    /// <summary>Kiểm thử: vùng làm việc thay cho màn hình thật (đặt cửa sổ ngoài màn hình).</summary>
    internal static Rectangle? TestArea { get; set; }

    private readonly bool _requireConfirm;
    /// <summary>Cửa sổ đang được chọn trước khi nhắc nhở hiện — trả lại focus cho nó khi bắt đầu che chắn.</summary>
    private readonly IntPtr _previousForeground;
    private Rectangle _bounds; // đọc từ luồng của flow (trong OpenLock)
    private Point _home;

    public ReminderForm(string title, string message, bool requireConfirm)
    {
        _requireConfirm = requireConfirm;
        var foreground = Win32.GetForegroundWindow();
        _previousForeground = WindowHelper.BelongsToThisApp(foreground) ? IntPtr.Zero : foreground;
        if (!_filterInstalled)
        {
            // Bộ lọc của luồng giao diện: bỏ chuột/phím giả lập nhắm vào nhắc nhở trong lúc che chắn.
            Application.AddMessageFilter(new InjectedInputFilter());
            _filterInstalled = true;
        }

        SuspendLayout();
        Text = "Nhắc nhở — ScheduleApp";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        Padding = new Padding(16, 12, 16, 12);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill
        };

        var lblTitle = new Label
        {
            Text = "⏰  " + title,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 84, 153),
            Margin = new Padding(0, 0, 0, 8)
        };
        var lblMessage = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            MinimumSize = new Size(320, 0),
            Font = new Font("Segoe UI", 10F),
            Margin = new Padding(0, 0, 0, 12)
        };
        var lblTime = new Label
        {
            Text = DateTime.Now.ToString("HH:mm:ss dd/MM/yyyy") + (requireConfirm ? "  •  Flow đang tạm dừng chờ bạn xác nhận" : ""),
            AutoSize = true,
            ForeColor = UiText.Muted,
            Margin = new Padding(0, 0, 0, 10)
        };
        var btnOk = new Button
        {
            Text = requireConfirm ? "Đã hiểu — tiếp tục flow" : "Đã hiểu",
            AutoSize = true,
            Padding = new Padding(10, 3, 10, 3),
            Anchor = AnchorStyles.Right
        };
        btnOk.Click += (_, _) => Close();
        AcceptButton = btnOk;

        layout.Controls.Add(lblTitle);
        if (!string.IsNullOrWhiteSpace(message)) layout.Controls.Add(lblMessage);
        layout.Controls.Add(lblTime);
        layout.Controls.Add(btnOk);
        Controls.Add(layout);
        ResumeLayout(true);
    }

    /// <summary>Đang che chắn (có flow đang thao tác chuột/phím).</summary>
    internal static bool Shielded => _shielded;

    /// <summary>
    /// Bật / tắt che chắn cho mọi nhắc nhở đang mở và nhắc nhở hiện sau đó (gọi được từ luồng bất kỳ — có hiệu lực ngay với chuột/phím
    /// giả lập, phần cửa sổ được cập nhật trên luồng giao diện).
    /// </summary>
    public static void SetShield(bool on)
    {
        _shielded = on;
        List<ReminderForm> open;
        lock (OpenLock) open = [.. OpenForms];
        foreach (var f in open)
        {
            try { f.BeginInvoke(new MethodInvoker(f.ApplyShield)); }
            catch (InvalidOperationException) { } // đang đóng
        }
    }

    /// <summary>
    /// Chuột giả lập sắp tới <paramref name="p"/> (gọi từ luồng của flow): nhắc nhở đang che chỗ đó thì dời sang góc khác và chờ dời xong
    /// rồi mới cho click — flow không bấm nhầm vào nút "Đã hiểu — tiếp tục flow" của flow khác.
    /// </summary>
    public static void Avoid(Point p)
    {
        if (!_shielded) return;
        List<ReminderForm> hit;
        lock (OpenLock) hit = OpenForms.Where(f => Rectangle.Inflate(f._bounds, EdgeMargin, EdgeMargin).Contains(p)).ToList();
        foreach (var f in hit)
        {
            if (!f.InvokeRequired)
            {
                f.MoveAway(p);
                continue;
            }
            var done = new ManualResetEventSlim();
            try
            {
                f.BeginInvoke(new MethodInvoker(() =>
                {
                    try { f.MoveAway(p); }
                    finally { done.Set(); }
                }));
            }
            catch (InvalidOperationException) { continue; }
            done.Wait(1000);
        }
    }

    protected override bool ShowWithoutActivation => !_requireConfirm || _shielded;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = TestArea ?? Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        int stackOffset = Application.OpenForms.OfType<ReminderForm>().Count(f => f != this && f.Visible) * 30;
        Location = _home = new Point(area.Right - Width - EdgeMargin - stackOffset, area.Bottom - Height - EdgeMargin - stackOffset);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        lock (OpenLock) OpenForms.Add(this);
        ApplyShield();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        lock (OpenLock) OpenForms.Remove(this);
        base.OnHandleDestroyed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SystemSounds.Exclamation.Play();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        TrackBounds();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        TrackBounds();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        TrackBounds();
    }

    private void TrackBounds()
    {
        lock (OpenLock) _bounds = Visible ? Bounds : Rectangle.Empty;
    }

    /// <summary>Che chắn theo trạng thái hiện tại: không lọt vào ảnh chụp màn hình, trả focus cho cửa sổ trước đó.</summary>
    private void ApplyShield()
    {
        if (IsDisposed || !IsHandleCreated) return;
        bool on = _shielded;
        // Flow khác tìm theo hình ảnh / chụp ảnh lỗi không thấy nhắc nhở — Windows 10 2004 trở lên (bản cũ hơn: không có tác dụng).
        Win32.SetWindowDisplayAffinity(Handle, on ? 0x11u /*WDA_EXCLUDEFROMCAPTURE*/ : 0u);
        if (!on || Win32.GetForegroundWindow() != Handle) return;
        // Đang giữ quyền nhận bàn phím: flow khác gõ phím (không chọn cửa sổ đích) sẽ báo "đang chọn ScheduleApp" hoặc bấm Enter vào nút OK.
        var previous = _previousForeground;
        Win32.SetForegroundWindow(previous != IntPtr.Zero && Win32.IsWindow(previous) && Win32.IsWindowVisible(previous) && !Win32.IsIconic(previous)
            ? previous : Win32.GetShellWindow());
    }

    private void MoveAway(Point p)
    {
        if (IsDisposed || !Visible) return;
        var area = TestArea ?? Screen.FromPoint(new Point(_home.X + Width / 2, _home.Y + Height / 2)).WorkingArea;
        var target = RunOverlay.Place(area, Size, _home, p, EdgeMargin);
        if (Location != target) Location = target;
    }

    /// <summary>Bỏ thông điệp chuột (bấm) / phím giả lập gửi tới nhắc nhở trong lúc che chắn.</summary>
    private sealed class InjectedInputFilter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (!_shielded || !IsClickOrKey(m.Msg)) return false;
            return Control.FromChildHandle(m.HWnd)?.FindForm() is ReminderForm && IsInjectedInput();
        }

        private static bool IsClickOrKey(int msg) =>
            msg is >= 0x100 and <= 0x109              // WM_KEYDOWN … WM_UNICHAR (kể cả WM_SYSKEYDOWN: Alt+F4)
                or >= 0x201 and <= 0x20E               // nút chuột, con lăn (không tính WM_MOUSEMOVE)
                or >= 0xA1 and <= 0xAD;                // nút chuột trên viền / thanh tiêu đề (nút đóng ✕)
    }
}
