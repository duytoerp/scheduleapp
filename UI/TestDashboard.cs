using System.Diagnostics;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Testing;

namespace ScheduleApp.UI;

/// <summary>Màn hình chính cần cung cấp cho trang Kiểm thử.</summary>
internal interface ITestHost
{
    IReadOnlyList<Job> AllJobs { get; }

    /// <summary>Tạo kịch bản kiểm thử mới; <paramref name="recordD365"/> = mở trình soạn và bắt đầu ghi thao tác D365 ngay.</summary>
    void NewTestCase(bool recordD365);

    void EditJob(Job job);
    Task RunTestsAsync(IReadOnlyList<Job> jobs, string name);
}

/// <summary>Trang "Kiểm thử": các kịch bản kiểm thử với kết quả lần chạy gần nhất, chạy một / nhiều / tất cả, báo cáo gần đây.</summary>
internal sealed class TestDashboard : UserControl
{
    private readonly ITestHost _host;
    private readonly StatCard _cardTotal = new("Kịch bản kiểm thử");
    private readonly StatCard _cardPassed = new("Đạt (lần chạy cuối)");
    private readonly StatCard _cardFailed = new("Không đạt");
    private readonly StatCard _cardNever = new("Chưa chạy");
    private readonly ListView _cases = NewList(checkBoxes: true);
    private readonly ListView _reports = NewList(checkBoxes: false);
    private readonly Label _empty = new()
    {
        Text = "Chưa có kịch bản kiểm thử nào.\n\nBấm \"⏺ Ghi kịch bản D365\" rồi thao tác trên form Dynamics 365 — các bước được tạo tự động,\n" +
               "hoặc \"+ Kịch bản mới\" để tự dựng, hoặc thêm từ Mẫu có sẵn (nhóm \"Mẫu kiểm thử Dynamics 365\").",
        TextAlign = ContentAlignment.MiddleCenter,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Muted,
        BackColor = Theme.Surface,
        Visible = false
    };

