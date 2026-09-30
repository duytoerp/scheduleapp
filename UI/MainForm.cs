using System.Diagnostics;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

internal sealed class MainForm : BaseForm, IUserNotifier
{
    private const int StopHotkeyId = 0x5AFE;
    private const int MaxLogChars = 200_000;

    private readonly List<Job> _jobs;
    private readonly FlowRunner _runner;
    private readonly Scheduler _scheduler;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        CheckBoxes = true,
        HideSelection = false,
        MultiSelect = false,
        GridLines = true
    };

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

    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Sẵn sàng" };
    private readonly ToolStripButton _btnStop = new("■ Dừng flow") { Enabled = false, ToolTipText = "Dừng flow đang chạy (Ctrl+Shift+Q)" };
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };

    private bool _hideOnFirstShow;
    private bool _exiting;
    private bool _suppressCheck;
    private bool _trayTipShown;

    public MainForm(bool startHidden)
    {
        _hideOnFirstShow = startHidden;
        _jobs = JobStore.Load();
        _runner = new FlowRunner(this);
        _scheduler = new Scheduler(_jobs, _runner);

        SuspendLayout();
        Text = "ScheduleApp — Đặt lịch, nhắc nhở & tự động thao tác";
        Size = new Size(1180, 720);
        MinimumSize = new Size(820, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        ResumeLayout(true);

        _ = Handle; // tạo handle sớm để nhận BeginInvoke / hotkey kể cả khi khởi động ẩn
        WireEvents();

        _scheduler.Start();
        RefreshList();
        Log.Info($"ScheduleApp khởi động — {_jobs.Count} công việc. Dữ liệu: {JobStore.DataDir}");
    }

    // ───────────────────────────── Giao diện ─────────────────────────────

    private void BuildUi()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2), ImageScalingSize = new Size(16, 16) };
        toolbar.Items.Add(Button("＋ Thêm công việc", (_, _) => AddJob()));
        toolbar.Items.Add(Button("Sửa", (_, _) => EditSelected()));
        toolbar.Items.Add(Button("Nhân bản", (_, _) => DuplicateSelected()));
        toolbar.Items.Add(Button("Xóa", (_, _) => DeleteSelected()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Button("▶ Chạy ngay", (_, _) => RunSelected()));
        _btnStop.Click += (_, _) => _runner.StopAll();
        toolbar.Items.Add(_btnStop);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Button("Nhập…", (_, _) => ImportJobs()));
        toolbar.Items.Add(Button("Xuất…", (_, _) => ExportJobs()));
        toolbar.Items.Add(Button("Thư mục log", (_, _) => OpenFolder(Log.LogDir)));

        var startup = new ToolStripButton("Khởi động cùng Windows")
        {
            CheckOnClick = true,
            Checked = StartupManager.IsEnabled,
            Alignment = ToolStripItemAlignment.Right,
            ToolTipText = "Tự chạy ScheduleApp (thu nhỏ ở khay hệ thống) khi đăng nhập Windows"
        };
        startup.CheckedChanged += (_, _) =>
        {
            try
            {
                StartupManager.Set(startup.Checked);
                Log.Info(startup.Checked ? "Đã bật khởi động cùng Windows." : "Đã tắt khởi động cùng Windows.");
            }
            catch (Exception ex) { ShowError("Không thay đổi được cài đặt khởi động: " + ex.Message); }
        };
        toolbar.Items.Add(startup);

        _list.Columns.Add("Công việc");
        _list.Columns.Add("Lịch");
        _list.Columns.Add("Lần chạy tới");
        _list.Columns.Add("Còn lại");
        _list.Columns.Add("Lần chạy trước");
        _list.Columns.Add("Kết quả");
        _list.Resize += (_, _) => FitColumns();

        var logHeader = new Label
        {
            Text = "Nhật ký hoạt động",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(2, 6, 0, 4),
            Font = new Font(Font, FontStyle.Bold)
        };
        var logPanel = new Panel { Dock = DockStyle.Fill };
        logPanel.Controls.Add(_log);
        logPanel.Controls.Add(logHeader);

        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(logPanel);
        var split = _split;

        var statusBar = new StatusStrip();
        statusBar.Items.Add(_status);
        statusBar.Items.Add(new ToolStripStatusLabel("Dừng khẩn cấp: Ctrl+Shift+Q") { ForeColor = UiText.Muted });

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(statusBar);

        // Khay hệ thống
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Mở ScheduleApp", null, (_, _) => ShowMain());
        trayMenu.Items.Add("Dừng flow đang chạy", null, (_, _) => _runner.StopAll());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Thoát", null, (_, _) => ExitApp());
        _tray.Icon = AppIcon.Get();
        _tray.Text = "ScheduleApp";
        _tray.ContextMenuStrip = trayMenu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private static ToolStripButton Button(string text, EventHandler onClick)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(4, 0, 4, 0) };
        b.Click += onClick;
        return b;
    }

    private void FitColumns()
    {
        float[] weights = [0.22f, 0.20f, 0.14f, 0.09f, 0.14f, 0.21f];
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
            BeginInvoke(new MethodInvoker(() => AppendLog(line)));
        };

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
            }));
        };

        _scheduler.Changed += RefreshList;
        _scheduler.ReminderDue += job =>
        {
            var msg = $"\"{job.Name}\" sẽ tự động chạy lúc {job.NextRun:HH:mm} ({job.Schedule.Describe()}).";
            Log.Info($"🔔 Nhắc trước: {msg}");
            _ = ShowReminderAsync($"Sắp chạy: {job.Name}", msg, false, CancellationToken.None);
        };

        _list.ItemChecked += (_, e) =>
        {
            // ListView tự phát ItemChecked (bỏ chọn rồi chọn lại) khi tạo handle — chỉ xử lý khi người dùng
            // đang thao tác trên danh sách (click/Space làm danh sách có focus) và trạng thái thực sự đổi.
            if (_suppressCheck || !_list.Focused || e.Item.Tag is not Job job || job.Enabled == e.Item.Checked) return;
            job.Enabled = e.Item.Checked;
            _scheduler.Recalculate(job);
            Log.Info($"{(job.Enabled ? "Bật" : "Tắt")} công việc \"{job.Name}\".");
            SaveJobs();
            UpdateItem(e.Item);
        };
        _list.DoubleClick += (_, _) => EditSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.Enter) EditSelected();
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
        _suppressCheck = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var job in _jobs)
            {
                var item = new ListViewItem(job.Name) { Tag = job, Checked = job.Enabled };
                for (int i = 1; i < _list.Columns.Count; i++) item.SubItems.Add("");
                UpdateItem(item);
                _list.Items.Add(item);
                if (job.Id == selectedId) item.Selected = true;
            }
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
        item.Text = job.Name;
        item.SubItems[1].Text = job.Schedule.Describe();
        item.SubItems[2].Text = job.NextRun?.ToString("HH:mm:ss  dd/MM/yyyy") ?? "—";
        item.SubItems[3].Text = Countdown(job);
        item.SubItems[4].Text = job.LastRun?.ToString("HH:mm:ss  dd/MM/yyyy") ?? "—";
        item.SubItems[5].Text = job.LastResult ?? "";
        item.ForeColor = job.Enabled ? SystemColors.WindowText : SystemColors.GrayText;
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

    // ───────────────────────────── Thao tác ─────────────────────────────

    private void AddJob()
    {
        using var editor = new JobEditorForm(new Job(), _runner, isNew: true);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        var job = editor.Job;
        _jobs.Add(job);
        _scheduler.Recalculate(job);
        SaveJobs();
        RefreshList();
        Log.Info($"Đã thêm công việc \"{job.Name}\".");
    }

    private void EditSelected()
    {
        var job = SelectedJob();
        if (job == null) return;
        using var editor = new JobEditorForm(job.Clone(), _runner, isNew: false);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        int index = _jobs.FindIndex(j => j.Id == job.Id);
        if (index < 0) return;
        var updated = editor.Job;
        _jobs[index] = updated;
        _scheduler.Recalculate(updated);
        SaveJobs();
        RefreshList();
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
        _scheduler.Recalculate(copy);
        SaveJobs();
        RefreshList();
    }

    private void DeleteSelected()
    {
        var job = SelectedJob();
        if (job == null) return;
        if (MessageBox.Show(this, $"Xóa công việc \"{job.Name}\"?", "Xác nhận xóa",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _jobs.Remove(job);
        SaveJobs();
        RefreshList();
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
        if (job.Steps.Count(s => s.Enabled) == 0)
        {
            MessageBox.Show(this, "Công việc chưa có bước nào được bật.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _btnStop.Enabled = true;
        _ = _runner.EnqueueAsync(job, "chạy thủ công");
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
            SaveJobs();
            RefreshList();
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
            Log.Info($"Đã xuất {_jobs.Count} công việc ra {dlg.FileName}.");
        }
        catch (Exception ex) { ShowError("Không xuất được file: " + ex.Message); }
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
        _uiTimer.Stop();
        _scheduler.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
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
        if (m.Msg == Win32.WM_HOTKEY && m.WParam == StopHotkeyId)
            _runner.StopAll();
        base.WndProc(ref m);
    }

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
            _tray.ShowBalloonTip(5000, title, text, isError ? ToolTipIcon.Error : ToolTipIcon.Info)));
    }

    public IDisposable ClearScreenForAutomation()
    {
        IDisposable? restore = null;
        Invoke(new MethodInvoker(() => restore = ScreenHelper.MoveAppWindowsAway()));
        return new InvokeOnDispose(this, restore!);
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
