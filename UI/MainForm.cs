using System.Diagnostics;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using ScheduleApp.Services.Testing;

namespace ScheduleApp.UI;

internal sealed class MainForm : BaseForm, IUserNotifier, IHotkeyHost, IRemoteHost, ITestHost, IHelpHost
{
    private const int StopHotkeyId = 0x5AFE;
    private const int MaxLogChars = 200_000;

    private readonly List<Job> _jobs;
    private readonly FlowRunner _runner;
    private readonly Scheduler _scheduler;
    private readonly TriggerManager _triggers;
    private readonly UserInputGuard _guard = new();
    private readonly TelegramBot _bot;
    private readonly RunOverlay _overlay = new();
    private DebugToolbar? _debugBar;
    private UpdateInfo? _pendingUpdate;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        CheckBoxes = true,
        HideSelection = false,
        MultiSelect = false,
        ShowGroups = true,
        BorderStyle = BorderStyle.None,
        SmallImageList = new ImageList { ImageSize = new Size(1, 28) } // dòng cao, dễ bấm
    };

    private readonly Label _emptyJobs = new()
    {
        Text = "Chưa có công việc nào.\n\nBấm \"＋ Thêm công việc\" để tự dựng, hoặc \"Mẫu có sẵn…\" để bắt đầu từ ví dụ.\n\nLần đầu dùng? Mở \"Hướng dẫn\" ở thanh bên trái (hoặc nhấn F1).",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Theme.Muted,
        BackColor = Theme.Surface,
        Visible = false
    };

    private readonly Panel _jobsPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private TestDashboard _testsPage = null!;
    private HelpView? _helpPage;
    private Panel _content = null!;
    private readonly NavButton _navHelp = new("", "Hướng dẫn");
    private readonly NavButton _navJobs = new("", "Công việc");
    private readonly NavButton _navTests = new("", "Kiểm thử");

    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        BackColor = Color.FromArgb(250, 250, 250),
        Font = new Font("Consolas", 9.5F)
    };

    private readonly SplitContainer _split = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        Padding = new Padding(8, 4, 8, 4)
    };

    private readonly ToolStripTextBox _search = new() { AutoSize = false, Width = 180, ToolTipText = "Tìm theo tên / nhóm công việc" };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Sẵn sàng" };
    private readonly ToolStripButton _btnStop = new("■ Dừng") { Enabled = false, ToolTipText = "Dừng flow đang chạy (Ctrl+Shift+Q)" };
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private readonly string? _startupCommand;

    private bool _hideOnFirstShow;
    private bool _exiting;
    private bool _suppressCheck;
    private bool _trayTipShown;

    public MainForm(bool startHidden, string? startupCommand = null)
    {
        _hideOnFirstShow = startHidden;
        _startupCommand = startupCommand;
        _jobs = JobStore.Load();
        _runner = new FlowRunner(this, FindJob);
        _scheduler = new Scheduler(_jobs, _runner);

        SuspendLayout();
        Text = "ScheduleApp — Đặt lịch, nhắc nhở & tự động thao tác";
        Size = new Size(1240, 760);
        MinimumSize = new Size(860, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        ResumeLayout(true);

        _ = Handle; // tạo handle sớm để nhận BeginInvoke / hotkey kể cả khi khởi động ẩn
        _triggers = new TriggerManager(_jobs, _runner, this);
        _bot = new TelegramBot(this);
        WireEvents();

        _scheduler.Start();
        _triggers.Reload();
        _bot.Restart();
        RefreshList();
        RefreshTests();
        Log.Info($"ScheduleApp {UpdateService.Current} khởi động — {_jobs.Count} công việc. Dữ liệu: {JobStore.DataDir}");
        _ = Task.Run(ErrorScreenshots.Cleanup);
        _ = Task.Run(UpdateService.CleanupOldVersion);

        // Kiểm tra bản mới sau khi khởi động một lúc (không làm chậm lúc đăng nhập Windows).
        var updateTimer = new System.Windows.Forms.Timer { Interval = 45_000 };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Dispose();
            await CheckUpdateInBackgroundAsync();
        };
        updateTimer.Start();

        // Trình kích hoạt "khi khởi động" và lệnh dòng lệnh chạy sau khi giao diện đã sẵn sàng.
        var startup = new System.Windows.Forms.Timer { Interval = 4000 };
        startup.Tick += (_, _) =>
        {
            startup.Dispose();
            _triggers.OnAppStartup();
        };
        startup.Start();
        if (_startupCommand != null) BeginInvoke(new MethodInvoker(() => HandleCommand(_startupCommand)));
    }

    private Job? FindJob(Guid id)
    {
        // Gọi từ luồng nền khi chạy flow con — đọc danh sách là an toàn (chỉ thay phần tử trên luồng UI).
        foreach (var j in _jobs.ToArray())
            if (j.Id == id) return j;
        return null;
    }

    // ───────────────────────────── Giao diện ─────────────────────────────

    private void BuildUi()
    {
        BackColor = Theme.Background;

        // ── Trang "Công việc" ──
        var bar = Theme.CommandBar();
        bar.Items.Add(Theme.CommandButton("＋ Thêm công việc", (_, _) => AddJob(), primary: true));
        bar.Items.Add(Theme.CommandButton("✨ Tạo bằng AI…", (_, _) => AddJob(new Job(), isNew: true, startWithAi: true),
            tip: "Mô tả việc cần làm bằng lời — AI (Claude) dựng sẵn flow để xem trước rồi áp dụng"));
        bar.Items.Add(Theme.CommandButton("Mẫu có sẵn…", (_, _) => AddFromTemplate(), tip: "Thêm từ kho mẫu (Notepad, Excel, Dynamics 365, kiểm thử…)"));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Theme.CommandButton("▶ Chạy", (_, _) => RunSelected(), tip: "Chạy công việc đang chọn (F5)"));
        _btnStop.Click += (_, _) => _runner.StopAll();
        _btnStop.Padding = new Padding(8, 3, 8, 3);
        bar.Items.Add(_btnStop);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Theme.CommandButton("Sửa", (_, _) => EditSelected(), tip: "Sửa công việc đang chọn (Enter / nhấp đúp)"));
        bar.Items.Add(Theme.CommandButton("Nhân bản", (_, _) => DuplicateSelected()));
        bar.Items.Add(Theme.CommandButton("Xóa", (_, _) => DeleteSelected(), tip: "Xóa công việc đang chọn (Delete)"));
        var more = new ToolStripDropDownButton("⋯ Thêm") { DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(8, 3, 8, 3), ShowDropDownArrow = false };
        more.DropDownItems.Add("Nhập công việc…", null, (_, _) => ImportJobs());
        more.DropDownItems.Add("Xuất công việc…", null, (_, _) => ExportJobs());
        more.DropDownItems.Add("Thiết lập biến && bí mật (mọi công việc)…", null, (_, _) =>
        {
            if (ShowTemplateSetup([.. _jobs], fromTemplate: false)) JobsChanged();
        });
        more.DropDownItems.Add(new ToolStripSeparator());
        more.DropDownItems.Add("Mở thư mục log", null, (_, _) => OpenFolder(Log.LogDir));
        more.DropDownItems.Add("Mở thư mục ảnh lỗi", null, (_, _) => OpenFolder(ErrorScreenshots.Dir));
        more.DropDownItems.Add("Mở thư mục báo cáo kiểm thử", null, (_, _) => OpenFolder(TestReport.RootDir));
        more.DropDownItems.Add("Mở thư mục dữ liệu", null, (_, _) => OpenFolder(JobStore.DataDir));
        more.DropDownItems.Add(new ToolStripSeparator());
        more.DropDownItems.Add("Kiểm tra cập nhật…", null, async (_, _) => await CheckUpdateAsync());
        more.DropDownItems.Add($"Giới thiệu (phiên bản {UpdateService.Current})", null, (_, _) =>
            MessageBox.Show(this, $"ScheduleApp {UpdateService.Current}\nĐặt lịch, nhắc nhở & tự động thao tác trên Windows.\n\nDữ liệu: {JobStore.DataDir}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information));
        bar.Items.Add(more);
        _search.Alignment = ToolStripItemAlignment.Right;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.Margin = new Padding(0, 1, 0, 1);
        _search.TextChanged += (_, _) => RefreshList();
        bar.Items.Add(_search);
        bar.Items.Add(new ToolStripLabel("🔍 Tìm") { Alignment = ToolStripItemAlignment.Right, ForeColor = Theme.Muted });

        _list.Columns.Add("Công việc");
        _list.Columns.Add("Lịch / kích hoạt");
        _list.Columns.Add("Lần chạy tới");
        _list.Columns.Add("Còn lại");
        _list.Columns.Add("Lần chạy trước");
        _list.Columns.Add("Kết quả");
        _list.Resize += (_, _) => FitColumns();

        var listMenu = new ContextMenuStrip();
        listMenu.Items.Add("▶ Chạy ngay", null, (_, _) => RunSelected());
        listMenu.Items.Add("🧪 Chạy kiểm thử && xem báo cáo", null, async (_, _) =>
        {
            if (SelectedJob() is { } j) await RunTestsAsync([j], j.Name);
        });
        var runGroup = new ToolStripMenuItem("🧪 Chạy cả nhóm như bộ kiểm thử");
        runGroup.Click += async (_, _) =>
        {
            if (SelectedJob() is { Group.Length: > 0 } j) await RunTestsAsync(TestSuite.Select(_jobs, j.Group), j.Group);
        };
        listMenu.Items.Add(runGroup);
        listMenu.Opening += (_, _) =>
        {
            var g = SelectedJob()?.Group ?? "";
            runGroup.Text = g.Length > 0 ? $"🧪 Chạy nhóm \"{g}\" như bộ kiểm thử" : "🧪 Chạy cả nhóm như bộ kiểm thử";
            runGroup.Enabled = g.Length > 0;
        };
        listMenu.Items.Add(new ToolStripSeparator());
        listMenu.Items.Add("Sửa…", null, (_, _) => EditSelected());
        listMenu.Items.Add("⚙ Thiết lập biến && bí mật…", null, (_, _) => SetupSelected());
        listMenu.Items.Add("Nhân bản", null, (_, _) => DuplicateSelected());
        listMenu.Items.Add("Lịch sử chạy…", null, (_, _) => { if (SelectedJob() is { } j) ShowHistory(j.Id); });
        listMenu.Items.Add("Tạo shortcut trên Desktop (chạy công việc này)", null, (_, _) => CreateShortcut());
        listMenu.Items.Add(new ToolStripSeparator());
        listMenu.Items.Add("Xóa", null, (_, _) => DeleteSelected());
        _list.ContextMenuStrip = listMenu;

        var listCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(1) };
        listCard.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, listCard.Width - 1, listCard.Height - 1);
        };
        listCard.Controls.Add(_list);
        listCard.Controls.Add(_emptyJobs);
        _emptyJobs.BringToFront();

        var logHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Surface, Padding = new Padding(6, 4, 6, 2), WrapContents = false };
        logHeader.Controls.Add(new Label { Text = "Nhật ký hoạt động", AutoSize = true, Font = Theme.BoldFont, Margin = new Padding(3, 5, 12, 3) });
        var clearLog = new LinkLabel { Text = "Xóa", AutoSize = true, Margin = new Padding(3, 5, 8, 3), LinkColor = Theme.Accent };
        clearLog.LinkClicked += (_, _) => _log.Clear();
        var hideLog = new LinkLabel { Text = "Ẩn", AutoSize = true, Margin = new Padding(3, 5, 8, 3), LinkColor = Theme.Accent };
        hideLog.LinkClicked += (_, _) => ToggleLog(false);
        logHeader.Controls.AddRange([clearLog, hideLog]);
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Theme.Surface;
        var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(1) };
        logPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, logPanel.Width - 1, logPanel.Height - 1);
        };
        var logInner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 4, 4), BackColor = Theme.Surface };
        logInner.Controls.Add(_log);
        logPanel.Controls.Add(logInner);
        logPanel.Controls.Add(logHeader);

        _split.BackColor = Theme.Background;
        _split.Padding = Padding.Empty;
        _split.SplitterWidth = 10;
        _split.Panel1.Controls.Add(listCard);
        _split.Panel2.Controls.Add(logPanel);
        var splitHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 12), BackColor = Theme.Background };
        splitHost.Controls.Add(_split);

        _jobsPage.Controls.Add(splitHost);
        _jobsPage.Controls.Add(bar);
        _jobsPage.Controls.Add(Theme.PageHeader("Công việc", "Đặt lịch & tự động thao tác — tick để bật/tắt, nhấp đúp để sửa, chuột phải để xem thêm"));

        // ── Thanh điều hướng ──
        var nav = new Panel { Dock = DockStyle.Left, Width = LogicalToDeviceUnits(206), BackColor = Theme.NavBackground, Padding = new Padding(0, 6, 0, 8) };
        nav.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, nav.Width - 1, 0, nav.Width - 1, nav.Height);
        };
        var brand = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(50), BackColor = Theme.NavBackground };
        var brandFont = new Font("Segoe UI Semibold", 12.5F);
        var brandIcon = AppIcon.Get().ToBitmap();
        brand.Paint += (_, e) =>
        {
            int size = LogicalToDeviceUnits(24), x = LogicalToDeviceUnits(16);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(brandIcon, x, (brand.Height - size) / 2, size, size);
            TextRenderer.DrawText(e.Graphics, "ScheduleApp", brandFont, new Rectangle(x + size + LogicalToDeviceUnits(10), 0, brand.Width, brand.Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        };
        _navJobs.Click += (_, _) => ShowPage(_jobsPage);
        _navTests.Click += (_, _) => ShowPage(_testsPage);
        var navHistory = new NavButton("", "Lịch sử chạy");
        navHistory.Click += (_, _) => ShowHistory(null);
        var navSecrets = new NavButton("", "Bí mật");
        navSecrets.Click += (_, _) => { using var f = new SecretsForm(); f.ShowDialog(this); };
        var navSettings = new NavButton("", "Cài đặt");
        navSettings.Click += (_, _) => ShowSettings();
        var spacer = new Label { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(14) };
        var section = new Label { Text = "      CÔNG CỤ", Dock = DockStyle.Top, Height = LogicalToDeviceUnits(26), ForeColor = Theme.Muted, Font = new Font(Font.FontFamily, 7.5F, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft };

        var startup = new CheckBox
        {
            Text = "Khởi động cùng Windows",
            Checked = StartupManager.IsEnabled,
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(30),
            Padding = new Padding(16, 0, 0, 0),
            ForeColor = Theme.Text
        };
        new ToolTip().SetToolTip(startup, "Tự chạy ScheduleApp (thu nhỏ ở khay hệ thống) khi đăng nhập Windows");
        startup.CheckedChanged += (_, _) =>
        {
            try
            {
                StartupManager.Set(startup.Checked);
                Log.Info(startup.Checked ? "Đã bật khởi động cùng Windows." : "Đã tắt khởi động cùng Windows.");
            }
            catch (Exception ex) { ShowError("Không thay đổi được cài đặt khởi động: " + ex.Message); }
        };
        var version = new Label
        {
            Text = $"Phiên bản {UpdateService.Current}",
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(22),
            ForeColor = Theme.Muted,
            Padding = new Padding(18, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };
        // Dock = Top xếp theo thứ tự ngược (control thêm sau nằm trên).
        _navHelp.Click += (_, _) => ShowHelp(HelpContent.Start);
        nav.Controls.AddRange([_navHelp, navSettings, navSecrets, navHistory, section, spacer, _navTests, _navJobs, brand, startup, version]);

        _testsPage = new TestDashboard(this) { Visible = false };
        var content = _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        content.Controls.Add(_jobsPage);
        content.Controls.Add(_testsPage);

        var statusBar = new StatusStrip { BackColor = Theme.Surface, SizingGrip = false };
        statusBar.Items.Add(_status);
        var logToggle = new ToolStripStatusLabel("📜 Nhật ký") { IsLink = true, LinkColor = Theme.Accent, ToolTipText = "Hiện / ẩn nhật ký hoạt động" };
        logToggle.Click += (_, _) => ToggleLog(_split.Panel2Collapsed);
        statusBar.Items.Add(logToggle);
        statusBar.Items.Add(new ToolStripStatusLabel("Dừng khẩn cấp: Ctrl+Shift+Q") { ForeColor = Theme.Muted });

        Controls.Add(content);
        Controls.Add(nav);
        Controls.Add(statusBar);
        ShowPage(_jobsPage);
        Shown += (_, _) =>
        {
            try { _split.SplitterDistance = Math.Max(150, _split.Height - LogicalToDeviceUnits(190)); } catch (InvalidOperationException) { }
        };

        // Khay hệ thống
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Mở ScheduleApp", null, (_, _) => ShowMain());
        var trayRun = new ToolStripMenuItem("Chạy công việc");
        trayRun.DropDownOpening += (_, _) =>
        {
            trayRun.DropDownItems.Clear();
            foreach (var j in _jobs.OrderBy(j => j.Group).ThenBy(j => j.Name))
            {
                var job = j;
                trayRun.DropDownItems.Add((job.Group.Length > 0 ? job.Group + " › " : "") + job.Name, null, (_, _) => RunJob(job, "chạy từ khay"));
            }
        };
        trayRun.DropDownItems.Add("(trống)");
        trayMenu.Items.Add(trayRun);
        trayMenu.Items.Add("Dừng flow đang chạy", null, (_, _) => _runner.StopAll());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Thoát", null, (_, _) => ExitApp());
        _tray.Icon = AppIcon.Get();
        _tray.Text = "ScheduleApp";
        _tray.ContextMenuStrip = trayMenu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.BalloonTipClicked += async (_, _) =>
        {
            if (_pendingUpdate == null) return;
            ShowMain();
            await CheckUpdateAsync();
        };
    }

    private void ShowPage(Control page)
    {
        SuspendLayout();
        _jobsPage.Visible = page == _jobsPage;
        _testsPage.Visible = page == _testsPage;
        if (_helpPage != null) _helpPage.Visible = page == _helpPage;
        _navJobs.Selected = page == _jobsPage;
        _navTests.Selected = page == _testsPage;
        _navHelp.Selected = page == _helpPage;
        if (page == _testsPage) _testsPage.RefreshData();
        ResumeLayout(true);
    }

    // ───────────────────────────── Hướng dẫn (IHelpHost) ─────────────────────────────

    protected override string HelpTopicId => _testsPage.Visible ? "d365-overview" : _helpPage?.Visible == true ? _helpPage.CurrentTopicId ?? HelpContent.Start : HelpContent.Start;

    /// <summary>Mở trang Hướng dẫn ngay trong cửa sổ chính (tạo khi cần lần đầu).</summary>
    protected override void ShowHelp(string topic)
    {
        if (_helpPage == null)
        {
            _helpPage = new HelpView(this, showHeader: true) { Visible = false };
            _content.Controls.Add(_helpPage);
        }
        ShowMain();
        ShowPage(_helpPage);
        _helpPage.ShowTopic(topic);
    }

    public void OpenHelp(string topic) => ShowHelp(topic);

    public void RunHelpCommand(string command)
    {
        switch (command)
        {
            case HelpContent.CmdNewJob: ShowPage(_jobsPage); AddJob(); break;
            case HelpContent.CmdTemplates: ShowPage(_jobsPage); AddFromTemplate(); break;
            case HelpContent.CmdOpenTests: ShowPage(_testsPage); break;
            case HelpContent.CmdNewTest: NewTestCase(false); break;
            case HelpContent.CmdRecordD365: NewTestCase(true); break;
            case HelpContent.CmdReports:
                Directory.CreateDirectory(TestReport.RootDir);
                Process.Start(new ProcessStartInfo(TestReport.RootDir) { UseShellExecute = true });
                break;
            case HelpContent.CmdHistory: ShowHistory(null); break;
            case HelpContent.CmdSettings: ShowSettings(); break;
            case HelpContent.CmdSecrets: { using var f = new SecretsForm(); f.ShowDialog(this); } break;
        }
    }

    private void ToggleLog(bool show)
    {
        _split.Panel2Collapsed = !show;
        if (show)
        {
            try { _split.SplitterDistance = Math.Max(150, _split.Height - LogicalToDeviceUnits(190)); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Làm mới trang Kiểm thử (nếu đang mở) và số kịch bản không đạt trên thanh điều hướng.</summary>
    private void RefreshTests()
    {
        if (_testsPage.Visible || _navTests.Badge.Length > 0 || _jobs.Any(j => j.IsTestCase)) _testsPage.RefreshData();
        _navTests.Badge = _testsPage.FailedCount > 0 ? _testsPage.FailedCount.ToString() : "";
        _navTests.Invalidate();
    }

    private void FitColumns()
    {
        float[] weights = [0.22f, 0.22f, 0.13f, 0.09f, 0.13f, 0.21f];
        int width = _list.ClientSize.Width;
        if (width <= 0) return;
        for (int i = 0; i < weights.Length && i < _list.Columns.Count; i++)
            _list.Columns[i].Width = (int)(width * weights[i]);
    }

    private void WireEvents()
    {
        Log.Written += line =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(() =>
            {
                AppendLog(line);
                if (_overlay.Visible) _overlay.AddLog(line);
            }));
        };

        // Khung trạng thái ở góc phải dưới màn hình: bước đang chạy, Tạm dừng / Bước tiếp / Chạy tiếp / Dừng.
        _runner.Progress += p =>
        {
            if (IsDisposed) return;
            BeginInvoke(new MethodInvoker(() =>
            {
                if (_overlay.IsDisposed) return;
                if (SettingsStore.Current.ShowRunOverlay) _overlay.ShowProgress(p);
                else _overlay.Hide();
            }));
        };
        _overlay.PauseClicked += () =>
        {
            if (_runner.RequestPause()) _overlay.MarkPauseRequested();
        };
        _overlay.StepClicked += () => _debugBar?.Finish(DebugCommand.Step);
        _overlay.ContinueClicked += () => _debugBar?.Finish(DebugCommand.Continue);
        _overlay.StopClicked += _runner.StopAll;
        InputSimulator.BeforePointer = _overlay.Avoid;

        _runner.StatusChanged += text =>
        {
            if (IsDisposed) return;
            BeginInvoke(new MethodInvoker(() =>
            {
                _status.Text = text;
                _btnStop.Enabled = _runner.IsBusy;
                _tray.Text = text.Length > 63 ? text[..60] + "…" : text;
            }));
        };

        _runner.JobFinished += (id, started, ok, message) =>
        {
            if (IsDisposed) return;
            BeginInvoke(new MethodInvoker(() =>
            {
                var job = _jobs.Find(j => j.Id == id);
                if (job == null) return;
                job.LastRun = started;
                job.LastResult = (ok ? "✔ " : "✖ ") + message;
                SaveJobs();
                RefreshList();
                RefreshTests();
            }));
        };

        // Chế độ an toàn: theo dõi chuột/phím thật trong lúc flow chạy.
        _runner.RunningChanged += running =>
        {
            if (IsDisposed) return;
            BeginInvoke(new MethodInvoker(() =>
            {
                if (running && SettingsStore.Current.SafeMode) _guard.Start();
                else _guard.Stop();
            }));
        };
        _runner.BeforeStep = async ctx =>
        {
            if (!_guard.Triggered) return;
            _guard.Reset();
            Log.Warn("   ⏸ Phát hiện người dùng dùng chuột/bàn phím — tạm dừng flow (chế độ an toàn).");
            bool go = await AskContinueAsync("Chế độ an toàn",
                $"Bạn vừa dùng chuột hoặc bàn phím trong lúc \"{ctx.RootJob.Name}\" đang chạy.\n\n" +
                "Chạy tiếp flow? (Hãy để yên chuột/bàn phím sau khi bấm Chạy tiếp.)", ctx.Ct);
            _guard.Reset();
            if (!go) throw new OperationCanceledException("Người dùng dừng flow (chế độ an toàn).");
        };

        _scheduler.Changed += RefreshList;
        _scheduler.ReminderDue += job =>
        {
            var msg = $"\"{job.Name}\" sẽ tự động chạy lúc {job.NextRun:HH:mm} ({job.Schedule.Describe()}).";
            Log.Info($"🔔 Nhắc trước: {msg}");
            _ = ShowReminderAsync($"Sắp chạy: {job.Name}", msg, false, CancellationToken.None);
        };
        _scheduler.MissedRunAsk += async (job, missedAt) =>
        {
            bool run = await AskContinueAsync("Lỡ lịch chạy",
                $"Công việc \"{job.Name}\" lẽ ra chạy lúc {missedAt:HH:mm dd/MM/yyyy} nhưng máy tắt / ngủ / ScheduleApp không chạy.\n\nChạy bù ngay bây giờ?",
                CancellationToken.None, "Chạy bù", "Bỏ qua");
            if (run) RunJob(job, $"chạy bù lịch {missedAt:HH:mm dd/MM}");
        };

        _list.ItemChecked += (_, e) =>
        {
            // ListView tự phát ItemChecked (bỏ chọn rồi chọn lại) khi tạo handle — chỉ xử lý khi người dùng
            // đang thao tác trên danh sách (click/Space làm danh sách có focus) và trạng thái thực sự đổi.
            if (_suppressCheck || !_list.Focused || e.Item.Tag is not Job job || job.Enabled == e.Item.Checked) return;
            job.Enabled = e.Item.Checked;
            _scheduler.Recalculate(job);
            _triggers.Reload();
            Log.Info($"{(job.Enabled ? "Bật" : "Tắt")} công việc \"{job.Name}\".");
            SaveJobs();
            UpdateItem(e.Item);
        };
        _list.DoubleClick += (_, _) => EditSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.Enter) EditSelected();
            else if (e.KeyCode == Keys.F5) RunSelected();
        };

        _uiTimer.Tick += (_, _) =>
        {
            if (!Visible) return;
            foreach (ListViewItem item in _list.Items)
                if (item.Tag is Job job) item.SubItems[3].Text = Countdown(job);
        };
        _uiTimer.Start();
    }

    private void AppendLog(string line)
    {
        if (_log.TextLength > MaxLogChars) _log.Text = _log.Text[(MaxLogChars / 2)..];
        _log.AppendText(line + Environment.NewLine);
    }

    // ───────────────────────────── Danh sách ─────────────────────────────

    private void RefreshList()
    {
        var selectedId = SelectedJob()?.Id;
        var filter = _search.Text.Trim();
        _suppressCheck = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var job in _jobs)
            {
                if (filter.Length > 0 &&
                    !job.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) &&
                    !job.Group.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                    continue;

                var groupName = string.IsNullOrWhiteSpace(job.Group) ? "(Chưa phân nhóm)" : job.Group.Trim();
                if (!groups.TryGetValue(groupName, out var group))
                {
                    group = new ListViewGroup(groupName, groupName);
                    groups[groupName] = group;
                }

                var item = new ListViewItem(job.Name) { Tag = job, Checked = job.Enabled, Group = group };
                for (int i = 1; i < _list.Columns.Count; i++) item.SubItems.Add("");
                UpdateItem(item);
                _list.Items.Add(item);
                if (job.Id == selectedId) item.Selected = true;
            }
            // Nhóm sắp theo tên, "(Chưa phân nhóm)" ở cuối.
            foreach (var g in groups.Values.OrderBy(g => g.Header.StartsWith('(')).ThenBy(g => g.Header, StringComparer.CurrentCultureIgnoreCase))
                _list.Groups.Add(g);
            _list.ShowGroups = groups.Count > 1;
            _emptyJobs.Text = _jobs.Count == 0
                ? "Chưa có công việc nào.\n\nBấm \"＋ Thêm công việc\" để tự dựng, hoặc \"Mẫu có sẵn…\" để bắt đầu từ ví dụ.\n\nLần đầu dùng? Mở \"Hướng dẫn\" ở thanh bên trái (hoặc nhấn F1)."
                : $"Không có công việc nào khớp \"{filter}\".";
            _emptyJobs.Visible = _list.Items.Count == 0;
        }
        finally
        {
            _list.EndUpdate();
            _suppressCheck = false;
        }
        FitColumns();
    }

    private static void UpdateItem(ListViewItem item)
    {
        var job = (Job)item.Tag!;
        int triggers = job.Triggers.Count(t => t.Enabled);
        item.Text = job.Name;
        item.SubItems[1].Text = job.Schedule.Describe() + (triggers > 0 ? $"  ⚡{string.Join(", ", job.Triggers.Where(t => t.Enabled).Select(t => t.Describe()))}" : "");
        item.SubItems[2].Text = job.NextRun?.ToString("HH:mm:ss  dd/MM/yyyy") ?? "—";
        item.SubItems[3].Text = Countdown(job);
        item.SubItems[4].Text = job.LastRun?.ToString("HH:mm:ss  dd/MM/yyyy") ?? "—";
        item.SubItems[5].Text = job.LastResult ?? "";
        item.UseItemStyleForSubItems = false;
        var color = job.Enabled ? Theme.Text : Theme.Muted;
        for (int i = 0; i < item.SubItems.Count; i++) item.SubItems[i].ForeColor = color;
        if (job.Enabled && job.LastResult is { Length: > 0 } r) item.SubItems[5].ForeColor = r.StartsWith('✔') ? Theme.Success : Theme.Danger;
        if (job.IsTestCase) item.SubItems[1].Text = "Kịch bản kiểm thử · " + item.SubItems[1].Text;
    }

    private static string Countdown(Job job)
    {
        if (job.NextRun is not DateTime next) return "";
        var left = next - DateTime.Now;
        if (left < TimeSpan.Zero) return "đang chạy…";
        return left.TotalDays >= 1 ? $"{(int)left.TotalDays} ngày {left.Hours} giờ"
             : left.TotalHours >= 1 ? $"{(int)left.TotalHours} giờ {left.Minutes} phút"
             : $"{left.Minutes:00}:{left.Seconds:00}";
    }

    private Job? SelectedJob() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Job : null;

    private void SaveJobs()
    {
        try { JobStore.Save(_jobs); }
        catch (Exception ex) { Log.Error("Không lưu được danh sách công việc: " + ex.Message); }
    }

    /// <summary>Lưu, tính lại lịch, đăng ký lại trình kích hoạt và vẽ lại danh sách sau khi công việc thay đổi.</summary>
    private void JobsChanged(Job? recalc = null)
    {
        if (recalc != null) _scheduler.Recalculate(recalc);
        _triggers.Reload();
        SaveJobs();
        RefreshList();
        RefreshTests();
    }

    // ───────────────────────────── Thao tác ─────────────────────────────

    private void AddJob() => AddJob(new Job(), isNew: true);

    private void AddJob(Job job, bool isNew, bool recordD365 = false, bool startWithAi = false)
    {
        using var editor = new JobEditorForm(job, _runner, _jobs, this, isNew) { StartD365RecordingOnShow = recordD365, StartWithAi = startWithAi };
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        var added = editor.Job;
        _jobs.Add(added);
        JobsChanged(added);
        Log.Info($"Đã thêm công việc \"{added.Name}\".");
    }

    private void AddFromTemplate()
    {
        using var picker = new TemplatePickerForm();
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;
        var newDeps = picker.Dependencies.Where(d => _jobs.All(j => j.Id != d.Id)).ToList();
        // Điền tenant / URL / tài khoản / bí mật / đường dẫn của bạn một lần cho mẫu và các mẫu đi kèm.
        ShowTemplateSetup([picker.Selected, .. newDeps], fromTemplate: true);
        // Mẫu gọi tới mẫu khác (vd "Nhập liệu" gọi "Đăng nhập") → thêm luôn các mẫu đó (Id đã được gán mới khi nạp).
        foreach (var dep in newDeps)
        {
            _jobs.Add(dep);
            _scheduler.Recalculate(dep);
            Log.Info($"Đã thêm công việc phụ thuộc \"{dep.Name}\" từ mẫu.");
        }
        if (picker.Dependencies.Count > 0) JobsChanged();
        AddJob(picker.Selected, isNew: true);
    }

    /// <summary>Màn hình "Thiết lập mẫu" cho nhóm công việc; true nếu có giá trị được thay đổi.</summary>
    private bool ShowTemplateSetup(IReadOnlyCollection<Job> jobs, bool fromTemplate)
    {
        var items = TemplateSetup.Collect(jobs, remember: fromTemplate);
        if (items.Count == 0)
        {
            if (!fromTemplate)
                MessageBox.Show(this, "Không có biến, bí mật, kết nối API hay đường dẫn nào cần thiết lập.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        var first = jobs.First();
        var intro = fromTemplate
            ? $"Điền thông tin của bạn cho mẫu \"{first.Name}\"" + (jobs.Count > 1 ? $" và {jobs.Count - 1} công việc dùng chung đi kèm" : "") +
              ". Mỗi giá trị được áp cho mọi công việc dùng nó. Bấm \"Để sau\" để giữ giá trị mẫu — mở lại bất cứ lúc nào: chuột phải công việc → Thiết lập biến & bí mật."
            : jobs.Count == 1
                ? $"Thiết lập cho \"{first.Name}\"."
                : $"Thiết lập cho {jobs.Count} công việc — mỗi giá trị được áp cho mọi công việc đang dùng nó (vd đổi tenant một lần cho tất cả).";
        using var form = new TemplateSetupForm(items, intro, ShowSettings);
        if (form.ShowDialog(this) != DialogResult.OK || form.Changes == 0) return false;
        Log.Info($"Thiết lập mẫu: cập nhật {form.Changes} giá trị cho {jobs.Count} công việc.");
        return true;
    }

    private void SetupSelected()
    {
        if (SelectedJob() is not { } job) return;
        // Kèm các công việc được gọi tới (vd kịch bản C2 gọi C1 "Mở Dynamics 365" chứa d365Url).
        if (ShowTemplateSetup(TemplateSetup.WithDependencies(job, _jobs), fromTemplate: false)) JobsChanged(job);
    }

    private void EditSelected()
    {
        if (SelectedJob() is { } job) EditJob(job);
    }

    // ───────────────────────────── Trang Kiểm thử (ITestHost) ─────────────────────────────

    IReadOnlyList<Job> ITestHost.AllJobs => _jobs;

    Task ITestHost.RunTestsAsync(IReadOnlyList<Job> jobs, string name) => RunTestsAsync(jobs, name);

    /// <summary>
    /// Kịch bản kiểm thử mới: bật sẵn "kịch bản kiểm thử" + "tự xóa dữ liệu test"; nếu đã có công việc mở Dynamics 365 dùng chung
    /// (có bước mở trình duyệt, không phải kịch bản) thì bước đầu gọi công việc đó.
    /// </summary>
    public void NewTestCase(bool recordD365)
    {
        var job = new Job
        {
            Name = recordD365 ? $"Kịch bản ghi {DateTime.Now:dd/MM HH:mm}" : "Kịch bản kiểm thử mới",
            Group = _jobs.Where(j => j.IsTestCase && j.Group.Length > 0).GroupBy(j => j.Group).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "Kiểm thử",
            IsTestCase = true,
            CleanupTestData = true
        };
        var opener = _jobs.FirstOrDefault(j => !j.IsTestCase && j.Steps.Any(s => s.Type == StepType.Browser && s.BrowserAction == BrowserAction.Launch)
                                               && j.Steps.Any(s => s.Type == StepType.Dynamics));
        if (opener != null)
        {
            var call = ActionStep.CreateDefault(StepType.CallJob);
            call.JobRef = opener.Id;
            call.Target = opener.Name;
            call.DelayAfterMs = 0;
            job.Steps.Add(call);
        }
        AddJob(job, isNew: true, recordD365);
    }

    public void EditJob(Job job)
    {
        using var editor = new JobEditorForm(job.Clone(), _runner, _jobs, this, isNew: false);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        int index = _jobs.FindIndex(j => j.Id == job.Id);
        if (index < 0) return;
        var updated = editor.Job;
        _jobs[index] = updated;
        JobsChanged(updated);
        Log.Info($"Đã cập nhật công việc \"{updated.Name}\".");
    }

    private void DuplicateSelected()
    {
        var job = SelectedJob();
        if (job == null) return;
        var copy = job.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name += " (bản sao)";
        copy.LastRun = null;
        copy.LastResult = null;
        _jobs.Insert(_jobs.IndexOf(job) + 1, copy);
        JobsChanged(copy);
    }

    private void DeleteSelected()
    {
        var job = SelectedJob();
        if (job == null) return;
        var callers = _jobs.Where(j => j.Id != job.Id && (j.OnFailureJobId == job.Id || j.Steps.Any(s => s.JobRef == job.Id))).Select(j => j.Name).ToList();
        var warning = callers.Count > 0 ? $"\n\n⚠ Công việc này đang được gọi bởi: {string.Join(", ", callers)}." : "";
        if (MessageBox.Show(this, $"Xóa công việc \"{job.Name}\"?{warning}", "Xác nhận xóa",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _jobs.Remove(job);
        JobsChanged();
        Log.Info($"Đã xóa công việc \"{job.Name}\".");
    }

    private void RunSelected()
    {
        var job = SelectedJob();
        if (job == null)
        {
            MessageBox.Show(this, "Hãy chọn một công việc để chạy.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        RunJob(job, "chạy thủ công");
    }

    private void RunJob(Job job, string trigger)
    {
        if (job.Steps.Count(s => s.Enabled) == 0)
        {
            Notify("Không chạy được", $"\"{job.Name}\" chưa có bước nào được bật.", true);
            return;
        }
        _btnStop.Enabled = true;
        _ = _runner.EnqueueAsync(job, trigger);
    }

    /// <summary>Lệnh từ dòng lệnh / phiên bản khác: "show", "run &lt;tên&gt;", "stop".</summary>
    public void HandleCommand(string command)
    {
        var cmd = command.Trim();
        if (cmd.Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            ShowMain();
        }
        else if (cmd.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            _runner.StopAll();
        }
        else if (cmd.StartsWith("run ", StringComparison.OrdinalIgnoreCase))
        {
            var name = cmd[4..].Trim().Trim('"');
            var job = _jobs.FirstOrDefault(j => j.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))
                      ?? (_jobs.Where(j => j.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null);
            if (job == null)
            {
                Log.Warn($"Dòng lệnh: không tìm thấy công việc \"{name}\".");
                Notify("Không tìm thấy công việc", name, true);
                return;
            }
            RunJob(job, "dòng lệnh");
        }
    }

    private void ShowHistory(Guid? jobId)
    {
        var form = new HistoryForm(_jobs, jobId);
        form.Show(this);
    }

    private void ShowSettings()
    {
        using var f = new SettingsForm();
        var result = f.ShowDialog(this);
        if (f.ExitForUpdate)
        {
            ExitForUpdate();
            return;
        }
        if (result != DialogResult.OK) return;
        _scheduler.RecalculateAll(); // ngày nghỉ có thể đã đổi
        _triggers.Reload();          // hộp thư có thể đã đổi
        _bot.Restart();              // bật/tắt nhận lệnh Telegram
        RefreshList();
    }

    // ───────────────────────────── Cập nhật ─────────────────────────────

    private async Task CheckUpdateInBackgroundAsync()
    {
        var s = SettingsStore.Current.Update;
        if (!s.CheckOnStartup || string.IsNullOrWhiteSpace(s.Source)) return;
        try
        {
            var info = await UpdateService.CheckAsync(CancellationToken.None);
            if (info == null || info.Version.ToString() == s.SkippedVersion) return;
            _pendingUpdate = info;
            Log.Info($"Có bản mới ScheduleApp {info.Version} — Thêm → Kiểm tra cập nhật để cài.");
            _tray.ShowBalloonTip(8000, $"ScheduleApp {info.Version} đã có", "Nhấp vào đây để xem thay đổi và cập nhật.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Warn("Không kiểm tra được bản mới: " + ex.Message);
        }
    }

    private async Task CheckUpdateAsync()
    {
        try
        {
            var info = _pendingUpdate ?? await UpdateService.CheckAsync(CancellationToken.None);
            if (info == null)
            {
                MessageBox.Show(this, $"Bạn đang dùng bản mới nhất ({UpdateService.Current}).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dlg = new UpdateForm(info);
            dlg.ShowDialog(this);
            if (dlg.ExitRequested) ExitForUpdate();
        }
        catch (Exception ex)
        {
            ShowError("Không kiểm tra được bản mới:\n" + ex.Message + "\n\nNhập nguồn cập nhật trong ⚙ Cài đặt → Chung.");
        }
    }

    /// <summary>Thoát để script cập nhật thay file exe (dừng flow đang chạy).</summary>
    private void ExitForUpdate()
    {
        _runner.StopAll();
        _exiting = true;
        Close();
    }

    // ───────────────────────────── Điều khiển từ xa (Telegram) ─────────────────────────────

    /// <summary>Chạy hàm trên luồng UI và lấy kết quả (gọi từ luồng của bot).</summary>
    private T OnUi<T>(Func<T> f) => InvokeRequired ? (T)Invoke(f) : f();

    private List<Job> OrderedJobs() => _jobs.OrderBy(j => j.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    string IRemoteHost.ListJobs() => OnUi(() =>
    {
        var jobs = OrderedJobs();
        if (jobs.Count == 0) return "Chưa có công việc nào.";
        return string.Join("\n", jobs.Select((j, i) =>
            $"{i + 1}. {(j.Enabled ? "" : "(tắt) ")}{j.Name}" + (j.NextRun is DateTime n ? $" — lần tới {n:HH:mm dd/MM}" : "")));
    });

    string IRemoteHost.Run(string nameOrNumber) => OnUi(() =>
    {
        var jobs = OrderedJobs();
        Job? job = int.TryParse(nameOrNumber, out int n) && n >= 1 && n <= jobs.Count ? jobs[n - 1]
            : jobs.FirstOrDefault(j => j.Name.Equals(nameOrNumber, StringComparison.CurrentCultureIgnoreCase))
              ?? (jobs.Where(j => j.Name.Contains(nameOrNumber, StringComparison.CurrentCultureIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null);
        if (job == null) return $"Không tìm thấy công việc \"{nameOrNumber}\" (hoặc có nhiều công việc trùng tên) — xem /list.";
        if (job.Steps.Count(s => s.Enabled) == 0) return $"\"{job.Name}\" chưa có bước nào được bật.";
        RunJob(job, "Telegram");
        return $"▶ Đã đưa \"{job.Name}\" vào hàng đợi. Kết quả sẽ có trong /history" +
               (NotificationService.AnyChannelEnabled && job.NotifyMode != NotifyMode.Never ? " và thông báo." : ".");
    });

    string IRemoteHost.Stop() => OnUi(() =>
    {
        if (!_runner.IsBusy) return "Không có flow nào đang chạy.";
        _runner.StopAll();
        return "■ Đã yêu cầu dừng flow đang chạy.";
    });

    FlowGenerator.Context IRemoteHost.NewJobContext() => OnUi(() => new FlowGenerator.Context
    {
        OtherJobs = [.. _jobs],
        Connections = [.. SettingsStore.Current.ApiConnections.Where(c => c.Name.Trim().Length > 0)
            .Select(c => (c.Name.Trim(), $"URL gốc {c.BaseUrl}, xác thực {c.Auth}"))],
        NotificationsEnabled = NotificationService.AnyChannelEnabled
    });

    string IRemoteHost.AddJob(Job job, bool run) => OnUi(() =>
    {
        // Tên trùng công việc có sẵn → thêm số để /run theo tên không nhầm.
        var name = job.Name.Trim();
        for (int i = 2; _jobs.Any(j => j.Name.Trim().Equals(name, StringComparison.CurrentCultureIgnoreCase)); i++) name = $"{job.Name.Trim()} ({i})";
        job.Name = name;
        _jobs.Add(job);
        JobsChanged(job);
        Log.Info($"Telegram: đã tạo công việc \"{job.Name}\" ({job.Steps.Count} bước, {job.Schedule.Describe()}).");

        int number = OrderedJobs().IndexOf(job) + 1;
        var lines = new List<string>
        {
            $"✅ Đã lưu \"{job.Name}\" — số {number} trong /list, nhóm \"{job.Group}\".",
            job.NextRun is DateTime next ? $"⏰ Lần chạy tới: {next:HH:mm dd/MM/yyyy}" : $"⏰ Không có lịch — chạy bằng /run {number}"
        };
        if (job.Triggers.Count > 0) lines.Add("⚡ " + string.Join("; ", job.Triggers.Select(t => t.Describe())));
        if (run)
        {
            RunJob(job, "Telegram");
            lines.Add("▶ Đã đưa vào hàng đợi chạy — kết quả trong /history.");
        }
        lines.Add("Xem / sửa chi tiết trên máy: mở công việc trong ScheduleApp.");
        return string.Join("\n", lines);
    });

    string IRemoteHost.Status() => OnUi(() =>
    {
        var lines = new List<string> { $"💻 {Environment.MachineName} — ScheduleApp {UpdateService.Current}", _runner.IsBusy ? "⏳ " + _status.Text : "✅ Rảnh" };
        var next = _jobs.Where(j => j.Enabled && j.NextRun != null).OrderBy(j => j.NextRun).Take(5).ToList();
        if (next.Count > 0) lines.Add("Sắp chạy:\n" + string.Join("\n", next.Select(j => $"  {j.NextRun:HH:mm dd/MM} {j.Name}")));
        return string.Join("\n", lines);
    });

    /// <summary>Tạo shortcut .lnk trên Desktop chạy công việc đang chọn (ScheduleApp.exe --run "Tên").</summary>
    private void CreateShortcut()
    {
        var job = SelectedJob();
        if (job == null || Environment.ProcessPath is not string exe) return;
        try
        {
            var safe = string.Concat(job.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), safe + ".lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Không có WScript.Shell.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = exe;
            link.Arguments = $"--run \"{job.Name}\"";
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = $"Chạy \"{job.Name}\" bằng ScheduleApp";
            link.Save();
            Log.Info($"Đã tạo shortcut: {path}");
            MessageBox.Show(this, $"Đã tạo shortcut trên Desktop:\n{path}\n\nCó thể gán phím tắt trong Properties của shortcut, hoặc dùng cho Stream Deck.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ShowError("Không tạo được shortcut: " + ex.Message);
        }
    }

    /// <summary>Nhập công việc từ thư mục kịch bản: cùng Id thì thay, chưa có thì thêm.</summary>
    TestFolder.MergeResult ITestHost.ImportTests(IReadOnlyList<Job> jobs)
    {
        var r = TestFolder.Merge(_jobs, jobs);
        _scheduler.RecalculateAll();
        JobsChanged();
        Log.Info($"Đã nhập từ thư mục kịch bản: {r.Updated} công việc cập nhật, {r.Added} công việc thêm mới.");
        return r;
    }

    private void ImportJobs()
    {
        using var dlg = new OpenFileDialog { Filter = "ScheduleApp (*.json)|*.json", Title = "Nhập công việc" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var imported = JobStore.Import(dlg.FileName);
            _jobs.AddRange(imported);
            foreach (var j in imported) _scheduler.Recalculate(j);
            JobsChanged();
            Log.Info($"Đã nhập {imported.Count} công việc từ {dlg.FileName}.");
        }
        catch (Exception ex) { ShowError("Không nhập được file: " + ex.Message); }
    }

    private void ExportJobs()
    {
        using var dlg = new SaveFileDialog { Filter = "ScheduleApp (*.json)|*.json", FileName = "ScheduleApp-jobs.json", Title = "Xuất công việc" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            JobStore.Export(dlg.FileName, _jobs);
            Log.Info($"Đã xuất {_jobs.Count} công việc ra {dlg.FileName} (bí mật không nằm trong file xuất).");
        }
        catch (Exception ex) { ShowError("Không xuất được file: " + ex.Message); }
    }

    /// <summary>Chạy các kịch bản như một bộ kiểm thử rồi mở báo cáo HTML.</summary>
    private async Task RunTestsAsync(IReadOnlyList<Job> jobs, string name)
    {
        var runnable = jobs.Where(j => j.Steps.Any(s => s.Enabled)).ToList();
        if (runnable.Count == 0)
        {
            Notify("Không chạy được", "Không có kịch bản nào có bước được bật.", true);
            return;
        }
        _btnStop.Enabled = true;
        try
        {
            var env = TestEnvironments.Current();
            var result = await TestSuite.RunAsync(_runner, runnable, name, null,
                new SuiteOptions { Environment = env?.Name, Variables = TestEnvironments.Variables(env) });
            RefreshTests();
            Notify(result.Ok ? "🧪 Kiểm thử ĐẠT" : "🧪 Kiểm thử KHÔNG ĐẠT", TestReport.Summary(result.Cases), !result.Ok);
            Process.Start(new ProcessStartInfo(result.ReportPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError("Không ghi / mở được báo cáo kiểm thử: " + ex.Message);
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);

    // ───────────────────────────── Cửa sổ & khay ─────────────────────────────

    public void ShowMain()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApp()
    {
        if (_runner.IsBusy &&
            MessageBox.Show(this, "Đang có flow chạy. Dừng và thoát?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _runner.StopAll();
        _exiting = true;
        Close();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Chỉ đặt được khi SplitContainer đã có kích thước thật.
        _split.Panel1MinSize = LogicalToDeviceUnits(150);
        _split.Panel2MinSize = LogicalToDeviceUnits(100);
        _split.SplitterDistance = (int)(_split.Height * 0.58);
        FitColumns();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && _hideOnFirstShow)
        {
            _hideOnFirstShow = false;
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            // Nút X chỉ ẩn xuống khay để lịch vẫn chạy.
            e.Cancel = true;
            Hide();
            if (!_trayTipShown)
            {
                _trayTipShown = true;
                _tray.ShowBalloonTip(4000, "ScheduleApp vẫn đang chạy",
                    "Lịch vẫn hoạt động ở khay hệ thống. Nhấp đúp biểu tượng để mở lại, chuột phải → Thoát để tắt hẳn.",
                    ToolTipIcon.Info);
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        InputSimulator.BeforePointer = null;
        _overlay.Dispose();
        _uiTimer.Stop();
        _scheduler.Dispose();
        _triggers.Dispose();
        _bot.Dispose();
        _guard.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        SettingsStore.Current.LastAlive = DateTime.Now;
        SettingsStore.Save();
        SaveJobs();
        base.OnFormClosed(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!Win32.RegisterHotKey(Handle, StopHotkeyId, Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, (uint)Keys.Q))
            Log.Warn("Không đăng ký được phím tắt Ctrl+Shift+Q (đã bị ứng dụng khác dùng).");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Win32.UnregisterHotKey(Handle, StopHotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY)
        {
            int id = (int)m.WParam;
            if (id == StopHotkeyId) _runner.StopAll();
            else _triggers?.OnHotkey(id);
        }
        base.WndProc(ref m);
    }

    // ───────────────────────────── IHotkeyHost ─────────────────────────────

    public bool RegisterHotkey(int id, uint modifiers, uint vk) => Win32.RegisterHotKey(Handle, id, modifiers, vk);

    public void UnregisterHotkey(int id) => Win32.UnregisterHotKey(Handle, id);

    // ───────────────────────────── IUserNotifier ─────────────────────────────

    public Task ShowReminderAsync(string title, string message, bool waitForUser, CancellationToken ct)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(new MethodInvoker(() =>
        {
            var form = new ReminderForm(title, message, waitForUser);
            var registration = ct.Register(() =>
            {
                try { form.BeginInvoke(new MethodInvoker(form.Close)); } catch (InvalidOperationException) { }
            });
            form.FormClosed += (_, _) =>
            {
                registration.Dispose();
                form.Dispose();
                closed.TrySetResult();
            };
            form.Show();
        }));
        return waitForUser ? closed.Task.WaitAsync(ct) : Task.CompletedTask;
    }

    public void Notify(string title, string text, bool isError)
    {
        if (IsDisposed) return;
        BeginInvoke(new MethodInvoker(() =>
            _tray.ShowBalloonTip(5000, title, text.Length > 0 ? text : " ", isError ? ToolTipIcon.Error : ToolTipIcon.Info)));
    }

    public IDisposable ClearScreenForAutomation()
    {
        IDisposable? restore = null;
        Invoke(new MethodInvoker(() => restore = ScreenHelper.MoveAppWindowsAway()));
        return new InvokeOnDispose(this, restore!);
    }

    /// <summary>Hiện một form không chặn trên luồng UI và chờ kết quả của nó.</summary>
    private Task<T> ShowAndWait<T>(Func<Form> create, Func<Form, T> result, T canceled, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(new MethodInvoker(() =>
        {
            var form = create();
            var registration = ct.Register(() =>
            {
                try { form.BeginInvoke(new MethodInvoker(form.Close)); } catch (InvalidOperationException) { }
            });
            form.FormClosed += (_, _) =>
            {
                registration.Dispose();
                tcs.TrySetResult(form.DialogResult is DialogResult.OK or DialogResult.Yes ? result(form) : canceled);
                form.Dispose();
            };
            form.Show();
        }));
        return tcs.Task.WaitAsync(ct);
    }

    public Task<string?> PromptAsync(string title, string message, string defaultValue, bool password, CancellationToken ct) =>
        ShowAndWait<string?>(() => new InputPromptForm(title, message, defaultValue, password), f => ((InputPromptForm)f).Value, null, ct);

    public Task<bool> AskContinueAsync(string title, string message, CancellationToken ct) =>
        AskContinueAsync(title, message, ct, "▶ Chạy tiếp", "■ Dừng flow");

    private Task<bool> AskContinueAsync(string title, string message, CancellationToken ct, string yes, string no) =>
        ShowAndWait(() => new ConfirmForm(title, message, yes, no), _ => true, false, ct);

    public Task<DebugCommand> DebugPauseAsync(string jobName, int stepIndex, string stepText, string reason,
        IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        var snapshot = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
        var tcs = new TaskCompletionSource<DebugCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(new MethodInvoker(() =>
        {
            var bar = new DebugToolbar(jobName, stepText, reason, snapshot);
            var registration = ct.Register(() =>
            {
                try { bar.BeginInvoke(new MethodInvoker(bar.Close)); } catch (InvalidOperationException) { }
            });
            _debugBar = bar;
            _overlay.SetPaused(reason, stepText);
            bar.FormClosed += (_, _) =>
            {
                registration.Dispose();
                if (_debugBar != bar) return;
                _debugBar = null;
                _overlay.SetPaused(null, null);
            };
            _ = bar.Result.ContinueWith(t => tcs.TrySetResult(t.Result), TaskScheduler.Default);
            bar.Show();
        }));
        return tcs.Task.WaitAsync(ct);
    }

    private sealed class InvokeOnDispose(Control owner, IDisposable inner) : IDisposable
    {
        public void Dispose()
        {
            if (owner.IsDisposed) return;
            owner.BeginInvoke(new MethodInvoker(inner.Dispose));
        }
    }
}
