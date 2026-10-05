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

    protected override string HelpTopicId => "errors";

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
        var openReport = new Button { Text = "Mở báo cáo kiểm thử", AutoSize = true };
        var openLog = new Button { Text = "Mở nhật ký ngày này", AutoSize = true };
        var clear = new Button { Text = "Xóa lịch sử", AutoSize = true };
        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        top.Controls.AddRange([new Label { Text = "Công việc:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, _cboJob, _chkErrors, openShot, openReport, openLog, clear, _lblStats]);

        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_picture);
        var runsPage = new TabPage("Các lần chạy") { Padding = new Padding(4), UseVisualStyleBackColor = true };
        runsPage.Controls.Add(_split);
        runsPage.Controls.Add(top);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(runsPage);
        tabs.TabPages.Add(BuildStatsPage());
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedIndex == 1) ReloadStats(); };
        Controls.Add(tabs);
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
        openReport.Click += (_, _) =>
        {
            if (Selected()?.Report is string report && File.Exists(report)) Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
        };
        openLog.Click += (_, _) =>
        {
            if (Selected() is not { } r) return;
            if (Log.FileFor(Log.LogDir, r.Start) is string path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
                (r.Ok ? "✔ " : "✖ ") + r.Message, (r.Screenshot != null ? "📷" : "") + (r.Report != null ? "📄" : "")
            ]) { Tag = r, ForeColor = r.Ok ? SystemColors.WindowText : Color.FromArgb(180, 30, 20) };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _lblStats.Text = total == 0 ? "Chưa có lần chạy nào." : $"{total} lần chạy · {ok} thành công ({ok * 100 / total}%) · {total - ok} lỗi";
        ShowSelectedScreenshot();
    }

    // ───────────────────────────── Thống kê ─────────────────────────────

    private readonly ComboBox _cboPeriod = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly CheckBox _chkIncludeTests = new() { Text = "Tính cả lần chạy thử", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private readonly Label _lblSummary = new() { AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private readonly ListView _stats = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly DailyChart _chart = new() { Dock = DockStyle.Fill };
    private static readonly int[] PeriodDays = [7, 30, 90, 0];

    private TabPage BuildStatsPage()
    {
        var page = new TabPage("Thống kê") { Padding = new Padding(4), UseVisualStyleBackColor = true };
        _cboPeriod.Items.AddRange(["7 ngày qua", "30 ngày qua", "90 ngày qua", "Toàn bộ"]);
        _cboPeriod.SelectedIndex = 1;
        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        top.Controls.AddRange([new Label { Text = "Khoảng thời gian:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, _cboPeriod, _chkIncludeTests, _lblSummary]);

        _stats.Columns.Add("Công việc", 220);
        _stats.Columns.Add("Số lần", 60, HorizontalAlignment.Right);
        _stats.Columns.Add("Thành công", 80, HorizontalAlignment.Right);
        _stats.Columns.Add("TB thời gian", 90, HorizontalAlignment.Right);
        _stats.Columns.Add("Lâu nhất", 80, HorizontalAlignment.Right);
        _stats.Columns.Add("Lần cuối", 120);
        _stats.Columns.Add("Bước hay lỗi nhất", 120);
        _stats.Columns.Add("Lỗi gần nhất", 320);
        _stats.ColumnClick += (_, e) => SortStats(e.Column);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_stats);
        split.Panel2.Controls.Add(_chart);
        page.Controls.Add(split);
        page.Controls.Add(top);
        page.Layout += (_, _) => { if (split.Height > 200 && split.SplitterDistance == 50) split.SplitterDistance = (int)(split.Height * 0.6); };
        split.SplitterDistance = 50;

        _cboPeriod.SelectedIndexChanged += (_, _) => ReloadStats();
        _chkIncludeTests.CheckedChanged += (_, _) => ReloadStats();
        return page;
    }

    private void ReloadStats()
    {
        int days = PeriodDays[Math.Max(0, _cboPeriod.SelectedIndex)];
        var since = days > 0 ? DateTime.Today.AddDays(-days + 1) : DateTime.MinValue;
        var records = RunHistory.All
            .Where(r => r.Start >= since && (_chkIncludeTests.Checked || !r.Trigger.Equals("chạy thử", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _stats.BeginUpdate();
        _stats.Items.Clear();
        foreach (var g in records.GroupBy(r => r.JobName.Replace(" (chạy thử)", "")).OrderByDescending(g => g.Count()))
        {
            int total = g.Count(), ok = g.Count(r => r.Ok);
            var avg = TimeSpan.FromSeconds(g.Average(r => r.Duration.TotalSeconds));
            var max = g.Max(r => r.Duration);
            var last = g.MaxBy(r => r.Start)!;
            var worstStep = g.Where(r => !r.Ok && r.FailedStep > 0).GroupBy(r => r.FailedStep).OrderByDescending(x => x.Count()).FirstOrDefault();
            var lastError = g.Where(r => !r.Ok).MaxBy(r => r.Start);
            var item = new ListViewItem([
                g.Key, total.ToString(), $"{ok * 100.0 / total:0}%", Duration(avg), Duration(max),
                last.Start.ToString("HH:mm dd/MM/yyyy"),
                worstStep == null ? "" : $"Bước {worstStep.Key} ({worstStep.Count()} lần)",
                lastError == null ? "" : $"{lastError.Start:dd/MM HH:mm}: {lastError.Message}"
            ]) { Tag = ok * 1.0 / total };
            if (ok < total) item.ForeColor = ok * 2 < total ? Color.FromArgb(180, 30, 20) : Color.FromArgb(160, 90, 0);
            _stats.Items.Add(item);
        }
        _stats.EndUpdate();

        int all = records.Count, success = records.Count(r => r.Ok);
        var busy = TimeSpan.FromSeconds(records.Sum(r => r.Duration.TotalSeconds));
        _lblSummary.Text = all == 0 ? "Chưa có lần chạy nào trong khoảng này."
            : $"{all} lần chạy · {success * 100 / all}% thành công · tổng thời gian máy tự làm: {Duration(busy)}";

        int chartDays = days > 0 ? days : Math.Clamp((int)(DateTime.Today - (records.Count > 0 ? records.Min(r => r.Start).Date : DateTime.Today)).TotalDays + 1, 7, 120);
        _chart.SetData(Enumerable.Range(0, chartDays).Select(i => DateTime.Today.AddDays(-chartDays + 1 + i))
            .Select(d => (d, records.Count(r => r.Ok && r.Start.Date == d), records.Count(r => !r.Ok && r.Start.Date == d))).ToList());
    }

    private int _sortColumn = -1;
    private bool _sortDesc;

    private void SortStats(int column)
    {
        _sortDesc = column == _sortColumn ? !_sortDesc : column > 0;
        _sortColumn = column;
        var items = _stats.Items.Cast<ListViewItem>().ToList();
        Comparison<ListViewItem> cmp = column switch
        {
            1 => (a, b) => int.Parse(a.SubItems[1].Text).CompareTo(int.Parse(b.SubItems[1].Text)),
            2 => (a, b) => ((double)a.Tag!).CompareTo((double)b.Tag!),
            _ => (a, b) => string.Compare(a.SubItems[column].Text, b.SubItems[column].Text, StringComparison.CurrentCultureIgnoreCase)
        };
        items.Sort(_sortDesc ? (a, b) => cmp(b, a) : cmp);
        _stats.BeginUpdate();
        _stats.Items.Clear();
        _stats.Items.AddRange([.. items]);
        _stats.EndUpdate();
    }

    private static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.#} giây" : t.TotalHours < 1 ? $"{t.TotalMinutes:0.#} phút" : $"{t.TotalHours:0.#} giờ";

    /// <summary>Biểu đồ cột số lần chạy theo ngày (xanh = thành công, đỏ = lỗi).</summary>
    private sealed class DailyChart : Control
    {
        private List<(DateTime Day, int Ok, int Fail)> _data = [];

        public DailyChart()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public void SetData(List<(DateTime Day, int Ok, int Fail)> data)
        {
            _data = data;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            int left = LogicalToDeviceUnits(36), bottom = LogicalToDeviceUnits(22), top = LogicalToDeviceUnits(10), right = LogicalToDeviceUnits(10);
            var area = new Rectangle(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));
            using var axis = new Pen(Color.FromArgb(200, 200, 200));
            g.DrawLine(axis, area.Left, area.Bottom, area.Right, area.Bottom);
            if (_data.Count == 0) return;

            int max = Math.Max(1, _data.Max(d => d.Ok + d.Fail));
            TextRenderer.DrawText(g, max.ToString(), Font, new Rectangle(0, area.Top - 6, left - 4, 16), Color.Gray, TextFormatFlags.Right);
            TextRenderer.DrawText(g, "0", Font, new Rectangle(0, area.Bottom - 8, left - 4, 16), Color.Gray, TextFormatFlags.Right);

            float slot = area.Width / (float)_data.Count;
            float bar = Math.Max(2, slot * 0.7f);
            using var okBrush = new SolidBrush(Color.FromArgb(76, 160, 80));
            using var failBrush = new SolidBrush(Color.FromArgb(214, 72, 56));
            int labelEvery = Math.Max(1, (int)Math.Ceiling(LogicalToDeviceUnits(42) / slot));
            for (int i = 0; i < _data.Count; i++)
            {
                var (day, ok, fail) = _data[i];
                float x = area.Left + i * slot + (slot - bar) / 2;
                float hOk = area.Height * ok / (float)max, hFail = area.Height * fail / (float)max;
                if (ok > 0) g.FillRectangle(okBrush, x, area.Bottom - hOk, bar, hOk);
                if (fail > 0) g.FillRectangle(failBrush, x, area.Bottom - hOk - hFail, bar, hFail);
                if (i % labelEvery == 0 || i == _data.Count - 1)
                    TextRenderer.DrawText(g, day.ToString("dd/MM"), Font, new Point((int)(x - 6), area.Bottom + 3), Color.Gray);
            }
        }
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
