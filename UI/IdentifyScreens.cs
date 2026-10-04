using ScheduleApp.Native;

namespace ScheduleApp.UI;

/// <summary>
/// "Hiện số màn hình" như trong Cài đặt Windows: mỗi màn hình hiện số lớn của nó ở góc trái dưới trong vài giây,
/// để biết chọn "Màn hình 2" là màn hình nào. Không lấy focus.
/// </summary>
internal static class IdentifyScreens
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);

    public static void Show(IWin32Window? owner = null)
    {
        var displays = Displays.All();
        var labels = displays.Select(d => new NumberForm(d, Displays.Label(d, displays))).ToList();
        foreach (var f in labels) f.Show();
        var timer = new System.Windows.Forms.Timer { Interval = (int)Duration.TotalMilliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            foreach (var f in labels) f.Close();
        };
        timer.Start();
    }

    private sealed class NumberForm : Form
    {
        private readonly Display _display;
        private readonly string _detail;
        private readonly Font _big, _small;

        public NumberForm(Display display, string detail)
        {
            _display = display;
            _detail = detail;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 32);
            ForeColor = Color.White;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            _big = new Font("Segoe UI Semibold", 96F, GraphicsUnit.Pixel);
            _small = new Font("Segoe UI", 15F, GraphicsUnit.Pixel);
            // Pixel thật của từng màn hình (ứng dụng nhận biết DPI theo màn hình).
            int w = Math.Min(360, display.WorkingArea.Width), h = Math.Min(190, display.WorkingArea.Height);
            Bounds = new Rectangle(display.WorkingArea.Left + 40, display.WorkingArea.Bottom - h - 40, w, h);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var accent = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(accent, 0, 0, 8, Height);
            TextRenderer.DrawText(e.Graphics, _display.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), _big,
                new Rectangle(24, 0, Width - 24, Height - 40), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, _detail, _small, new Rectangle(28, Height - 44, Width - 32, 32), Color.FromArgb(210, 210, 210),
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _big.Dispose();
                _small.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
