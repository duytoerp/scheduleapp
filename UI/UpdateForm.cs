using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Thông báo có bản mới: xem thay đổi, cập nhật ngay (tải về, thay file, mở lại) hoặc bỏ qua bản này.</summary>
internal sealed class UpdateForm : BaseForm
{
    private readonly UpdateInfo _info;
    private readonly Label _hint;
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Button _btnUpdate = new() { Text = "Cập nhật ngay", AutoSize = true, MinimumSize = new Size(120, 0) };
    private readonly Button _btnSkip = new() { Text = "Bỏ qua bản này", AutoSize = true };
    private readonly Button _btnLater = new() { Text = "Để sau", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };

    /// <summary>Đã tải xong và chạy script cập nhật — nơi gọi phải thoát ứng dụng.</summary>
    public bool ExitRequested { get; private set; }

    public UpdateForm(UpdateInfo info)
    {
        _info = info;
        SuspendLayout();
        Text = "Có bản mới — ScheduleApp";
        Size = new Size(620, 460);
        MinimumSize = new Size(480, 340);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MinimizeBox = false;
        Padding = new Padding(12);

        var header = new Label
        {
            Text = $"ScheduleApp {info.Version} đã có (bạn đang dùng {UpdateService.Current}).",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold),
            Padding = new Padding(0, 0, 0, 8)
        };
        var notes = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Text = string.IsNullOrWhiteSpace(info.Notes) ? "(Không có ghi chú thay đổi.)" : info.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
        };
        bool canUpdate = UpdateService.CanSelfUpdate(out var reason);
        var hint = _hint = new Label
        {
            Text = canUpdate
                ? "Bấm \"Cập nhật ngay\": tải bản mới, kiểm tra mã SHA-256" + (info.HashSource.Length > 0 ? $" ({info.HashSource})" : "") +
                  " và chữ ký số, đóng ScheduleApp, thay file rồi tự mở lại (lịch và dữ liệu giữ nguyên). Bản mới không mở được thì tự quay về bản đang dùng."
                : "⚠ " + reason,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            ForeColor = canUpdate ? UiText.Muted : Color.FromArgb(180, 60, 0),
            Padding = new Padding(0, 6, 0, 6)
        };
        var progressPanel = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(22) };
        progressPanel.Controls.Add(_progress);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange([_btnLater, _btnUpdate, _btnSkip]);
        _btnUpdate.Enabled = canUpdate;
        CancelButton = _btnLater;

        Controls.Add(notes);
        Controls.Add(hint);
        Controls.Add(progressPanel);
        Controls.Add(buttons);
        Controls.Add(header);
        ResumeLayout(true);

        _btnUpdate.Click += async (_, _) => await UpdateAsync();
        _btnSkip.Click += (_, _) =>
        {
            SettingsStore.Current.Update.SkippedVersion = _info.Version.ToString();
            SettingsStore.Save();
            DialogResult = DialogResult.Cancel;
            Close();
        };
    }

    private async Task UpdateAsync()
    {
        _btnUpdate.Enabled = _btnSkip.Enabled = _btnLater.Enabled = false;
        _progress.Visible = true;
        try
        {
            var progress = new Progress<int>(p => _progress.Value = Math.Clamp(p, 0, 100));
            var download = await UpdateService.DownloadAsync(_info, progress, CancellationToken.None);
            // Cho người dùng thấy kết quả kiểm tra trước khi ScheduleApp đóng để thay file.
            _hint.ForeColor = Color.FromArgb(0, 110, 40);
            _hint.Text = $"✓ Mã SHA-256 khớp ({download.Sha256[..12]}…)" + Environment.NewLine +
                         $"✓ Đúng bản {download.Version}, mới hơn bản đang dùng" + Environment.NewLine +
                         (download.Signed ? "✓ Chữ ký số: " : "• Chữ ký số: ") + download.Signature + Environment.NewLine +
                         "Đang đóng ScheduleApp để thay file…";
            await Task.Delay(1500);
            UpdateService.ApplyAndRestart(download);
            ExitRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Cập nhật không thành công: " + UpdateService.Explain(ex));
            MessageBox.Show(this, "Cập nhật không thành công:\n" + UpdateService.Explain(ex), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            _btnUpdate.Enabled = _btnSkip.Enabled = _btnLater.Enabled = true;
            _progress.Visible = false;
        }
    }
}
