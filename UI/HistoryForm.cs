using System.Diagnostics;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Lịch sử chạy: lọc theo công việc / kết quả, xem ảnh chụp lỗi, mở nhật ký ngày tương ứng.</summary>
internal sealed class HistoryForm : BaseForm
{
    private readonly IReadOnlyList<Job> _jobs;
    private readonly ComboBox _cboJob = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _chkErrors = new() { Text = "Chỉ lần lỗi", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private readonly Label _lblStats = new() { AutoSize = true, ForeColor = UiText.Muted, Margin = new Padding(12, 6, 3, 3) };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false };
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(40, 40, 40) };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private List<Guid?> _jobFilter = [];

    public HistoryForm(IReadOnlyList<Job> jobs, Guid? jobId)
    {
        _jobs = jobs;
        SuspendLayout();
        Text = "Lịch sử chạy — ScheduleApp";
        Size = new Size(1100, 720);
        MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(8);

        _list.Columns.Add("Bắt đầu", 140);
        _list.Columns.Add("Công việc", 230);
        _list.Columns.Add("Kích hoạt", 130);
        _list.Columns.Add("Thời lượng", 80);
        _list.Columns.Add("Kết quả", 420);
        _list.Columns.Add("Ảnh", 50);

        var openShot = new Button { Text = "Mở ảnh", AutoSize = true };
        var openLog = new Button { Text = "Mở nhật ký ngày này", AutoSize = true };
        var clear = new Button { Text = "Xóa lịch sử", AutoSize = true };
        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        top.Controls.AddRange([new Label { Text = "Công việc:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, _cboJob, _chkErrors, openShot, openLog, clear, _lblStats]);

        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_picture);
        Controls.Add(_split);
        Controls.Add(top);
        ResumeLayout(true);

        _cboJob.Items.Add("(Tất cả)");
        _jobFilter.Add(null);
        foreach (var j in jobs.OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _cboJob.Items.Add(j.Name);
            _jobFilter.Add(j.Id);
        }
        _cboJob.SelectedIndex = jobId is Guid id ? Math.Max(0, _jobFilter.IndexOf(id)) : 0;

        _cboJob.SelectedIndexChanged += (_, _) => Reload();
        _chkErrors.CheckedChanged += (_, _) => Reload();
        _list.SelectedIndexChanged += (_, _) => ShowSelectedScreenshot();
        _list.DoubleClick += (_, _) => OpenScreenshot();
        openShot.Click += (_, _) => OpenScreenshot();
        openLog.Click += (_, _) =>
        {
            if (Selected() is not { } r) return;
            var path = Path.Combine(Log.LogDir, $"{r.Start:yyyy-MM-dd}.log");
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Xóa toàn bộ lịch sử chạy? (ảnh chụp lỗi vẫn giữ trong thư mục log)", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunHistory.Clear();
            Reload();
        };
        RunHistory.Added += OnAdded;
        Reload();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.SplitterDistance = (int)(_split.Height * 0.55);
    }

    private void OnAdded(RunRecord r)
    {
        if (IsDisposed) return;
        try { BeginInvoke(new MethodInvoker(Reload)); } catch (InvalidOperationException) { }
    }

    private void Reload()
    {
        var filter = _jobFilter[Math.Max(0, _cboJob.SelectedIndex)];
        var records = RunHistory.All
            .Where(r => filter == null || r.JobId == filter || (filter is Guid id && r.JobName == _jobs.FirstOrDefault(j => j.Id == id)?.Name + " (chạy thử)"))
            .ToList();
        int total = records.Count, ok = records.Count(r => r.Ok);
        if (_chkErrors.Checked) records = records.Where(r => !r.Ok).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in records.AsEnumerable().Reverse().Take(1000))
        {
            var item = new ListViewItem([
                r.Start.ToString("HH:mm:ss dd/MM/yyyy"), r.JobName, r.Trigger,
                r.Duration.TotalSeconds < 60 ? $"{r.Duration.TotalSeconds:0.#} giây" : $"{r.Duration.TotalMinutes:0.#} phút",
                (r.Ok ? "✔ " : "✖ ") + r.Message, r.Screenshot != null ? "📷" : ""
            ]) { Tag = r, ForeColor = r.Ok ? SystemColors.WindowText : Color.FromArgb(180, 30, 20) };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _lblStats.Text = total == 0 ? "Chưa có lần chạy nào." : $"{total} lần chạy · {ok} thành công ({ok * 100 / total}%) · {total - ok} lỗi";
        ShowSelectedScreenshot();
    }

    private RunRecord? Selected() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as RunRecord : null;

    private void ShowSelectedScreenshot()
    {
        var old = _picture.Image;
        _picture.Image = null;
        old?.Dispose();
        if (Selected()?.Screenshot is string path && File.Exists(path))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _picture.Image = Image.FromStream(fs);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException) { }
        }
    }

    private void OpenScreenshot()
    {
        if (Selected()?.Screenshot is string path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        RunHistory.Added -= OnAdded;
        _picture.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
