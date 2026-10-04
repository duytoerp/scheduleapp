using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;
using ScheduleApp.Services.Data;
using ScheduleApp.Services.Engine;
using ScheduleApp.Vision;

namespace ScheduleApp.UI;

/// <summary>Thông tin về flow đang soạn, giúp trình soạn bước gợi ý nhãn, biến, công việc khác.</summary>
internal sealed class StepEditorContext
{
    public IReadOnlyList<Job> Jobs { get; init; } = [];
    public Guid CurrentJobId { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<string> Variables { get; init; } = [];
    public IReadOnlyList<VariableDef> JobVariables { get; init; } = [];
    public IUserNotifier? Notifier { get; init; }
}

/// <summary>Soạn một bước trong flow. Các trường hiển thị thay đổi theo loại thao tác.</summary>
internal sealed class StepEditorForm : BaseForm
{
    private static readonly StepType[] Types = StepVisuals.Categories.SelectMany(c => c.Types)
        .Concat(Enum.GetValues<StepType>()).Distinct().ToArray();
    private static readonly ConditionKind[] Conditions = Enum.GetValues<ConditionKind>();
    private static readonly CompareOp[] CompareOps = Enum.GetValues<CompareOp>();
    private static readonly LoopKind[] LoopKinds = Enum.GetValues<LoopKind>();
    private static readonly VarSource[] VarSources = Enum.GetValues<VarSource>();
    private static readonly BrowserAction[] BrowserActions = Enum.GetValues<BrowserAction>();
    private static readonly ErrorAction[] ErrorActions = Enum.GetValues<ErrorAction>();
    private static readonly DataAction[] DataActions = Enum.GetValues<DataAction>();
    private static readonly D365Action[] D365Actions = Enum.GetValues<D365Action>();

    private readonly ActionStep _step;
    private readonly StepEditorContext _ctx;
    private readonly List<Job> _callableJobs;

    private readonly ComboBox _cboType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, MaxDropDownItems = 30 };
    private readonly Label _lblSub = Caption("");
    private readonly ComboBox _cboSub = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly Label _lblCondition = Caption("Điều kiện:");
    private readonly FlowLayoutPanel _pnlCondition = Row();
    private readonly ComboBox _cboCondition = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _chkNegate = new() { Text = "Đảo ngược (KHÔNG)", AutoSize = true, Margin = new Padding(12, 5, 3, 3) };

