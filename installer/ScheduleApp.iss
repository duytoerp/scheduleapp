; Bộ cài ScheduleApp (Inno Setup 6) — cài cho người dùng hiện tại, không cần quyền admin.
; Biên dịch: chạy build.ps1 ở thư mục gốc (tự publish rồi gọi ISCC), hoặc:
;   ISCC.exe /DAppVersion=2.1.0 installer\ScheduleApp.iss
; Cài vào %LocalAppData%\Programs\ScheduleApp → ScheduleApp tự cập nhật được (thư mục ghi được).

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
; Đóng ScheduleApp đang chạy trước khi thay file (Restart Manager).
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.DesktopIcon=Tạo biểu tượng trên Desktop
english.StartupIcon=Tự chạy ScheduleApp (thu nhỏ ở khay) khi đăng nhập Windows
english.LaunchApp=Mở ScheduleApp
english.Extra=Tùy chọn thêm:

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
Filename: "{app}\ScheduleApp.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Bản cũ còn sót sau khi tự cập nhật. Dữ liệu người dùng (%AppData%\ScheduleApp) được giữ lại.
Type: files; Name: "{app}\ScheduleApp.exe.old"
