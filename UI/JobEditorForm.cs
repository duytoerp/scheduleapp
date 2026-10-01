using System.Globalization;
using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Recording;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.UI;

/// <summary>Soạn một công việc: tên, lịch chạy, kích hoạt, biến, xử lý lỗi và danh sách bước (flow).</summary>
internal sealed class JobEditorForm : BaseForm
{
    private static readonly CultureInfo Vi = new("vi-VN");
    private static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
    private static readonly MissedRunPolicy[] MissedPolicies = Enum.GetValues<MissedRunPolicy>();
    private static readonly NotifyMode[] NotifyModes = Enum.GetValues<NotifyMode>();

    private readonly Job _job;
    private readonly FlowRunner _runner;
    private readonly IReadOnlyList<Job> _allJobs;
    private readonly IUserNotifier _notifier;
    private readonly List<Job> _otherJobs;

    private readonly TextBox _txtName = new() { Width = 420 };
    private readonly ComboBox _cboGroup = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 180 };
    private readonly CheckBox _chkEnabled = new() { Text = "Kích hoạt (lịch + trình kích hoạt)", AutoSize = true, Margin = new Padding(16, 6, 3, 3) };

    // Lịch
    private readonly ComboBox _cboType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly DateTimePicker _dtDate = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "dd/MM/yyyy", Width = 120 };
    private readonly DateTimePicker _dtTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm:ss", ShowUpDown = true, Width = 100 };
    private readonly CheckBox[] _chkDays;
    private readonly FlowLayoutPanel _pnlDays = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    private readonly NumericUpDown _numInterval = new() { Minimum = 1, Maximum = 100_000, Width = 80 };
    private readonly CheckBox _chkWindow = new() { Text = "Chỉ trong khung giờ", AutoSize = true, Margin = new Padding(16, 5, 3, 3) };
    private readonly DateTimePicker _dtWindowStart = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 75 };
    private readonly DateTimePicker _dtWindowEnd = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 75 };
    private readonly ComboBox _cboMonthly = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _numDayOfMonth = new() { Minimum = 1, Maximum = 31, Width = 60 };
    private readonly ComboBox _cboWeekOfMonth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly ComboBox _cboMonthWeekday = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly NumericUpDown _numRemind = new() { Minimum = 0, Maximum = 1440, Width = 70 };
    private readonly CheckBox _chkHolidays = new() { Text = "Bỏ qua ngày nghỉ lễ", AutoSize = true };
    private readonly CheckBox _chkWake = new() { Text = "Đánh thức máy khi đang ngủ (Sleep)", AutoSize = true, Margin = new Padding(16, 3, 3, 3) };
    private readonly ComboBox _cboMissed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _lblNext = new() { AutoSize = true, ForeColor = Color.FromArgb(0, 100, 0), Margin = new Padding(3, 8, 3, 3) };
    private readonly Label _lblDate = Caption("Ngày:");
    private readonly FlowLayoutPanel _rowDays = Row();
    private readonly FlowLayoutPanel _rowInterval = Row();
    private readonly FlowLayoutPanel _rowMonthly = Row();

    // Kích hoạt, biến, lỗi
    private readonly ListBox _lstTriggers = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly DataGridView _gridVars = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        RowHeadersWidth = 28,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None
    };
    private readonly CheckBox _chkStopOnError = new() { Text = "Dừng flow khi một bước bị lỗi (mặc định cho các bước \"Theo cài đặt của công việc\")", AutoSize = true };
    private readonly ComboBox _cboFailureJob = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _cboNotify = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    private readonly FlowDesigner _designer = new() { Dock = DockStyle.Fill };
    private readonly StepToolbox _toolbox = new() { Dock = DockStyle.Fill };
    private readonly Label _lblStepCount = new() { AutoSize = true, ForeColor = UiText.Muted, Margin = new Padding(3, 6, 3, 0) };
    private readonly Label _lblStructure = new() { AutoSize = true, ForeColor = Color.FromArgb(200, 40, 30), Margin = new Padding(0, 2, 0, 0) };

    private readonly Button _btnTest = new() { Text = "▶ Chạy thử flow", AutoSize = true };
    private readonly Button _btnRunFrom = new() { Text = "⤵ Chạy từ bước chọn", AutoSize = true };
    private readonly Button _btnStepMode = new() { Text = "⏭ Chạy từng bước", AutoSize = true };
    private readonly CheckBox _chkBreakpoints = new() { Text = "Dừng ở điểm dừng (F9)", AutoSize = true, Checked = true, Margin = new Padding(6, 2, 3, 2) };
    private bool _testing;

    // Hoàn tác / làm lại thay đổi trên danh sách bước (ảnh chụp JSON sau mỗi thay đổi).
    private const int MaxUndo = 100;
    private readonly List<string> _history = [];
    private int _historyPos = -1;
    private bool _restoring;
    private readonly Button _btnUndo = new() { Text = "↶ Hoàn tác", AutoSize = true, Enabled = false };
    private readonly Button _btnRedo = new() { Text = "↷ Làm lại", AutoSize = true, Enabled = false };

    public Job Job => _job;

    public JobEditorForm(Job job, FlowRunner runner, IReadOnlyList<Job> allJobs, IUserNotifier notifier, bool isNew)
    {
        _job = job;
        _runner = runner;
        _allJobs = allJobs;
        _notifier = notifier;
        _otherJobs = allJobs.Where(j => j.Id != job.Id).OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _chkDays = WeekOrder.Select(d => new CheckBox { Text = ScheduleConfig.DayName(d), AutoSize = true, Tag = d }).ToArray();

        SuspendLayout();
        Text = isNew ? "Thêm công việc" : $"Sửa công việc — {job.Name}";
        Size = new Size(1200, 860);
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(10);
        KeyPreview = true;
        BuildUi();
        ResumeLayout(true);

        LoadJob();
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };

    private static FlowLayoutPanel Row() =>
        new() { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(205)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Tên, nhóm
        var general = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        _cboGroup.Items.AddRange([.. _allJobs.Select(j => j.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.CurrentCultureIgnoreCase).Order()]);
        general.Controls.AddRange([Caption("Tên công việc:"), _txtName, Caption("Nhóm:"), _cboGroup, _chkEnabled]);
        root.Controls.Add(general);

        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        tabs.TabPages.Add(BuildScheduleTab());
        tabs.TabPages.Add(BuildTriggersTab());
        tabs.TabPages.Add(BuildVariablesTab());
        tabs.TabPages.Add(BuildErrorTab());
        root.Controls.Add(tabs);

        // Bước: hộp công cụ | khung thiết kế flow | nút lệnh
        var toolboxHeader = new Label
        {
            Text = "Hộp công cụ\nKéo thả vào luồng ➜",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 8, 4, 6),
            BackColor = Color.White,
            ForeColor = UiText.Muted
        };
        var toolboxPanel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Margin = new Padding(0, 3, 6, 3) };
        toolboxPanel.Controls.Add(_toolbox);
        toolboxPanel.Controls.Add(toolboxHeader);

        var designerPanel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 0, 3) };
        designerPanel.Controls.Add(_designer);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, WrapContents = false };
        var btnRecord = SideButton("⏺ Ghi thao tác…", async (_, _) => await RecordAsync());
        btnRecord.ForeColor = Color.FromArgb(196, 43, 28);
        btnRecord.Margin = new Padding(3, 2, 3, 12);
        buttons.Controls.Add(btnRecord);
        buttons.Controls.Add(SideButton("Sửa bước…", (_, _) => EditStep(_designer.SelectedIndex)));
        buttons.Controls.Add(SideButton("Nhân bản", (_, _) => _designer.DuplicateSelected()));
        buttons.Controls.Add(SideButton("Bật / Tắt", (_, _) => _designer.ToggleSelected()));
        buttons.Controls.Add(SideButton("Xóa bước", (_, _) => _designer.DeleteSelected()));
        buttons.Controls.Add(SideButton("▲ Lên", (_, _) => _designer.MoveSelected(-1)));
        buttons.Controls.Add(SideButton("▼ Xuống", (_, _) => _designer.MoveSelected(1)));
        _btnUndo.Click += (_, _) => Undo();
        _btnRedo.Click += (_, _) => Redo();
        _btnTest.Margin = new Padding(3, 18, 3, 3);
        foreach (var b in new[] { _btnTest, _btnRunFrom, _btnStepMode }) b.MinimumSize = new Size(150, 0);
        _btnTest.Click += async (_, _) => await TestRunAsync(new RunOptions { UseBreakpoints = _chkBreakpoints.Checked });
        _btnRunFrom.Click += async (_, _) => await RunFromAsync(_designer.SelectedIndex);
        _btnStepMode.Click += async (_, _) => await TestRunAsync(new RunOptions { StepMode = true, UseBreakpoints = true });
        buttons.Controls.AddRange([_btnTest, _btnRunFrom, _btnStepMode, _chkBreakpoints, _lblStepCount]);

        var stepsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        stepsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(235)));
        stepsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stepsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        stepsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stepsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stepsLayout.Controls.Add(toolboxPanel, 0, 0);
        stepsLayout.Controls.Add(designerPanel, 1, 0);
        stepsLayout.Controls.Add(buttons, 2, 0);

        var hints = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        hints.Controls.Add(new Label
        {
            Text = "Kéo thẻ để sắp xếp  ·  Nhấp đúp để sửa  ·  Chuột phải để xem thêm  ·  " +
                   "↑↓ chọn, Ctrl+↑↓ di chuyển, Space bật/tắt, F9 điểm dừng, Ctrl+C/V sao chép, Delete xóa, Ctrl+Z/Y hoàn tác/làm lại",
            AutoSize = true,
            ForeColor = UiText.Muted,
            Margin = new Padding(0, 2, 0, 0)
        });
        hints.Controls.Add(_lblStructure);
        stepsLayout.Controls.Add(hints, 1, 1);
        stepsLayout.SetColumnSpan(hints, 2);

        var grpSteps = new GroupBox { Text = "Luồng thao tác", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        grpSteps.Controls.Add(stepsLayout);
        root.Controls.Add(grpSteps);

        // Hoàn tác / phiên bản (trái) · Lưu / Hủy (phải)
        var bottom = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var history = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var btnVersions = new Button { Text = "Phiên bản cũ…", AutoSize = true, Margin = new Padding(12, 3, 3, 3) };
        btnVersions.Click += (_, _) => ShowVersions();
        history.Controls.AddRange([_btnUndo, _btnRedo, btnVersions]);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0) };
        var btnCancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        var btnOk = new Button { Text = "Lưu", AutoSize = true, MinimumSize = new Size(90, 0) };
        btnOk.Click += (_, _) => Save();
        actions.Controls.Add(btnCancel);
        actions.Controls.Add(btnOk);
        bottom.Controls.Add(history, 0, 0);
        bottom.Controls.Add(actions, 1, 0);
        root.Controls.Add(bottom);
        var tips = new ToolTip();
        tips.SetToolTip(_btnUndo, "Hoàn tác thay đổi trên danh sách bước (Ctrl+Z)");
        tips.SetToolTip(_btnRedo, "Làm lại (Ctrl+Y)");
        tips.SetToolTip(btnVersions, "Xem và khôi phục các bản đã lưu trước đây của công việc này");
        CancelButton = btnCancel;

        Controls.Add(root);

        _designer.EditRequested += EditStep;
        _designer.AddRequested += AddStep;
        _designer.RunFromRequested += async i => await RunFromAsync(i);
        _designer.StepsChanged += (_, _) =>
        {
            UpdateStepCount();
            Snapshot();
        };
        _toolbox.ItemActivated += type => AddStep(type, _designer.SelectedIndex >= 0 ? _designer.SelectedIndex + 1 : _designer.StepCount);
    }

    private TabPage BuildScheduleTab()
    {
        var page = new TabPage("Lịch chạy") { Padding = new Padding(6), UseVisualStyleBackColor = true };
        var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _cboType.Items.AddRange(ScheduleConfig.TypeNames);
        var row1 = Row();
        row1.Controls.AddRange([_cboType, _lblDate, _dtDate, Caption("Giờ:"), _dtTime, Caption("Nhắc trước (phút):"), _numRemind]);
        grid.Controls.Add(Caption("Kiểu lịch:"), 0, 0);
        grid.Controls.Add(row1, 1, 0);

        _pnlDays.Controls.AddRange(_chkDays);
        _rowDays.Controls.Add(_pnlDays);
        grid.Controls.Add(Caption("Các ngày:"), 0, 1);
        grid.Controls.Add(_rowDays, 1, 1);

        _rowInterval.Controls.AddRange([Caption("Lặp mỗi (phút):"), _numInterval, _chkWindow, Caption("từ"), _dtWindowStart, Caption("đến"), _dtWindowEnd]);
        grid.Controls.Add(_rowInterval, 1, 2);

        _cboMonthly.Items.AddRange(ScheduleConfig.MonthlyModeNames);
        _cboWeekOfMonth.Items.AddRange(ScheduleConfig.WeekOfMonthNames);
        foreach (var d in WeekOrder) _cboMonthWeekday.Items.Add(ScheduleConfig.DayLongName(d));
        _rowMonthly.Controls.AddRange([Caption("Hằng tháng:"), _cboMonthly, Caption("ngày"), _numDayOfMonth, _cboMonthWeekday, _cboWeekOfMonth]);
        grid.Controls.Add(_rowMonthly, 1, 3);

        foreach (var p in MissedPolicies)
            _cboMissed.Items.Add(p switch
            {
                MissedRunPolicy.RunOnce => "Chạy bù một lần khi máy/app mở lại",
                MissedRunPolicy.Ask => "Hỏi tôi có chạy bù không",
                _ => "Bỏ qua (chỉ ghi log)"
            });
        var row5 = Row();
        row5.Controls.AddRange([_chkHolidays, Caption("   Khi lỡ lịch (máy tắt/ngủ):"), _cboMissed, _chkWake]);
        grid.Controls.Add(row5, 1, 4);
        grid.Controls.Add(_lblNext, 1, 5);

        var tips = new ToolTip();
        tips.SetToolTip(_numRemind, "Hiện cửa sổ nhắc nhở N phút trước giờ chạy (0 = không nhắc).");
        tips.SetToolTip(_chkHolidays, "Danh sách ngày nghỉ sửa trong ⚙ Cài đặt.");
        tips.SetToolTip(_chkWake, "Cần bật \"Allow wake timers\" trong Power Options. Máy sẽ thức trước giờ chạy 1 phút.\n" +
                                  "Lưu ý: nếu máy có mật khẩu màn hình khóa, các bước chuột/bàn phím không chạy được khi máy đang khóa.");
        tips.SetToolTip(_chkWindow, "Vd mỗi 30 phút nhưng chỉ trong giờ hành chính 8:00–17:30 các ngày đã chọn.");

        page.Controls.Add(grid);

        _cboType.SelectedIndexChanged += (_, _) => UpdateScheduleUi();
        _cboMonthly.SelectedIndexChanged += (_, _) => UpdateScheduleUi();
        _chkWindow.CheckedChanged += (_, _) => UpdateScheduleUi();
        foreach (var c in new Control[] { _dtDate, _dtTime, _numInterval, _dtWindowStart, _dtWindowEnd, _numDayOfMonth, _cboWeekOfMonth, _cboMonthWeekday, _chkHolidays })
        {
            switch (c)
            {
                case DateTimePicker d: d.ValueChanged += (_, _) => UpdateNextPreview(); break;
                case NumericUpDown n: n.ValueChanged += (_, _) => UpdateNextPreview(); break;
                case ComboBox cb: cb.SelectedIndexChanged += (_, _) => UpdateNextPreview(); break;
                case CheckBox ch: ch.CheckedChanged += (_, _) => UpdateNextPreview(); break;
            }
        }
        foreach (var c in _chkDays) c.CheckedChanged += (_, _) => UpdateNextPreview();
        return page;
    }

    private TabPage BuildTriggersTab()
    {
        var page = new TabPage("Kích hoạt khác") { Padding = new Padding(6), UseVisualStyleBackColor = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Right, WrapContents = false };
        var add = new Button { Text = "＋ Thêm…", AutoSize = true, MinimumSize = new Size(100, 0) };
        var edit = new Button { Text = "Sửa…", AutoSize = true, MinimumSize = new Size(100, 0) };
        var del = new Button { Text = "Xóa", AutoSize = true, MinimumSize = new Size(100, 0) };
        buttons.Controls.AddRange([add, edit, del]);
        var hint = new Label
        {
            Text = "Ngoài lịch chạy, công việc còn có thể chạy khi: bấm phím tắt, có file mới trong thư mục, mở/đóng một ứng dụng, " +
                   "máy rảnh N phút, mở khóa màn hình, hoặc khi ScheduleApp khởi động. Từ dòng lệnh: ScheduleApp.exe --run \"Tên công việc\".",
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = UiText.Muted,
            Padding = new Padding(0, 4, 0, 0)
        };
        page.Controls.Add(_lstTriggers);
        page.Controls.Add(buttons);
        page.Controls.Add(hint);

        add.Click += (_, _) =>
        {
            using var dlg = new TriggerEditorForm(new JobTrigger());
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _job.Triggers.Add(dlg.Trigger);
            RefreshTriggers();
        };
        void EditSelected()
        {
            int i = _lstTriggers.SelectedIndex;
            if (i < 0) return;
            var copy = new JobTrigger { Type = _job.Triggers[i].Type, Enabled = _job.Triggers[i].Enabled, Value = _job.Triggers[i].Value, Value2 = _job.Triggers[i].Value2, Minutes = _job.Triggers[i].Minutes };
            using var dlg = new TriggerEditorForm(copy);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _job.Triggers[i] = dlg.Trigger;
            RefreshTriggers();
        }
        edit.Click += (_, _) => EditSelected();
        _lstTriggers.DoubleClick += (_, _) => EditSelected();
        del.Click += (_, _) =>
        {
            int i = _lstTriggers.SelectedIndex;
            if (i < 0) return;
            _job.Triggers.RemoveAt(i);
            RefreshTriggers();
        };
        return page;
    }

    private TabPage BuildVariablesTab()
    {
        var page = new TabPage("Biến") { Padding = new Padding(6), UseVisualStyleBackColor = true };
        _gridVars.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Tên biến", FillWeight = 30 });
        _gridVars.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Giá trị ban đầu (có thể dùng {{today}}, {{env:USERNAME}}…)", FillWeight = 70 });
        var hint = new Label
        {
            Text = "Dùng trong mọi ô chữ của bước bằng {{tên}}. Có sẵn: {{today}} {{now}} {{today-1:dd/MM/yyyy}} {{now+30m:HH:mm}} {{clipboard}} " +
                   "{{env:TÊN}} {{secret:Tên}} {{random:1-100}} {{job.name}} {{computer}} {{user}} {{lastOutput}} {{lastError}} {{loop.index}}. " +
                   "Định dạng: {{biến:upper}} {{biến:N0}} {{biến:dd/MM/yyyy}}.",
            Dock = DockStyle.Bottom,
            AutoSize = true,
            MaximumSize = new Size(1100, 0),
            ForeColor = UiText.Muted,
            Padding = new Padding(0, 4, 0, 0)
        };
        page.Controls.Add(_gridVars);
        page.Controls.Add(hint);
        return page;
    }

    private TabPage BuildErrorTab()
    {
        var page = new TabPage("Lỗi & thông báo") { Padding = new Padding(6), UseVisualStyleBackColor = true };
        var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.Controls.Add(_chkStopOnError, 0, 0);
        grid.SetColumnSpan(_chkStopOnError, 2);

        _cboFailureJob.Items.Add("(không)");
        foreach (var j in _otherJobs) _cboFailureJob.Items.Add(j.Name);
        grid.Controls.Add(Caption("Khi flow thất bại, chạy công việc:"), 0, 1);
        grid.Controls.Add(_cboFailureJob, 1, 1);

        foreach (var m in NotifyModes)
            _cboNotify.Items.Add(m switch
            {
                NotifyMode.Always => "Mỗi lần chạy xong",
                NotifyMode.Never => "Không gửi",
                _ => "Chỉ khi lỗi"
            });
        grid.Controls.Add(Caption("Gửi thông báo (Telegram/email/webhook):"), 0, 2);
        grid.Controls.Add(_cboNotify, 1, 2);

        var hint = new Label
        {
            Text = "Mỗi bước còn có cài đặt riêng \"Khi bước lỗi\": thử lại N lần, bỏ qua, hoặc nhảy tới một nhãn. " +
                   "Ảnh chụp màn hình lúc lỗi được lưu trong thư mục log và gửi kèm thông báo. Kênh thông báo cấu hình trong ⚙ Cài đặt." +
                   (NotificationService.AnyChannelEnabled ? "" : "  (Chưa bật kênh thông báo nào.)"),
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            ForeColor = UiText.Muted,
            Margin = new Padding(3, 10, 3, 3)
        };
        grid.Controls.Add(hint, 0, 3);
        grid.SetColumnSpan(hint, 2);
        page.Controls.Add(grid);
        return page;
    }

    private static Button SideButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(150, 0), Margin = new Padding(3, 2, 3, 2) };
        b.Click += onClick;
        return b;
    }

    // ───────────────────────────── Dữ liệu ─────────────────────────────

    private void LoadJob()
    {
        var s = _job.Schedule;
        _txtName.Text = _job.Name;
        _cboGroup.Text = _job.Group;
        _chkEnabled.Checked = _job.Enabled;
        _cboType.SelectedIndex = (int)s.Type;
        _dtDate.Value = s.StartAt.Date;
        _dtTime.Value = DateTime.Today + s.StartAt.TimeOfDay;
        foreach (var c in _chkDays) c.Checked = s.Days.Contains((DayOfWeek)c.Tag!);
        _numInterval.Value = Math.Clamp(s.IntervalMinutes, 1, 100_000);
        _chkWindow.Checked = s.UseTimeWindow;
        _dtWindowStart.Value = DateTime.Today + s.WindowStart;
        _dtWindowEnd.Value = DateTime.Today + s.WindowEnd;
        _cboMonthly.SelectedIndex = (int)s.MonthlyMode;
        _numDayOfMonth.Value = Math.Clamp(s.DayOfMonth, 1, 31);
        _cboWeekOfMonth.SelectedIndex = Math.Clamp(s.WeekOfMonth, 1, 5) - 1;
        _cboMonthWeekday.SelectedIndex = Array.IndexOf(WeekOrder, s.MonthWeekday);
        _numRemind.Value = Math.Clamp(_job.RemindBeforeMinutes, 0, 1440);
        _chkHolidays.Checked = _job.SkipHolidays;
        _chkWake.Checked = _job.WakeComputer;
        _cboMissed.SelectedIndex = Array.IndexOf(MissedPolicies, _job.MissedRunPolicy);
        _chkStopOnError.Checked = _job.StopOnError;
        _cboFailureJob.SelectedIndex = _job.OnFailureJobId is Guid fid ? _otherJobs.FindIndex(j => j.Id == fid) + 1 : 0;
        _cboNotify.SelectedIndex = Array.IndexOf(NotifyModes, _job.NotifyMode);
        foreach (var v in _job.Variables) _gridVars.Rows.Add(v.Name, v.Value);
        RefreshTriggers();
        _designer.SetSteps(_job.Steps);
        UpdateStepCount();
        UpdateScheduleUi();
        Snapshot();
    }

    // ───────────────────────────── Hoàn tác / phiên bản ─────────────────────────────

    /// <summary>Lưu trạng thái danh sách bước hiện tại vào lịch sử hoàn tác (bỏ qua nếu không đổi).</summary>
    private void Snapshot()
    {
        if (_restoring) return;
        var json = JsonSerializer.Serialize(_job.Steps, JsonDefaults.Options);
        if (_historyPos >= 0 && _history[_historyPos] == json) return;
        _history.RemoveRange(_historyPos + 1, _history.Count - _historyPos - 1);
        _history.Add(json);
        if (_history.Count > MaxUndo) _history.RemoveAt(0);
        _historyPos = _history.Count - 1;
        UpdateUndoButtons();
    }

    private void Undo()
    {
        if (_historyPos <= 0) return;
        RestoreSteps(--_historyPos);
    }

    private void Redo()
    {
        if (_historyPos >= _history.Count - 1) return;
        RestoreSteps(++_historyPos);
    }

    private void RestoreSteps(int position)
    {
        var steps = JsonSerializer.Deserialize<List<ActionStep>>(_history[position], JsonDefaults.Options) ?? [];
        int selected = _designer.SelectedIndex;
        _restoring = true;
        try
        {
            _job.Steps.Clear();
            _job.Steps.AddRange(steps);
            _designer.SetSteps(_job.Steps);
            _designer.SelectStep(Math.Min(selected, steps.Count - 1));
        }
        finally
        {
            _restoring = false;
        }
        UpdateStepCount();
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        _btnUndo.Enabled = _historyPos > 0;
        _btnRedo.Enabled = _historyPos < _history.Count - 1;
    }

    /// <summary>Xem các phiên bản đã lưu trước đây của công việc và khôi phục một bản (có thể hoàn tác).</summary>
    private void ShowVersions()
    {
        var versions = JobVersions.List(_job.Id);
        if (versions.Count == 0)
        {
            MessageBox.Show(this, "Chưa có phiên bản cũ nào. Mỗi lần bạn lưu thay đổi, bản trước đó sẽ được giữ lại (tối đa " +
                                  $"{JobVersions.Keep} bản) để khôi phục khi cần.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new VersionPickerForm(versions);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected is not { } v) return;

        var old = v.Job;
        old.Id = _job.Id;
        old.LastRun = _job.LastRun;
        old.LastResult = _job.LastResult;
        // Chép toàn bộ nội dung bản cũ vào công việc đang soạn rồi nạp lại giao diện (bước khôi phục vẫn hoàn tác được).
        foreach (var p in typeof(Job).GetProperties().Where(p => p.CanRead && p.CanWrite && p.Name != nameof(Job.Steps)))
            p.SetValue(_job, p.GetValue(old));
        _job.Steps.Clear();
        _job.Steps.AddRange(old.Steps);
        _gridVars.Rows.Clear();
        LoadJob();
        Log.Info($"Đã khôi phục \"{_job.Name}\" về bản lúc {v.SavedAt:HH:mm dd/MM/yyyy} (bấm Lưu để áp dụng).");
    }

    private void RefreshTriggers()
    {
        _lstTriggers.Items.Clear();
        foreach (var t in _job.Triggers) _lstTriggers.Items.Add(t.Describe());
    }

    private ScheduleConfig ReadSchedule() => new()
    {
        Type = (ScheduleType)Math.Max(0, _cboType.SelectedIndex),
        StartAt = _dtDate.Value.Date + _dtTime.Value.TimeOfDay,
        Days = _chkDays.Where(c => c.Checked).Select(c => (DayOfWeek)c.Tag!).ToList(),
        IntervalMinutes = (int)_numInterval.Value,
        UseTimeWindow = _chkWindow.Checked,
        WindowStart = new TimeSpan(_dtWindowStart.Value.Hour, _dtWindowStart.Value.Minute, 0),
        WindowEnd = new TimeSpan(_dtWindowEnd.Value.Hour, _dtWindowEnd.Value.Minute, 0),
        MonthlyMode = (MonthlyMode)Math.Max(0, _cboMonthly.SelectedIndex),
        DayOfMonth = (int)_numDayOfMonth.Value,
        WeekOfMonth = Math.Max(0, _cboWeekOfMonth.SelectedIndex) + 1,
        MonthWeekday = WeekOrder[Math.Max(0, _cboMonthWeekday.SelectedIndex)]
    };

    private List<VariableDef> ReadVariables() =>
        _gridVars.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => new VariableDef { Name = (r.Cells[0].Value as string ?? "").Trim(), Value = r.Cells[1].Value as string ?? "" })
            .Where(v => v.Name.Length > 0)
            .ToList();

    private void ApplyTo(Job job)
    {
        job.Name = _txtName.Text.Trim();
        job.Group = _cboGroup.Text.Trim();
        job.Enabled = _chkEnabled.Checked;
        job.Schedule = ReadSchedule();
        job.RemindBeforeMinutes = (int)_numRemind.Value;
        job.SkipHolidays = _chkHolidays.Checked;
        job.WakeComputer = _chkWake.Checked;
        job.MissedRunPolicy = MissedPolicies[Math.Max(0, _cboMissed.SelectedIndex)];
        job.StopOnError = _chkStopOnError.Checked;
        job.OnFailureJobId = _cboFailureJob.SelectedIndex > 0 ? _otherJobs[_cboFailureJob.SelectedIndex - 1].Id : null;
        job.NotifyMode = NotifyModes[Math.Max(0, _cboNotify.SelectedIndex)];
        job.Variables = ReadVariables();
        if (!ReferenceEquals(job, _job)) job.Triggers = _job.Triggers.ToList();
    }

    private void UpdateScheduleUi()
    {
        var type = (ScheduleType)Math.Max(0, _cboType.SelectedIndex);
        bool manual = type == ScheduleType.Manual;
        _dtDate.Enabled = !manual;
        _dtTime.Enabled = !manual;
        _lblDate.Text = type == ScheduleType.Once ? "Ngày:" : "Bắt đầu từ:";
        bool interval = type == ScheduleType.Interval;
        _rowDays.Enabled = type == ScheduleType.Weekly || (interval && _chkWindow.Checked);
        _rowInterval.Visible = interval;
        _dtWindowStart.Enabled = _dtWindowEnd.Enabled = _chkWindow.Checked;
        _rowMonthly.Visible = type == ScheduleType.Monthly;
        var mode = (MonthlyMode)Math.Max(0, _cboMonthly.SelectedIndex);
        _numDayOfMonth.Visible = mode == MonthlyMode.DayOfMonth;
        _cboMonthWeekday.Visible = _cboWeekOfMonth.Visible = mode == MonthlyMode.NthWeekday;
        _numRemind.Enabled = _chkHolidays.Enabled = _cboMissed.Enabled = _chkWake.Enabled = !manual;
        UpdateNextPreview();
    }

    private void UpdateNextPreview()
    {
        var schedule = ReadSchedule();
        if (schedule.Type == ScheduleType.Manual)
        {
            _lblNext.Text = "Công việc chỉ chạy khi bấm \"Chạy ngay\", theo trình kích hoạt, hoặc được công việc khác gọi.";
            _lblNext.ForeColor = UiText.Muted;
            return;
        }
        var next = schedule.NextOccurrence(DateTime.Now, _chkHolidays.Checked ? SettingsStore.IsHoliday : null);
        if (next is DateTime n)
        {
            var after = schedule.NextOccurrence(n, _chkHolidays.Checked ? SettingsStore.IsHoliday : null);
            _lblNext.Text = $"Lần chạy tới: {n.ToString("dddd, dd/MM/yyyy 'lúc' HH:mm:ss", Vi)}" +
                            (after is DateTime a ? $"   ·   tiếp theo: {a.ToString("dddd dd/MM HH:mm", Vi)}" : "");
            _lblNext.ForeColor = Color.FromArgb(0, 110, 0);
        }
        else
        {
            _lblNext.Text = schedule.Type is ScheduleType.Weekly or ScheduleType.Interval && schedule.Days.Count == 0
                ? "⚠ Chưa chọn ngày nào trong tuần."
                : "⚠ Thời điểm đã qua — công việc sẽ không chạy theo lịch.";
            _lblNext.ForeColor = Color.FromArgb(180, 60, 0);
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_txtName.Text))
        {
            MessageBox.Show(this, "Hãy nhập tên công việc.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }
        var schedule = ReadSchedule();
        if (schedule.Type == ScheduleType.Weekly && schedule.Days.Count == 0)
        {
            MessageBox.Show(this, "Hãy chọn ít nhất một ngày trong tuần.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (schedule.Type == ScheduleType.Once && _chkEnabled.Checked && schedule.NextOccurrence(DateTime.Now) == null &&
            MessageBox.Show(this, "Thời điểm chạy đã qua nên công việc sẽ không chạy. Vẫn lưu?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        var structure = FlowStructure.Build(_job.Steps);
        if (!structure.IsValid &&
            MessageBox.Show(this, "Flow có lỗi cấu trúc nên sẽ không chạy được:\n\n" + string.Join("\n", structure.Errors.Take(8)) + "\n\nVẫn lưu?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        ApplyTo(_job);
        DialogResult = DialogResult.OK;
        Close();
    }

    // ───────────────────────────── Danh sách bước ─────────────────────────────

    private StepEditorContext EditorContext() => new()
    {
        Jobs = _allJobs,
        CurrentJobId = _job.Id,
        Labels = _job.Steps.Where(s => s.Type == StepType.Label && s.Target.Trim().Length > 0).Select(s => s.Target.Trim()).Distinct().ToList(),
        Variables = KnownVariables(),
        JobVariables = ReadVariables(),
        Notifier = _notifier
    };

    /// <summary>Tên biến xuất hiện trong flow (để gợi ý khi soạn bước).</summary>
    private List<string> KnownVariables()
    {
        var names = new List<string>();
        names.AddRange(ReadVariables().Select(v => v.Name));
        foreach (var s in _job.Steps)
        {
            if (s.Type is StepType.SetVariable or StepType.RunCommand or StepType.Browser or StepType.HttpRequest or StepType.AskAi
                && s.Variable.Trim().Length > 0) names.Add(s.Variable.Trim());
            if (s.Type == StepType.Loop && s.LoopKind is LoopKind.Rows or LoopKind.Lines or LoopKind.Files) names.Add(s.LoopVar);
            if (s.Type == StepType.Loop && s.LoopKind == LoopKind.Rows) names.Add(s.LoopVar + ".rowNumber");
            if (s.Type == StepType.HttpRequest) names.AddRange(["http.status", "http.body"]);
            if (s.Type == StepType.AskAi) names.Add("ai.answer");
            if (s.Type == StepType.WriteData) names.Add("lastRow");
        }
        if (_job.Triggers.Any(t => t.Type == TriggerType.EmailReceived))
            names.AddRange(["email.subject", "email.from", "email.body", "email.attachments", "email.attachmentDir"]);
        names.AddRange(["loop.index", "lastOutput", "lastError", "job.name", "today", "now", "clipboard"]);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Mở trình soạn cho bước mới loại <paramref name="type"/>; chỉ chèn vào flow nếu bấm OK.</summary>
    private void AddStep(StepType type, int index)
    {
        // Bước đánh dấu không có gì để soạn → chèn luôn.
        if (type is StepType.Else or StepType.EndIf or StepType.EndLoop or StepType.BreakLoop or StepType.ContinueLoop)
        {
            _designer.InsertStep(index, ActionStep.CreateDefault(type));
            _designer.Focus();
            return;
        }

        using var editor = new StepEditorForm(ActionStep.CreateDefault(type), EditorContext());
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _designer.InsertStep(index, editor.Step);
        // Khối Nếu / Lặp: tự thêm bước kết thúc khối.
        if (editor.Step.Type == StepType.If) _designer.InsertStep(index + 1, ActionStep.CreateDefault(StepType.EndIf));
        if (editor.Step.Type == StepType.Loop) _designer.InsertStep(index + 1, ActionStep.CreateDefault(StepType.EndLoop));
        if (editor.Step.Type is StepType.If or StepType.Loop) _designer.SelectStep(index);
        _designer.Focus();
    }

    private void EditStep(int index)
    {
        if (index < 0 || index >= _job.Steps.Count) return;
        if (_job.Steps[index].Type is StepType.Else or StepType.EndIf or StepType.EndLoop or StepType.BreakLoop or StepType.ContinueLoop) return;
        using var editor = new StepEditorForm(_job.Steps[index].Clone(), EditorContext());
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _designer.ReplaceStep(index, editor.Step);
        _designer.Focus();
    }

    /// <summary>
    /// Ghi thao tác chuột/bàn phím trên các ứng dụng khác rồi chèn các bước sinh ra vào sau bước đang chọn.
    /// </summary>
    private async Task RecordAsync()
    {
        List<ActionStep> recorded;
        using (var recorder = new MacroRecorder())
        using (ScreenHelper.MoveAppWindowsAway())
        {
            var finished = new TaskCompletionSource<bool>();
            using var toolbar = new RecorderToolbar(recorder);
            toolbar.Finished += save => finished.TrySetResult(save);
            try
            {
                recorder.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            toolbar.Show();
            bool save = await finished.Task;
            recorded = recorder.Stop();
            toolbar.Close();
            if (!save) return;
        }

        Activate();
        if (recorded.Count == 0)
        {
            MessageBox.Show(this, "Chưa ghi được thao tác nào.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int index = _designer.SelectedIndex >= 0 ? _designer.SelectedIndex + 1 : _designer.StepCount;
        foreach (var step in recorded) _designer.InsertStep(index++, step);
        _designer.Focus();
        bool hasSecret = recorded.Any(s => s.Text.Contains("{{secret:"));
        int elementClicks = recorded.Count(s => s.Type == StepType.ClickElement);
        int pointClicks = recorded.Count(s => s.Type is StepType.MouseClick or StepType.MouseDrag);
        MessageBox.Show(this,
            $"Đã thêm {recorded.Count} bước từ thao tác vừa ghi.\n\n" +
            (elementClicks > 0 ? $"✔ {elementClicks} click được ghi theo phần tử UI (không phụ thuộc vị trí cửa sổ / độ phân giải; không tìm thấy phần tử thì tự click theo tọa độ lúc ghi).\n" : "") +
            "Đã tự chèn bước \"Chờ cửa sổ\" khi chuyển sang cửa sổ khác." +
            (pointClicks > 0 ? $" Còn {pointClicks} thao tác theo tọa độ — nên xem lại, thay click quan trọng bằng \"Click vào hình ảnh\" nếu cần." : "") +
            (hasSecret ? "\n\n🔑 Phát hiện ô mật khẩu: chữ gõ vào đó được thay bằng {{secret:MatKhau}} — hãy thêm bí mật \"MatKhau\" trong mục 🔑 Bí mật." : ""),
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateStepCount()
    {
        int total = _job.Steps.Count, enabled = _job.Steps.Count(s => s.Enabled);
        _lblStepCount.Text = total == enabled ? $"{total} bước" : $"{total} bước ({total - enabled} đang tắt)";
        var structure = _designer.Structure;
        _lblStructure.Text = structure.IsValid ? "" : "⚠ " + structure.Errors[0] + (structure.Errors.Count > 1 ? $"  (+{structure.Errors.Count - 1} lỗi khác)" : "");
    }

    private async Task RunFromAsync(int index)
    {
        if (index < 0)
        {
            MessageBox.Show(this, "Hãy chọn bước muốn bắt đầu chạy.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await TestRunAsync(new RunOptions { StartIndex = index, UseBreakpoints = _chkBreakpoints.Checked });
    }

    private async Task TestRunAsync(RunOptions options)
    {
        if (_testing) return;
        if (_job.Steps.Count(s => s.Enabled) == 0)
        {
            MessageBox.Show(this, "Chưa có bước nào được bật.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var structure = FlowStructure.Build(_job.Steps);
        if (!structure.IsValid)
        {
            MessageBox.Show(this, "Flow có lỗi cấu trúc:\n\n" + string.Join("\n", structure.Errors.Take(8)), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var test = _job.Clone();
        ApplyTo(test);
        test.Id = Guid.NewGuid();
        test.Name = $"{test.Name} (chạy thử)";

        var run = new RunOptions
        {
            StartIndex = options.StartIndex,
            StepMode = options.StepMode,
            UseBreakpoints = options.UseBreakpoints,
            IsTest = true,
            StepStarted = i =>
            {
                if (IsDisposed) return;
                try { BeginInvoke(new MethodInvoker(() => _designer.SetRunning(i))); } catch (InvalidOperationException) { }
            }
        };

        _testing = true;
        foreach (var b in new[] { _btnTest, _btnRunFrom, _btnStepMode }) b.Enabled = false;
        _btnTest.Text = "Đang chạy… (Ctrl+Shift+Q dừng)";
        _designer.SetFailed(-1);
        try
        {
            var result = await _runner.EnqueueAsync(test, "chạy thử", run);
            if (IsDisposed) return;
            _designer.SetRunning(-1);
            if (result is { Ok: false, FailedStep: > 0 }) _designer.SetFailed(result.FailedStep - 1);
        }
        finally
        {
            _testing = false;
            if (!IsDisposed)
            {
                foreach (var b in new[] { _btnTest, _btnRunFrom, _btnStepMode }) b.Enabled = true;
                _btnTest.Text = "▶ Chạy thử flow";
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.F5 && !_testing)
        {
            e.Handled = true;
            _ = TestRunAsync(new RunOptions { UseBreakpoints = _chkBreakpoints.Checked });
            return;
        }
        // Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) cho danh sách bước — ô nhập chữ vẫn giữ hoàn tác riêng của nó.
        bool undo = e.Control && e.KeyCode == Keys.Z && !e.Shift;
        bool redo = e.Control && (e.KeyCode == Keys.Y || (e.KeyCode == Keys.Z && e.Shift));
        if ((undo || redo) && FocusedLeaf() is not (TextBoxBase or ComboBox or DataGridView or DataGridViewTextBoxEditingControl or NumericUpDown or DateTimePicker))
        {
            e.Handled = e.SuppressKeyPress = true;
            if (undo) Undo();
            else Redo();
        }
    }

    private Control? FocusedLeaf()
    {
        Control? c = ActiveControl;
        while (c is ContainerControl { ActiveControl: { } inner }) c = inner;
        return c;
    }
}