    private readonly Label _lblTarget = Caption("");
    private readonly ComboBox _cboTarget = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill, MaxDropDownItems = 20 };
    private readonly Button _btnTargetAction = new() { AutoSize = true };
    private readonly Label _lblCompare = Caption("Phép so sánh:");
    private readonly ComboBox _cboCompare = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Label _lblArgs = Caption("Tham số:");
    private readonly ComboBox _cboArgs = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    private readonly Button _btnArgsAction = new() { Text = "Sao chép hồ sơ thật…", AutoSize = true };
    private readonly Label _lblText = Caption("");
    private readonly TextBox _txtText = new() { Dock = DockStyle.Fill, Multiline = true, Height = 90, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };

    // Phát video / nhạc: thời lượng từng file và tổng, tính lại mỗi khi danh sách thay đổi.
    private readonly Label _lblMediaInfo = new() { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.FromArgb(30, 70, 120), Margin = new Padding(3, 2, 3, 6) };
    private readonly MediaStrip _mediaStrip = new() { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 6) };
    private readonly System.Windows.Forms.Timer _mediaTimer = new() { Interval = 400 };
    /// <summary>Phát thử một file từ dải ảnh thu nhỏ (dừng khi đóng hộp thoại).</summary>
    private CancellationTokenSource? _previewCts;
    private int _mediaVersion;
    private Services.MediaInfo.Plan? _mediaPlan;
    /// <summary>Nội dung ô danh sách phát ứng với <see cref="_mediaPlan"/>.</summary>
    private string? _mediaPlanText;
    /// <summary>Nội dung ô danh sách phát ứng với các ảnh dải đang hiện — thứ tự mục của dải chỉ đúng khi bằng nội dung ô.</summary>
    private string? _stripText;
    private readonly Button _btnTextAction = new() { Text = "◎ Bắt phần tử (3 giây)", AutoSize = true };
    private readonly Label _lblVariable = Caption("Lưu vào biến:");
    private readonly ComboBox _cboVariable = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 240 };
    private readonly Label _lblCount = Caption("");
    private readonly NumericUpDown _numCount = Num(-100_000, 1_000_000, 100);
    private readonly Label _lblJob = Caption("Công việc:");
    private readonly ComboBox _cboJob = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };

    // Gọi API
    private readonly Label _lblHttp = Caption("Phương thức:");
    private readonly FlowLayoutPanel _pnlHttp = Row();
    private readonly ComboBox _cboMethod = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 90 };
    private readonly ComboBox _cboConnection = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly Label _lblHeaders = Caption("Header thêm\n(mỗi dòng Tên: giá trị):");
    private readonly TextBox _txtHeaders = new() { Dock = DockStyle.Fill, Multiline = true, Height = 54, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };

    // Tùy chọn nâng cao (thu gọn mặc định — nhớ trạng thái giữa các lần mở)
    private static bool _advancedOpen;
    private readonly LinkLabel _lnkAdvanced = new() { AutoSize = true, LinkColor = Theme.Accent, Margin = new Padding(3, 8, 3, 2) };
    private bool _errorApplies;
    private bool _markerType;

    // Kiểm tra (Assert)
    private readonly Label _lblMessage = Caption("Mô tả kiểm tra\n(hiện trong báo cáo):");
    private readonly TextBox _txtMessage = new() { Dock = DockStyle.Fill };

    // Ghi Excel / CSV
    private readonly Label _lblRowRef = Caption("Dòng cần sửa:");
    private readonly ComboBox _cboRowRef = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };

    // Dynamics 365: ô phụ — form chính (mở form), dòng subgrid, chữ tìm trong view, khóa TOTP (đăng nhập)
    private readonly Label _lblD365Extra = Caption("");
    private readonly ComboBox _cboD365Extra = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };

    private readonly CheckBox _chkRelative = new() { Text = "Tọa độ tương đối theo cửa sổ (click đúng kể cả khi cửa sổ bị di chuyển)", AutoSize = true };
    private readonly Label _lblXY = Caption("Tọa độ X, Y:");
    private readonly FlowLayoutPanel _pnlXY = Row();
    private readonly NumericUpDown _numX = Num(-20_000, 20_000, 80);
    private readonly NumericUpDown _numY = Num(-20_000, 20_000, 80);
    private readonly Button _btnCapture = new() { Text = "◎ Lấy tọa độ (3 giây)", AutoSize = true };
    private readonly Button _btnPreview = new() { Text = "Xem vị trí", AutoSize = true };
    private readonly Label _lblXY2 = Caption("Kéo tới X, Y:");
    private readonly FlowLayoutPanel _pnlXY2 = Row();
    private readonly NumericUpDown _numX2 = Num(-20_000, 20_000, 80);
    private readonly NumericUpDown _numY2 = Num(-20_000, 20_000, 80);
    private readonly Button _btnCapture2 = new() { Text = "◎ Lấy điểm cuối (3 giây)", AutoSize = true };
    private readonly Label _lblButton = Caption("Nút chuột:");
    private readonly FlowLayoutPanel _pnlButton = Row();
    private readonly ComboBox _cboButton = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly CheckBox _chkDouble = new() { Text = "Double-click", AutoSize = true, Margin = new Padding(12, 5, 3, 3) };
    private readonly Label _lblDelay = Caption("");
    private readonly NumericUpDown _numDelay = Num(0, 86_400_000, 120);
    private readonly CheckBox _chkWaitUser = new() { Text = "Tạm dừng flow cho tới khi bấm \"Đã hiểu\"", AutoSize = true };
    private readonly CheckBox _chkForce = new() { Text = "", AutoSize = true };

    // Phát video: màn hình phát (máy nhiều màn hình)
    private readonly Label _lblMonitor = Caption("Màn hình phát:");
    private readonly FlowLayoutPanel _pnlMonitor = Row();
    private readonly ComboBox _cboMonitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, AccessibleName = "Màn hình phát" };
    private readonly Button _btnIdentify = new() { Text = "Hiện số màn hình", AutoSize = true, Margin = new Padding(8, 2, 3, 2) };
    /// <summary>Giá trị (<see cref="Native.Displays"/>) ứng với từng dòng của <see cref="_cboMonitor"/>.</summary>
    private readonly List<int> _monitorChoices = [];

    private readonly Label _lblError = Caption("Khi bước lỗi:");
    private readonly FlowLayoutPanel _pnlError = Row();
    private readonly NumericUpDown _numRetries = Num(0, 100, 55);
    private readonly NumericUpDown _numRetryDelay = Num(0, 600_000, 85);
    private readonly ComboBox _cboOnError = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly ComboBox _cboErrorLabel = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 120 };

    private readonly Label _lblAfter = Caption("Nghỉ sau bước (ms):");
    private readonly NumericUpDown _numAfter = Num(0, 600_000, 120);
    private readonly FlowLayoutPanel _pnlFlags = Row();
    private readonly CheckBox _chkEnabled = new() { Text = "Bật bước này", AutoSize = true };
    private readonly CheckBox _chkBreakpoint = new() { Text = "Điểm dừng khi chạy thử (F9)", AutoSize = true, Margin = new Padding(16, 3, 3, 3) };
    private readonly Label _lblHint = new() { AutoSize = true, ForeColor = UiText.Muted, MaximumSize = new Size(680, 0), Margin = new Padding(3, 10, 3, 3) };
    private readonly Label _lblCaptureInfo = new() { AutoSize = true, ForeColor = Color.FromArgb(0, 100, 0), Margin = new Padding(3, 7, 3, 3), MaximumSize = new Size(520, 0) };

    private readonly Label _lblTypeMode = Caption("Cách nhập:");
    private readonly ComboBox _cboTypeMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };

    // Nhận dạng màn hình
    private readonly Label _lblImage = Caption("Hình mẫu:");
    private readonly FlowLayoutPanel _pnlImage = Row();
    private readonly PictureBox _picImage = new() { Size = new Size(240, 96), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
    private readonly Button _btnSnip = new() { Text = "✂ Chụp hình mẫu", AutoSize = true, Margin = new Padding(10, 2, 3, 2) };
    private readonly Button _btnClearImage = new() { Text = "✕ Bỏ hình mẫu", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
    private readonly Label _lblConfidence = Caption("Độ khớp tối thiểu (%):");
    private readonly NumericUpDown _numConfidence = Num(50, 100, 80);
    private readonly Label _lblMatchIndex = Caption("Lần xuất hiện thứ:");
    private readonly NumericUpDown _numMatchIndex = Num(1, 50, 80);
    private readonly Label _lblTest = Caption("Kiểm tra:");
    private readonly FlowLayoutPanel _pnlTest = Row();
    private readonly Button _btnTestFind = new() { Text = "Thử tìm trên màn hình", AutoSize = true };
    private readonly Button _btnTestStep = new() { Text = "▶ Thử bước này", AutoSize = true };
    private string _imageData = "";
    private int _imageWidth, _imageHeight;
    private double _imageScale;
    private Point _imageOffset;

    private bool _loading;

    public ActionStep Step => _step;

    public StepEditorForm(ActionStep step, StepEditorContext? context = null)
    {
        _step = step;
        _ctx = context ?? new StepEditorContext();
        _callableJobs = _ctx.Jobs.Where(j => j.Id != _ctx.CurrentJobId).OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

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

    private static Label Small(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 6, 2, 0) };

    private void BuildUi()
    {
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 500));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var t in Types) _cboType.Items.Add(ActionStep.TypeNames[t]);
        _cboButton.Items.AddRange(["Trái", "Phải", "Giữa"]);
        foreach (var c in Conditions) _cboCondition.Items.Add(ActionStep.ConditionNames[c]);
        foreach (var o in CompareOps) _cboCompare.Items.Add(ActionStep.CompareNames[o]);
        foreach (var e in ErrorActions) _cboOnError.Items.Add(ActionStep.ErrorActionNames[e]);
        _cboErrorLabel.Items.AddRange([.. _ctx.Labels]);
        _cboVariable.Items.AddRange([.. _ctx.Variables]);
        foreach (var j in _callableJobs) _cboJob.Items.Add(j.Name);

        int row = 0;
        void AddRow(Control? label, Control? main, Control? extra = null, bool span = false)
        {
            if (label != null) grid.Controls.Add(label, 0, row);
            if (main != null)
            {
                grid.Controls.Add(main, 1, row);
                if (span) grid.SetColumnSpan(main, 2);
            }
            if (extra != null) grid.Controls.Add(extra, 2, row);
            row++;
        }

        AddRow(Caption("Loại thao tác:"), _cboType);
        AddRow(_lblSub, _cboSub);
        _cboMethod.Items.AddRange(ActionStep.HttpMethods);
        _cboConnection.Items.Add("(không — nhập URL đầy đủ)");
        foreach (var c in SettingsStore.Current.ApiConnections) _cboConnection.Items.Add(c.Name);
        _pnlHttp.Controls.AddRange([_cboMethod, Small("Kết nối:"), _cboConnection]);
        AddRow(_lblHttp, _pnlHttp, span: true);
        _pnlCondition.Controls.AddRange([_cboCondition, _chkNegate]);
        AddRow(_lblCondition, _pnlCondition, span: true);
        AddRow(_lblMessage, _txtMessage);
        AddRow(_lblTarget, _cboTarget, _btnTargetAction);
        AddRow(_lblCompare, _cboCompare);
        AddRow(_lblRowRef, _cboRowRef);
        AddRow(_lblHeaders, _txtHeaders);
        AddRow(_lblText, _txtText, _btnTextAction);
        AddRow(null, _lblMediaInfo, span: true);
        AddRow(null, _mediaStrip, span: true);
        AddRow(_lblArgs, _cboArgs, _btnArgsAction);
        AddRow(_lblD365Extra, _cboD365Extra);
        AddRow(_lblVariable, _cboVariable);
        AddRow(_lblCount, _numCount);
        AddRow(_lblJob, _cboJob);
        _cboTypeMode.Items.AddRange(["Tự động (dán nếu có UniKey/EVKey đang chạy)", "Gõ từng phím", "Dán qua clipboard (Ctrl+V)"]);
        AddRow(_lblTypeMode, _cboTypeMode);

        _pnlImage.Controls.AddRange([_picImage, _btnSnip, _btnClearImage]);
        AddRow(_lblImage, _pnlImage, span: true);
        AddRow(_lblConfidence, _numConfidence);
        AddRow(_lblMatchIndex, _numMatchIndex);
        AddRow(null, _chkRelative, span: true);

        _pnlXY.Controls.AddRange([new Label { Text = "X", AutoSize = true, Margin = new Padding(0, 6, 2, 0) }, _numX,
            new Label { Text = "Y", AutoSize = true, Margin = new Padding(10, 6, 2, 0) }, _numY, _btnCapture, _btnPreview]);
        _btnCapture.Margin = new Padding(12, 2, 3, 2);
        AddRow(_lblXY, _pnlXY, span: true);
        _pnlXY2.Controls.AddRange([new Label { Text = "X", AutoSize = true, Margin = new Padding(0, 6, 2, 0) }, _numX2,
            new Label { Text = "Y", AutoSize = true, Margin = new Padding(10, 6, 2, 0) }, _numY2, _btnCapture2]);
        _btnCapture2.Margin = new Padding(12, 2, 3, 2);
        AddRow(_lblXY2, _pnlXY2, span: true);

        _pnlButton.Controls.AddRange([_cboButton, _chkDouble]);
        AddRow(_lblButton, _pnlButton);
        AddRow(_lblDelay, _numDelay);
        AddRow(null, _chkWaitUser);
        AddRow(null, _chkForce);
        _pnlMonitor.Controls.AddRange([_cboMonitor, _btnIdentify]);
        AddRow(_lblMonitor, _pnlMonitor, span: true);
        _btnIdentify.Click += (_, _) => IdentifyScreens.Show(this);

        _pnlError.Controls.AddRange([Small("Thử lại"), _numRetries, Small("lần, cách (ms)"), _numRetryDelay, Small("rồi"), _cboOnError, _cboErrorLabel]);
        ((Label)_pnlError.Controls[0]).Margin = new Padding(0, 6, 2, 0);
        _lnkAdvanced.LinkClicked += (_, _) =>
        {
            _advancedOpen = !_advancedOpen;
            OnTypeChanged(resetSub: false);
        };
        AddRow(null, _lnkAdvanced, span: true);
        AddRow(_lblError, _pnlError, span: true);
        AddRow(_lblAfter, _numAfter);
        _pnlFlags.Controls.AddRange([_chkEnabled, _chkBreakpoint]);
        AddRow(null, _pnlFlags, span: true);

        _pnlTest.Controls.AddRange([_btnTestFind, _btnTestStep]);
        AddRow(_lblTest, _pnlTest, span: true);
        AddRow(null, _lblCaptureInfo, span: true);
        AddRow(null, _lblHint, span: true);

        var bottom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var btnCancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        var btnOk = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 0) };
        btnOk.Click += async (_, _) =>
        {
            // Phát video: danh sách vừa sửa mà chưa kịp tính thời lượng → tính xong rồi mới lưu.
            if (CurrentType == StepType.PlayMedia && _mediaPlanText != _txtText.Text)
            {
                _mediaTimer.Stop();
                btnOk.Enabled = false;
                _txtText.ReadOnly = true;
                _mediaStrip.Enabled = false;   // không sửa danh sách từ dải trong lúc chờ
                try
                {
                    while (!IsDisposed && CurrentType == StepType.PlayMedia && _mediaPlanText != _txtText.Text)
                        await RefreshMediaInfoAsync();
                }
                finally
                {
                    if (!IsDisposed)
                    {
                        btnOk.Enabled = true;
                        _txtText.ReadOnly = false;
                        _mediaStrip.Enabled = true;
                    }
                }
                // Hộp thoại đã bị đóng / hủy trong lúc chờ.
                if (IsDisposed || !Visible) return;
            }
            Save();
        };
        _mediaTimer.Tick += async (_, _) =>
        {
            _mediaTimer.Stop();
            await RefreshMediaInfoAsync();
        };
        FormClosed += (_, _) =>
        {
            _mediaTimer.Stop();
            _mediaTimer.Dispose();
            _previewCts?.Cancel();
        };
        _mediaStrip.PlayRequested += async path => await PreviewMediaAsync(path);
        _mediaStrip.MoveRequested += (from, to) => EditPlaylist(PlaylistText.Move(_txtText.Text, from, to));
        _mediaStrip.RemoveRequested += item => EditPlaylist(PlaylistText.Remove(_txtText.Text, item));
        _mediaStrip.FilesDropped += AddMediaPaths;
        // Kéo thả file video / thư mục từ Explorer vào ô danh sách cũng được.
        _txtText.AllowDrop = true;
        _txtText.DragEnter += (_, e) =>
            e.Effect = CurrentType == StepType.PlayMedia && !_txtText.ReadOnly && PlaylistText.DroppedPaths(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        _txtText.DragDrop += (_, e) =>
        {
            if (CurrentType == StepType.PlayMedia) AddMediaPaths(PlaylistText.DroppedPaths(e.Data));
        };
        _txtText.TextChanged += (_, _) =>
        {
            if (_loading || CurrentType != StepType.PlayMedia) return;
            _mediaTimer.Stop();
            _mediaTimer.Start();
        };
        bottom.Controls.AddRange([btnCancel, btnOk, HelpLink()]);
        AcceptButton = null; // Enter dùng để xuống dòng trong ô văn bản
        CancelButton = btnCancel;

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(grid);
        root.Controls.Add(bottom);
        Controls.Add(root);

        _cboType.SelectedIndexChanged += (_, _) => OnTypeChanged(resetSub: true);
        _cboSub.SelectedIndexChanged += (_, _) => { if (!_loading) OnTypeChanged(resetSub: false); };
        _cboCondition.SelectedIndexChanged += (_, _) => { if (!_loading) OnTypeChanged(resetSub: false); };
        _cboCompare.SelectedIndexChanged += (_, _) => { if (!_loading) OnTypeChanged(resetSub: false); };
        _cboOnError.SelectedIndexChanged += (_, _) => _cboErrorLabel.Visible = CurrentOnError == ErrorAction.GotoLabel;
        _btnTargetAction.Click += (_, _) => OnTargetAction();
        _btnTextAction.Click += async (_, _) =>
        {
            if (CurrentType == StepType.PlayMedia) AddMediaFiles();
            else if (D365PickContext != null) await PickFromD365Async();
            else await CaptureElementAsync();
        };
        _btnArgsAction.Click += (_, _) => ShowRealProfiles();
        _cboTarget.TextChanged += (_, _) => { if (!_loading && IsBrowserLaunch) FillProfileList(); };
        _cboTarget.SelectionChangeCommitted += (_, _) =>
        {
            if (_cboTarget.SelectedItem is WindowInfo w)
                BeginInvoke(new MethodInvoker(() => _cboTarget.Text = w.Title));
        };
        _cboTarget.Leave += (_, _) => { if (UsesTable) LoadSheetNames(); };
        _chkRelative.CheckedChanged += (_, _) => { if (!_loading) OnTypeChanged(resetSub: false); };
        _btnCapture.Click += async (_, _) => await CaptureAsync(_numX, _numY);
        _btnCapture2.Click += async (_, _) => await CaptureAsync(_numX2, _numY2);
        _btnPreview.Click += (_, _) => PreviewPoint();
        _btnSnip.Click += async (_, _) => await SnipAsync();
        _btnClearImage.Click += (_, _) => ClearImage();
        _picImage.Paint += (_, e) => DrawClickMarker(e.Graphics);
        _numX.ValueChanged += (_, _) => { if (CurrentType == StepType.ClickImage) _picImage.Invalidate(); };
        _numY.ValueChanged += (_, _) => { if (CurrentType == StepType.ClickImage) _picImage.Invalidate(); };
        _btnTestFind.Click += async (_, _) => await TestFindAsync();
        _btnTestStep.Click += async (_, _) => await TestStepAsync();
    }

    private StepType CurrentType => Types[Math.Max(0, _cboType.SelectedIndex)];

    protected override string HelpTopicId => CurrentType switch
    {
        StepType.Dynamics => CurrentD365 switch
        {
            var d when IsSubgrid(d) => "d365-grids",
            D365Action.ViewQuery or D365Action.ViewOpenRecord => "d365-grids",
            D365Action.QuickCreate => "d365-forms",
            D365Action.Login or D365Action.GetUser => "d365-login",
            _ => "d365-steps"
        },
        StepType.Assert => CurrentCondition switch
        {
            ConditionKind.D365SubgridCount or ConditionKind.D365SubgridRow => "d365-grids",
            ConditionKind.D365Command or ConditionKind.D365CurrentForm => "d365-forms",
            ConditionKind.D365UserRole => "d365-login",
            _ => "assert"
        },
        StepType.SetVariable => "variables",
        StepType.If or StepType.Else or StepType.Loop or StepType.BreakLoop or StepType.ContinueLoop => "conditions",
        _ => "flow"
    };
    private LoopKind CurrentLoop => LoopKinds[Math.Clamp(_cboSub.SelectedIndex, 0, LoopKinds.Length - 1)];
    private VarSource CurrentSource => VarSources[Math.Clamp(_cboSub.SelectedIndex, 0, VarSources.Length - 1)];
    private BrowserAction CurrentBrowser => BrowserActions[Math.Clamp(_cboSub.SelectedIndex, 0, BrowserActions.Length - 1)];
    private DataAction CurrentData => DataActions[Math.Clamp(_cboSub.SelectedIndex, 0, DataActions.Length - 1)];
    private D365Action CurrentD365 => D365Actions[Math.Clamp(_cboSub.SelectedIndex, 0, D365Actions.Length - 1)];
    private ConditionKind CurrentCondition => Conditions[Math.Max(0, _cboCondition.SelectedIndex)];
    private CompareOp CurrentCompare => CompareOps[Math.Max(0, _cboCompare.SelectedIndex)];
    private ErrorAction CurrentOnError => ErrorActions[Math.Max(0, _cboOnError.SelectedIndex)];

    /// <summary>Bước hiện tại đọc/ghi bảng Excel / CSV (có ô chọn sheet).</summary>
    private bool UsesTable => (CurrentType == StepType.Loop && CurrentLoop == LoopKind.Rows) || (CurrentType == StepType.WriteData && !TextWrite);

    /// <summary>Bước "Ghi file" đang ở chế độ ghi file văn bản.</summary>
    private bool TextWrite => CurrentType == StepType.WriteData && CurrentData is DataAction.WriteText or DataAction.AppendText;

    /// <summary>Bước hiện tại dùng điều kiện (Nếu, Kiểm tra, hoặc Lặp khi).</summary>
    private bool UsesCondition => CurrentType is StepType.If or StepType.Assert || (CurrentType == StepType.Loop && CurrentLoop == LoopKind.While);

    // ───────────────────────────── Nạp / lưu ─────────────────────────────

    private void LoadStep()
    {
        _loading = true;
        _cboType.SelectedIndex = Array.IndexOf(Types, _step.Type);
        FillSubList();
        _cboSub.SelectedIndex = _step.Type switch
        {
            StepType.Loop => Array.IndexOf(LoopKinds, _step.LoopKind),
            StepType.SetVariable => Array.IndexOf(VarSources, _step.VarSource),
            StepType.Browser => Array.IndexOf(BrowserActions, _step.BrowserAction),
            StepType.WriteData => Array.IndexOf(DataActions, _step.DataAction),
            StepType.Dynamics => Array.IndexOf(D365Actions, _step.D365Action),
            _ => -1
        };
        _txtMessage.Text = _step.Message;
        _cboMethod.Text = string.IsNullOrWhiteSpace(_step.Method) ? "GET" : _step.Method.ToUpperInvariant();
        _cboConnection.SelectedIndex = Math.Max(0, _cboConnection.Items.IndexOf(_step.Connection.Trim()));
        if (_step.Connection.Trim().Length > 0 && _cboConnection.SelectedIndex == 0)
        {
            // Kết nối đã bị xóa trong Cài đặt — vẫn giữ tên để người dùng thấy.
            _cboConnection.Items.Add(_step.Connection.Trim());
            _cboConnection.SelectedIndex = _cboConnection.Items.Count - 1;
        }
        _txtHeaders.Text = _step.Headers.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _cboRowRef.Text = _step.Type == StepType.WriteData ? _step.RowRef : "";
        _cboD365Extra.Text = _step.Type != StepType.Dynamics ? "" : _step.D365Action == D365Action.OpenForm ? _step.Form : _step.RowRef;
        _cboCondition.SelectedIndex = Array.IndexOf(Conditions, _step.Condition);
        _chkNegate.Checked = _step.Negate;
        _cboCompare.SelectedIndex = Array.IndexOf(CompareOps, _step.CompareOp);
        _cboTarget.Text = _step.Target;
        _cboArgs.Text = _step.Arguments;
        _txtText.Text = _step.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _cboVariable.Text = _step.Variable;
        _numCount.Value = Math.Clamp(_step.Count, -100_000, 1_000_000);
        if (_step.JobRef is Guid id) _cboJob.SelectedIndex = _callableJobs.FindIndex(j => j.Id == id);
        _numX.Value = Math.Clamp(_step.X, -20_000, 20_000);
        _numY.Value = Math.Clamp(_step.Y, -20_000, 20_000);
        _numX2.Value = Math.Clamp(_step.X2, -20_000, 20_000);
        _numY2.Value = Math.Clamp(_step.Y2, -20_000, 20_000);
        _cboButton.SelectedIndex = (int)_step.Button;
        _chkDouble.Checked = _step.DoubleClick;
        _numDelay.Value = Math.Clamp(_step.DelayMs, 0, 86_400_000);
        _chkWaitUser.Checked = _step.WaitForUser;
        _chkForce.Checked = _step.Force;
        FillMonitors(_step.Monitor);
        _numAfter.Value = Math.Clamp(_step.DelayAfterMs, 0, 600_000);
        _chkEnabled.Checked = _step.Enabled;
        _chkBreakpoint.Checked = _step.Breakpoint;
        _chkRelative.Checked = !string.IsNullOrWhiteSpace(_step.Target);
        _imageData = _step.ImageData;
        _imageWidth = _step.ImageWidth;
        _imageHeight = _step.ImageHeight;
        _imageScale = _step.ImageScale;
        _imageOffset = new Point(_step.ImageOffsetX, _step.ImageOffsetY);
        UpdateImagePreview();
        _numConfidence.Value = Math.Clamp(_step.Confidence, 50, 100);
        _numMatchIndex.Value = Math.Clamp(_step.MatchIndex, 1, 50);
        _cboTypeMode.SelectedIndex = (int)_step.TypeMode;
        _numRetries.Value = Math.Clamp(_step.Retries, 0, 100);
        _numRetryDelay.Value = Math.Clamp(_step.RetryDelayMs, 0, 600_000);
        _cboOnError.SelectedIndex = Array.IndexOf(ErrorActions, _step.OnError);
        _cboErrorLabel.Text = _step.ErrorLabel;
        _cboErrorLabel.Visible = _step.OnError == ErrorAction.GotoLabel;
        if (_step.Retries > 0 || _step.OnError != ErrorAction.Default || !_step.Enabled || _step.Breakpoint
            || _step.DelayAfterMs != ActionStep.CreateDefault(_step.Type).DelayAfterMs)
            _advancedOpen = true;
        _loading = false;
        OnTypeChanged(resetSub: false);
        if (UsesTable) LoadSheetNames();
    }

    /// <summary>Tạo bước từ nội dung đang nhập (không kiểm tra hợp lệ).</summary>
    private ActionStep BuildStep()
    {
        var type = CurrentType;
        bool image = type is StepType.ClickImage or StepType.WaitForImage || (UsesCondition && CurrentCondition == ConditionKind.ImageOnScreen)
                     || ActionStep.CanHaveImageAnchor(type);
        bool relativeType = type is StepType.MouseClick or StepType.MouseScroll or StepType.MouseDrag;

        var s = _step.ShallowCopy();
        s.Type = type;
        s.Target = relativeType && !_chkRelative.Checked ? "" : _cboTarget.Visible ? _cboTarget.Text.Trim() : "";
        s.Arguments = _cboArgs.Visible ? _cboArgs.Text.Trim() : "";
        s.Text = _txtText.Visible ? (_txtText.Multiline ? _txtText.Text.Replace("\r\n", "\n") : _txtText.Text.Trim()) : "";
        s.Variable = _cboVariable.Visible ? _cboVariable.Text.Trim() : "";
        s.Count = (int)_numCount.Value;
        s.JobRef = type == StepType.CallJob && _cboJob.SelectedIndex >= 0 ? _callableJobs[_cboJob.SelectedIndex].Id : null;
        if (type == StepType.CallJob && s.JobRef != null) s.Target = _callableJobs[_cboJob.SelectedIndex].Name;
        s.LoopKind = type == StepType.Loop ? CurrentLoop : LoopKind.Count;
        s.VarSource = type == StepType.SetVariable ? CurrentSource : VarSource.Value;
        s.BrowserAction = type == StepType.Browser ? CurrentBrowser : BrowserAction.Navigate;
        s.DataAction = type == StepType.WriteData ? CurrentData : DataAction.AppendRow;
        s.D365Action = type == StepType.Dynamics ? CurrentD365 : D365Action.SetField;
        s.Message = type == StepType.Assert ? _txtMessage.Text.Trim() : "";
        s.RowRef = type == StepType.WriteData && CurrentData == DataAction.UpdateRow ? _cboRowRef.Text.Trim()
            : type == StepType.Dynamics && D365Extra(CurrentD365) != null && CurrentD365 != D365Action.OpenForm ? _cboD365Extra.Text.Trim() : "";
        s.Form = type == StepType.Dynamics && CurrentD365 == D365Action.OpenForm ? _cboD365Extra.Text.Trim() : "";
        s.Method = type == StepType.HttpRequest || (type == StepType.Dynamics && CurrentD365 == D365Action.WebApi)
            ? (_cboMethod.Text.Trim().Length == 0 ? "GET" : _cboMethod.Text.Trim().ToUpperInvariant())
            : "GET";
        s.Connection = type == StepType.HttpRequest && _cboConnection.SelectedIndex > 0 ? (string)_cboConnection.SelectedItem! : "";
        s.Headers = type == StepType.HttpRequest ? _txtHeaders.Text.Replace("\r\n", "\n").Trim() : "";
        s.Condition = UsesCondition ? CurrentCondition : ConditionKind.Compare;
        s.Negate = UsesCondition && _chkNegate.Checked;
        s.CompareOp = CurrentCompare;
        s.ImageData = image ? _imageData : "";
        s.ImageWidth = image ? _imageWidth : 0;
        s.ImageHeight = image ? _imageHeight : 0;
        s.ImageScale = image ? _imageScale : 0;
        bool anchor = ActionStep.CanHaveImageAnchor(type) && _imageData.Length > 0;
        s.ImageOffsetX = anchor ? _imageOffset.X : 0;
        s.ImageOffsetY = anchor ? _imageOffset.Y : 0;
        s.Confidence = (int)_numConfidence.Value;
        s.MatchIndex = (int)_numMatchIndex.Value;
        s.TypeMode = (TypeMode)Math.Max(0, _cboTypeMode.SelectedIndex);
        s.X = (int)_numX.Value;
        s.Y = (int)_numY.Value;
        s.X2 = (int)_numX2.Value;
        s.Y2 = (int)_numY2.Value;
        s.Button = (MouseButtonKind)Math.Max(0, _cboButton.SelectedIndex);
        s.DoubleClick = _chkDouble.Checked;
        s.DelayMs = (int)_numDelay.Value;
        s.WaitForUser = _chkWaitUser.Checked;
        s.Force = _chkForce.Visible && _chkForce.Checked;
        s.Monitor = type == StepType.PlayMedia && _cboMonitor.SelectedIndex >= 0 ? _monitorChoices[_cboMonitor.SelectedIndex] : 0;
        s.DelayAfterMs = (int)_numAfter.Value;
        s.Enabled = _chkEnabled.Checked;
        s.Breakpoint = !_markerType && _chkBreakpoint.Checked;
        s.Retries = _errorApplies ? (int)_numRetries.Value : 0;
        s.RetryDelayMs = (int)_numRetryDelay.Value;
        s.OnError = _errorApplies ? CurrentOnError : ErrorAction.Default;
        s.ErrorLabel = s.OnError == ErrorAction.GotoLabel ? _cboErrorLabel.Text.Trim() : "";
        s.MediaDurationMs = type != StepType.PlayMedia ? 0
            : _mediaPlan != null && _mediaPlanText == _txtText.Text ? Services.MediaInfo.ToMs(_mediaPlan)
            : s.Text == _step.Text ? _step.MediaDurationMs
            : 0;
        return s;
    }

    // ───────────────────────────── Thời lượng video / nhạc ─────────────────────────────

    /// <summary>Đọc thời lượng các file trong danh sách phát và hiện từng file + tổng ngay dưới ô danh sách.</summary>
    private async Task RefreshMediaInfoAsync()
    {
        if (CurrentType != StepType.PlayMedia) return;
        int version = ++_mediaVersion;
        var text = _txtText.Text;
        var lines = new ActionStep { Type = StepType.PlayMedia, Text = text.Replace("\r\n", "\n") }.MediaLines;
        if (lines.Count == 0)
        {
            (_mediaPlan, _mediaPlanText) = (null, text);
            _lblMediaInfo.Text = "Chưa có file nào — bấm \"＋ Thêm file…\", kéo thả video vào hoặc dán đường dẫn, mỗi dòng một file.";
            _mediaStrip.SetPlan(null);
            _stripText = text;
            return;
        }
        _lblMediaInfo.Text = "Đang tính thời lượng…";
        Services.MediaInfo.Plan plan;
        try
        {
            var expand = Services.MediaInfo.ExpanderFor(_ctx.JobVariables);
            plan = await Task.Run(() => Services.MediaInfo.AnalyzeAsync(lines, expand));
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            // Vẫn coi như đã tính xong nội dung này (thời lượng chưa rõ = 0) — nút OK không chờ mãi.
            if (text == _txtText.Text) (_mediaPlan, _mediaPlanText) = (null, text);
            if (version == _mediaVersion) _lblMediaInfo.Text = "✖ Không tính được thời lượng: " + ex.Message;
            return;
        }
        if (IsDisposed) return;
        // Kết quả vẫn đúng với nội dung hiện tại thì giữ (kể cả khi có lần tính khác chồng lên); chỉ lần tính mới nhất được hiện.
        if (text == _txtText.Text) (_mediaPlan, _mediaPlanText) = (plan, text);
        if (version != _mediaVersion) return;
        // Từng file (ảnh, thứ tự, thời lượng, lỗi) hiện ở dải ảnh thu nhỏ — nhãn chỉ còn dòng tổng.
        _lblMediaInfo.Text = DescribePlan(plan, details: false);
        _mediaStrip.SetPlan(plan);
        _stripText = text;
    }

    /// <summary>
    /// Danh sách màn hình: chính, đang có chuột, rồi từng màn hình đang cắm. Màn hình đã chọn mà giờ không cắm (vd máy chiếu)
    /// vẫn giữ lựa chọn và ghi rõ "chưa cắm" — khi chạy sẽ phát ở màn hình chính.
    /// </summary>
    private void FillMonitors(int selected)
    {
        var displays = Native.Displays.All();
        _cboMonitor.Items.Clear();
        _monitorChoices.Clear();
        void Add(int value, string text)
        {
            _monitorChoices.Add(value);
            _cboMonitor.Items.Add(text);
        }
        Add(Native.Displays.Primary, "Màn hình chính");
        Add(Native.Displays.UnderMouse, "Màn hình đang có chuột (lúc phát)");
        foreach (var d in displays) Add(d.Number, Native.Displays.Label(d, displays));
        if (selected > 0 && displays.All(d => d.Number != selected)) Add(selected, $"Màn hình {selected} (chưa cắm — sẽ phát ở màn hình chính)");
        _cboMonitor.SelectedIndex = Math.Max(0, _monitorChoices.IndexOf(selected));
        _btnIdentify.Enabled = displays.Count > 1;
        _btnIdentify.Text = displays.Count > 1 ? "Hiện số màn hình" : "Máy có 1 màn hình";
    }

    /// <summary>Sửa danh sách phát từ dải ảnh thu nhỏ (đổi thứ tự, bỏ mục) rồi tính lại ngay.</summary>
    private void EditPlaylist(string text)
    {
        // Dải đang hiện danh sách cũ (vừa gõ, chưa tính lại) → thứ tự mục không còn đúng với ô văn bản.
        if (_stripText != _txtText.Text || _txtText.ReadOnly || text == _txtText.Text) return;
        _txtText.Text = text;
        _mediaTimer.Stop();
        _ = RefreshMediaInfoAsync();
    }

    /// <summary>Thêm file / thư mục vào cuối danh sách phát (nút "＋ Thêm file…", kéo thả từ Explorer).</summary>
    internal void AddMediaPaths(IReadOnlyCollection<string> paths)
    {
        // Đang chờ tính thời lượng để lưu (đã bấm OK) → không đổi danh sách nữa.
        if (paths.Count == 0 || _txtText.ReadOnly) return;
        _txtText.Text = PlaylistText.Append(_txtText.Text, paths);
        _mediaTimer.Stop();
        _ = RefreshMediaInfoAsync();
        ShowInfo($"✔ Đã thêm {paths.Count} mục — kéo ảnh thu nhỏ để đổi thứ tự, chuột phải để bỏ.", true);
    }

    /// <summary>Phát thử riêng một file (cửa sổ thường, không toàn màn hình) — nhấp đúp ảnh thu nhỏ.</summary>
    private async Task PreviewMediaAsync(string path)
    {
        if (_previewCts != null) return;
        using var cts = _previewCts = new CancellationTokenSource();
        try
        {
            int volume = int.TryParse(_cboArgs.Text.Trim().TrimEnd('%'), out int v) ? Math.Clamp(v, 0, 100) : 100;
            ShowInfo($"▶ Đang phát thử \"{Path.GetFileName(path)}\" — Esc trong cửa sổ phát: dừng.", true);
            var r = await Services.MediaPlayback.PlayAsync([path], fullscreen: false, volume, cts.Token);
            if (IsDisposed) return;
            var (message, ok) = DescribePreview(r, path);
            ShowInfo(message, ok);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) ShowInfo("■ Đã dừng phát thử.", true);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) ShowInfo("✖ Không phát được: " + ex.Message, false);
        }
        finally
        {
            _previewCts = null;
        }
    }

    /// <summary>Kết quả phát thử một file: phát xong, dừng, bỏ qua (→ / N / PageDown) — chỉ báo lỗi khi thật sự có lỗi.</summary>
    internal static (string Text, bool Ok) DescribePreview(Services.PlaybackResult r, string path)
    {
        var name = Path.GetFileName(path);
        if (r.Played > 0) return ($"✔ Đã phát xong \"{name}\".", true);
        if (r.StoppedByUser) return ("■ Đã dừng phát thử.", true);
        if (r.Problems.Count > 0) return ("✖ Không phát được: " + string.Join("; ", r.Problems), false);
        if (r.Skipped > 0) return ($"⏭ Đã bỏ qua phát thử \"{name}\".", true);
        return ("■ Đã dừng phát thử.", true);
    }

    /// <summary>Đang phát thử: Esc dừng phát thử thay vì bấm "Hủy" (đóng ô sửa bước, mất mọi chỗ đã sửa).</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape && _previewCts != null)
        {
            _previewCts.Cancel();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    /// <param name="details">Thêm từng file / dòng lỗi bên dưới dòng tổng.</param>
    internal static string DescribePlan(Services.MediaInfo.Plan plan, bool details = true)
    {
        const int maxLines = 10;
        var sb = new System.Text.StringBuilder();
        int files = plan.FileCount;
        if (files == 0 && plan.Entries.Any(e => e.Problem == Services.MediaInfo.VariableProblem))
            sb.Append("Thời lượng: tính khi chạy (danh sách dùng biến).");
        else if (files == 0) sb.Append("✖ Không có file nào phát được.");
        else
        {
            sb.Append(plan.Complete ? "Tổng thời lượng: " : "Tổng thời lượng: ít nhất ").Append(ActionStep.FormatDuration(plan.Total))
              .Append($" · {files} file");
            if (plan.UnknownCount > 0) sb.Append($" ({plan.UnknownCount} file chưa đọc được thời lượng)");
        }
        if (!details)
        {
            int problems = plan.Entries.Count(e => e.Path == null && e.Problem != Services.MediaInfo.VariableProblem);
            if (problems > 0) sb.Append($" · ✖ {problems} dòng lỗi (ô đỏ)");
            return sb.ToString();
        }
        foreach (var (e, i) in plan.Entries.Take(maxLines).Select((e, i) => (e, i)))
        {
            sb.AppendLine();
            sb.Append(e.Path == null
                ? $"   {(e.Problem == Services.MediaInfo.VariableProblem ? "•" : "✖")} {Shorten(e.Line)} — {e.Problem}"
                : $"   {plan.Entries.Take(i + 1).Count(x => x.Path != null)}. {Path.GetFileName(e.Path)} — " +
                  (e.Duration is { } d ? ActionStep.FormatDuration(d) : "chưa rõ thời lượng"));
        }
        if (plan.Entries.Count > maxLines) sb.AppendLine().Append($"   … và {plan.Entries.Count - maxLines} mục nữa");
        return sb.ToString();
    }

    private void Save()
    {
        var s = BuildStep();
        var error = Validate(s);
        if (error != null)
        {
            Warn(error.Value.Message);
            error.Value.Focus?.Focus();
            return;
        }

        // Ghi ngược vào đối tượng bước gốc (giữ tham chiếu cho nơi gọi).
        foreach (var p in typeof(ActionStep).GetProperties().Where(p => p.CanWrite && p.CanRead))
            p.SetValue(_step, p.GetValue(s));

        DialogResult = DialogResult.OK;
        Close();
    }

    private (string Message, Control? Focus)? Validate(ActionStep s)
    {
        // Tên ô dùng trong thông báo lỗi: bỏ phần ví dụ trong ngoặc — "Giá trị (vd {{biến}}):" → "Giá trị".
        string targetName = System.Text.RegularExpressions.Regex.Replace(_lblTarget.Text.Split('\n')[0], @"\s*\(.*?\)", "").TrimEnd(':', ' ');
        bool targetRequired = s.Type switch
        {
            StepType.LaunchApp or StepType.WaitForWindow or StepType.FocusWindow or StepType.RunCommand or StepType.CloseApp
                or StepType.Label or StepType.Goto => true,
            StepType.MouseClick or StepType.MouseDrag => _chkRelative.Checked,
            StepType.SetVariable => s.VarSource is VarSource.Command or VarSource.File,
            StepType.Loop => s.LoopKind is LoopKind.Rows or LoopKind.Lines or LoopKind.Files,
            StepType.WriteData => true,
            StepType.HttpRequest => s.Connection.Length == 0,
            _ when UsesCondition => s.Condition is ConditionKind.Compare or ConditionKind.WindowExists or ConditionKind.ProcessRunning or ConditionKind.FileExists,
            _ => false
        };
        if (targetRequired && s.Target.Length == 0) return ($"Hãy nhập \"{targetName}\".", _cboTarget);

        switch (s.Type)
        {
            case StepType.Reminder when s.Target.Trim().Length == 0 && s.Text.Trim().Length == 0:
                return ("Hãy nhập tiêu đề hoặc nội dung nhắc nhở.", _cboTarget);
            case StepType.LogMessage when s.Text.Trim().Length == 0:
                return ("Hãy nhập nội dung cần ghi vào nhật ký.", _txtText);
            case StepType.TypeText when s.Text.Length == 0:
                return ("Hãy nhập văn bản cần gõ.", _txtText);
            case StepType.KeyPress:
                if (s.Text.Trim().Length == 0) return ("Hãy nhập phím cần nhấn.", _txtText);
                if (!s.Text.Contains("{{"))
                {
                    try { InputSimulator.Validate(s.Text); }
                    catch (FormatException ex) { return (ex.Message, _txtText); }
                }
                break;
            case StepType.ClickText or StepType.WaitForText when s.Text.Trim().Length == 0:
                return ("Hãy nhập chữ cần tìm trên màn hình.", _txtText);
            case StepType.ClickImage or StepType.WaitForImage when string.IsNullOrEmpty(s.ImageData):
                return ("Hãy bấm \"Chụp hình mẫu\" để chọn vùng hình cần tìm.", _btnSnip);
            case StepType.SetVariable:
                if (s.Variable.Length == 0) return ("Hãy nhập tên biến.", _cboVariable);
                if (s.VarSource == VarSource.Calc && s.Text.Trim().Length == 0) return ("Hãy nhập phép tính.", _txtText);
                if (s.VarSource == VarSource.Element) return SelectorError(s.Text);
                if (s.VarSource == VarSource.AskUser && s.Text.Trim().Length == 0) return ("Hãy nhập câu hỏi.", _txtText);
                if (s.VarSource == VarSource.JsonPath && s.Text.Trim().Length == 0) return ("Hãy nhập nội dung JSON, vd {{http.body}}.", _txtText);
                break;
            case StepType.WriteData when s.IsTextWrite:
                if (s.Target.Trim().Length == 0) return ("Hãy chọn file văn bản cần ghi.", _cboTarget);
                break;
            case StepType.PlayMedia:
                if (s.MediaLines.Count == 0) return ("Hãy thêm ít nhất một file video — bấm \"＋ Thêm file…\" hoặc dán đường dẫn, mỗi dòng một file.", _txtText);
                if (s.Arguments.Trim().Length > 0 && !s.Arguments.Contains("{{") &&
                    !(int.TryParse(s.Arguments.Trim().TrimEnd('%'), out int vol) && vol is >= 0 and <= 100))
                    return ("Âm lượng là số từ 0 đến 100 (trống = 100).", _cboArgs);
                break;
            case StepType.WriteData:
                if (s.Text.Trim().Length == 0) return ("Hãy nhập các ô cần ghi, mỗi dòng dạng TênCột=giá trị.", _txtText);
                try { TabularWriter.ParseAssignments(s.Text, x => x); }
                catch (FormatException ex) { return (ex.Message, _txtText); }
                if (s.DataAction == DataAction.UpdateRow && s.RowRef.Length == 0)
                    return ("Hãy nhập dòng cần sửa: số dòng (vd {{row.rowNumber}} trong vòng lặp Excel) hoặc Cột=giá trị.", _cboRowRef);
                break;
            case StepType.HttpRequest:
                if (!System.Text.RegularExpressions.Regex.IsMatch(s.Method, "^[A-Z]+$")) return ("Phương thức HTTP không hợp lệ (GET, POST, PATCH…).", _cboMethod);
                try { _ = ApiClient.ParseHeaders(s.Headers).ToList(); }
                catch (FormatException ex) { return (ex.Message, _txtHeaders); }
                break;
            case StepType.AskAi when s.Text.Trim().Length == 0:
                return ("Hãy nhập yêu cầu cho AI.", _txtText);
            case StepType.Notify when s.Text.Trim().Length == 0:
                return ("Hãy nhập nội dung thông báo.", _txtText);
            case StepType.Loop when s.LoopKind == LoopKind.Count && s.Count < 0:
                return ("Số lần lặp phải ≥ 0.", _numCount);
            case StepType.CallJob when s.JobRef == null:
                return ("Hãy chọn công việc cần chạy.", _cboJob);
            case StepType.ClickElement or StepType.SetElementText or StepType.WaitForElement:
                return SelectorError(s.Text);
            case StepType.MouseScroll when s.Count == 0:
                return ("Số nấc cuộn phải khác 0 (dương = lên, âm = xuống).", _numCount);
            case StepType.Browser:
                if (s.BrowserAction is BrowserAction.Navigate && s.Text.Trim().Length == 0) return ("Hãy nhập địa chỉ URL.", _txtText);
                if (s.BrowserAction is BrowserAction.Click or BrowserAction.SetValue or BrowserAction.ReadText or BrowserAction.WaitFor
                    && s.Text.Trim().Length == 0) return ("Hãy nhập bộ chọn phần tử (CSS selector).", _txtText);
                if (s.BrowserAction == BrowserAction.RunScript && s.Text.Trim().Length == 0) return ("Hãy nhập JavaScript.", _txtText);
                if (s.BrowserAction == BrowserAction.ReadText && s.Variable.Length == 0) return ("Hãy nhập tên biến nhận giá trị.", _cboVariable);
                break;
            case StepType.Dynamics:
                switch (s.D365Action)
                {
                    case D365Action.OpenForm or D365Action.OpenView when s.Text.Trim().Length == 0:
                        return ("Hãy nhập tên bảng (logical name), vd account, contact, opportunity.", _txtText);
                    case D365Action.SetField or D365Action.GetField when s.Text.Trim().Length == 0:
                        return ("Hãy nhập tên field (logical name), vd name, telephone1, parentcustomerid.", _txtText);
                    case D365Action.Command when s.Text.Trim().Length == 0:
                        return ("Hãy nhập nhãn nút (vd Lưu & đóng, Deactivate) hoặc command id.", _txtText);
                    case D365Action.SelectTab when s.Text.Trim().Length == 0:
                        return ("Hãy nhập tên hoặc nhãn tab.", _txtText);
                    case D365Action.RunScript when s.Text.Trim().Length == 0:
                        return ("Hãy nhập JavaScript.", _txtText);
                    case D365Action.WebApi:
                        if (s.Arguments.Trim().Length == 0) return ("Hãy nhập đường dẫn Web API, vd accounts?$select=name&$top=5.", _cboArgs);
                        if (!System.Text.RegularExpressions.Regex.IsMatch(s.Method, "^[A-Z]+$")) return ("Phương thức HTTP không hợp lệ (GET, POST, PATCH…).", _cboMethod);
                        break;
                    case D365Action.GetField or D365Action.GetRecordId or D365Action.GetNotifications or D365Action.SubgridGetValue
                        when s.Variable.Length == 0:
                        return ("Hãy nhập tên biến nhận giá trị.", _cboVariable);
                    case var a when IsSubgrid(a) && s.Text.Trim().Length == 0:
                        return ("Hãy nhập tên subgrid (tên control trên form, vd Contacts) hoặc bấm \"Chọn subgrid từ form…\".", _txtText);
                    case D365Action.ViewQuery or D365Action.ViewOpenRecord when s.Text.Trim().Length == 0:
                        return ("Hãy nhập tên bảng (logical name), vd account.", _txtText);
                    case D365Action.QuickCreate:
                        if (s.Arguments.Trim().Length == 0) return ("Hãy nhập tên bảng cần tạo nhanh (logical name), vd contact.", _cboArgs);
                        if (s.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.IndexOf('=') <= 0) is { } badLine)
                            return ($"Dòng \"{badLine}\" không đúng dạng field=giá trị.", _txtText);
                        break;
                    case D365Action.Login:
                        if (s.Text.Trim().Length == 0) return ("Hãy nhập tài khoản đăng nhập (email của tài khoản test).", _txtText);
                        if (s.Arguments.Trim().Length == 0) return ("Hãy nhập mật khẩu — nên lưu trong mục Bí mật rồi dùng {{secret:Tên}}.", _cboArgs);
                        if (s.RowRef.Length > 0 && !s.RowRef.Contains("{{"))
                        {
                            try { Automation.Totp.DecodeBase32(s.RowRef); }
                            catch (FormatException ex) { return (ex.Message, _cboD365Extra); }
                        }
                        break;
                }
                break;
        }

        if (UsesCondition)
        {
            switch (s.Condition)
            {
                case ConditionKind.ImageOnScreen when string.IsNullOrEmpty(s.ImageData):
                    return ("Hãy bấm \"Chụp hình mẫu\" để chọn hình cần kiểm tra.", _btnSnip);
                case ConditionKind.TextOnScreen when s.Text.Trim().Length == 0:
                    return ("Hãy nhập chữ cần kiểm tra.", _txtText);
                case ConditionKind.ElementExists:
                    return SelectorError(s.Text);
                case ConditionKind.BrowserElement when s.Text.Trim().Length == 0:
                    return ("Hãy nhập bộ chọn phần tử (CSS selector).", _txtText);
                case ConditionKind.D365FieldValue or ConditionKind.D365FieldState when s.Text.Trim().Length == 0:
                    return ("Hãy nhập tên field (logical name).", _txtText);
                case ConditionKind.D365FieldState when !ActionStep.D365FieldStates.ContainsKey(s.Arguments.Trim()):
                    return ("Hãy chọn trạng thái: " + string.Join(", ", ActionStep.D365FieldStates.Keys) + ".", _cboArgs);
                case ConditionKind.D365RecordCount when s.Text.Trim().Length == 0:
                    return ("Hãy nhập truy vấn Web API, vd accounts?$filter=name eq 'ABC'.", _txtText);
                case ConditionKind.D365SubgridCount or ConditionKind.D365SubgridRow when s.Text.Trim().Length == 0:
                    return ("Hãy nhập tên subgrid (tên control trên form, vd Contacts).", _txtText);
                case ConditionKind.D365Command when s.Text.Trim().Length == 0:
                    return ("Hãy nhập nhãn nút (vd Lưu & đóng, Deactivate) hoặc command id.", _txtText);
                case ConditionKind.D365Command when !ActionStep.D365CommandStates.ContainsKey(s.Arguments.Trim()):
                    return ("Hãy chọn trạng thái nút: " + string.Join(", ", ActionStep.D365CommandStates.Keys) + ".", _cboArgs);
                case ConditionKind.D365UserRole when s.Text.Trim().Length == 0:
                    return ("Hãy nhập tên vai trò (security role), vd Salesperson.", _txtText);
                case ConditionKind.Compare when s.CompareOp == CompareOp.Regex:
                    if (!s.Arguments.Contains("{{"))
                    {
                        try { _ = new System.Text.RegularExpressions.Regex(s.Arguments); }
                        catch (ArgumentException ex) { return ("Regex không hợp lệ: " + ex.Message, _cboArgs); }
                    }
                    break;
            }
        }

        if (s.OnError == ErrorAction.GotoLabel && s.ErrorLabel.Length == 0) return ("Hãy nhập nhãn cần nhảy tới khi lỗi.", _cboErrorLabel);
        return null;
    }

    private (string, Control?)? SelectorError(string selector)
    {
        if (selector.Trim().Length == 0) return ("Hãy nhập bộ chọn phần tử hoặc bấm \"Bắt phần tử\".", _txtText);
        if (selector.Contains("{{")) return null;
        try { UiElementFinder.Validate(selector); }
        catch (FormatException ex) { return (ex.Message, _txtText); }
        return null;
    }

    private void Warn(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // ───────────────────────────── Hiển thị theo loại ─────────────────────────────

    private void FillSubList()
    {
        _cboSub.BeginUpdate();
        _cboSub.Items.Clear();
        switch (CurrentType)
        {
            case StepType.Loop:
                foreach (var k in LoopKinds) _cboSub.Items.Add(ActionStep.LoopNames[k]);
                break;
            case StepType.SetVariable:
                foreach (var v in VarSources) _cboSub.Items.Add(ActionStep.VarSourceNames[v]);
                break;
            case StepType.Browser:
                foreach (var b in BrowserActions) _cboSub.Items.Add(ActionStep.BrowserActionNames[b]);
                break;
            case StepType.WriteData:
                foreach (var d in DataActions) _cboSub.Items.Add(ActionStep.DataActionNames[d]);
                break;
            case StepType.Dynamics:
                foreach (var d in D365Actions) _cboSub.Items.Add(ActionStep.D365ActionNames[d]);
                break;
        }
        _cboSub.EndUpdate();
    }

    private void OnTypeChanged(bool resetSub)
    {
        var t = CurrentType;
        if (resetSub && !_loading)
        {
            var wasLoading = _loading;
            _loading = true;
            FillSubList();
            if (_cboSub.Items.Count > 0) _cboSub.SelectedIndex = 0;
            if (_cboCondition.SelectedIndex < 0) _cboCondition.SelectedIndex = 0;
            _loading = wasLoading;
            var defaults = ActionStep.CreateDefault(t);
            _numDelay.Value = defaults.DelayMs;
            _numAfter.Value = defaults.DelayAfterMs;
            if (t is StepType.Loop or StepType.MouseScroll) _numCount.Value = defaults.Count;
        }

        bool cond = UsesCondition;
        var ck = CurrentCondition;
        var loop = CurrentLoop;
        var src = CurrentSource;
        var br = CurrentBrowser;
        var data = CurrentData;
        var d = CurrentD365;
        bool dyn = t == StepType.Dynamics;
        bool http = t == StepType.HttpRequest;
        bool dynApi = dyn && d == D365Action.WebApi;
        // Điều kiện đọc trang web / form Dynamics 365 qua trình duyệt điều khiển
        bool webCond = cond && (ck == ConditionKind.BrowserElement || IsD365Condition(ck));
        bool compareCond = cond && ck is ConditionKind.Compare or ConditionKind.D365FieldValue or ConditionKind.D365RecordCount
            or ConditionKind.D365SubgridCount or ConditionKind.D365CurrentForm;

        bool image = t is StepType.ClickImage or StepType.WaitForImage || (cond && ck == ConditionKind.ImageOnScreen);
        bool text = t is StepType.ClickText or StepType.WaitForText || (cond && ck == ConditionKind.TextOnScreen);
        bool element = t is StepType.ClickElement or StepType.SetElementText or StepType.WaitForElement
                       || (t == StepType.SetVariable && src == VarSource.Element) || (cond && ck == ConditionKind.ElementExists);
        bool vision = image || text;
        bool visionClick = t is StepType.ClickImage or StepType.ClickText;
        // Click chuột / click phần tử kèm hình mẫu (thường do trình ghi thao tác chụp) — tìm theo hình trước, rồi mới tới tọa độ.
        bool anchor = ActionStep.CanHaveImageAnchor(t);
        bool anchorImage = anchor && _imageData.Length > 0;
        bool pointer = t is StepType.MouseClick or StepType.MouseScroll or StepType.MouseDrag;
        bool marker = StepVisuals.IsMarker(t);

        SuspendLayout();

        // Kiểu con (kiểu lặp / nguồn giá trị / hành động trình duyệt)
        SetVisible(t is StepType.Loop or StepType.SetVariable or StepType.Browser or StepType.WriteData or StepType.Dynamics, _lblSub, _cboSub);
        _lblSub.Text = t switch
        {
            StepType.Loop => "Kiểu lặp:",
            StepType.SetVariable => "Lấy giá trị từ:",
            StepType.WriteData => "Cách ghi:",
            _ => "Hành động:"
        };
        SetVisible(cond, _lblCondition, _pnlCondition);
        SetVisible(t == StepType.Assert, _lblMessage, _txtMessage);
        SetVisible(http || dynApi, _lblHttp, _pnlHttp);
        SetVisible(http, _lblHeaders, _txtHeaders, _pnlHttp.Controls[1], _cboConnection);
        SetVisible(t == StepType.WriteData && data == DataAction.UpdateRow, _lblRowRef, _cboRowRef);
        SetVisible(dyn && D365Extra(d) != null, _lblD365Extra, _cboD365Extra);
        _lblD365Extra.Text = dyn ? D365Extra(d) ?? "" : "";
        if (_cboRowRef.Items.Count == 0) _cboRowRef.Items.AddRange(["{{row.rowNumber}}", "MaKH={{row.MaKH}}", "{{lastRow}}"]);

        // Ô "Target"
        bool showTarget = t switch
        {
            StepType.Wait or StepType.LogMessage or StepType.Else or StepType.EndIf or StepType.EndLoop or StepType.BreakLoop
                or StepType.ContinueLoop or StepType.StopFlow or StepType.CallJob or StepType.PlayMedia => false,
            StepType.SetVariable => src is not (VarSource.Value or VarSource.Calc or VarSource.Clipboard or VarSource.ListAdd
                or VarSource.Split or VarSource.JsonPath),
            StepType.Loop => loop != LoopKind.Count,
            StepType.If or StepType.Assert => ck != ConditionKind.LastStepFailed,
            _ => true
        };
        if (t == StepType.Loop && loop == LoopKind.While) showTarget = ck != ConditionKind.LastStepFailed;
        SetVisible(showTarget, _lblTarget, _cboTarget);
        _lblTarget.Text = t switch
        {
            StepType.LaunchApp => "Ứng dụng / file / URL:",
            StepType.Reminder => "Tiêu đề:",
            StepType.MouseClick or StepType.MouseScroll or StepType.MouseDrag => "Cửa sổ chứa:",
            StepType.TypeText or StepType.KeyPress => "Cửa sổ đích (tùy chọn):",
            StepType.RunCommand => "Lệnh:",
            StepType.CloseApp => "Tên tiến trình:",
            StepType.MinimizeWindow => "Cửa sổ (trống = cửa sổ\nđang dùng):",
            StepType.Label => "Tên nhãn:",
            StepType.Goto => "Nhảy tới nhãn:",
            StepType.Browser => br == BrowserAction.Launch ? "Trình duyệt:" : "Tab (một phần URL/tiêu đề,\ntrống = tab đầu tiên):",
            StepType.Dynamics => D365TabCaption,
            StepType.SetVariable => src switch
            {
                VarSource.Command => "Lệnh:",
                VarSource.File => "File:",
                VarSource.AskUser => "Tiêu đề hộp thoại:",
                _ => "Cửa sổ (trống = cửa sổ\nđang chọn / cả màn hình):"
            },
            StepType.WriteData => TextWrite ? "File văn bản\n(chưa có sẽ tự tạo):" : "File Excel / CSV\n(chưa có sẽ tự tạo):",
            StepType.HttpRequest => "URL (hoặc phần sau\nURL gốc của kết nối):",
            StepType.AskAi => "Cửa sổ chụp ảnh\n(trống = cả màn hình):",
            StepType.Notify => "Tiêu đề (tùy chọn):",
            StepType.Loop when loop == LoopKind.Rows => "File Excel / CSV:",
            StepType.Loop when loop == LoopKind.Lines => "File hoặc {{biến}}:",
            StepType.Loop when loop == LoopKind.Files => "Thư mục:",
            _ when cond => ck switch
            {
                ConditionKind.Compare => "Giá trị (vd {{biến}}):",
                ConditionKind.WindowExists => "Cửa sổ (tiêu đề/tiến trình):",
                ConditionKind.ProcessRunning => "Tên tiến trình:",
                ConditionKind.FileExists => "Đường dẫn file/thư mục:",
                ConditionKind.BrowserElement => "Tab (một phần URL/tiêu đề,\ntrống = tab đầu tiên):",
                _ when IsD365Condition(ck) => D365TabCaption,
                _ => "Chỉ tìm trong cửa sổ\n(trống = cả màn hình):"
            },
            _ when vision || element => "Chỉ tìm trong cửa sổ\n(trống = cả màn hình):",
            _ => "Cửa sổ (tiêu đề/tiến trình):"
        };

        bool fileTarget = t is StepType.LaunchApp or StepType.WriteData || (t == StepType.Loop && loop != LoopKind.While && loop != LoopKind.Count)
                          || (t == StepType.SetVariable && src == VarSource.File) || (cond && ck == ConditionKind.FileExists);
        bool windowTarget = t is StepType.WaitForWindow or StepType.FocusWindow or StepType.MinimizeWindow or StepType.MouseClick or StepType.TypeText or StepType.KeyPress
                                or StepType.MouseScroll or StepType.MouseDrag or StepType.AskAi
                            || vision || element || (t == StepType.SetVariable && src == VarSource.ScreenText)
                            || (cond && ck is ConditionKind.WindowExists);
        _btnTargetAction.Visible = showTarget && (fileTarget || windowTarget || t == StepType.CloseApp || (cond && ck == ConditionKind.ProcessRunning));
        _btnTargetAction.Text = fileTarget ? (t == StepType.Loop && loop == LoopKind.Files ? "Chọn thư mục…" : "Duyệt…") : "↻ Làm mới";
        FillTargetList();

        // So sánh / tham số
        SetVisible(compareCond, _lblCompare, _cboCompare);
        bool args = t switch
        {
            StepType.LaunchApp or StepType.SetElementText or StepType.HttpRequest or StepType.PlayMedia => true,
            StepType.WriteData => !TextWrite,
            StepType.SetVariable => src is not (VarSource.Value or VarSource.Calc or VarSource.ListAdd),
            StepType.Loop => loop is LoopKind.Rows or LoopKind.Files,
            StepType.Browser => br is BrowserAction.SetValue or BrowserAction.Launch,
            StepType.Dynamics => d is D365Action.OpenForm or D365Action.OpenView or D365Action.WaitForm or D365Action.SetField or D365Action.WebApi
                or D365Action.SubgridGetValue or D365Action.ViewQuery or D365Action.ViewOpenRecord or D365Action.QuickCreate or D365Action.Login,
            _ => (compareCond && CurrentCompare is not (CompareOp.IsEmpty or CompareOp.IsNotEmpty))
                 || (cond && ck is ConditionKind.D365FieldState or ConditionKind.D365SubgridRow or ConditionKind.D365Command)
        };
        SetVisible(args, _lblArgs, _cboArgs);
        SetVisible(t == StepType.Browser && br == BrowserAction.Launch, _btnArgsAction);
        _lblArgs.Text = t switch
        {
            StepType.LaunchApp => "Tham số:",
            StepType.Browser when br == BrowserAction.Launch => "Hồ sơ (profile)\n(trống = mặc định):",
            StepType.SetElementText or StepType.Browser => "Giá trị cần nhập:",
            StepType.WriteData => "Sheet (trống = sheet đầu):",
            StepType.PlayMedia => "Âm lượng %\n(trống = 100):",
            StepType.HttpRequest => "Trích từ JSON trả về\n(vd value[0].name, tùy chọn):",
            StepType.SetVariable => src switch
            {
                VarSource.AskUser => "Giá trị mặc định:",
                VarSource.Split => "Dấu phân cách\n(trống = dấu phẩy, \\n = xuống dòng):",
                VarSource.JsonPath => "Đường dẫn\n(vd value[0].name, items[*].id):",
                _ => "Trích bằng regex\n(tùy chọn):"
            },
            StepType.Loop => loop == LoopKind.Rows ? "Sheet (trống = sheet đầu):" : "Mẫu tên file (vd *.pdf;*.xlsx):",
            StepType.Dynamics => d switch
            {
                D365Action.OpenForm => "Id bản ghi\n(trống = tạo mới):",
                D365Action.OpenView => "Id view (tùy chọn):",
                D365Action.WaitForm => "Id bản ghi (tùy chọn):",
                D365Action.WebApi => "Đường dẫn Web API\n(vd accounts?$top=5):",
                D365Action.SubgridGetValue => "Cột (logical name,\ntrống = cột tên):",
                D365Action.ViewQuery or D365Action.ViewOpenRecord => "View (tên hoặc Id,\ntrống = view mặc định):",
                D365Action.QuickCreate => "Bảng (logical name,\nvd contact):",
                D365Action.Login => "Mật khẩu\n(nên dùng {{secret:Tên}}):",
                _ => "Giá trị:"
            },
            _ when cond => ck switch
            {
                ConditionKind.D365FieldState or ConditionKind.D365Command => "Trạng thái:",
                ConditionKind.D365FieldValue => "Giá trị mong đợi:",
                ConditionKind.D365RecordCount => "Số bản ghi:",
                ConditionKind.D365SubgridCount => "Số dòng:",
                ConditionKind.D365SubgridRow => "Dòng có chứa chữ:",
                ConditionKind.D365CurrentForm => "Tên form:",
                _ => "So với:"
            },
            _ => "So với:"
        };
        if (!((t == StepType.Loop && loop == LoopKind.Rows) || t == StepType.WriteData)) _cboArgs.Items.Clear();
        if (cond && ck == ConditionKind.D365FieldState) _cboArgs.Items.AddRange([.. ActionStep.D365FieldStates.Keys]);
        if (cond && ck == ConditionKind.D365Command) _cboArgs.Items.AddRange([.. ActionStep.D365CommandStates.Keys]);
        if (dyn && d == D365Action.Login) _cboArgs.Items.AddRange([.. SecretStore.Names.Select(n => "{{secret:" + n + "}}")]);
        _cboD365Extra.Items.Clear();
        if (dyn && d == D365Action.Login) _cboD365Extra.Items.AddRange([.. SecretStore.Names.Select(n => "{{secret:" + n + "}}")]);
        if (t == StepType.Browser && br == BrowserAction.Launch) FillProfileList();

        // Ô văn bản
        bool showText = t switch
        {
            StepType.Reminder or StepType.TypeText or StepType.KeyPress or StepType.LogMessage or StepType.StopFlow => true,
            StepType.WriteData or StepType.HttpRequest or StepType.AskAi or StepType.Notify or StepType.PlayMedia => true,
            StepType.SetVariable => src is VarSource.Value or VarSource.Calc or VarSource.Element or VarSource.AskUser
                or VarSource.ListAdd or VarSource.Split or VarSource.JsonPath,
            StepType.Browser => true,
            StepType.Dynamics => d is not (D365Action.Save or D365Action.BpfNext or D365Action.BpfPrevious or D365Action.GetRecordId
                or D365Action.GetNotifications or D365Action.Cleanup or D365Action.GetUser),
            _ => text || element || (webCond && ck != ConditionKind.D365CurrentForm)
        };
        SetVisible(showText, _lblText, _txtText);
        _lblText.Text = t switch
        {
            StepType.Reminder or StepType.Notify => "Nội dung:",
            StepType.KeyPress => "Phím:",
            StepType.LogMessage => "Nội dung ghi log:",
            StepType.StopFlow => "Lý do (tùy chọn):",
            StepType.WriteData => TextWrite ? "Nội dung\n(dùng được {{biến}}):" : "Các ô cần ghi\n(mỗi dòng Cột=giá trị):",
            StepType.PlayMedia => "Danh sách phát\n(mỗi dòng một file,\nphát từ trên xuống):",
            StepType.HttpRequest => "Nội dung gửi (body)\n— POST/PATCH/PUT:",
            StepType.AskAi => "Yêu cầu cho AI:",
            StepType.SetVariable => src switch
            {
                VarSource.Calc => "Phép tính:",
                VarSource.Element => "Bộ chọn phần tử:",
                VarSource.AskUser => "Câu hỏi:",
                VarSource.ListAdd => "Phần tử thêm vào:",
                VarSource.Split => "Chuỗi cần tách:",
                VarSource.JsonPath => "Nội dung JSON\n(vd {{http.body}}):",
                _ => "Giá trị:"
            },
            StepType.Browser => br switch
            {
                BrowserAction.Launch => "Mở URL (tùy chọn):",
                BrowserAction.Navigate => "URL:",
                BrowserAction.RunScript => "JavaScript:",
                _ => "Bộ chọn phần tử:"
            },
            StepType.Dynamics => d switch
            {
                D365Action.OpenForm or D365Action.OpenView => "Bảng (logical name,\nvd account, contact):",
                D365Action.WaitForm => "Bảng (tùy chọn):",
                D365Action.Command => "Nhãn nút hoặc\ncommand id:",
                D365Action.SelectTab => "Tab (tên hoặc nhãn):",
                D365Action.ConfirmDialog => "Nút cần bấm\n(trống = nút chính):",
                D365Action.WebApi => "Nội dung gửi (JSON)\n— POST/PATCH:",
                D365Action.RunScript => "JavaScript\n(formContext, Xrm):",
                _ when IsSubgrid(d) => "Subgrid (tên control\nhoặc nhãn, vd Contacts):",
                D365Action.ViewQuery or D365Action.ViewOpenRecord => "Bảng (logical name,\nvd account):",
                D365Action.QuickCreate => "Giá trị điền sẵn\n(mỗi dòng field=giá trị,\nlookup = bảng:guid):",
                D365Action.Login => "Tài khoản (email):",
                _ => "Field (logical name):"
            },
            _ when cond && ck == ConditionKind.BrowserElement => "Bộ chọn phần tử\n(CSS / xpath: / text:):",
            _ when cond && ck is ConditionKind.D365SubgridCount or ConditionKind.D365SubgridRow => "Subgrid (tên control\nhoặc nhãn):",
            _ when cond && ck == ConditionKind.D365Command => "Nhãn nút hoặc\ncommand id:",
            _ when cond && ck == ConditionKind.D365UserRole => "Vai trò (security role):",
            _ when cond && ck is ConditionKind.D365FieldValue or ConditionKind.D365FieldState => "Field (logical name):",
            _ when cond && ck == ConditionKind.D365Notification => "Thông báo có chứa\n(trống = có bất kỳ):",
            _ when cond && ck == ConditionKind.D365RecordCount => "Truy vấn Web API\n(vd accounts?$filter=…):",
            _ when element => "Bộ chọn phần tử:",
            _ when text => "Chữ cần tìm:",
            _ => "Văn bản:"
        };
        bool singleLine = t is StepType.KeyPress or StepType.StopFlow || text || element
                          || (t == StepType.SetVariable && src is VarSource.Calc or VarSource.ListAdd)
                          || (t == StepType.Browser && br != BrowserAction.RunScript)
                          || (dyn && d is not (D365Action.WebApi or D365Action.RunScript or D365Action.QuickCreate)) || webCond;
        _txtText.Multiline = !singleLine;
        _txtText.Height = singleLine ? _cboArgs.Height : LogicalToDeviceUnits(90);
        var pick = D365PickContext;
        _btnTextAction.Visible = showText && (element || pick != null || t == StepType.PlayMedia);
        _btnTextAction.Text = pick switch
        {
            null when t == StepType.PlayMedia => "＋ Thêm file…",
            null => "◎ Bắt phần tử (3 giây)",
            D365PickKind.Tab => "Chọn tab từ form…",
            D365PickKind.Command => "Chọn nút từ form…",
            D365PickKind.Subgrid => "Chọn subgrid từ form…",
            D365PickKind.Fields => "Lấy từ form đang mở",
            _ => "Chọn field từ form…"
        };

        // Biến nhận kết quả
        bool variable = t switch
        {
            StepType.SetVariable or StepType.RunCommand or StepType.HttpRequest or StepType.AskAi or StepType.MinimizeWindow => true,
            StepType.Loop => loop is LoopKind.Rows or LoopKind.Lines or LoopKind.Files,
            StepType.Browser => br is BrowserAction.ReadText or BrowserAction.RunScript,
            StepType.Dynamics => d is D365Action.GetField or D365Action.Save or D365Action.ConfirmDialog or D365Action.GetRecordId
                or D365Action.GetNotifications or D365Action.WebApi or D365Action.RunScript or D365Action.SubgridGetValue
                or D365Action.ViewQuery or D365Action.QuickCreate or D365Action.GetUser,
            _ => false
        };
        SetVisible(variable, _lblVariable, _cboVariable);
        _lblVariable.Text = t switch
        {
            StepType.SetVariable => src == VarSource.ListAdd ? "Tên biến danh sách:" : "Tên biến:",
            StepType.Loop => "Tên biến (tiền tố):",
            StepType.RunCommand => "Lưu output vào biến\n(tùy chọn):",
            StepType.MinimizeWindow => "Nhớ cửa sổ vào biến\n(để mở lại sau):",
            StepType.HttpRequest => "Lưu kết quả vào biến\n(tùy chọn):",
            StepType.AskAi => "Lưu câu trả lời vào biến:",
            StepType.Dynamics => d switch
            {
                D365Action.Save => "Lưu Id bản ghi vào biến\n(tùy chọn):",
                D365Action.ConfirmDialog => "Lưu nội dung hộp thoại\nvào biến (tùy chọn):",
                D365Action.WebApi => "Lưu kết quả vào biến\n(POST: Id bản ghi mới):",
                D365Action.RunScript => "Lưu kết quả vào biến\n(tùy chọn):",
                D365Action.ViewQuery => "Lưu số bản ghi vào biến\n(tùy chọn):",
                D365Action.QuickCreate => "Lưu Id bản ghi mới\nvào biến (tùy chọn):",
                D365Action.GetUser => "Lưu tên người dùng\nvào biến (tùy chọn):",
                _ => "Lưu vào biến:"
            },
            _ => "Lưu vào biến:"
        };

        SetVisible(t == StepType.MouseScroll || (t == StepType.Loop && loop == LoopKind.Count), _lblCount, _numCount);
        _lblCount.Text = t == StepType.MouseScroll ? "Số nấc (+ lên, − xuống):" : "Số lần lặp:";
        SetVisible(t == StepType.CallJob, _lblJob, _cboJob);

        SetVisible(t == StepType.TypeText, _lblTypeMode, _cboTypeMode);
        SetVisible(t == StepType.PlayMedia, _lblMediaInfo, _mediaStrip);
        if (t == StepType.PlayMedia && _mediaPlanText != _txtText.Text)
        {
            _mediaTimer.Stop();
            _mediaTimer.Start();
        }
        SetVisible(image || anchor, _lblImage, _pnlImage);
        SetVisible(image || anchorImage, _lblConfidence, _numConfidence);
        _lblImage.Text = anchor ? "Tìm theo hình\n(tùy chọn):" : "Hình mẫu:";
        _btnClearImage.Visible = anchorImage;
        _picImage.Visible = !anchor || anchorImage;
        _picImage.Invalidate();
        SetVisible(text, _lblMatchIndex, _numMatchIndex);

        SetVisible(pointer, _chkRelative);
        SetVisible(t is StepType.MouseClick or StepType.MouseDrag || visionClick || t == StepType.MouseScroll, _lblXY, _pnlXY);
        SetVisible(t is StepType.MouseClick or StepType.MouseScroll or StepType.MouseDrag, _btnCapture, _btnPreview);
        SetVisible(t == StepType.MouseDrag, _lblXY2, _pnlXY2);
        SetVisible(t is StepType.MouseClick or StepType.ClickElement or StepType.MouseDrag || visionClick, _lblButton, _pnlButton);
        _chkDouble.Visible = t != StepType.MouseDrag;
        _lblXY.Text = t switch
        {
            StepType.MouseClick => "Tọa độ X, Y:",
            StepType.MouseScroll => "Cuộn tại X, Y\n(0, 0 = giữa cửa sổ):",
            StepType.MouseDrag => "Kéo từ X, Y:",
            _ => "Lệch khỏi tâm X, Y:"
        };
        if (pointer)
        {
            _cboTarget.Enabled = _btnTargetAction.Enabled = _chkRelative.Checked;
        }
        else
        {
            _cboTarget.Enabled = _btnTargetAction.Enabled = true;
        }

        bool delay = vision || element || t is StepType.Wait or StepType.WaitForWindow or StepType.FocusWindow or StepType.MinimizeWindow or StepType.RunCommand or StepType.Browser
                         or StepType.HttpRequest or StepType.AskAi or StepType.Dynamics
                     || (t == StepType.SetVariable && src is VarSource.Command or VarSource.Element)
                     || (cond && ck is not (ConditionKind.Compare or ConditionKind.LastStepFailed))
                     || (t == StepType.MouseClick && anchorImage);
        SetVisible(delay, _lblDelay, _numDelay);
        _lblDelay.Text = t == StepType.Wait ? "Thời gian chờ (ms):"
                       : t == StepType.MouseClick ? "Chờ hình mẫu tối đa (ms):"
                       : cond ? "Chờ tối đa (ms, 0 = kiểm tra 1 lần):"
                       : "Timeout (ms):";

        SetVisible(t == StepType.Reminder, _chkWaitUser);
        bool force = t is StepType.CloseApp or StepType.StopFlow or StepType.HttpRequest or StepType.AskAi or StepType.Notify
                     || (t == StepType.SetVariable && src == VarSource.AskUser) || (dyn && d is D365Action.SetField or D365Action.WebApi)
                     || (t == StepType.Browser && br == BrowserAction.Launch) || t == StepType.PlayMedia;
        SetVisible(force, _chkForce);
        SetVisible(t == StepType.PlayMedia, _lblMonitor, _pnlMonitor);
        _chkForce.Text = t switch
        {
            StepType.CloseApp => "Buộc đóng (kill tiến trình, không hỏi lưu)",
            StepType.StopFlow => "Tính là thất bại (gửi thông báo lỗi, chạy công việc xử lý lỗi)",
            StepType.HttpRequest => "Không báo lỗi khi API trả mã lỗi (≥ 400) — tự kiểm tra {{http.status}}",
            StepType.Dynamics when d == D365Action.WebApi => "Không báo lỗi khi API trả mã lỗi (≥ 400) — tự kiểm tra {{http.status}}",
            StepType.Dynamics => "Cho phép nhập cả khi field bị khóa / ẩn (người dùng thật không nhập được)",
            StepType.Browser => "Chạy ẩn (headless) — không hiện cửa sổ, vẫn chụp ảnh khi lỗi được; hồ sơ phải đã đăng nhập sẵn",
            StepType.AskAi => "Gửi kèm ảnh chụp màn hình (cửa sổ ở trên hoặc cả màn hình) cho AI đọc",
            StepType.Notify => "Gửi kèm ảnh chụp màn hình hiện tại",
            StepType.PlayMedia => "Toàn màn hình (khi phát: Esc = dừng · → = sang file kế · Space = tạm dừng)",
            _ => "Ẩn ký tự khi nhập (mật khẩu) — không ghi giá trị vào log"
        };

        bool errorPanel = !ActionStep.IsControlType(t) || t is StepType.If or StepType.Loop;
        SetVisible(errorPanel, _lblError, _pnlError);
        // Kiểm tra (Assert) mặc định là kiểm tra mềm — xem FlowEngine.HandleFailure.
        var defaultError = t == StepType.Assert ? "Ghi là không đạt, chạy tiếp" : ActionStep.ErrorActionNames[ErrorAction.Default];
        if ((string)_cboOnError.Items[0]! != defaultError)
        {
            int selected = _cboOnError.SelectedIndex;
            _cboOnError.Items[0] = defaultError;
            _cboOnError.SelectedIndex = selected;
        }
        _cboErrorLabel.Visible = errorPanel && CurrentOnError == ErrorAction.GotoLabel;
        SetVisible(!marker, _lblAfter, _numAfter, _chkBreakpoint);
        _errorApplies = errorPanel;
        _markerType = marker;
        // Tùy chọn ít dùng gom vào "Nâng cao" (tự mở khi bước đã có giá trị khác mặc định).
        _lnkAdvanced.Visible = errorPanel || !marker;
        _lnkAdvanced.Text = _advancedOpen ? "▾ Ẩn tùy chọn nâng cao" : "▸ Nâng cao: thử lại khi lỗi, xử lý lỗi, nghỉ sau bước, bật/tắt, điểm dừng";
        if (!_advancedOpen) SetVisible(false, _lblError, _pnlError, _lblAfter, _numAfter, _pnlFlags);
        else _pnlFlags.Visible = true;

        SetVisible(pointer || vision || element || TestableType(t), _lblCaptureInfo);
        bool testFind = (vision && !cond) || anchorImage;
        bool testStep = TestableType(t) || (cond && ck != ConditionKind.LastStepFailed);
        _btnTestFind.Visible = testFind;
        _btnTestStep.Visible = testStep;
        // Đọc Visible của control con khi panel cha đang ẩn luôn trả về false → dùng biến tạm.
        SetVisible(testFind || testStep, _lblTest, _pnlTest);

        _lblHint.Text = "Gợi ý: " + Hint(t, src, loop, br, ck, d) +
                        (ActionStep.IsControlType(t) && t is not (StepType.If or StepType.Loop) ? "" :
                            "\nMọi ô chữ dùng được {{biến}}: {{today}}, {{now:HH:mm}}, {{today-1:dd/MM/yyyy}}, {{clipboard}}, {{secret:Tên}}, {{env:USERNAME}}…");

        ResumeLayout(true);
    }

    private static bool TestableType(StepType t) => t is StepType.SetVariable or StepType.ClickElement or StepType.SetElementText or StepType.PlayMedia
        or StepType.WaitForElement or StepType.Browser or StepType.RunCommand or StepType.LogMessage or StepType.Loop
        or StepType.WriteData or StepType.HttpRequest or StepType.AskAi or StepType.Notify or StepType.Dynamics or StepType.Assert;

    private const string D365TabCaption = "Tab Dynamics 365\n(trống = tab D365 đầu tiên):";

    /// <summary>Nhãn ô phụ của bước Dynamics 365 (null = hành động không dùng ô phụ).</summary>
    private static string? D365Extra(D365Action d) => d switch
    {
        D365Action.OpenForm => "Form chính (tên hoặc Id,\ntrống = form mặc định):",
        D365Action.SubgridOpenRow or D365Action.SubgridGetValue => "Dòng (số thứ tự hoặc chữ\ncó trong dòng, trống = 1):",
        D365Action.ViewQuery or D365Action.ViewOpenRecord => "Tìm theo tên (tùy chọn,\nnhư ô tìm nhanh):",
        D365Action.Login => "Khóa TOTP cho MFA (tùy chọn,\nnên dùng {{secret:Tên}}):",
        _ => null
    };

    /// <summary>Bước Dynamics 365 thao tác trên subgrid (ô văn bản là tên subgrid).</summary>
    private static bool IsSubgrid(D365Action d) =>
        d is D365Action.SubgridOpenRow or D365Action.SubgridGetValue or D365Action.SubgridNew or D365Action.SubgridRefresh;

    /// <summary>Điều kiện đọc form / trang Dynamics 365 qua trình duyệt điều khiển.</summary>
    private static bool IsD365Condition(ConditionKind k) => k is ConditionKind.D365FieldValue or ConditionKind.D365FieldState
        or ConditionKind.D365Notification or ConditionKind.D365RecordCount or ConditionKind.D365SubgridCount or ConditionKind.D365SubgridRow
        or ConditionKind.D365Command or ConditionKind.D365CurrentForm or ConditionKind.D365UserRole;

    private static string Hint(StepType t, VarSource src, LoopKind loop, BrowserAction br, ConditionKind ck, D365Action d) => t switch
    {
        StepType.Dynamics => D365Hint(d),
        StepType.Assert =>
            "Kiểm tra một điều kiện và ghi kết quả ĐẠT / KHÔNG ĐẠT vào báo cáo kiểm thử. \"Chờ tối đa\" > 0 thì kiểm tra lại tới khi đạt " +
            "(form đang tính toán, plugin chạy chậm…). Mặc định sai thì ghi \"không đạt\" và chạy tiếp để thấy hết chỗ sai; chọn \"Khi bước lỗi → Dừng flow\" " +
            "nếu các bước sau phụ thuộc vào điều kiện này." + ConditionHint(ck),
        StepType.LaunchApp => "Nhập đường dẫn .exe, file tài liệu hoặc URL (vd: notepad.exe, C:\\Tools\\app.exe, https://google.com). " +
                              "Nên thêm bước \"Chờ cửa sổ xuất hiện\" ngay sau bước này.",
        StepType.Reminder => "Hiện cửa sổ nhắc nhở ở góc phải màn hình kèm âm báo. Bật \"Tạm dừng flow\" để flow chờ bạn xác nhận rồi mới chạy tiếp.",
        StepType.Wait => "Tạm dừng flow một khoảng thời gian (1000 ms = 1 giây).",
        StepType.WaitForWindow => "Nhập một phần tiêu đề cửa sổ hoặc tên tiến trình (vd: Notepad, chrome, EXCEL, exe:chrome), hoặc chọn từ danh sách. " +
                                  "Bước lỗi nếu hết timeout mà chưa thấy cửa sổ.",
        StepType.FocusWindow => "Đưa cửa sổ lên trên cùng và nhận bàn phím (khôi phục nếu đang thu nhỏ). " +
                                "Mở lại cửa sổ đã thu nhỏ bằng bước \"Thu nhỏ cửa sổ\": nhập {{tên biến đã nhớ}} hoặc {{lastWindow}}.",
        StepType.MinimizeWindow => "Ẩn cửa sổ xuống thanh tác vụ. Để trống = cửa sổ bạn đang dùng lúc flow chạy (không tính ScheduleApp). " +
                                   "Cửa sổ được nhớ vào biến (và {{lastWindow}}) — thêm bước \"Kích hoạt cửa sổ\" với {{tên biến}} để mở lại đúng cửa sổ đó.",
        StepType.MouseClick => "Bấm \"Lấy tọa độ\" rồi di chuột tới vị trí cần click trong 3 giây. " +
                               "Có hình mẫu (trình ghi thao tác tự chụp, hoặc bấm \"Chụp hình mẫu\"): khi chạy tìm chỗ đó theo hình ảnh trước — cửa sổ dời chỗ, " +
                               "đổi kích thước vẫn click đúng — hết thời gian chờ mà không thấy mới click theo tọa độ X, Y.",
        StepType.MouseScroll => "Cuộn bánh xe chuột. Số dương cuộn lên, số âm cuộn xuống (1 nấc ≈ 3 dòng).",
        StepType.MouseDrag => "Nhấn giữ chuột tại điểm đầu, kéo tới điểm cuối rồi thả (kéo file, thanh trượt, chọn vùng…).",
        StepType.TypeText => "Hỗ trợ tiếng Việt có dấu và emoji. Xuống dòng = phím Enter. Nếu nhập cửa sổ đích, cửa sổ đó sẽ được kích hoạt trước khi gõ. " +
                             "Bộ gõ UniKey/EVKey có thể làm sai chữ khi gõ từng phím — chế độ Tự động sẽ dán qua clipboard.",
        StepType.KeyPress => "Ví dụ: Enter · Ctrl+S · Alt+F4 · Win+R · Ctrl+Shift+Esc · Tab*3 (nhấn 3 lần). Nhiều tổ hợp cách nhau dấu phẩy: Ctrl+A, Delete.",
        StepType.RunCommand => "Lệnh chạy ẩn bằng cmd.exe (vd: robocopy D:\\src E:\\bak /MIR, powershell -File C:\\script.ps1). " +
                               "Timeout = 0 nghĩa là không chờ. Mã thoát khác 0 được tính là lỗi. Output luôn có trong {{lastOutput}}.",
        StepType.CloseApp => "Tên tiến trình không cần .exe (vd: notepad, EXCEL, chrome). Mặc định yêu cầu đóng lịch sự như bấm nút X.",
        StepType.ClickImage or StepType.WaitForImage =>
            "Bấm \"Chụp hình mẫu\" rồi kéo chọn đúng phần cần tìm (vd: nút Lưu) — nên chọn vùng đặc trưng, tránh chữ/số hay thay đổi. " +
            "Hình mẫu tự co giãn khi màn hình đổi mức scale (100% ↔ 125%…). Giảm \"Độ khớp\" nếu không tìm thấy, tăng nếu click nhầm chỗ.",
        StepType.ClickText or StepType.WaitForText =>
            "Tìm chữ hiển thị trên màn hình bằng Windows OCR (vd: Đăng nhập, Lưu, Submit). So sánh không phân biệt hoa thường và bỏ qua dấu. " +
            (ScreenOcr.VietnameseAvailable ? "" : "⚠ Máy chưa cài gói OCR tiếng Việt — đang dùng OCR tiếng Anh."),
        StepType.SetVariable => src switch
        {
            VarSource.Value => "Gán giá trị cho biến, dùng lại ở bước sau bằng {{tên_biến}}. Giá trị có thể ghép biến: Báo cáo {{today:dd-MM}}.",
            VarSource.Calc => "Phép tính số học: + − * / % và ngoặc, vd {{dem}} + 1, {{tong}} * 1.1. Dùng làm bộ đếm trong vòng lặp.",
            VarSource.Clipboard => "Lấy nội dung clipboard (vd sau bước Nhấn phím Ctrl+C). Có thể trích một phần bằng regex, vd Mã: (\\d+).",
            VarSource.Command => "Lấy output của lệnh cmd/powershell. Regex (tùy chọn) để trích một phần — nhóm (...) đầu tiên được lấy.",
            VarSource.ScreenText => "Đọc toàn bộ chữ trong cửa sổ bằng OCR rồi trích bằng regex, vd Tổng tiền:\\s*([\\d.,]+).",
            VarSource.Element => "Đọc giá trị ô nhập / nhãn qua UI Automation. Bấm \"Bắt phần tử\" và trỏ chuột vào phần tử cần đọc.",
            VarSource.AskUser => "Hiện hộp thoại hỏi người dùng nhập (flow tạm dừng chờ). Bấm Hủy = bước lỗi.",
            VarSource.File => "Đọc nội dung file văn bản (UTF-8). Regex (tùy chọn) để trích một phần.",
            VarSource.ListAdd => "Danh sách = mỗi phần tử một dòng. Thêm phần tử vào cuối (biến chưa có thì tạo mới). Dùng lại bằng " +
                                 "Lặp \"Mỗi dòng văn bản\" với {{ds}}, hoặc {{ds:count}}, {{ds:first}}, {{ds:last}}, {{ds:item(2)}}, {{ds:join(, )}}, {{ds:sort}}, {{ds:unique}}.",
            VarSource.Split => "Tách chuỗi thành danh sách (mỗi phần tử một dòng), vd \"a@x.com; b@y.com\" với dấu phân cách ; .",
            VarSource.JsonPath => "Trích giá trị từ JSON, vd {{http.body}} với đường dẫn value[0].name · data.items[*].id (mọi phần tử) · " +
                                  "value[0][\"@odata.etag\"] (khóa có dấu chấm) · value.length (số phần tử).",
            _ => ""
        },
        StepType.LogMessage => "Ghi một dòng vào nhật ký — tiện để xem giá trị biến khi gỡ lỗi, vd: Đang xử lý {{row.MaKH}}.",
        StepType.If => "Các bước nằm giữa \"Nếu\" và \"Không thì\"/\"Hết Nếu\" chỉ chạy khi điều kiện đúng. " +
                       "Kéo thêm bước \"Không thì\" từ hộp công cụ vào giữa khối nếu cần nhánh ngược lại." + ConditionHint(ck),
        StepType.Else => "Các bước từ đây tới \"Hết Nếu\" chạy khi điều kiện của khối \"Nếu\" sai.",
        StepType.EndIf => "Đánh dấu cuối khối \"Nếu\".",
        StepType.Loop => loop switch
        {
            LoopKind.Count => "Lặp các bước bên trong N lần. Lần hiện tại: {{loop.index}}.",
            LoopKind.While => "Lặp khi điều kiện còn đúng (kiểm tra trước mỗi lần). Dùng \"Thoát vòng lặp\" để dừng sớm." + ConditionHint(ck),
            LoopKind.Rows => "Đọc file .xlsx / .csv (dòng đầu là tiêu đề cột) và lặp mỗi dòng. Dùng {{row.TênCột}} hoặc {{row.1}} (cột 1). " +
                             "Ví dụ nhập liệu hàng loạt vào CRM từ Excel. File đang mở trong Excel vẫn đọc được.",
            LoopKind.Lines => "Lặp mỗi dòng (không rỗng) của file văn bản hoặc nội dung biến, vd {{danhSach}}. Dòng hiện tại: {{item}}.",
            LoopKind.Files => "Lặp mỗi file trong thư mục: {{item}} (đường dẫn đầy đủ), {{item.name}}, {{item.base}} (không đuôi), {{item.ext}}.",
            _ => ""
        },
        StepType.EndLoop => "Đánh dấu cuối khối \"Lặp\" — quay lại đầu vòng lặp.",
        StepType.BreakLoop => "Thoát khỏi vòng lặp đang chạy, tiếp tục sau \"Hết lặp\". Thường đặt trong khối \"Nếu\".",
        StepType.Label => "Đánh dấu một vị trí trong flow để \"Nhảy tới nhãn\" hoặc \"Khi lỗi → nhảy tới nhãn\" (vd nhãn DangNhapLai).",
        StepType.Goto => "Nhảy tới vị trí nhãn và chạy tiếp từ đó. Cẩn thận vòng lặp vô hạn.",
        StepType.StopFlow => "Kết thúc flow ngay tại đây (thường đặt trong khối \"Nếu\").",
        StepType.CallJob => "Chạy toàn bộ các bước của một công việc khác (vd \"Đăng nhập CRM\" dùng chung cho nhiều công việc). " +
                            "Hai công việc dùng chung biến.",
        StepType.ClickElement or StepType.SetElementText or StepType.WaitForElement =>
            "Tìm phần tử bằng Windows UI Automation — không phụ thuộc vị trí, độ phân giải hay zoom. Bấm \"Bắt phần tử\" rồi trỏ chuột vào nút / ô nhập. " +
            "Bộ chọn: AutomationId=…; Name=…; ControlType=Button/Edit/…; Index=2; Name~=một phần tên." +
            (t == StepType.ClickElement ? " Không tìm thấy phần tử thì tìm theo hình mẫu (nếu có), rồi tới tọa độ lúc ghi." : ""),
        StepType.ContinueLoop => "Bỏ qua các bước còn lại của lần lặp hiện tại, sang lần kế tiếp (vd dòng Excel đã xử lý rồi). Thường đặt trong khối \"Nếu\".",
        StepType.WriteData =>
            "Ghi vào .xlsx / .csv mà không cần mở Excel — giữ nguyên định dạng và các sheet khác. Mỗi dòng một ô: TrangThai=Đã nhập, MaDon={{maDon}}, " +
            "NgayNhap={{now:dd/MM/yyyy HH:mm}}. Cột chưa có sẽ được thêm vào cuối. Sửa dòng: dùng {{row.rowNumber}} trong vòng lặp \"Mỗi dòng Excel\" " +
            "để ghi kết quả vào đúng dòng đang xử lý, hoặc MaKH=KH001 để tìm theo cột khóa. Excel khóa file khi đang mở — đóng file trước khi chạy. " +
            "Số dòng vừa ghi có trong {{lastRow}}. Ghi chữ tự do (báo cáo, nhật ký, \"đã chạy xong\"…): chọn cách ghi \"Ghi file văn bản\" — " +
            "mở file đó bằng bước \"Mở ứng dụng\" notepad.exe.",
        StepType.PlayMedia =>
            "Phát lần lượt bằng trình phát có sẵn trong ScheduleApp: hết file này tự sang file kế, phát xong cả danh sách mới chạy bước sau. " +
            "Mỗi dòng một file (dán đường dẫn \"Copy as path\" của Explorer được, dòng # là ghi chú); dòng là thư mục thì phát mọi video trong đó theo tên. " +
            "Ảnh thu nhỏ (như Explorer), thứ tự phát và thời lượng từng file hiện ngay dưới danh sách: kéo ảnh để đổi thứ tự, nhấp đúp để phát thử, " +
            "chuột phải để bỏ; kéo thả video từ Explorer vào để thêm. Máy nhiều màn hình: chọn \"Màn hình phát\" (vd máy chiếu). " +
            "File thiếu / lỗi được bỏ qua và ghi vào nhật ký. " +
            "Sau bước: {{media.played}} (số file đã phát), {{media.duration}} (tổng thời lượng, vd 0:26).",
        StepType.HttpRequest =>
            "Gọi REST API: kết quả trong {{http.body}}, mã trả về trong {{http.status}}. Trích một giá trị bằng đường dẫn JSON, vd value[0].accountid, " +
            "value[*].name (mọi phần tử, mỗi dòng một giá trị), value.length. Dynamics 365: tạo kết nối loại \"Microsoft Entra ID\" với URL gốc " +
            "https://<org>.crm5.dynamics.com/api/data/v9.2/ rồi nhập accounts?$select=name&$filter=… · body JSON dùng {{biến:json}} để thoát dấu nháy. " +
            "Header Prefer: return=representation để POST trả về bản ghi vừa tạo.",
        StepType.AskAi =>
            "Gửi yêu cầu cho Claude, câu trả lời lưu vào biến (và {{ai.answer}}). Vd: \"Trích số hóa đơn, ngày, tổng tiền từ đoạn sau, trả về JSON: {{noiDung}}\" " +
            "rồi dùng \"Gán biến → Trích từ JSON\". Bật gửi kèm ảnh để AI đọc màn hình (vd đọc số tiền trên phần mềm không copy được chữ). " +
            "Nhập khóa API trong ⚙ Cài đặt → Tích hợp. Lưu ý: nội dung và ảnh được gửi tới Anthropic.",
        StepType.Notify => "Gửi tin qua các kênh đã bật trong ⚙ Cài đặt → Thông báo (Telegram / email / webhook), vd báo đã xử lý xong {{loop.count}} dòng.",
        StepType.Browser => br switch
        {
            BrowserAction.Launch => "Mở Chrome/Edge ở chế độ cho phép điều khiển. Mỗi hồ sơ giữ đăng nhập riêng: gõ tên mới (vd \"Kế toán\") để tạo hồ sơ mới, " +
                                    "lần đầu đăng nhập các trang web, lần sau tự đăng nhập. \"Sao chép hồ sơ thật…\" lấy đăng nhập / tiện ích / dấu trang " +
                                    "từ hồ sơ Chrome/Edge bạn đang dùng (Chrome không cho điều khiển trực tiếp hồ sơ đó). Các bước Trình duyệt khác cần bước này chạy trước.",
            BrowserAction.RunScript => "Chạy JavaScript trên trang, giá trị trả về lưu vào biến. Vd: document.title  hoặc  document.querySelectorAll('tr').length",
            _ => "Bộ chọn: CSS (#id, .lop, input[name=email], button[type=submit]), xpath://button[.='Lưu'] hoặc text:Đăng nhập. " +
                 "Chuột phải phần tử trên trang → Inspect → Copy selector để lấy CSS selector."
        },
        _ => ""
    };

    private static string ConditionHint(ConditionKind ck) => ck switch
    {
        ConditionKind.Compare => " So sánh số nếu cả hai là số, ngược lại so chữ không phân biệt hoa thường.",
        ConditionKind.ImageOnScreen or ConditionKind.TextOnScreen or ConditionKind.ElementExists or ConditionKind.WindowExists =>
            " \"Chờ tối đa\" > 0 sẽ đợi tới khi thấy (hữu ích khi trang tải chậm).",
        ConditionKind.LastStepFailed => " Dùng với \"Khi bước lỗi → Bỏ qua\" ở bước trước. Thông báo lỗi có trong {{lastError}}.",
        ConditionKind.BrowserElement => " Kiểm tra trên tab của trình duyệt điều khiển (mở bằng bước Trình duyệt).",
        ConditionKind.D365FieldValue =>
            " Field theo tên logic (name, statuscode, parentcustomerid). So với giá trị hiển thị: lookup = tên bản ghi, option set = nhãn, " +
            "ngày = dd/MM/yyyy, Có/Không = true/false. Thêm :raw để lấy giá trị gốc (Id lookup, số option set), vd statuscode:raw.",
        ConditionKind.D365FieldState => " Trạng thái: required (bắt buộc), recommended, disabled (khóa), visible (hiện), dirty (đã sửa chưa lưu), empty (trống). " +
                                        "Tick \"Đảo ngược\" để kiểm tra ngược lại, vd KHÔNG disabled.",
        ConditionKind.D365Notification => " Đọc thanh thông báo của form, lỗi dưới field và hộp thoại lỗi (vd lỗi plugin khi lưu).",
        ConditionKind.D365RecordCount =>
            " Truy vấn Web API bằng phiên đăng nhập của trình duyệt, vd contacts?$filter=emailaddress1 eq '{{email}}'&$select=contactid. " +
            "Dùng để kiểm tra dữ liệu thật đã được tạo / cập nhật (plugin, flow Power Automate…).",
        ConditionKind.D365SubgridCount => " Tổng số bản ghi của subgrid theo view của nó (không chỉ trang đang hiện). Chờ tối đa > 0 để chờ subgrid tải xong.",
        ConditionKind.D365SubgridRow => " Đúng nếu có dòng mà cột tên hoặc bất kỳ ô nào chứa chữ (không phân biệt hoa thường / dấu).",
        ConditionKind.D365Command => " visible = nút có trên thanh lệnh hoặc trong menu \"…\"; enabled = hiện và bấm được; disabled = hiện nhưng bị mờ. " +
                                     "Tick Đảo ngược để kiểm tra nút bị ẩn (vd người dùng không có quyền).",
        ConditionKind.D365CurrentForm => " Tên form chính đang mở (theo bộ chọn form). Mở đúng form bằng ô \"Form chính\" của bước Mở form bản ghi.",
        ConditionKind.D365UserRole => " Vai trò bảo mật của người dùng đang đăng nhập (so tên không phân biệt dấu). Dùng khi kịch bản chạy với nhiều tài khoản / vai trò.",
        _ => ""
    };

    private static string D365Hint(D365Action d) => d switch
    {
        D365Action.OpenForm =>
            "Mở form bằng Xrm.Navigation.openForm rồi chờ form tải xong. Trống Id = form tạo mới. Trước đó cần một bước \"Trình duyệt → Mở trình duyệt " +
            "ở chế độ điều khiển\" mở URL app (vd https://org.crm5.dynamics.com/main.aspx?appid=…) với hồ sơ đã đăng nhập.",
        D365Action.OpenView => "Mở danh sách bản ghi của bảng; Id view (savedquery) tùy chọn, trống = view mặc định.",
        D365Action.WaitForm => "Chờ tới khi trang có form Dynamics 365 sẵn sàng (sau khi bấm nút chuyển trang, tạo bản ghi liên quan…). " +
                               "Nhập bảng / Id để chờ đúng form đó.",
        D365Action.SetField =>
            "Nhập giá trị qua Client API rồi chạy OnChange (business rule, script của form chạy như người dùng nhập). Theo kiểu field: " +
            "lookup = tên bản ghi hoặc tenbang:guid (vd account:Contoso, account:{{accountId}}) · option set = nhãn hoặc số · " +
            "nhiều lựa chọn = nhãn cách nhau dấu ; · ngày = dd/MM/yyyy hoặc dd/MM/yyyy HH:mm · Có/Không = có/không, true/false · trống = xóa giá trị. " +
            "Field bị khóa / ẩn sẽ báo lỗi như người dùng thật.",
        D365Action.GetField => "Đọc giá trị hiển thị (lookup = tên, option set = nhãn). Thêm :raw để lấy giá trị gốc, vd parentcustomerid:raw = Id.",
        D365Action.Save =>
            "Lưu bản ghi (formContext.data.save). Lỗi validate, field bắt buộc, lỗi plugin → bước lỗi kèm thông báo. Id lưu trong {{d365.lastId}}; " +
            "bản ghi mới được ghi vào {{d365.created}} để \"Xóa dữ liệu test đã tạo\".",
        D365Action.Command => "Bấm nút trên thanh lệnh theo nhãn hiển thị (vd Lưu & đóng, Deactivate, Qualify) hoặc một phần command id " +
                              "(vd Mscrm.Form.account.Deactivate). Nút nằm trong \"Thêm lệnh\" (…) được tự mở ra.",
        D365Action.SelectTab => "Chuyển tới tab của form theo tên (name) hoặc nhãn hiển thị.",
        D365Action.BpfNext or D365Action.BpfPrevious => "Chuyển giai đoạn của quy trình nghiệp vụ (Business Process Flow). Thiếu field bắt buộc " +
                                                         "của giai đoạn hoặc form chưa lưu → bước lỗi kèm lý do.",
        D365Action.ConfirmDialog => "Chờ hộp thoại (xác nhận, cảnh báo, lỗi) rồi bấm nút theo nhãn; trống = nút chính (OK / Xác nhận). " +
                                    "Nội dung hộp thoại lưu được vào biến để kiểm tra.",
        D365Action.GetRecordId => "Lấy Id của bản ghi đang mở (trống nếu chưa lưu).",
        D365Action.GetNotifications => "Đọc các thông báo đang hiện trên form (thanh thông báo, lỗi dưới field, hộp thoại lỗi), mỗi thông báo một dòng.",
        D365Action.WebApi =>
            "Gọi Dynamics 365 Web API bằng phiên đăng nhập của trình duyệt — không cần đăng ký ứng dụng Entra ID. Kết quả trong {{http.body}}, mã trong " +
            "{{http.status}}. POST tạo bản ghi: Id mới trong {{d365.lastId}} và được ghi vào {{d365.created}} để dọn sau khi test. " +
            "Vd POST accounts với body {\"name\":\"Test {{now:HHmmss}}\"}.",
        D365Action.Cleanup =>
            "Xóa mọi bản ghi trong {{d365.created}} (tạo bằng bước Lưu / Web API POST), bản tạo sau xóa trước. Hoặc bật \"Tự xóa dữ liệu test\" " +
            "ở tab Kiểm thử của công việc để luôn dọn kể cả khi test thất bại.",
        D365Action.RunScript => "JavaScript chạy trong trang với formContext và Xrm có sẵn, dùng await được; giá trị return lưu vào biến. " +
                                "Vd: return formContext.getAttribute('revenue').getValue() * 2",
        D365Action.SubgridOpenRow => "Mở bản ghi của một dòng trong subgrid (chờ subgrid tải xong). Dòng: số thứ tự (1 = dòng đầu) hoặc chữ có trong dòng " +
                                     "(tên, email… — không phân biệt dấu). Bấm \"Chọn subgrid từ form…\" để xem các subgrid của form đang mở.",
        D365Action.SubgridGetValue => "Đọc một ô của subgrid vào biến: cột theo tên logic (vd emailaddress1, parentcustomerid); trống = cột tên của bản ghi.",
        D365Action.SubgridNew => "Như bấm \"+ Mới\" trên subgrid: mở form tạo bản ghi liên quan, các field của bản ghi cha được điền sẵn theo ánh xạ " +
                                 "của quan hệ. Bản ghi đang mở phải được lưu trước.",
        D365Action.SubgridRefresh => "Tải lại dữ liệu của subgrid (sau khi plugin / flow tạo bản ghi liên quan).",
        D365Action.ViewQuery => "Đọc các bản ghi của view bằng chính FetchXML của view (bộ lọc, sắp xếp) qua Web API — kiểm tra view lọc đúng dữ liệu. " +
                                "Kết quả: {{view.count}}, {{view.ids}}, {{view.names}} (mỗi dòng một bản ghi). Ô tìm lọc thêm theo cột tên (chứa chữ).",
        D365Action.ViewOpenRecord => "Tìm bản ghi trong view (theo bộ lọc của view + ô tìm theo tên) rồi mở bản ghi đầu tiên. Lỗi nếu không có bản ghi nào.",
        D365Action.QuickCreate => "Mở form tạo nhanh, điền sẵn giá trị (mỗi dòng field=giá trị, giá trị gốc: số cho option set, bảng:guid cho lookup), " +
                                  "bấm \"Lưu và đóng\" rồi lấy Id bản ghi mới ({{d365.lastId}}, được ghi vào {{d365.created}} để dọn).",
        D365Action.Login => "Đăng nhập trang Microsoft bằng tài khoản test: email → mật khẩu → mã xác thực TOTP (nếu tài khoản có MFA bằng ứng dụng " +
                            "xác thực) → duy trì đăng nhập. Đặt sau bước \"Mở trình duyệt\". Đã đăng nhập sẵn thì bỏ qua. Lưu mật khẩu và khóa TOTP trong " +
                            "mục Bí mật. Không tự duyệt được thông báo đẩy (Authenticator push).",
        D365Action.GetUser => "Đọc người dùng đang đăng nhập: {{d365.user}}, {{d365.userId}}, {{d365.roles}} (mỗi dòng một vai trò).",
        _ => ""
    };

    private static void SetVisible(bool visible, params Control[] controls)
    {
        foreach (var c in controls) c.Visible = visible;
    }

    private void FillTargetList()
    {
        var text = _cboTarget.Text;
        _cboTarget.BeginUpdate();
        _cboTarget.Items.Clear();
        var t = CurrentType;
        bool cond = UsesCondition;
        if (t == StepType.Goto)
        {
            _cboTarget.Items.AddRange([.. _ctx.Labels]);
        }
        else if (t == StepType.Browser && CurrentBrowser == BrowserAction.Launch)
        {
            _cboTarget.Items.AddRange(["Chrome", "Edge"]);
        }
        else if (t is StepType.CloseApp || (cond && CurrentCondition == ConditionKind.ProcessRunning))
        {
            foreach (var p in WindowHelper.GetOpenWindows().Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order())
                _cboTarget.Items.Add(p);
        }
        else if (t is StepType.WaitForWindow or StepType.FocusWindow or StepType.MinimizeWindow or StepType.MouseClick or StepType.TypeText or StepType.KeyPress
                     or StepType.ClickImage or StepType.WaitForImage or StepType.ClickText or StepType.WaitForText
                     or StepType.ClickElement or StepType.SetElementText or StepType.WaitForElement or StepType.MouseScroll or StepType.MouseDrag or StepType.AskAi
                 || (t == StepType.SetVariable && CurrentSource is VarSource.ScreenText or VarSource.Element)
                 || (cond && CurrentCondition is ConditionKind.WindowExists or ConditionKind.ImageOnScreen or ConditionKind.TextOnScreen or ConditionKind.ElementExists))
        {
            foreach (var w in WindowHelper.GetOpenWindows().OrderBy(w => w.ProcessName)) _cboTarget.Items.Add(w);
        }
        else if (cond && CurrentCondition == ConditionKind.Compare)
        {
            foreach (var v in _ctx.Variables) _cboTarget.Items.Add("{{" + v + "}}");
        }
        _cboTarget.EndUpdate();
        _cboTarget.Text = text;
    }

    private void OnTargetAction()
    {
        var t = CurrentType;
        bool folder = t == StepType.Loop && CurrentLoop == LoopKind.Files;
        bool file = t is StepType.LaunchApp or StepType.WriteData || (t == StepType.Loop && CurrentLoop is LoopKind.Rows or LoopKind.Lines)
                    || (t == StepType.SetVariable && CurrentSource == VarSource.File) || (UsesCondition && CurrentCondition == ConditionKind.FileExists);

        if (folder)
        {
            using var dlg = new FolderBrowserDialog { Description = "Chọn thư mục", UseDescriptionForTitle = true, SelectedPath = _cboTarget.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _cboTarget.Text = dlg.SelectedPath;
            return;
        }
        if (file)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Chọn file",
                CheckFileExists = t != StepType.WriteData,
                Filter = t == StepType.LaunchApp ? "Ứng dụng (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|Tất cả file (*.*)|*.*"
                       : UsesTable ? "Excel / CSV (*.xlsx;*.xlsm;*.csv)|*.xlsx;*.xlsm;*.csv;*.tsv|Tất cả file (*.*)|*.*"
                       : TextWrite ? "Văn bản (*.txt;*.log;*.md;*.csv)|*.txt;*.log;*.md;*.csv|Tất cả file (*.*)|*.*"
                       : "Tất cả file (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _cboTarget.Text = dlg.FileName;
            if (UsesTable) LoadSheetNames();
            return;
        }
        FillTargetList();
        _cboTarget.DroppedDown = _cboTarget.Items.Count > 0;
    }

    /// <summary>Phát video: chọn nhiều file, thêm vào cuối danh sách (mỗi dòng một file) theo thứ tự tên.</summary>
    private void AddMediaFiles()
    {
        var exts = string.Join(";", Services.MediaPlayback.Extensions.Select(e => "*" + e));
        using var dlg = new OpenFileDialog
        {
            Title = "Chọn video / nhạc (chọn được nhiều file)",
            Multiselect = true,
            Filter = $"Video / nhạc ({exts})|{exts}|Tất cả file (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AddMediaPaths(dlg.FileNames.Order(Services.MediaPlayback.NameOrder).ToList());
    }

    /// <summary>Nạp tên sheet của file Excel và hiện cột tiêu đề để gợi ý biến {{row.Cột}}.</summary>
    private void LoadSheetNames()
    {
        var path = _cboTarget.Text.Trim().Trim('"');
        if (path.Contains("{{") || !File.Exists(path)) return;
        try
        {
            var text = _cboArgs.Text;
            _cboArgs.Items.Clear();
            if (Path.GetExtension(path).StartsWith(".xls", StringComparison.OrdinalIgnoreCase))
                _cboArgs.Items.AddRange([.. TabularReader.SheetNames(path)]);
            _cboArgs.Text = text;
            var table = TabularReader.Read(path, string.IsNullOrWhiteSpace(text) ? null : text);
            if (CurrentType == StepType.WriteData)
            {
                ShowInfo($"✔ {table.Rows.Count} dòng dữ liệu (dòng cuối: {(table.RowNumbers.Count > 0 ? table.RowNumbers[^1] : table.HeaderRowNumber)}). " +
                         "Cột: " + string.Join(", ", table.Headers.Take(15)), true);
                return;
            }
            var prefix = string.IsNullOrWhiteSpace(_cboVariable.Text) ? "row" : _cboVariable.Text.Trim();
            ShowInfo($"✔ {table.Rows.Count} dòng. Cột: " + string.Join("  ", table.Headers.Take(10).Select(h => "{{" + prefix + "." + h + "}}")) +
                     $"  · số dòng Excel: {{{{{prefix}.rowNumber}}}}", true);
        }
        catch (Exception ex)
        {
            ShowInfo("✖ " + ex.Message, false);
        }
    }

    // ───────────────────────────── Tọa độ chuột ─────────────────────────────

    private async Task CaptureAsync(NumericUpDown numX, NumericUpDown numY)
    {
        _btnCapture.Enabled = _btnCapture2.Enabled = false;
        try
        {
            var (point, window) = await ScreenHelper.CaptureCursorAsync(3);
            if (_chkRelative.Checked && window != IntPtr.Zero)
            {
                // Giữ tiêu đề người dùng đã nhập nếu nó vẫn khớp đúng cửa sổ này.
                if (WindowHelper.Find(_cboTarget.Text) != window) _cboTarget.Text = WindowHelper.GetTitle(window);
                var rect = WindowHelper.GetRect(WindowHelper.Find(_cboTarget.Text) is var h && h != IntPtr.Zero ? h : window);
                numX.Value = Math.Clamp(point.X - rect.Left, -20_000, 20_000);
                numY.Value = Math.Clamp(point.Y - rect.Top, -20_000, 20_000);
                ShowInfo($"✔ Màn hình ({point.X}, {point.Y}) → tương đối ({numX.Value}, {numY.Value}) trong \"{Shorten(_cboTarget.Text)}\"", true);
            }
            else
            {
                numX.Value = point.X;
                numY.Value = point.Y;
                ShowInfo($"✔ Tọa độ màn hình ({point.X}, {point.Y})", true);
            }
        }
        finally
        {
            _btnCapture.Enabled = _btnCapture2.Enabled = true;
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
                ShowInfo($"✖ Không tìm thấy cửa sổ \"{Shorten(_cboTarget.Text)}\"", false);
                return;
            }
            var rect = WindowHelper.GetRect(h);
            x += rect.Left;
            y += rect.Top;
        }
        Win32.SetCursorPos(x, y);
        ShowInfo($"Con trỏ đã được đưa tới ({x}, {y}) trên màn hình.", true);
    }

    private static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;

    // ───────────────────────────── Phần tử UI ─────────────────────────────

    /// <summary>Ô chữ hiện tại chọn được từ form D365 đang mở (null = không): field, tab, nút, hoặc lấy bảng/Id của form.</summary>
    private D365PickKind? D365PickContext
    {
        get
        {
            var t = CurrentType;
            if (t == StepType.Dynamics)
                return CurrentD365 switch
                {
                    D365Action.SetField or D365Action.GetField => D365PickKind.Field,
                    D365Action.SelectTab => D365PickKind.Tab,
                    D365Action.Command => D365PickKind.Command,
                    D365Action.OpenForm or D365Action.WaitForm => D365PickKind.Fields, // dùng làm "lấy bảng / Id của form đang mở"
                    var d when IsSubgrid(d) => D365PickKind.Subgrid,
                    _ => null
                };
            if (!UsesCondition) return null;
            return CurrentCondition switch
            {
                ConditionKind.D365FieldValue or ConditionKind.D365FieldState => D365PickKind.Field,
                ConditionKind.D365SubgridCount or ConditionKind.D365SubgridRow => D365PickKind.Subgrid,
                ConditionKind.D365Command => D365PickKind.Command,
                _ => null
            };
        }
    }

    /// <summary>Điền ô từ form Dynamics 365 đang mở trong trình duyệt điều khiển.</summary>
    private async Task PickFromD365Async()
    {
        var kind = D365PickContext!.Value;
        var tab = _cboTarget.Text.Trim();
        if (kind == D365PickKind.Fields)
        {
            // Mở form: lấy bảng và Id của form đang mở, không cần hộp thoại.
            _btnTextAction.Enabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var form = await Task.Run(() => Automation.D365Client.DescribeFormAsync(tab, cts.Token));
                _txtText.Text = form.Entity;
                _cboArgs.Text = form.IsNew ? "" : form.Id;
                ShowInfo($"✔ Form đang mở: {form.Entity}{(form.IsNew ? " (tạo mới)" : " · " + form.Id)}", true);
            }
            catch (Exception ex)
            {
                ShowInfo("✖ " + ex.Message + " — mở trình duyệt điều khiển và một form Dynamics 365 trước.", false);
            }
            finally
            {
                _btnTextAction.Enabled = true;
            }
            return;
        }

        using var picker = new D365PickerForm(kind, tab);
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _txtText.Text = picker.SelectedText;
        if (picker.SelectedField is not { } f) return;

        // Gợi ý giá trị: các lựa chọn của option set / Có-Không, giá trị hiện tại cho bước kiểm tra.
        var args = _cboArgs.Text;
        _cboArgs.Items.Clear();
        if (UsesCondition && CurrentCondition == ConditionKind.D365FieldState)
            _cboArgs.Items.AddRange([.. ActionStep.D365FieldStates.Keys]);
        else if (f.Options.Count > 0)
            _cboArgs.Items.AddRange([.. f.Options]);
        else if (f.Type == "boolean")
            _cboArgs.Items.AddRange(["có", "không"]);
        _cboArgs.Text = args.Length == 0 && UsesCondition && CurrentCondition == ConditionKind.D365FieldValue ? f.Value : args;
        ShowInfo($"✔ {(f.Label.Length > 0 ? f.Label : f.Name)} · {f.Type}" + (f.Value.Length > 0 ? $" · hiện tại: \"{f.Value}\"" : " · đang trống") +
                 (f.Required == "required" ? " · bắt buộc" : "") + (f.Disabled ? " · bị khóa" : ""), true);
    }

    private async Task CaptureElementAsync()
    {
        _btnTextAction.Enabled = false;
        try
        {
            var (point, _) = await ScreenHelper.CaptureCursorAsync(3, "Trỏ chuột vào nút / ô nhập cần bắt…");
            var captured = await Task.Run(() =>
            {
                try { return UiElementFinder.Capture(point); }
                catch (Exception ex) { return new UiElementFinder.CapturedElement(IntPtr.Zero, "", "✖ " + ex.Message, Rectangle.Empty); }
            });
            if (captured == null || captured.Selector.Length == 0)
            {
                ShowInfo(captured?.Description ?? "✖ Không đọc được phần tử dưới con trỏ (ứng dụng không hỗ trợ UI Automation?) — thử \"Click vào hình ảnh\".", false);
                return;
            }
            _txtText.Text = captured.Selector;
            var title = WindowHelper.GetTitle(captured.Window);
            if (WindowHelper.Find(_cboTarget.Text) != captured.Window) _cboTarget.Text = title;
            if (!captured.Bounds.IsEmpty) HighlightForm.Flash(captured.Bounds);
            ShowInfo($"✔ {captured.Description} trong \"{Shorten(title)}\"", true);
        }
        finally
        {
            _btnTextAction.Enabled = true;
            Activate();
        }
    }

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
            _imageScale = RegionSelectorForm.LastScale;
            _imageOffset = Point.Empty;
            UpdateImagePreview();
            if (ActionStep.CanHaveImageAnchor(CurrentType))
            {
                // Click theo hình: click vào giữa hình mẫu; chờ hình xuất hiện một lúc trước khi dùng tọa độ.
                if (CurrentType == StepType.MouseClick && _numDelay.Value < 2_000) _numDelay.Value = ClickAnchor.DefaultTimeoutMs;
                OnTypeChanged(resetSub: false);
            }
            if (ImageMatcher.IsLowDetail(bmp))
                ShowInfo($"⚠ Hình mẫu {bmp.Width}×{bmp.Height} gần như một màu nên dễ khớp nhầm chỗ khác — nên chụp vùng có chữ/icon đặc trưng.", false);
            else
                ShowInfo($"✔ Đã chụp hình mẫu {bmp.Width}×{bmp.Height} px (scale {_imageScale:P0}). Bấm \"Thử tìm\" để kiểm tra.", true);
        }
        finally
        {
            _btnSnip.Enabled = true;
            Activate();
        }
    }

    /// <summary>Dấu chữ thập trên hình mẫu tại điểm sẽ click (tâm hình + độ lệch).</summary>
    private void DrawClickMarker(Graphics g)
    {
        if (_picImage.Image is not { } img) return;
        var t = CurrentType;
        Point? offset = ActionStep.CanHaveImageAnchor(t) ? _imageOffset
            : t == StepType.ClickImage ? new Point((int)_numX.Value, (int)_numY.Value) : null;
        if (offset is not { } o) return;
        var c = _picImage.ClientSize;
        float k = Math.Min((float)c.Width / img.Width, (float)c.Height / img.Height);
        float x = (c.Width - img.Width * k) / 2 + (img.Width / 2 + o.X) * k;
        float y = (c.Height - img.Height * k) / 2 + (img.Height / 2 + o.Y) * k;
        if (x < 0 || y < 0 || x > c.Width || y > c.Height) return;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var halo = new Pen(Color.FromArgb(200, 255, 255, 255), 4);
        using var pen = new Pen(Color.FromArgb(220, 30, 30), 2);
        foreach (var p in new[] { halo, pen })
        {
            g.DrawEllipse(p, x - 5, y - 5, 10, 10);
            g.DrawLine(p, x - 12, y, x - 6, y);
            g.DrawLine(p, x + 6, y, x + 12, y);
            g.DrawLine(p, x, y - 12, x, y - 6);
            g.DrawLine(p, x, y + 6, x, y + 12);
        }
    }

    private void ClearImage()
    {
        _imageData = "";
        _imageWidth = _imageHeight = 0;
        _imageScale = 0;
        _imageOffset = Point.Empty;
        UpdateImagePreview();
        OnTypeChanged(resetSub: false);
        ShowInfo("Đã bỏ hình mẫu — bước sẽ click theo " + (CurrentType == StepType.MouseClick ? "tọa độ X, Y." : "phần tử UI."), true);
    }

    /// <summary>Tìm thử ngay với cấu hình đang nhập: khoanh đỏ vị trí tìm được và đưa con trỏ tới điểm sẽ click.</summary>
    private async Task TestFindAsync()
    {
        var probe = BuildStep();
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

                var area = ScreenLocator.AreaOf(window);
                using var template = ScreenLocator.LoadTemplate(probe, area);
                // Click kèm hình mẫu: nhiều chỗ giống nhau thì chọn chỗ gần tọa độ lúc ghi nhất (như khi chạy).
                Point? recorded = null;
                if (probe.HasImageAnchor && (probe.Type == StepType.MouseClick || probe.HasRecordedPoint))
                {
                    var origin = probe.Target.Length > 0 && window != IntPtr.Zero ? WindowHelper.GetRect(window).Location : Point.Empty;
                    recorded = new Point(origin.X + probe.X, origin.Y + probe.Y);
                }
                result = await ScreenLocator.LocateOnceAsync(probe, area, template, recorded);
                if (result.Bounds.Width > 0)
                {
                    HighlightForm.Flash(result.Bounds);
                    if (result.Found && (probe.Type is StepType.ClickImage or StepType.ClickText || probe.HasImageAnchor))
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

    /// <summary>Chạy thử riêng bước đang soạn (với biến khai báo sẵn của công việc) và hiện kết quả.</summary>
    private async Task TestStepAsync()
    {
        var step = BuildStep();
        var error = Validate(step);
        if (error != null) { Warn(error.Value.Message); return; }
        if (_ctx.Notifier == null) { Warn("Không chạy thử được ở đây."); return; }

        _btnTestStep.Enabled = false;
        ShowInfo("Đang chạy thử…", true);
        try
        {
            // Phát video: chạy hết danh sách (dừng bằng Esc); bước khác tối đa 2 phút.
            using var cts = step.Type == StepType.PlayMedia ? new CancellationTokenSource() : new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var job = new Job { Name = "Thử bước", Variables = [.. _ctx.JobVariables] };
            var ctx = new FlowContext(job, _ctx.Notifier, RunOptions.Default, id => _ctx.Jobs.FirstOrDefault(j => j.Id == id), cts.Token);
            foreach (var v in _ctx.JobVariables.Where(v => v.Name.Trim().Length > 0)) ctx.Vars[v.Name.Trim()] = v.Value;
            var before = new Dictionary<string, string>(ctx.Vars, StringComparer.OrdinalIgnoreCase);
            bool needScreen = step.UsesInput || step.UsesScreen || step.Type is StepType.ClickElement or StepType.SetElementText;

            string summary;
            using (needScreen ? ScreenHelper.MoveAppWindowsAway() : null)
            {
                if (needScreen) await Task.Delay(250);
                summary = await Task.Run(async () =>
                {
                    var expanded = ctx.ExpandStep(step);
                    if (step.Type == StepType.If || (step.Type == StepType.Loop && step.LoopKind == LoopKind.While))
                        return $"Điều kiện → {(await ConditionEvaluator.EvaluateAsync(expanded, ctx) ? "ĐÚNG" : "SAI")}" +
                               (ctx.ConditionDetail.Length > 0 ? " — " + ctx.ConditionDetail : "");
                    if (step.Type == StepType.Assert)
                    {
                        await ConditionEvaluator.AssertAsync(expanded, ctx);
                        return "Đạt" + (ctx.ConditionDetail.Length > 0 ? " — " + ctx.ConditionDetail : "");
                    }
                    if (step.Type == StepType.Loop)
                    {
                        var frame = LoopFrame.Create(expanded, 0, 1);
                        if (!await frame.MoveNextAsync(step, ctx)) return "Không có lần lặp nào.";
                        return $"{frame.Total ?? 0} lần lặp. Lần đầu: " + VarDiff(before, ctx.Vars);
                    }
                    await StepExecutor.ExecuteAsync(expanded, job, ctx);
                    var diff = VarDiff(before, ctx.Vars);
                    return "Thành công" + (diff.Length > 0 ? ". " + diff : ".");
                });
            }
            ShowInfo("✔ " + summary, true);
        }
        catch (OperationCanceledException)
        {
            ShowInfo("✖ Hết 2 phút chạy thử — đã dừng bước.", false);
        }
        catch (Exception ex)
        {
            ShowInfo("✖ " + ex.Message, false);
        }
        finally
        {
            _btnTestStep.Enabled = true;
            Activate();
        }
    }

    private static string VarDiff(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var changed = after.Where(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value)
            .Where(kv => !kv.Key.StartsWith("loop.", StringComparison.OrdinalIgnoreCase) && kv.Key != "lastError")
            .Take(8)
            .Select(kv => $"{{{{{kv.Key}}}}} = \"{Shorten(Log.Redact(kv.Value).Replace("\r", "").Replace("\n", " ⏎ "))}\"");
        return string.Join(" · ", changed);
    }

    private void ShowInfo(string text, bool ok)
    {
        _lblCaptureInfo.Visible = true;
        _lblCaptureInfo.Text = text;
        _lblCaptureInfo.ForeColor = ok ? Color.FromArgb(0, 100, 0) : Color.FromArgb(180, 40, 20);
    }

    // ───────────────────────────── Hồ sơ trình duyệt ─────────────────────────────

    private bool IsBrowserLaunch => CurrentType == StepType.Browser && CurrentBrowser == BrowserAction.Launch;

    /// <summary>Gợi ý các hồ sơ ScheduleApp đã có của trình duyệt đang chọn.</summary>
    private void FillProfileList()
    {
        var text = _cboArgs.Text;
        _cboArgs.BeginUpdate();
        _cboArgs.Items.Clear();
        try
        {
            _cboArgs.Items.AddRange([.. BrowserProfiles.List(_cboTarget.Text)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* chỉ là gợi ý */ }
        _cboArgs.EndUpdate();
        _cboArgs.Text = text;
    }

    /// <summary>Menu các hồ sơ thật của Chrome / Edge trên máy để sao chép sang ScheduleApp.</summary>
    private void ShowRealProfiles()
    {
        var browser = _cboTarget.Text;
        var name = BrowserProfiles.DisplayName(browser);
        var profiles = BrowserProfiles.RealProfiles(browser);
        if (profiles.Count == 0)
        {
            ShowInfo($"✖ Không thấy hồ sơ nào của {name} trên máy (chỉ hỗ trợ Chrome và Edge cài đặt bình thường).", false);
            return;
        }
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel($"Sao chép hồ sơ {name} sang ScheduleApp:") { ForeColor = SystemColors.GrayText });
        foreach (var p in profiles)
            menu.Items.Add(p.ToString(), null, async (_, _) => await CopyProfileAsync(p));
        menu.Closed += (_, _) => BeginInvoke(new MethodInvoker(menu.Dispose));
        menu.Show(_btnArgsAction, new Point(0, _btnArgsAction.Height));
    }

    private async Task CopyProfileAsync(BrowserProfiles.RealProfile source)
    {
        var browserName = BrowserProfiles.DisplayName(source.Browser);
        var profileName = BrowserProfiles.SafeName(source.Name);
        if (profileName.Length == 0) profileName = BrowserProfiles.SafeName(source.Directory);
        bool exists = Directory.Exists(BrowserProfiles.Dir(source.Browser, profileName));

        var message =
            $"Sao chép hồ sơ \"{source.Name}\" của {browserName} sang hồ sơ \"{profileName}\" của ScheduleApp?\n\n" +
            $"• Chép đăng nhập (cookie), mật khẩu đã lưu, tiện ích, dấu trang — không chép bộ nhớ đệm, không thay đổi hồ sơ gốc.\n" +
            $"• {browserName} không cho điều khiển trực tiếp hồ sơ bạn đang dùng nên ScheduleApp dùng bản sao. " +
            "Đăng nhập mới sau này nằm riêng trong bản sao.\n" +
            $"• Cần đóng các cửa sổ {browserName} đang dùng hồ sơ \"{source.Name}\" trong lúc chép (vài giây tới vài phút)." +
            (exists ? $"\n\n⚠ Hồ sơ \"{profileName}\" đã có trong ScheduleApp và sẽ bị thay bằng bản chép mới." : "");
        if (MessageBox.Show(this, message, "Sao chép hồ sơ trình duyệt", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        ShowInfo($"Đang sao chép hồ sơ \"{source.Name}\"…", true);
        UseWaitCursor = true;
        Enabled = false;
        try
        {
            var result = await Task.Run(() => BrowserProfiles.CopyFromReal(source, profileName));
            _cboArgs.Text = result.Profile;
            FillProfileList();
            ShowInfo($"✔ Đã sao chép {result.Files:N0} file ({result.Bytes / 1048576.0:N0} MB) vào hồ sơ \"{result.Profile}\". " +
                     "Chạy thử bước này để mở trình duyệt với hồ sơ đó.", true);
            Log.Info($"Đã sao chép hồ sơ {browserName} \"{source.Name}\" ({source.Directory}) sang hồ sơ ScheduleApp \"{result.Profile}\".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowInfo("✖ " + ex.Message, false);
        }
        finally
        {
            Enabled = true;
            UseWaitCursor = false;
        }
    }
}