    public TestDashboard(ITestHost host)
    {
        _host = host;
        BackColor = Theme.Background;
        Dock = DockStyle.Fill;

        var bar = Theme.CommandBar();
        bar.Items.Add(Theme.CommandButton("＋ Kịch bản mới", (_, _) => _host.NewTestCase(false), primary: true));
        bar.Items.Add(Theme.CommandButton("⏺ Ghi kịch bản D365", (_, _) => _host.NewTestCase(true),
            tip: "Tạo kịch bản mới và ghi thao tác của bạn trên form Dynamics 365 thành các bước"));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Theme.CommandButton("▶ Chạy đã chọn", async (_, _) => await RunCheckedAsync(), tip: "Chạy các kịch bản đã tick (hoặc đang chọn)"));
        bar.Items.Add(Theme.CommandButton("▶ Chạy tất cả", async (_, _) =>
        {
            var all = TestCases();
            if (all.Count > 0) await _host.RunTestsAsync(all, "Tất cả kịch bản kiểm thử");
        }));
        bar.Items.Add(Theme.CommandButton("✖ Chạy lại các kịch bản lỗi", async (_, _) =>
        {
            var failed = TestCases().Where(j => LastRun(j) is { Ok: false }).ToList();
            if (failed.Count > 0) await _host.RunTestsAsync(failed, "Chạy lại các kịch bản không đạt");
        }));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Theme.CommandButton("Sửa", (_, _) => { if (SelectedCase() is { } j) _host.EditJob(j); }));
        bar.Items.Add(Theme.CommandButton("📄 Báo cáo", (_, _) => OpenReport(SelectedCase() is { } j ? TestReport.LatestFor(j.Id) : _recent.FirstOrDefault()?.Path),
            tip: "Báo cáo gần nhất của kịch bản đang chọn"));
        bar.Items.Add(Theme.CommandButton("📁 Thư mục báo cáo", (_, _) =>
        {
            Directory.CreateDirectory(TestReport.RootDir);
            Process.Start(new ProcessStartInfo(TestReport.RootDir) { UseShellExecute = true });
        }));

        var cards = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(18, 4, 18, 10), BackColor = Theme.Background, WrapContents = false };
        cards.Controls.AddRange([_cardTotal, _cardPassed, _cardFailed, _cardNever]);

        _cases.Columns.Add("Kịch bản", 260);
        _cases.Columns.Add("Nhóm", 150);
        _cases.Columns.Add("Nội dung", 150);
        _cases.Columns.Add("Lần chạy cuối", 130);
        _cases.Columns.Add("Kết quả", 120);
        _cases.Columns.Add("Thời lượng", 90);
        _cases.Columns.Add("Chi tiết", 300);
        _cases.DoubleClick += (_, _) => { if (SelectedCase() is { } j) _host.EditJob(j); };
        _cases.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && SelectedCase() is { } j) _host.EditJob(j);
            if (e.KeyCode == Keys.F5) _ = RunCheckedAsync();
        };
        var caseMenu = new ContextMenuStrip();
        caseMenu.Items.Add("▶ Chạy kịch bản này", null, async (_, _) => { if (SelectedCase() is { } j) await _host.RunTestsAsync([j], j.Name); });
        caseMenu.Items.Add("Sửa…", null, (_, _) => { if (SelectedCase() is { } j) _host.EditJob(j); });
        caseMenu.Items.Add("📄 Mở báo cáo gần nhất", null, (_, _) => { if (SelectedCase() is { } j) OpenReport(TestReport.LatestFor(j.Id)); });
        _cases.ContextMenuStrip = caseMenu;

        _reports.Columns.Add("Thời gian", 150);
        _reports.Columns.Add("Bộ kiểm thử", 320);
        _reports.Columns.Add("Kết quả", 160);
        _reports.DoubleClick += (_, _) => OpenReport((_reports.SelectedItems.Count > 0 ? _reports.SelectedItems[0].Tag as TestReport.ReportInfo : null)?.Path);

        var casesPanel = Card("Kịch bản  ·  tick để chọn nhiều, nhấp đúp để sửa", _cases, _empty);
        var reportsPanel = Card("Báo cáo gần đây  ·  nhấp đúp để mở", _reports);
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Theme.Background,
            Padding = Padding.Empty,
            SplitterWidth = 10
        };
        split.Panel1.Controls.Add(casesPanel);
        split.Panel2.Controls.Add(reportsPanel);
        HandleCreated += (_, _) => BeginInvoke(new MethodInvoker(() =>
        {
            try { split.SplitterDistance = Math.Max(120, (int)(split.Height * 0.62)); } catch (InvalidOperationException) { }
        }));

        var splitHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 12), BackColor = Theme.Background };
        splitHost.Controls.Add(split);
        Controls.Add(splitHost);
        Controls.Add(cards);
        Controls.Add(bar);
        Controls.Add(Theme.PageHeader("Kiểm thử", "Kịch bản kiểm thử tự động (Dynamics 365 và các flow khác) — chạy, xem kết quả, mở báo cáo"));

        Resize += (_, _) => FitColumns();
        _cases.Resize += (_, _) => FitColumns();
        VisibleChanged += (_, _) => { if (Visible) BeginInvoke(new MethodInvoker(FitColumns)); };
    }

    private List<TestReport.ReportInfo> _recent = [];

    /// <summary>Số kịch bản không đạt ở lần chạy cuối (hiện trên thanh điều hướng).</summary>
    public int FailedCount { get; private set; }

    private static ListView NewList(bool checkBoxes)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            CheckBoxes = checkBoxes,
            BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            SmallImageList = new ImageList { ImageSize = new Size(1, 28) } // dòng cao hơn
        };
        return list;
    }

    /// <summary>Khung trắng bo viền có tiêu đề nhỏ.</summary>
    private static Panel Card(string title, params Control[] content)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(1) };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        var inner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(8, 4, 8, 6) };
        foreach (var c in content) inner.Controls.Add(c);
        var header = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Font = Theme.BoldFont,
            ForeColor = Theme.Text,
            BackColor = Theme.Surface
        };
        card.Controls.Add(inner);
        card.Controls.Add(header);
        return card;
    }

    private List<Job> TestCases() => _host.AllJobs.Where(j => j.IsTestCase)
        .OrderBy(j => j.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static RunRecord? LastRun(Job job) => RunHistory.All.LastOrDefault(r => r.JobId == job.Id);

    private Job? SelectedCase() => _cases.SelectedItems.Count > 0 ? _cases.SelectedItems[0].Tag as Job : null;

    private async Task RunCheckedAsync()
    {
        var jobs = _cases.CheckedItems.Cast<ListViewItem>().Select(i => (Job)i.Tag!).ToList();
        if (jobs.Count == 0 && SelectedCase() is { } j) jobs.Add(j);
        if (jobs.Count == 0)
        {
            MessageBox.Show(this, "Tick các kịch bản cần chạy (hoặc chọn một kịch bản).", "Kiểm thử", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await _host.RunTestsAsync(jobs, jobs.Count == 1 ? jobs[0].Name : $"{jobs.Count} kịch bản đã chọn");
    }

    private void OpenReport(string? path)
    {
        if (path == null || !File.Exists(path))
        {
            MessageBox.Show(this, "Chưa có báo cáo — hãy chạy kịch bản trước.", "Kiểm thử", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Nạp lại danh sách kịch bản, kết quả và báo cáo gần đây.</summary>
    public void RefreshData()
    {
        var cases = TestCases();
        var history = RunHistory.All;
        var checkedIds = _cases.CheckedItems.Cast<ListViewItem>().Select(i => ((Job)i.Tag!).Id).ToHashSet();
        var selectedId = SelectedCase()?.Id;
        int passed = 0, failed = 0, never = 0;

        _cases.BeginUpdate();
        _cases.Items.Clear();
        foreach (var job in cases)
        {
            var last = history.LastOrDefault(r => r.JobId == job.Id);
            int asserts = job.Steps.Count(s => s.Enabled && s.Type == StepType.Assert);
            var item = new ListViewItem(job.Name) { Tag = job, Checked = checkedIds.Contains(job.Id), UseItemStyleForSubItems = false };
            item.SubItems.Add(job.Group);
            item.SubItems.Add($"{job.Steps.Count(s => s.Enabled)} bước · {asserts} kiểm tra");
            item.SubItems.Add(last?.Start.ToString("HH:mm dd/MM/yyyy") ?? "—");
            var result = item.SubItems.Add(last == null ? "Chưa chạy" : last.Ok ? "✔ ĐẠT" : "✖ KHÔNG ĐẠT");
            result.ForeColor = last == null ? Theme.Muted : last.Ok ? Theme.Success : Theme.Danger;
            result.Font = Theme.BoldFont;
            item.SubItems.Add(last == null ? "" : TestReport.Duration(last.Duration.TotalSeconds));
            item.SubItems.Add(last == null || last.Ok ? "" : last.Message).ForeColor = Theme.Muted;
            foreach (ListViewItem.ListViewSubItem s in item.SubItems) if (s != result) s.Font = _cases.Font;
            if (!job.Enabled) item.ForeColor = Theme.Muted;
            _cases.Items.Add(item);
            if (job.Id == selectedId) item.Selected = true;
            if (last == null) never++;
            else if (last.Ok) passed++;
            else failed++;
        }
        _cases.EndUpdate();
        _empty.Visible = cases.Count == 0;
        _empty.BringToFront();

        _cardTotal.SetValue(cases.Count.ToString());
        _cardPassed.SetValue(passed.ToString(), passed > 0 ? Theme.Success : null);
        _cardFailed.SetValue(failed.ToString(), failed > 0 ? Theme.Danger : null);
        _cardNever.SetValue(never.ToString(), never > 0 ? Theme.Muted : null);
        FailedCount = failed;

        _recent = TestReport.Recent();
        _reports.BeginUpdate();
        _reports.Items.Clear();
        foreach (var r in _recent)
        {
            var item = new ListViewItem(r.Time.ToString("HH:mm:ss  dd/MM/yyyy")) { Tag = r, UseItemStyleForSubItems = false };
            item.SubItems.Add(r.Name);
            item.SubItems.Add(r.Failures == 0 ? $"✔ {r.Tests}/{r.Tests} đạt" : $"✖ {r.Tests - r.Failures}/{r.Tests} đạt")
                .ForeColor = r.Failures == 0 ? Theme.Success : Theme.Danger;
            _reports.Items.Add(item);
        }
        _reports.EndUpdate();
        FitColumns();
    }

    private void FitColumns()
    {
        if (_cases.Columns.Count < 7 || _cases.ClientSize.Width <= 0) return;
        float[] weights = [0.27f, 0.16f, 0.13f, 0.12f, 0.11f, 0.09f, 0.12f];
        int width = _cases.ClientSize.Width - 4;
        for (int i = 0; i < weights.Length; i++) _cases.Columns[i].Width = (int)(width * weights[i]);
        if (_reports.Columns.Count == 3 && _reports.ClientSize.Width > 0)
            _reports.Columns[1].Width = Math.Max(200, _reports.ClientSize.Width - _reports.Columns[0].Width - _reports.Columns[2].Width - 4);
    }
}
