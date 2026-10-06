namespace ScheduleApp.UI;

/// <summary>Xem lớn ảnh cả cửa sổ lúc ghi một click, dấu đỏ tại chỗ đã click. Esc / bấm vào ảnh để đóng.</summary>
internal sealed class ContextShotViewer : BaseForm
{
    private readonly PictureBox _picture;
    private readonly Point _click;

    protected override SizeF ScreenShare => new(0.8f, 0.85f);

    public ContextShotViewer(Bitmap image, Point click, string caption)
    {
        _click = click;
        Text = "Ảnh lúc ghi — " + caption;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        KeyPreview = true;
        Size = new Size(1100, 760);
        BackColor = Color.FromArgb(32, 32, 36);
        _picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = image, Cursor = Cursors.Hand };
        _picture.Paint += (_, e) => DrawMarker(e.Graphics, _picture, _click, large: true);
        _picture.Click += (_, _) => Close();
        var hint = new Label
        {
            Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gainsboro,
            Text = $"Cửa sổ ứng dụng lúc ghi thao tác ({image.Width}×{image.Height}) — dấu đỏ là chỗ đã click. Esc hoặc bấm vào ảnh để đóng."
        };
        Controls.Add(_picture);
        Controls.Add(hint);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    /// <summary>Vị trí điểm <paramref name="click"/> (px của ảnh) trên PictureBox kiểu Zoom; null nếu chưa có ảnh.</summary>
    internal static PointF? ToView(PictureBox box, Point click)
    {
        if (box.Image is not { } img || img.Width == 0 || img.Height == 0) return null;
        var c = box.ClientSize;
        float k = Math.Min((float)c.Width / img.Width, (float)c.Height / img.Height);
        return new PointF((c.Width - img.Width * k) / 2 + click.X * k, (c.Height - img.Height * k) / 2 + click.Y * k);
    }

    /// <summary>Vòng tròn đỏ viền trắng quanh chỗ click, vạch ngắn ở ngoài vòng — giữa vòng để trống, vẫn đọc được chữ đã click.</summary>
    internal static void DrawMarker(Graphics g, PictureBox box, Point click, bool large = false)
    {
        if (ToView(box, click) is not { } p) return;
        float r = large ? 22 : 10, arm = large ? 12 : 6;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var halo = new Pen(Color.FromArgb(220, 255, 255, 255), large ? 6 : 4);
        using var pen = new Pen(Color.FromArgb(235, 220, 30, 30), large ? 3 : 2);
        foreach (var pn in new[] { halo, pen })
        {
            g.DrawEllipse(pn, p.X - r, p.Y - r, 2 * r, 2 * r);
            g.DrawLine(pn, p.X - r - arm, p.Y, p.X - r - 2, p.Y);
            g.DrawLine(pn, p.X + r + 2, p.Y, p.X + r + arm, p.Y);
            g.DrawLine(pn, p.X, p.Y - r - arm, p.X, p.Y - r - 2);
            g.DrawLine(pn, p.X, p.Y + r + 2, p.X, p.Y + r + arm);
        }
        using var dot = new SolidBrush(Color.FromArgb(235, 220, 30, 30));
        g.FillEllipse(dot, p.X - 1.5f, p.Y - 1.5f, 3, 3);   // tâm: điểm click chính xác
    }
}
