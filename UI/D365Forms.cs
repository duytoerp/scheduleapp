using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Đối tượng chọn trong form D365 đang mở.</summary>
internal enum D365PickKind
{
    Field,
    Fields,
    Tab,
    Command
}

/// <summary>
/// Chọn field / tab / nút từ form Dynamics 365 đang mở trong trình duyệt điều khiển — thấy nhãn tiếng Việt, kiểu field,
/// giá trị hiện tại thay vì phải nhớ tên logic. Chế độ <see cref="D365PickKind.Fields"/> cho tick nhiều field để tạo bước Kiểm tra.
/// </summary>
internal sealed class D365PickerForm : BaseForm
{
    private readonly D365PickKind _kind;
    private readonly string _tab;
    private readonly TextBox _search = new() { Width = 320, PlaceholderText = "Tìm theo nhãn, tên logic, giá trị…" };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        BorderStyle = BorderStyle.FixedSingle,
        SmallImageList = new ImageList { ImageSize = new Size(1, 26) }
    };
    private readonly Label _info = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 8, 3, 3), MaximumSize = new Size(820, 0) };
    private readonly CheckBox _onlyFilled = new() { Text = "Chỉ field có giá trị", AutoSize = true, Margin = new Padding(12, 5, 3, 3) };
    private readonly Button _ok = new() { Text = "Chọn", AutoSize = true, MinimumSize = new Size(110, 0), Enabled = false };
    private D365Client.FormInfo? _form;

    public D365Client.FieldInfo? SelectedField { get; private set; }
    public List<D365Client.FieldInfo> CheckedFields { get; } = [];
    public string SelectedText { get; private set; } = "";
    public D365Client.FormInfo? Form => _form;

    public D365PickerForm(D365PickKind kind, string tab = "")
    {
        _kind = kind;
        _tab = tab;
        Text = kind switch
        {
            D365PickKind.Fields => "Tạo bước Kiểm tra từ form Dynamics 365",
            D365PickKind.Tab => "Chọn tab của form",
            D365PickKind.Command => "Chọn nút trên thanh lệnh",
            _ => "Chọn field từ form Dynamics 365"
        };
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 600);
        MinimumSize = new Size(640, 400);
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(12);
        BackColor = Theme.Background;

        _list.CheckBoxes = kind == D365PickKind.Fields;
        if (kind is D365PickKind.Field or D365PickKind.Fields)
        {
            _list.Columns.Add("Nhãn trên form", 220);
            _list.Columns.Add("Tên logic", 170);
            _list.Columns.Add("Kiểu", 110);
            _list.Columns.Add("Giá trị hiện tại", 220);
            _list.Columns.Add("Trạng thái", 130);
        }
        else
        {
            _list.Columns.Add(kind == D365PickKind.Tab ? "Nhãn tab" : "Nút", 360);
            if (kind == D365PickKind.Tab) _list.Columns.Add("Tên (name)", 260);
        }

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        var reload = new Button { Text = "↻ Đọc lại form", AutoSize = true, Margin = new Padding(8, 0, 3, 3) };
        reload.Click += async (_, _) => await LoadAsync();
        top.Controls.Add(_search);
        if (kind is D365PickKind.Field or D365PickKind.Fields) top.Controls.Add(_onlyFilled);
        top.Controls.Add(reload);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var cancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        if (kind == D365PickKind.Fields)
        {
            _ok.Text = "Tạo bước Kiểm tra";
            var all = new Button { Text = "Tick field có giá trị", AutoSize = true };
            all.Click += (_, _) =>
            {
                foreach (ListViewItem i in _list.Items) i.Checked = i.Tag is D365Client.FieldInfo { Value.Length: > 0 };
            };
            bottom.Controls.AddRange([cancel, _ok, all]);
        }
        else
        {
            bottom.Controls.AddRange([cancel, _ok]);
        }
        bottom.Controls.Add(_info);
        _ok.Click += (_, _) => Accept();
        AcceptButton = _ok;
        CancelButton = cancel;

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bottom);

        _search.TextChanged += (_, _) => Fill();
        _onlyFilled.CheckedChanged += (_, _) => Fill();
        _list.SelectedIndexChanged += (_, _) => UpdateOk();
        _list.ItemChecked += (_, _) => UpdateOk();
        _list.DoubleClick += (_, _) => { if (_kind != D365PickKind.Fields) Accept(); };
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _info.Text = "Đang đọc form đang mở trong trình duyệt…";
        _list.Items.Clear();
        UseWaitCursor = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _form = await Task.Run(() => D365Client.DescribeFormAsync(_tab, cts.Token));
            _info.Text = $"Form: {_form.Entity}{(_form.IsNew ? " (tạo mới)" : " · " + _form.Id)} — {_form.Fields.Count} field, {_form.Tabs.Count} tab.";
            Fill();
            _search.Focus();
        }
        catch (Exception ex)
        {
            _info.Text = "✖ " + ex.Message;
            _info.ForeColor = Theme.Danger;
            MessageBox.Show(this,
                "Không đọc được form Dynamics 365:\n" + ex.Message + "\n\nHãy mở trình duyệt điều khiển (bước \"Trình duyệt → Mở trình duyệt ở chế độ " +
                "điều khiển\", hoặc nút \"Mở trình duyệt\" trong cửa sổ ghi thao tác D365) và mở một form bản ghi trước.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void Fill()
    {
        if (_form == null) return;
        var q = Normalize(_search.Text);
        bool Match(params string[] parts) => q.Length == 0 || parts.Any(p => Normalize(p).Contains(q, StringComparison.Ordinal));
        var checkedNames = _list.CheckedItems.Cast<ListViewItem>().Select(i => (i.Tag as D365Client.FieldInfo)?.Name).ToHashSet();

        _list.BeginUpdate();
        _list.Items.Clear();
        switch (_kind)
        {
            case D365PickKind.Field or D365PickKind.Fields:
                foreach (var f in _form.Fields)
                {
                    if (_onlyFilled.Checked && f.Value.Length == 0) continue;
                    if (!Match(f.Label, f.Name, f.Value)) continue;
                    var state = new List<string>();
                    if (f.Required == "required") state.Add("bắt buộc");
                    if (f.Disabled) state.Add("khóa");
                    if (!f.Visible) state.Add("ẩn");
                    var item = new ListViewItem(f.Label.Length > 0 ? f.Label : "(không có trên form)") { Tag = f, Checked = checkedNames.Contains(f.Name) };
                    item.SubItems.AddRange([f.Name, TypeName(f.Type), f.Value, string.Join(", ", state)]);
                    if (f.Label.Length == 0 || !f.Visible) item.ForeColor = Theme.Muted;
                    _list.Items.Add(item);
                }
                break;
            case D365PickKind.Tab:
                foreach (var (name, label) in _form.Tabs)
                    if (Match(name, label)) _list.Items.Add(new ListViewItem([label, name]) { Tag = label.Length > 0 ? label : name });
                break;
            case D365PickKind.Command:
                foreach (var c in _form.Commands)
                    if (Match(c)) _list.Items.Add(new ListViewItem(c) { Tag = c });
                if (_form.Commands.Count == 0) _info.Text = "Không thấy nút nào trên thanh lệnh — nút trong menu \"…\" có thể gõ tay theo nhãn.";
                break;
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0 && _kind != D365PickKind.Fields) _list.Items[0].Selected = true;
        UpdateOk();
    }

    private void UpdateOk() =>
        _ok.Enabled = _kind == D365PickKind.Fields ? _list.CheckedItems.Count > 0 : _list.SelectedItems.Count > 0;

    private void Accept()
    {
        if (_kind == D365PickKind.Fields)
        {
            CheckedFields.AddRange(_list.CheckedItems.Cast<ListViewItem>().Select(i => (D365Client.FieldInfo)i.Tag!));
            if (CheckedFields.Count == 0) return;
        }
        else
        {
            if (_list.SelectedItems.Count == 0) return;
            var tag = _list.SelectedItems[0].Tag;
            SelectedField = tag as D365Client.FieldInfo;
            SelectedText = SelectedField?.Name ?? tag as string ?? "";
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string TypeName(string type) => type switch
    {
        "string" => "Chữ",
        "memo" => "Văn bản dài",
        "lookup" => "Tra cứu (lookup)",
        "optionset" => "Lựa chọn",
        "multiselectoptionset" => "Nhiều lựa chọn",
        "boolean" => "Có / Không",
        "datetime" => "Ngày giờ",
        "integer" => "Số nguyên",
        "decimal" or "double" => "Số thập phân",
        "money" => "Tiền",
        _ => type
    };

    private static string Normalize(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(d.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
            .Replace('đ', 'd');
    }
}

/// <summary>
/// Ghi thao tác trên Dynamics 365: người dùng làm bình thường trên trình duyệt (mở form, nhập field, lưu, bấm nút…),
/// cửa sổ nhỏ luôn nổi này hiện các bước được tạo ra; "Thêm kiểm tra" chụp giá trị field hiện tại thành bước Kiểm tra.
/// </summary>
internal sealed class D365RecorderForm : BaseForm
{
    private readonly List<D365Client.RecordedEvent> _events = [];
    private readonly List<ActionStep> _prefix = [];
    private readonly List<(int AfterEvent, ActionStep Step)> _asserts = [];
    private readonly ListBox _steps = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, BackColor = Theme.Surface };
    private readonly Label _status = new() { AutoSize = true, Font = Theme.BoldFont, ForeColor = Theme.Danger, Margin = new Padding(3, 7, 12, 3) };
    private readonly Panel _launchPanel = new() { Dock = DockStyle.Top, Height = 0, Visible = false, BackColor = Theme.Surface };
    private readonly TextBox _url = new() { Width = 420, PlaceholderText = "https://<org>.crm5.dynamics.com/main.aspx?appid=…" };
    private readonly ComboBox _browser = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly TextBox _profile = new() { Width = 120, Text = "D365 Test" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 700 };
    private bool _recording;
    private bool _busy;

    /// <summary>Các bước ghi được (khi bấm "Dừng & thêm vào flow").</summary>
    public List<ActionStep> Steps { get; } = [];

    public D365RecorderForm()
    {
        Text = "Ghi thao tác Dynamics 365 — ScheduleApp";
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Size = new Size(470, 560);
        Location = new Point(area.Right - Width - 16, area.Top + 60);
        TopMost = true;
        ShowInTaskbar = true;
        MinimizeBox = true;
        MaximizeBox = false;
        BackColor = Theme.Background;
        Padding = new Padding(10);

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        header.Controls.Add(_status);

        var hint = new Label
        {
            Text = "Thao tác bình thường trên form Dynamics 365 trong cửa sổ trình duyệt điều khiển: mở bản ghi, nhập field, Lưu, bấm nút, " +
                   "chuyển tab, BPF, hộp thoại… Mỗi thao tác thành một bước bên dưới. Bấm \"✓ Thêm kiểm tra\" để ghi lại giá trị field cần kiểm tra.",
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            ForeColor = Theme.Muted,
            Padding = new Padding(3, 0, 0, 8)
        };

        _browser.Items.AddRange(["Edge", "Chrome"]);
        _browser.SelectedIndex = 0;
        var launch = new Button { Text = "Mở trình duyệt", AutoSize = true };
        launch.Click += async (_, _) => await LaunchAsync();
        var lp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(8) };
        lp.Controls.Add(new Label { Text = "Chưa có trình duyệt điều khiển. Mở app Dynamics 365:", AutoSize = true, Font = Theme.BoldFont }, 0, 0);
        lp.SetColumnSpan(lp.Controls[0], 2);
        lp.Controls.Add(new Label { Text = "URL app:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 1);
        lp.Controls.Add(_url, 1, 1);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange([_browser, new Label { Text = "Hồ sơ:", AutoSize = true, Margin = new Padding(10, 7, 3, 3) }, _profile, launch]);
        lp.Controls.Add(new Label { Text = "Trình duyệt:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 2);
        lp.Controls.Add(row, 1, 2);
        _url.Width = 330;
        _launchPanel.Controls.Add(lp);
        _launchPanel.AutoSize = true;

        var stepsCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(8) };
        stepsCard.Controls.Add(_steps);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var stop = new Button { Text = "■ Dừng && thêm vào flow", AutoSize = true };
        var cancel = new Button { Text = "Hủy", AutoSize = true };
        var assert = new Button { Text = "✓ Thêm kiểm tra…", AutoSize = true };
        var undo = new Button { Text = "↶ Bỏ bước cuối", AutoSize = true };
        stop.Click += async (_, _) => await FinishAsync(true);
        cancel.Click += async (_, _) => await FinishAsync(false);
        assert.Click += async (_, _) => await AddAssertsAsync();
        undo.Click += (_, _) => UndoLast();
        buttons.Controls.AddRange([stop, cancel, assert, undo]);
        Theme.StyleButton(stop, primary: true);

        Controls.Add(stepsCard);
        Controls.Add(_launchPanel);
        Controls.Add(hint);
        Controls.Add(header);
        Controls.Add(buttons);

        _timer.Tick += async (_, _) => await PollAsync();
        Shown += async (_, _) => await StartAsync();
        FormClosing += (_, e) =>
        {
            if (_recording && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _ = FinishAsync(false);
            }
        };
    }

    private async Task StartAsync()
    {
        SetStatus("Đang kết nối trình duyệt…", Theme.Muted);
        try
        {
            if (!await BrowserClient.IsRunningAsync(CancellationToken.None))
            {
                _launchPanel.Visible = true;
                SetStatus("Chưa mở trình duyệt điều khiển", Theme.Warning);
                return;
            }
            await BeginRecordingAsync();
        }
        catch (Exception ex)
        {
            _launchPanel.Visible = true;
            SetStatus("✖ " + ex.Message, Theme.Danger);
        }
    }

    private async Task LaunchAsync()
    {
        if (_url.Text.Trim().Length == 0) { _url.Focus(); return; }
        var step = ActionStep.CreateDefault(StepType.Browser);
        step.BrowserAction = BrowserAction.Launch;
        step.Target = (string)_browser.SelectedItem!;
        step.Arguments = _profile.Text.Trim();
        step.Text = _url.Text.Trim();
        SetStatus("Đang mở trình duyệt…", Theme.Muted);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Task.Run(() => BrowserClient.ExecuteAsync(step, (_, _) => Task.CompletedTask, cts.Token));
            _prefix.Add(step);
            _launchPanel.Visible = false;
            RefreshSteps();
            SetStatus("Đăng nhập (nếu được hỏi) rồi mở app — đang chờ Dynamics 365…", Theme.Warning);
            await BeginRecordingAsync();
        }
        catch (Exception ex)
        {
            SetStatus("✖ " + ex.Message, Theme.Danger);
        }
    }

    private async Task BeginRecordingAsync()
    {
        _recording = true;
        _timer.Start();
        await PollAsync();
    }

    /// <summary>Lấy thao tác mới; trang chưa có Xrm (đang đăng nhập / tải) hoặc vừa tải lại thì cài lại trình ghi.</summary>
    private async Task PollAsync()
    {
        if (_busy || !_recording) return;
        _busy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var events = await Task.Run(() => D365Client.DrainRecordingAsync("", cts.Token));
            if (events == null)
            {
                await Task.Run(() => D365Client.StartRecordingAsync("", cts.Token));
                events = await Task.Run(() => D365Client.DrainRecordingAsync("", cts.Token)) ?? [];
            }
            if (events.Count > 0)
            {
                _events.AddRange(events);
                RefreshSteps();
            }
            SetStatus($"● Đang ghi — {StepCount()} bước", Theme.Danger);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException
                                       or System.Net.WebSockets.WebSocketException)
        {
            // Trang đang tải / chưa đăng nhập / tab chưa phải D365 — thử lại ở lần sau.
            SetStatus("○ Chờ form Dynamics 365… (" + Short(ex.Message) + ")", Theme.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private int StepCount() => BuildSteps().Count;

    private List<ActionStep> BuildSteps()
    {
        var list = new List<ActionStep>(_prefix);
        int done = 0;
        var recorded = new List<ActionStep>();
        // Chèn bước kiểm tra vào đúng chỗ: sau các thao tác đã có lúc bấm "Thêm kiểm tra".
        foreach (var group in _asserts.GroupBy(a => a.AfterEvent).OrderBy(g => g.Key))
        {
            recorded.AddRange(D365Client.ToSteps(_events.Skip(done).Take(group.Key - done)));
            recorded.AddRange(group.Select(a => a.Step));
            done = group.Key;
        }
        recorded.AddRange(D365Client.ToSteps(_events.Skip(done)));
        list.AddRange(recorded);
        return list;
    }

    private void RefreshSteps()
    {
        var steps = BuildSteps();
        _steps.BeginUpdate();
        _steps.Items.Clear();
        for (int i = 0; i < steps.Count; i++) _steps.Items.Add($"{i + 1,2}. {steps[i].Describe()}");
        _steps.EndUpdate();
        if (_steps.Items.Count > 0) _steps.TopIndex = _steps.Items.Count - 1;
    }

    private void UndoLast()
    {
        if (_asserts.Count > 0 && _asserts[^1].AfterEvent == _events.Count) _asserts.RemoveAt(_asserts.Count - 1);
        else if (_events.Count > 0) _events.RemoveAt(_events.Count - 1);
        else if (_prefix.Count > 0) _prefix.RemoveAt(_prefix.Count - 1);
        RefreshSteps();
    }

    private async Task AddAssertsAsync()
    {
        await PollAsync(); // lấy hết thao tác trước khi chèn kiểm tra
        TopMost = false;
        using var picker = new D365PickerForm(D365PickKind.Fields);
        var ok = picker.ShowDialog(this) == DialogResult.OK;
        TopMost = true;
        if (!ok) return;
        foreach (var f in picker.CheckedFields) _asserts.Add((_events.Count, D365Client.AssertFieldStep(f)));
        RefreshSteps();
    }

    private async Task FinishAsync(bool save)
    {
        if (!_recording && !save) { DialogResult = DialogResult.Cancel; _recording = false; Close(); return; }
        _timer.Stop();
        if (_recording)
        {
            await PollAsync();
            _recording = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Task.Run(() => D365Client.StopRecordingAsync("", cts.Token));
            }
            catch (Exception ex) { Log.Warn("Không tắt được trình ghi trong trang: " + ex.Message); }
        }
        if (save) Steps.AddRange(BuildSteps());
        DialogResult = save ? DialogResult.OK : DialogResult.Cancel;
        Close();
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
