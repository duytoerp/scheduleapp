using System.Globalization;
using ScheduleApp.Models;
using ScheduleApp.Recording;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Soạn một công việc: tên, lịch chạy, nhắc trước và danh sách bước (flow).</summary>
internal sealed class JobEditorForm : BaseForm
{
    private static readonly CultureInfo Vi = new("vi-VN");
    private static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private readonly Job _job;
    private readonly FlowRunner _runner;

    private readonly TextBox _txtName = new() { Width = 460 };
    private readonly CheckBox _chkEnabled = new() { Text = "Kích hoạt lịch", AutoSize = true, Margin = new Padding(16, 6, 3, 3) };
    private readonly ComboBox _cboType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly DateTimePicker _dtDate = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "dd/MM/yyyy", Width = 130 };
    private readonly DateTimePicker _dtTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm:ss", ShowUpDown = true, Width = 110 };
    private readonly CheckBox[] _chkDays;
    private readonly NumericUpDown _numInterval = new() { Minimum = 1, Maximum = 100_000, Width = 90 };
    private readonly NumericUpDown _numRemind = new() { Minimum = 0, Maximum = 1440, Width = 90 };
    private readonly CheckBox _chkStopOnError = new() { Text = "Dừng flow khi một bước bị lỗi", AutoSize = true };
    private readonly Label _lblNext = new() { AutoSize = true, ForeColor = Color.FromArgb(0, 100, 0), Margin = new Padding(3, 8, 3, 3) };
    private readonly Label _lblDate = Caption("Ngày:");

    private readonly FlowDesigner _designer = new() { Dock = DockStyle.Fill };
    private readonly StepToolbox _toolbox = new() { Dock = DockStyle.Fill };
    private readonly Label _lblStepCount = new() { AutoSize = true, ForeColor = UiText.Muted, Margin = new Padding(3, 6, 3, 0) };

    private readonly Button _btnTest = new() { Text = "▶ Chạy thử flow", AutoSize = true };

    public Job Job => _job;

    public JobEditorForm(Job job, FlowRunner runner, bool isNew)
    {
        _job = job;
        _runner = runner;
        _chkDays = WeekOrder.Select(d => new CheckBox { Text = ScheduleConfig.DayName(d), AutoSize = true, Tag = d }).ToArray();

        SuspendLayout();
        Text = isNew ? "Thêm công việc" : $"Sửa công việc — {job.Name}";
        Size = new Size(1120, 800);
        MinimumSize = new Size(920, 620);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(10);
        BuildUi();
        ResumeLayout(true);

        LoadJob();
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Tên
        var general = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        general.Controls.Add(Caption("Tên công việc:"));
        general.Controls.Add(_txtName);
        general.Controls.Add(_chkEnabled);
        root.Controls.Add(general);

        // Lịch
        var schedule = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 6, Padding = new Padding(4) };
        for (int i = 0; i < 6; i++) schedule.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _cboType.Items.AddRange(ScheduleConfig.TypeNames);
        schedule.Controls.Add(Caption("Kiểu lịch:"), 0, 0);
        schedule.Controls.Add(_cboType, 1, 0);
        schedule.Controls.Add(_lblDate, 2, 0);
        schedule.Controls.Add(_dtDate, 3, 0);
        schedule.Controls.Add(Caption("Giờ:"), 4, 0);
        schedule.Controls.Add(_dtTime, 5, 0);

        var days = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        days.Controls.AddRange(_chkDays);
        schedule.Controls.Add(Caption("Các ngày:"), 0, 1);
        schedule.Controls.Add(days, 1, 1);
        schedule.SetColumnSpan(days, 5);

        schedule.Controls.Add(Caption("Lặp mỗi (phút):"), 0, 2);
        schedule.Controls.Add(_numInterval, 1, 2);
        schedule.Controls.Add(Caption("Nhắc trước (phút):"), 2, 2);
        schedule.Controls.Add(_numRemind, 3, 2);
        schedule.Controls.Add(_chkStopOnError, 4, 2);
        schedule.SetColumnSpan(_chkStopOnError, 2);
        _chkStopOnError.Anchor = AnchorStyles.Left;

        schedule.Controls.Add(_lblNext, 0, 3);
        schedule.SetColumnSpan(_lblNext, 6);

        var tips = new ToolTip();
        tips.SetToolTip(_numRemind, "Hiện cửa sổ nhắc nhở N phút trước giờ chạy (0 = không nhắc).");
        tips.SetToolTip(_numInterval, "Chỉ dùng cho kiểu \"Lặp lại theo phút\".");

        var grpSchedule = new GroupBox
        {
            Text = "Lịch chạy",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 6),
            Margin = new Padding(0, 0, 0, 8)
        };
        grpSchedule.Controls.Add(schedule);
        root.Controls.Add(grpSchedule);

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
        _btnTest.Margin = new Padding(3, 18, 3, 3);
        _btnTest.MinimumSize = new Size(140, 0);
        _btnTest.Click += async (_, _) => await TestRunAsync();
        buttons.Controls.Add(_btnTest);
        buttons.Controls.Add(_lblStepCount);

        var stepsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        stepsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        stepsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stepsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        stepsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stepsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stepsLayout.Controls.Add(toolboxPanel, 0, 0);
        stepsLayout.Controls.Add(designerPanel, 1, 0);
        stepsLayout.Controls.Add(buttons, 2, 0);

        var hint = new Label
        {
            Text = "Kéo thẻ để sắp xếp  ·  Nhấp đúp để sửa  ·  Chuột phải để xem thêm  ·  " +
                   "Phím: ↑↓ chọn, Ctrl+↑↓ di chuyển, Space bật/tắt, Delete xóa",
            AutoSize = true,
            ForeColor = UiText.Muted,
            Margin = new Padding(0, 2, 0, 0)
        };
        stepsLayout.Controls.Add(hint, 1, 1);
        stepsLayout.SetColumnSpan(hint, 2);

        var grpSteps = new GroupBox { Text = "Luồng thao tác (chạy lần lượt từ trên xuống)", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        grpSteps.Controls.Add(stepsLayout);
        root.Controls.Add(grpSteps);

        // OK / Hủy
        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 8, 0, 0) };
        var btnCancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        var btnOk = new Button { Text = "Lưu", AutoSize = true, MinimumSize = new Size(90, 0) };
        btnOk.Click += (_, _) => Save();
        bottom.Controls.Add(btnCancel);
        bottom.Controls.Add(btnOk);
        root.Controls.Add(bottom);
        CancelButton = btnCancel;

        Controls.Add(root);

        // Sự kiện
        _cboType.SelectedIndexChanged += (_, _) => UpdateScheduleUi();
        _dtDate.ValueChanged += (_, _) => UpdateNextPreview();
        _dtTime.ValueChanged += (_, _) => UpdateNextPreview();
        _numInterval.ValueChanged += (_, _) => UpdateNextPreview();
        foreach (var c in _chkDays) c.CheckedChanged += (_, _) => UpdateNextPreview();

        _designer.EditRequested += EditStep;
        _designer.AddRequested += AddStep;
        _designer.StepsChanged += (_, _) => UpdateStepCount();
        _toolbox.ItemActivated += type => AddStep(type, _designer.SelectedIndex >= 0 ? _designer.SelectedIndex + 1 : _designer.StepCount);
    }

    private static Button SideButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(140, 0), Margin = new Padding(3, 2, 3, 2) };
        b.Click += onClick;
        return b;
    }

    // ───────────────────────────── Dữ liệu ─────────────────────────────

    private void LoadJob()
    {
        _txtName.Text = _job.Name;
        _chkEnabled.Checked = _job.Enabled;
        _cboType.SelectedIndex = (int)_job.Schedule.Type;
        _dtDate.Value = _job.Schedule.StartAt.Date;
        _dtTime.Value = DateTime.Today + _job.Schedule.StartAt.TimeOfDay;
        foreach (var c in _chkDays) c.Checked = _job.Schedule.Days.Contains((DayOfWeek)c.Tag!);
        _numInterval.Value = Math.Clamp(_job.Schedule.IntervalMinutes, 1, 100_000);
        _numRemind.Value = Math.Clamp(_job.RemindBeforeMinutes, 0, 1440);
        _chkStopOnError.Checked = _job.StopOnError;
        _designer.SetSteps(_job.Steps);
        UpdateStepCount();
        UpdateScheduleUi();
    }

    private ScheduleConfig ReadSchedule() => new()
    {
        Type = (ScheduleType)Math.Max(0, _cboType.SelectedIndex),
        StartAt = _dtDate.Value.Date + _dtTime.Value.TimeOfDay,
        Days = _chkDays.Where(c => c.Checked).Select(c => (DayOfWeek)c.Tag!).ToList(),
        IntervalMinutes = (int)_numInterval.Value
    };

    private void ApplyTo(Job job)
    {
        job.Name = _txtName.Text.Trim();
        job.Enabled = _chkEnabled.Checked;
        job.Schedule = ReadSchedule();
        job.RemindBeforeMinutes = (int)_numRemind.Value;
        job.StopOnError = _chkStopOnError.Checked;
    }

    private void UpdateScheduleUi()
    {
        var type = (ScheduleType)Math.Max(0, _cboType.SelectedIndex);
        bool manual = type == ScheduleType.Manual;
        _dtDate.Enabled = !manual;
        _dtTime.Enabled = !manual;
        _lblDate.Text = type == ScheduleType.Once ? "Ngày:" : "Bắt đầu từ:";
        foreach (var c in _chkDays) c.Enabled = type == ScheduleType.Weekly;
        _numInterval.Enabled = type == ScheduleType.Interval;
        _numRemind.Enabled = !manual;
        UpdateNextPreview();
    }

    private void UpdateNextPreview()
    {
        var schedule = ReadSchedule();
        if (schedule.Type == ScheduleType.Manual)
        {
            _lblNext.Text = "Công việc chỉ chạy khi bấm \"Chạy ngay\".";
            _lblNext.ForeColor = UiText.Muted;
            return;
        }
        var next = schedule.NextOccurrence(DateTime.Now);
        if (next is DateTime n)
        {
            _lblNext.Text = $"Lần chạy tới: {n.ToString("dddd, dd/MM/yyyy 'lúc' HH:mm:ss", Vi)}";
            _lblNext.ForeColor = Color.FromArgb(0, 110, 0);
        }
        else
        {
            _lblNext.Text = schedule.Type == ScheduleType.Weekly
                ? "⚠ Chưa chọn ngày nào trong tuần."
                : "⚠ Thời điểm đã qua — công việc sẽ không chạy.";
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

        ApplyTo(_job);
        DialogResult = DialogResult.OK;
        Close();
    }

    // ───────────────────────────── Danh sách bước ─────────────────────────────

    /// <summary>Mở trình soạn cho bước mới loại <paramref name="type"/>; chỉ chèn vào flow nếu bấm OK.</summary>
    private void AddStep(StepType type, int index)
    {
        using var editor = new StepEditorForm(ActionStep.CreateDefault(type));
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _designer.InsertStep(index, editor.Step);
        _designer.Focus();
    }

    private void EditStep(int index)
    {
        if (index < 0 || index >= _job.Steps.Count) return;
        using var editor = new StepEditorForm(_job.Steps[index].Clone());
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
        MessageBox.Show(this,
            $"Đã thêm {recorded.Count} bước từ thao tác vừa ghi.\n\n" +
            "Nên xem lại: thêm bước \"Chờ cửa sổ\" / \"Chờ hình ảnh\" ở những chỗ ứng dụng cần thời gian tải, " +
            "và thay các click quan trọng bằng \"Click vào hình ảnh\" hoặc \"Click vào chữ\" để chạy ổn định hơn.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateStepCount()
    {
        int total = _job.Steps.Count, enabled = _job.Steps.Count(s => s.Enabled);
        _lblStepCount.Text = total == enabled ? $"{total} bước" : $"{total} bước ({total - enabled} đang tắt)";
    }

    private async Task TestRunAsync()
    {
        if (_job.Steps.Count(s => s.Enabled) == 0)
        {
            MessageBox.Show(this, "Chưa có bước nào được bật.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var test = _job.Clone();
        ApplyTo(test);
        test.Id = Guid.NewGuid();
        test.Name = $"{test.Name} (chạy thử)";

        _btnTest.Enabled = false;
        _btnTest.Text = "Đang chạy… (Ctrl+Shift+Q để dừng)";
        try
        {
            await _runner.EnqueueAsync(test, "chạy thử");
        }
        finally
        {
            if (!IsDisposed)
            {
                _btnTest.Enabled = true;
                _btnTest.Text = "▶ Chạy thử flow";
            }
        }
    }
}
