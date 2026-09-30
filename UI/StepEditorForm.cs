using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Vision;

namespace ScheduleApp.UI;

/// <summary>Soạn một bước trong flow. Các trường hiển thị thay đổi theo loại thao tác.</summary>
internal sealed class StepEditorForm : BaseForm
{
    private static readonly StepType[] Types = Enum.GetValues<StepType>();

    private readonly ActionStep _step;

    private readonly ComboBox _cboType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _lblTarget = Caption("");
    private readonly ComboBox _cboTarget = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill, MaxDropDownItems = 20 };
    private readonly Button _btnTargetAction = new() { AutoSize = true };
    private readonly Label _lblArgs = Caption("Tham số:");
    private readonly TextBox _txtArgs = new() { Dock = DockStyle.Fill };
    private readonly Label _lblText = Caption("");
    private readonly TextBox _txtText = new() { Dock = DockStyle.Fill, Multiline = true, Height = 90, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly CheckBox _chkRelative = new() { Text = "Tọa độ tương đối theo cửa sổ (click đúng kể cả khi cửa sổ bị di chuyển)", AutoSize = true };
    private readonly Label _lblXY = Caption("Tọa độ X, Y:");
    private readonly FlowLayoutPanel _pnlXY = Row();
    private readonly NumericUpDown _numX = Num(-20_000, 20_000, 80);
    private readonly NumericUpDown _numY = Num(-20_000, 20_000, 80);
    private readonly Button _btnCapture = new() { Text = "◎ Lấy tọa độ (3 giây)", AutoSize = true };
    private readonly Button _btnPreview = new() { Text = "Xem vị trí", AutoSize = true };
    private readonly Label _lblButton = Caption("Nút chuột:");
    private readonly FlowLayoutPanel _pnlButton = Row();
    private readonly ComboBox _cboButton = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly CheckBox _chkDouble = new() { Text = "Double-click", AutoSize = true, Margin = new Padding(12, 5, 3, 3) };
    private readonly Label _lblDelay = Caption("");
    private readonly NumericUpDown _numDelay = Num(0, 86_400_000, 120);
    private readonly CheckBox _chkWaitUser = new() { Text = "Tạm dừng flow cho tới khi bấm \"Đã hiểu\"", AutoSize = true };
    private readonly CheckBox _chkForce = new() { Text = "Buộc đóng (kill tiến trình, không hỏi lưu)", AutoSize = true };
    private readonly NumericUpDown _numAfter = Num(0, 600_000, 120);
    private readonly CheckBox _chkEnabled = new() { Text = "Bật bước này", AutoSize = true };
    private readonly Label _lblHint = new() { AutoSize = true, ForeColor = UiText.Muted, MaximumSize = new Size(640, 0), Margin = new Padding(3, 10, 3, 3) };
    private readonly Label _lblCaptureInfo = new() { AutoSize = true, ForeColor = Color.FromArgb(0, 100, 0), Margin = new Padding(3, 7, 3, 3), MaximumSize = new Size(480, 0) };

    private readonly Label _lblTypeMode = Caption("Cách nhập:");
    private readonly ComboBox _cboTypeMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };

    // Nhận dạng màn hình
    private readonly Label _lblImage = Caption("Hình mẫu:");
    private readonly FlowLayoutPanel _pnlImage = Row();
    private readonly PictureBox _picImage = new() { Size = new Size(240, 96), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
    private readonly Button _btnSnip = new() { Text = "✂ Chụp hình mẫu", AutoSize = true, Margin = new Padding(10, 2, 3, 2) };
    private readonly Label _lblConfidence = Caption("Độ khớp tối thiểu (%):");
    private readonly NumericUpDown _numConfidence = Num(50, 100, 80);
    private readonly Label _lblMatchIndex = Caption("Lần xuất hiện thứ:");
    private readonly NumericUpDown _numMatchIndex = Num(1, 50, 80);
    private readonly Label _lblTest = Caption("Kiểm tra:");
    private readonly Button _btnTestFind = new() { Text = "Thử tìm trên màn hình", AutoSize = true };
    private string _imageData = "";
    private int _imageWidth, _imageHeight;

    private bool _loading;

    public ActionStep Step => _step;

    public StepEditorForm(ActionStep step)
    {
        _step = step;

        SuspendLayout();
        Text = "Bước thao tác";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        BuildUi();
        ResumeLayout(true);

        LoadStep();
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 8, 3) };

    private static FlowLayoutPanel Row() =>
        new() { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };

    private static NumericUpDown Num(int min, int max, int width) =>
        new() { Minimum = min, Maximum = max, Width = width, ThousandsSeparator = true };

    private void BuildUi()
    {
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 480));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var t in Types) _cboType.Items.Add(ActionStep.TypeNames[t]);
        _cboButton.Items.AddRange(["Trái", "Phải", "Giữa"]);

        int row = 0;
        void AddRow(Control? label, Control? main, Control? extra = null)
        {
            if (label != null) grid.Controls.Add(label, 0, row);
            if (main != null) grid.Controls.Add(main, 1, row);
            if (extra != null) grid.Controls.Add(extra, 2, row);
            row++;
        }

        AddRow(Caption("Loại thao tác:"), _cboType);
        AddRow(_lblTarget, _cboTarget, _btnTargetAction);
        AddRow(_lblArgs, _txtArgs);
        AddRow(_lblText, _txtText);
        _cboTypeMode.Items.AddRange(["Tự động (dán nếu có UniKey/EVKey đang chạy)", "Gõ từng phím", "Dán qua clipboard (Ctrl+V)"]);
        AddRow(_lblTypeMode, _cboTypeMode);

        _pnlImage.Controls.AddRange([_picImage, _btnSnip]);
        AddRow(_lblImage, _pnlImage);
        grid.SetColumnSpan(_pnlImage, 2);
        AddRow(_lblConfidence, _numConfidence);
        AddRow(_lblMatchIndex, _numMatchIndex);
        AddRow(null, _chkRelative);

        _pnlXY.Controls.AddRange([new Label { Text = "X", AutoSize = true, Margin = new Padding(0, 6, 2, 0) }, _numX,
            new Label { Text = "Y", AutoSize = true, Margin = new Padding(10, 6, 2, 0) }, _numY, _btnCapture, _btnPreview]);
        _btnCapture.Margin = new Padding(12, 2, 3, 2);
        AddRow(_lblXY, _pnlXY);
        grid.SetColumnSpan(_pnlXY, 2);
        AddRow(_lblTest, _btnTestFind);
        AddRow(null, _lblCaptureInfo);

        _pnlButton.Controls.AddRange([_cboButton, _chkDouble]);
        AddRow(_lblButton, _pnlButton);
        AddRow(_lblDelay, _numDelay);
        AddRow(null, _chkWaitUser);
        AddRow(null, _chkForce);
        AddRow(Caption("Nghỉ sau bước (ms):"), _numAfter);
        AddRow(null, _chkEnabled);
        AddRow(null, _lblHint);
        grid.SetColumnSpan(_lblHint, 2);

        var bottom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var btnCancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        var btnOk = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 0) };
        btnOk.Click += (_, _) => Save();
        bottom.Controls.AddRange([btnCancel, btnOk]);
        AcceptButton = null; // Enter dùng để xuống dòng trong ô văn bản
        CancelButton = btnCancel;

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(grid);
        root.Controls.Add(bottom);
        Controls.Add(root);

        _cboType.SelectedIndexChanged += (_, _) => OnTypeChanged();
        _btnTargetAction.Click += (_, _) => OnTargetAction();
        _cboTarget.SelectionChangeCommitted += (_, _) =>
        {
            if (_cboTarget.SelectedItem is WindowInfo w)
                BeginInvoke(new MethodInvoker(() => _cboTarget.Text = w.Title));
        };
        _chkRelative.CheckedChanged += (_, _) => _cboTarget.Enabled = _btnTargetAction.Enabled = _chkRelative.Checked || CurrentType != StepType.MouseClick;
        _btnCapture.Click += async (_, _) => await CaptureAsync();
        _btnPreview.Click += (_, _) => PreviewPoint();
        _btnSnip.Click += async (_, _) => await SnipAsync();
        _btnTestFind.Click += async (_, _) => await TestFindAsync();
    }

    private StepType CurrentType => Types[Math.Max(0, _cboType.SelectedIndex)];

    // ───────────────────────────── Nạp / lưu ─────────────────────────────

    private void LoadStep()
    {
        _loading = true;
        _cboType.SelectedIndex = Array.IndexOf(Types, _step.Type);
        _cboTarget.Text = _step.Target;
        _txtArgs.Text = _step.Arguments;
        _txtText.Text = _step.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _numX.Value = Math.Clamp(_step.X, -20_000, 20_000);
        _numY.Value = Math.Clamp(_step.Y, -20_000, 20_000);
        _cboButton.SelectedIndex = (int)_step.Button;
        _chkDouble.Checked = _step.DoubleClick;
        _numDelay.Value = Math.Clamp(_step.DelayMs, 0, 86_400_000);
        _chkWaitUser.Checked = _step.WaitForUser;
        _chkForce.Checked = _step.Force;
        _numAfter.Value = Math.Clamp(_step.DelayAfterMs, 0, 600_000);
        _chkEnabled.Checked = _step.Enabled;
        _chkRelative.Checked = !string.IsNullOrWhiteSpace(_step.Target);
        _imageData = _step.ImageData;
        _imageWidth = _step.ImageWidth;
        _imageHeight = _step.ImageHeight;
        UpdateImagePreview();
        _numConfidence.Value = Math.Clamp(_step.Confidence, 50, 100);
        _numMatchIndex.Value = Math.Clamp(_step.MatchIndex, 1, 50);
        _cboTypeMode.SelectedIndex = (int)_step.TypeMode;
        _loading = false;
        OnTypeChanged();
    }

    private void Save()
    {
        var type = CurrentType;
        var target = _cboTarget.Text.Trim();
        bool needsTarget = type is StepType.LaunchApp or StepType.WaitForWindow or StepType.FocusWindow or StepType.RunCommand or StepType.CloseApp
                           || (type == StepType.MouseClick && _chkRelative.Checked);
        if (needsTarget && target.Length == 0)
        {
            Warn($"Hãy nhập \"{_lblTarget.Text.TrimEnd(':')}\".");
            _cboTarget.Focus();
            return;
        }
        if (type is StepType.TypeText or StepType.KeyPress && _txtText.Text.Length == 0)
        {
            Warn(type == StepType.TypeText ? "Hãy nhập văn bản cần gõ." : "Hãy nhập phím cần nhấn.");
            _txtText.Focus();
            return;
        }
        if (type == StepType.KeyPress)
        {
            try { InputSimulator.Validate(_txtText.Text); }
            catch (FormatException ex) { Warn(ex.Message); _txtText.Focus(); return; }
        }
        bool isImage = type is StepType.ClickImage or StepType.WaitForImage;
        bool isText = type is StepType.ClickText or StepType.WaitForText;
        if (isImage && string.IsNullOrEmpty(_imageData))
        {
            Warn("Hãy bấm \"Chụp hình mẫu\" để chọn vùng hình cần tìm.");
            return;
        }
        if (isText && _txtText.Text.Trim().Length == 0)
        {
            Warn("Hãy nhập chữ cần tìm trên màn hình.");
            _txtText.Focus();
            return;
        }

        _step.Type = type;
        _step.Target = type == StepType.MouseClick && !_chkRelative.Checked ? "" : (type == StepType.Wait ? "" : target);
        _step.Arguments = type == StepType.LaunchApp ? _txtArgs.Text.Trim() : "";
        _step.Text = type is StepType.Reminder or StepType.TypeText or StepType.KeyPress ? _txtText.Text.Replace("\r\n", "\n")
                   : isText ? _txtText.Text.Trim()
                   : "";
        _step.ImageData = isImage ? _imageData : "";
        _step.ImageWidth = isImage ? _imageWidth : 0;
        _step.ImageHeight = isImage ? _imageHeight : 0;
        _step.Confidence = (int)_numConfidence.Value;
        _step.MatchIndex = (int)_numMatchIndex.Value;
        _step.TypeMode = (TypeMode)Math.Max(0, _cboTypeMode.SelectedIndex);
        _step.X = (int)_numX.Value;
        _step.Y = (int)_numY.Value;
        _step.Button = (MouseButtonKind)Math.Max(0, _cboButton.SelectedIndex);
        _step.DoubleClick = _chkDouble.Checked;
        _step.DelayMs = (int)_numDelay.Value;
        _step.WaitForUser = _chkWaitUser.Checked;
        _step.Force = _chkForce.Checked;
        _step.DelayAfterMs = (int)_numAfter.Value;
        _step.Enabled = _chkEnabled.Checked;

        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // ───────────────────────────── Hiển thị theo loại ─────────────────────────────

    private void OnTypeChanged()
    {
        var t = CurrentType;
        bool image = t is StepType.ClickImage or StepType.WaitForImage;
        bool text = t is StepType.ClickText or StepType.WaitForText;
        bool vision = image || text;
        bool visionClick = t is StepType.ClickImage or StepType.ClickText;
        bool windowTarget = vision || t is StepType.WaitForWindow or StepType.FocusWindow or StepType.MouseClick or StepType.TypeText or StepType.KeyPress;

        SuspendLayout();

        SetVisible(t != StepType.Wait, _lblTarget, _cboTarget);
        _lblTarget.Text = t switch
        {
            StepType.LaunchApp => "Ứng dụng / file / URL:",
            StepType.Reminder => "Tiêu đề:",
            StepType.MouseClick => "Cửa sổ chứa:",
            StepType.TypeText or StepType.KeyPress => "Cửa sổ đích (tùy chọn):",
            StepType.RunCommand => "Lệnh:",
            StepType.CloseApp => "Tên tiến trình:",
            _ when vision => "Chỉ tìm trong cửa sổ\n(trống = cả màn hình):",
            _ => "Cửa sổ (tiêu đề/tiến trình):"
        };

        _btnTargetAction.Visible = t is StepType.LaunchApp or StepType.CloseApp || windowTarget;
        _btnTargetAction.Text = t == StepType.LaunchApp ? "Duyệt…" : "↻ Làm mới";
        _cboTarget.DropDownStyle = ComboBoxStyle.DropDown;
        FillTargetList();

        SetVisible(t == StepType.LaunchApp, _lblArgs, _txtArgs);

        SetVisible(text || t is StepType.Reminder or StepType.TypeText or StepType.KeyPress, _lblText, _txtText);
        _lblText.Text = t switch { StepType.Reminder => "Nội dung:", StepType.KeyPress => "Phím:", _ when text => "Chữ cần tìm:", _ => "Văn bản:" };
        bool singleLine = t == StepType.KeyPress || text;
        _txtText.Multiline = !singleLine;
        _txtText.Height = singleLine ? _txtArgs.Height : LogicalToDeviceUnits(90);

        SetVisible(t == StepType.TypeText, _lblTypeMode, _cboTypeMode);
        SetVisible(image, _lblImage, _pnlImage, _lblConfidence, _numConfidence);
        SetVisible(text, _lblMatchIndex, _numMatchIndex);

        bool click = t == StepType.MouseClick;
        SetVisible(click, _chkRelative, _btnCapture, _btnPreview);
        SetVisible(click || visionClick, _lblXY, _pnlXY, _lblButton, _pnlButton);
        SetVisible(click || vision, _lblCaptureInfo);
        SetVisible(vision, _lblTest, _btnTestFind);
        _lblXY.Text = click ? "Tọa độ X, Y:" : "Lệch khỏi tâm X, Y:";
        _cboTarget.Enabled = _btnTargetAction.Enabled = !click || _chkRelative.Checked;

        SetVisible(vision || t is StepType.Wait or StepType.WaitForWindow or StepType.FocusWindow or StepType.RunCommand, _lblDelay, _numDelay);
        _lblDelay.Text = t == StepType.Wait ? "Thời gian chờ (ms):" : "Timeout (ms):";
        if (!_loading) _numDelay.Value = ActionStep.CreateDefault(t).DelayMs;

        SetVisible(t == StepType.Reminder, _chkWaitUser);
        SetVisible(t == StepType.CloseApp, _chkForce);

        _lblHint.Text = "Gợi ý: " + t switch
        {
            StepType.LaunchApp => "Nhập đường dẫn .exe, file tài liệu hoặc URL (vd: notepad.exe, C:\\Tools\\app.exe, https://google.com). " +
                                  "Nên thêm bước \"Chờ cửa sổ xuất hiện\" ngay sau bước này.",
            StepType.Reminder => "Hiện cửa sổ nhắc nhở ở góc phải màn hình kèm âm báo. Bật \"Tạm dừng flow\" để flow chờ bạn xác nhận rồi mới chạy tiếp.",
            StepType.Wait => "Tạm dừng flow một khoảng thời gian (1000 ms = 1 giây).",
            StepType.WaitForWindow => "Nhập một phần tiêu đề cửa sổ hoặc tên tiến trình (vd: Notepad, chrome, EXCEL), hoặc chọn từ danh sách cửa sổ đang mở. " +
                                      "Bước lỗi nếu hết timeout mà chưa thấy cửa sổ.",
            StepType.FocusWindow => "Đưa cửa sổ lên trên cùng và nhận bàn phím (khôi phục nếu đang thu nhỏ).",
            StepType.MouseClick => "Bấm \"Lấy tọa độ\" rồi di chuột tới vị trí cần click trong 3 giây. Khi bật tọa độ tương đối, " +
                                   "cửa sổ nằm dưới con trỏ sẽ được tự điền — có thể rút gọn tiêu đề (vd: \"Notepad\") để khớp ổn định hơn.",
            StepType.TypeText => "Hỗ trợ tiếng Việt có dấu và emoji. Xuống dòng = phím Enter. Nếu nhập cửa sổ đích, cửa sổ đó sẽ được kích hoạt trước khi gõ. " +
                                 "Bộ gõ UniKey/EVKey có thể làm sai chữ khi gõ từng phím — chế độ Tự động sẽ dán qua clipboard (clipboard cũ được khôi phục).",
            StepType.KeyPress => "Ví dụ: Enter · Ctrl+S · Alt+F4 · Win+R · Ctrl+Shift+Esc · Tab*3 (nhấn 3 lần). " +
                                 "Nhiều tổ hợp cách nhau dấu phẩy: Ctrl+A, Delete.",
            StepType.RunCommand => "Lệnh chạy ẩn bằng cmd.exe (vd: robocopy D:\\src E:\\bak /MIR, powershell -File C:\\script.ps1). " +
                                   "Timeout = 0 nghĩa là không chờ lệnh chạy xong. Mã thoát khác 0 được tính là lỗi.",
            StepType.CloseApp => "Tên tiến trình không cần .exe (vd: notepad, EXCEL, chrome). Mặc định yêu cầu đóng lịch sự như bấm nút X.",
            StepType.ClickImage or StepType.WaitForImage =>
                "Bấm \"Chụp hình mẫu\" rồi kéo chọn đúng phần cần tìm (vd: nút Lưu) — nên chọn vùng đặc trưng, tránh chữ/số hay thay đổi. " +
                "Hình được tìm ở mọi vị trí trên màn hình nên không lo cửa sổ bị di chuyển. Cần giữ nguyên độ phân giải và mức zoom màn hình như lúc chụp. " +
                "Giảm \"Độ khớp\" nếu không tìm thấy, tăng nếu click nhầm chỗ.",
            StepType.ClickText or StepType.WaitForText =>
                "Tìm chữ hiển thị trên màn hình bằng Windows OCR (vd: Đăng nhập, Lưu, Submit). So sánh không phân biệt hoa thường và bỏ qua dấu tiếng Việt. " +
                "Giới hạn vùng tìm trong một cửa sổ giúp nhanh và chính xác hơn. " +
                (Vision.ScreenOcr.VietnameseAvailable ? "" : "⚠ Máy chưa cài gói OCR tiếng Việt — đang dùng OCR tiếng Anh (vẫn tìm được chữ không dấu)."),
            _ => ""
        };

        ResumeLayout(true);
    }

    private static void SetVisible(bool visible, params Control[] controls)
    {
        foreach (var c in controls) c.Visible = visible;
    }

    private void FillTargetList()
    {
        var text = _cboTarget.Text;
        _cboTarget.BeginUpdate();
        _cboTarget.Items.Clear();
        switch (CurrentType)
        {
            case StepType.WaitForWindow or StepType.FocusWindow or StepType.MouseClick or StepType.TypeText or StepType.KeyPress
                or StepType.ClickImage or StepType.WaitForImage or StepType.ClickText or StepType.WaitForText:
                foreach (var w in WindowHelper.GetOpenWindows().OrderBy(w => w.ProcessName)) _cboTarget.Items.Add(w);
                break;
            case StepType.CloseApp:
                foreach (var p in WindowHelper.GetOpenWindows().Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order())
                    _cboTarget.Items.Add(p);
                break;
        }
        _cboTarget.EndUpdate();
        _cboTarget.Text = text;
    }

    private void OnTargetAction()
    {
        if (CurrentType != StepType.LaunchApp)
        {
            FillTargetList();
            _cboTarget.DroppedDown = _cboTarget.Items.Count > 0;
            return;
        }
        using var dlg = new OpenFileDialog
        {
            Title = "Chọn ứng dụng hoặc file",
            Filter = "Ứng dụng (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|Tất cả file (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _cboTarget.Text = dlg.FileName;
    }

    // ───────────────────────────── Tọa độ chuột ─────────────────────────────

    private async Task CaptureAsync()
    {
        _btnCapture.Enabled = false;
        try
        {
            var (point, window) = await ScreenHelper.CaptureCursorAsync(3);
            if (_chkRelative.Checked && window != IntPtr.Zero)
            {
                // Giữ tiêu đề người dùng đã nhập nếu nó vẫn khớp đúng cửa sổ này.
                if (WindowHelper.Find(_cboTarget.Text) != window) _cboTarget.Text = WindowHelper.GetTitle(window);
                var rect = WindowHelper.GetRect(window);
                _numX.Value = Math.Clamp(point.X - rect.Left, -20_000, 20_000);
                _numY.Value = Math.Clamp(point.Y - rect.Top, -20_000, 20_000);
                _lblCaptureInfo.Text = $"✔ Màn hình ({point.X}, {point.Y}) → tương đối ({_numX.Value}, {_numY.Value}) trong \"{Shorten(_cboTarget.Text)}\"";
            }
            else
            {
                _numX.Value = point.X;
                _numY.Value = point.Y;
                _lblCaptureInfo.Text = $"✔ Tọa độ màn hình ({point.X}, {point.Y})";
            }
        }
        finally
        {
            _btnCapture.Enabled = true;
            Activate();
        }
    }

    private void PreviewPoint()
    {
        int x = (int)_numX.Value, y = (int)_numY.Value;
        if (_chkRelative.Checked)
        {
            var h = WindowHelper.Find(_cboTarget.Text);
            if (h == IntPtr.Zero)
            {
                _lblCaptureInfo.Text = $"✖ Không tìm thấy cửa sổ \"{Shorten(_cboTarget.Text)}\"";
                return;
            }
            var rect = WindowHelper.GetRect(h);
            x += rect.Left;
            y += rect.Top;
        }
        Win32.SetCursorPos(x, y);
        _lblCaptureInfo.Text = $"Con trỏ đã được đưa tới ({x}, {y}) trên màn hình.";
    }

    private static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;

    // ───────────────────────────── Nhận dạng màn hình ─────────────────────────────

    private void UpdateImagePreview()
    {
        var old = _picImage.Image;
        _picImage.Image = string.IsNullOrEmpty(_imageData) ? null : ScreenCapture.FromBase64Png(_imageData);
        old?.Dispose();
    }

    private async Task SnipAsync()
    {
        _btnSnip.Enabled = false;
        try
        {
            using var bmp = await RegionSelectorForm.SelectAsync(this);
            if (bmp == null) return;
            _imageData = ScreenCapture.ToBase64Png(bmp);
            _imageWidth = bmp.Width;
            _imageHeight = bmp.Height;
            UpdateImagePreview();
            if (ImageMatcher.IsLowDetail(bmp))
                ShowInfo($"⚠ Hình mẫu {bmp.Width}×{bmp.Height} gần như một màu nên dễ khớp nhầm chỗ khác — nên chụp vùng có chữ/icon đặc trưng.", false);
            else
                ShowInfo($"✔ Đã chụp hình mẫu {bmp.Width}×{bmp.Height} px. Bấm \"Thử tìm\" để kiểm tra.", true);
        }
        finally
        {
            _btnSnip.Enabled = true;
            Activate();
        }
    }

    /// <summary>Tìm thử ngay với cấu hình đang nhập: khoanh đỏ vị trí tìm được và đưa con trỏ tới điểm sẽ click.</summary>
    private async Task TestFindAsync()
    {
        var type = CurrentType;
        var probe = new ActionStep
        {
            Type = type,
            Text = _txtText.Text.Trim(),
            ImageData = _imageData,
            Confidence = (int)_numConfidence.Value,
            MatchIndex = (int)_numMatchIndex.Value,
            X = (int)_numX.Value,
            Y = (int)_numY.Value
        };
        if (probe.IsImageStep && probe.ImageData.Length == 0) { Warn("Chưa chụp hình mẫu."); return; }
        if (probe.IsTextStep && probe.Text.Length == 0) { Warn("Chưa nhập chữ cần tìm."); return; }

        _btnTestFind.Enabled = false;
        ShowInfo("Đang tìm…", true);
        try
        {
            LocateResult result;
            using (ScreenHelper.MoveAppWindowsAway())
            {
                await Task.Delay(250);
                var window = IntPtr.Zero;
                var target = _cboTarget.Text.Trim();
                if (target.Length > 0)
                {
                    window = WindowHelper.Find(target);
                    if (window == IntPtr.Zero)
                    {
                        ShowInfo($"✖ Không tìm thấy cửa sổ \"{Shorten(target)}\".", false);
                        return;
                    }
                    WindowHelper.Focus(window);
                    await Task.Delay(200);
                }

                using var template = ScreenLocator.LoadTemplate(probe);
                result = await ScreenLocator.LocateOnceAsync(probe, ScreenLocator.AreaOf(window), template);
                if (result.Bounds.Width > 0)
                {
                    HighlightForm.Flash(result.Bounds);
                    if (result.Found && probe.Type is StepType.ClickImage or StepType.ClickText)
                        Win32.SetCursorPos(result.ClickPoint.X, result.ClickPoint.Y);
                    await Task.Delay(1500); // để kịp nhìn khung đỏ trước khi cửa sổ quay lại
                }
            }

            ShowInfo(result.Found
                ? $"✔ Tìm thấy tại ({result.Bounds.X}, {result.Bounds.Y}) — {result.Detail}"
                : $"✖ Không đạt — {result.Detail}" + (result.Bounds.Width > 0 ? " (khung đỏ là vị trí giống nhất)" : ""), result.Found);
        }
        catch (Exception ex)
        {
            ShowInfo("✖ " + ex.Message, false);
        }
        finally
        {
            _btnTestFind.Enabled = true;
            Activate();
        }
    }

    private void ShowInfo(string text, bool ok)
    {
        _lblCaptureInfo.Text = text;
        _lblCaptureInfo.ForeColor = ok ? Color.FromArgb(0, 100, 0) : Color.FromArgb(180, 40, 20);
    }
}
