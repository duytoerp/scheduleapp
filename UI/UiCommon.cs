using System.Drawing.Drawing2D;
using ScheduleApp.Native;

namespace ScheduleApp.UI;

/// <summary>Form cơ sở: scale theo DPI và dùng icon chung.</summary>
internal class BaseForm : Form
{
    public BaseForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = AppIcon.Get();
    }

    protected override void OnLoad(EventArgs e)
    {
        Theme.Apply(this);
        base.OnLoad(e);
    }

    /// <summary>Chủ đề hướng dẫn mở khi nhấn F1 ở cửa sổ này.</summary>
    protected virtual string HelpTopicId => HelpContent.Start;

    protected virtual void ShowHelp(string topic) => HelpWindow.ShowTopic(topic);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F1)
        {
            ShowHelp(HelpTopicId);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Liên kết "? Hướng dẫn (F1)" mở chủ đề của cửa sổ này.</summary>
    protected LinkLabel HelpLink(string text = "? Hướng dẫn (F1)")
    {
        var link = new LinkLabel { Text = text, AutoSize = true, LinkColor = Theme.Accent, Margin = new Padding(3, 8, 16, 3), UseMnemonic = false };
        link.LinkClicked += (_, _) => ShowHelp(HelpTopicId);
        return link;
    }
}

/// <summary>Icon đồng hồ vẽ bằng code (không cần file .ico).</summary>
internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon Get() => _icon ??= Create();

    private static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = new SolidBrush(Color.FromArgb(0, 120, 212));
            g.FillEllipse(ring, 1, 1, 30, 30);
            using var face = new SolidBrush(Color.White);
            g.FillEllipse(face, 5, 5, 22, 22);
            using var hand = new Pen(Color.FromArgb(0, 84, 153), 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(hand, 16, 16, 16, 9);
            g.DrawLine(hand, 16, 16, 21, 19);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

internal static class UiText
{
    public static readonly Color Muted = Color.FromArgb(96, 96, 96);
}

/// <summary>Dời tạm các cửa sổ của ứng dụng ra khỏi màn hình để không che vùng tự động thao tác.</summary>
internal static class ScreenHelper
{
    public static IDisposable MoveAppWindowsAway()
    {
        var offscreen = new Point(SystemInformation.VirtualScreen.Right + 200, SystemInformation.VirtualScreen.Top);
        var moved = new List<(Form Form, Point Location, FormWindowState State)>();

        foreach (var f in Application.OpenForms.Cast<Form>().ToList())
        {
            if (!f.Visible || f.WindowState == FormWindowState.Minimized || f is ReminderForm or CaptureOverlay or RunOverlay) continue;
            moved.Add((f, f.Location, f.WindowState));
            if (f.WindowState == FormWindowState.Maximized) f.WindowState = FormWindowState.Normal;
            f.Location = offscreen;
        }

        return new Restorer(() =>
        {
            foreach (var (f, loc, state) in moved)
            {
                if (f.IsDisposed) continue;
                f.Location = loc;
                if (state == FormWindowState.Maximized) f.WindowState = state;
            }
        });
    }

    /// <summary>
    /// Đếm ngược rồi trả về vị trí con trỏ chuột và cửa sổ nằm dưới con trỏ.
    /// Các cửa sổ ScheduleApp được dời đi trong lúc đếm để người dùng trỏ vào ứng dụng đích.
    /// </summary>
    public static async Task<(Point Point, IntPtr Window)> CaptureCursorAsync(int seconds, string message = "Di chuột tới vị trí cần click…")
    {
        using var away = MoveAppWindowsAway();
        using var overlay = new CaptureOverlay();
        overlay.Show();
        for (int tick = seconds * 10; tick > 0; tick--)
        {
            overlay.UpdateAt(Cursor.Position, $"{message} {Math.Ceiling(tick / 10.0)}");
            await Task.Delay(100);
        }
        var p = Cursor.Position;
        overlay.Hide();
        return (p, WindowHelper.RootWindowAt(p));
    }

    private sealed class Restorer(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose()
        {
            _restore?.Invoke();
            _restore = null;
        }
    }
}

/// <summary>Nhãn nhỏ đi theo con trỏ, click xuyên qua, không lấy focus.</summary>
internal sealed class CaptureOverlay : Form
{
    private readonly Label _label = new()
    {
        AutoSize = true,
        ForeColor = Color.White,
        Padding = new Padding(8, 5, 8, 5),
        Font = new Font("Segoe UI", 10F, FontStyle.Bold)
    };

    public CaptureOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0, 120, 212);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Controls.Add(_label);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x8 /*TOPMOST*/ | 0x80 /*TOOLWINDOW*/ | 0x20 /*TRANSPARENT*/ | 0x80000 /*LAYERED*/ | 0x08000000 /*NOACTIVATE*/;
            return cp;
        }
    }

    public void UpdateAt(Point cursor, string text)
    {
        _label.Text = text;
        Location = new Point(cursor.X + 18, cursor.Y + 22);
    }
}
