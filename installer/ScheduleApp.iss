; Bộ cài ScheduleApp (Inno Setup 6.1 trở lên) — cài cho người dùng hiện tại, không cần quyền admin.
; Biên dịch: chạy build.ps1 ở thư mục gốc (tự publish rồi gọi ISCC), hoặc:
;   ISCC.exe /DAppVersion=2.1.0 installer\ScheduleApp.iss
; Cài vào %LocalAppData%\Programs\ScheduleApp → ScheduleApp tự cập nhật được (thư mục ghi được).
; ScheduleApp.exe cần .NET 10 Desktop Runtime (x64): máy chưa có thì bộ cài hỏi rồi tải và cài từ Microsoft (xem [Code]).

#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\publish\ScheduleApp.exe")
#endif

[Setup]
AppId={{6E2A4C1B-7D3F-4B8E-9A51-3C7F0B2D9E64}
AppName=ScheduleApp
AppVersion={#AppVersion}
AppVerName=ScheduleApp {#AppVersion}
AppPublisher=ScheduleApp
DefaultDirName={autopf}\ScheduleApp
DefaultGroupName=ScheduleApp
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\publish
OutputBaseFilename=ScheduleApp-Setup-{#AppVersion}
UninstallDisplayIcon={app}\ScheduleApp.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; ScheduleApp đang chạy: hỏi rồi mới đóng (Restart Manager), chỉ yêu cầu đóng chứ không buộc tắt —
; "force" có thể giết ScheduleApp khi flow đang chạy dở một bước (đang gõ, đang click…).
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.DesktopIcon=Tạo biểu tượng trên Desktop
english.StartupIcon=Tự chạy ScheduleApp (thu nhỏ ở khay) khi đăng nhập Windows
english.LaunchApp=Mở ScheduleApp
english.Extra=Tùy chọn thêm:
english.RuntimeTitle=.NET 10 Desktop Runtime
english.RuntimeDescription=ScheduleApp cần .NET 10 Desktop Runtime (x64) của Microsoft để chạy.
english.RuntimeMissing=Máy này chưa có .NET 10 Desktop Runtime (x64) — ScheduleApp cần nó để chạy.
english.RuntimeAsk=Tải từ Microsoft (khoảng 60 MB) và cài luôn trong lúc cài ScheduleApp? Windows sẽ hỏi quyền quản trị khi cài.
english.RuntimeInstalling=Đang cài .NET 10 Desktop Runtime (x64)…
english.RuntimeInstallingHint=Windows hỏi quyền quản trị thì bấm Có (Yes). Có thể mất vài phút.
english.RuntimeDeclined=Bạn chọn chưa cài .NET 10 Desktop Runtime (x64).
english.RuntimeCancelled=Đã hủy tải .NET 10 Desktop Runtime (x64).
english.RuntimeDownloadFailed=Không tải được .NET 10 Desktop Runtime (x64): %1
english.RuntimeInstallCancelled=Đã hủy cài .NET 10 Desktop Runtime (x64) (chưa cấp quyền quản trị?).
english.RuntimeInstallFailed=Cài .NET 10 Desktop Runtime (x64) chưa xong (mã lỗi %1).
english.RuntimeStartFailed=Không chạy được bộ cài .NET 10 Desktop Runtime (x64): %1
english.RuntimeManual=ScheduleApp vẫn được cài nhưng chỉ mở được khi máy có .NET 10 Desktop Runtime (x64). Mở trang tải của Microsoft ngay bây giờ? Ở đó chọn ".NET Desktop Runtime 10" → Windows x64.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Extra}"
Name: "startup"; Description: "{cm:StartupIcon}"; GroupDescription: "{cm:Extra}"

[Files]
Source: "..\publish\ScheduleApp.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ScheduleApp"; Filename: "{app}\ScheduleApp.exe"
Name: "{autodesktop}\ScheduleApp"; Filename: "{app}\ScheduleApp.exe"; Tasks: desktopicon

[Registry]
; Cùng khóa với nút "Khởi động cùng Windows" trong ứng dụng.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ScheduleApp"; \
  ValueData: """{app}\ScheduleApp.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Chưa có .NET 10 (người dùng không cài / cài lỗi) thì không đề nghị mở — ScheduleApp sẽ chỉ báo thiếu .NET.
Filename: "{app}\ScheduleApp.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent; Check: DesktopRuntimeInstalled

[UninstallDelete]
; Bản cũ còn sót sau khi tự cập nhật. Dữ liệu người dùng (%AppData%\ScheduleApp) được giữ lại.
Type: files; Name: "{app}\ScheduleApp.exe.old"

[Code]
// ScheduleApp.exe là bản publish phụ thuộc framework (.github/workflows/release.yml) → máy phải có .NET 10 Desktop Runtime x64.
// Chưa có: hỏi, rồi tải bộ cài của Microsoft và cài ngầm (bộ cài .NET tự hỏi quyền quản trị). Không tải / không cài được
// hoặc người dùng không đồng ý: giải thích và đề nghị mở trang tải — ScheduleApp vẫn được cài.
const
  RuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  RuntimeFile = 'windowsdesktop-runtime-win-x64.exe';
  RuntimePage = 'https://dotnet.microsoft.com/download/dotnet/10.0';

var
  DownloadPage: TDownloadWizardPage;
  RuntimeWanted: Boolean;
  RuntimeRestart: Boolean;

// Thư mục .NET có shared\Microsoft.WindowsDesktop.App\10.x (bản chính thức — bỏ qua bản thử "10.0.0-rc…").
function HasDesktopRuntime10(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if Root = '' then
    Exit;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (Pos('-', FindRec.Name) = 0) then
          Result := True;
      until Result or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Đúng các chỗ ScheduleApp.exe (apphost .NET) tìm runtime: thư mục mặc định (máy ARM64: bản x64 nằm trong dotnet\x64) và thư mục
// .NET đã đăng ký (registry 32-bit). Không dựa vào danh sách phiên bản trong registry: bản đã gỡ / đã thay bằng bản vá mới vẫn còn ở đó.
function DesktopRuntimeInstalled(): Boolean;
var
  Location: String;
begin
  Result := HasDesktopRuntime10(ExpandConstant('{commonpf64}\dotnet'))
    or HasDesktopRuntime10(ExpandConstant('{commonpf64}\dotnet\x64'));
  if (not Result) and RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) then
    Result := HasDesktopRuntime10(Location);
end;

// Giải thích lý do, nói rõ ScheduleApp cần .NET 10 Desktop Runtime và đề nghị mở trang tải của Microsoft.
procedure OfferRuntimePage(const Reason: String);
var
  ErrorCode: Integer;
begin
  if SuppressibleMsgBox(Reason + #13#10#13#10 + CustomMessage('RuntimeManual'), mbInformation, MB_YESNO, IDNO) = IDYES then
    ShellExecAsOriginalUser('open', RuntimePage, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

// Tải bộ cài .NET (trang tải có nút hủy), chạy "/install /quiet /norestart" và chờ xong, rồi kiểm tra lại.
procedure InstallRuntime();
var
  Downloaded, Started: Boolean;
  ResultCode: Integer;
begin
  Downloaded := False;
  Started := False;
  ResultCode := 0;
  DownloadPage.Clear;
  DownloadPage.Add(RuntimeUrl, RuntimeFile, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      Downloaded := True;
    except
      if DownloadPage.AbortedByUser then
        OfferRuntimePage(CustomMessage('RuntimeCancelled'))
      else
        OfferRuntimePage(FmtMessage(CustomMessage('RuntimeDownloadFailed'), [GetExceptionMessage]));
    end;
    if Downloaded then
    begin
      DownloadPage.SetText(CustomMessage('RuntimeInstalling'), CustomMessage('RuntimeInstallingHint'));
      DownloadPage.ProgressBar.Style := npbstMarquee;
      Started := ShellExec('', ExpandConstant('{tmp}\') + RuntimeFile, '/install /quiet /norestart', '',
        SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
      Log('Bộ cài .NET 10 Desktop Runtime: mã ' + IntToStr(ResultCode));
    end;
  finally
    DownloadPage.Hide;
  end;
  if not Downloaded then
    Exit;
  if DesktopRuntimeInstalled() then
    RuntimeRestart := (ResultCode = 3010) or (ResultCode = 1641)
  else if not Started then
    OfferRuntimePage(FmtMessage(CustomMessage('RuntimeStartFailed'), [SysErrorMessage(ResultCode)]))
  else if (ResultCode = 1223) or (ResultCode = 1602) then
    OfferRuntimePage(CustomMessage('RuntimeInstallCancelled'))
  else
    OfferRuntimePage(FmtMessage(CustomMessage('RuntimeInstallFailed'), [IntToStr(ResultCode)]));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if DesktopRuntimeInstalled() then
    Log('.NET 10 Desktop Runtime (x64): đã có.')
  else
  begin
    Log('.NET 10 Desktop Runtime (x64): chưa có.');
    RuntimeWanted := SuppressibleMsgBox(CustomMessage('RuntimeMissing') + #13#10#13#10 + CustomMessage('RuntimeAsk'),
      mbConfirmation, MB_YESNO, IDYES) = IDYES;
    if not RuntimeWanted then
      OfferRuntimePage(CustomMessage('RuntimeDeclined'));
  end;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(CustomMessage('RuntimeTitle'), CustomMessage('RuntimeDescription'), @OnDownloadProgress);
end;

// Bấm Install: tải + cài .NET trước khi chép ScheduleApp. Chỉ thử một lần — lỗi thì đã giải thích, vẫn cài ScheduleApp.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and RuntimeWanted then
  begin
    RuntimeWanted := False;
    InstallRuntime();
  end;
end;

// Bộ cài .NET báo cần khởi động lại (3010) → Inno hỏi khởi động lại khi cài xong.
function NeedRestart(): Boolean;
begin
  Result := RuntimeRestart;
end;
