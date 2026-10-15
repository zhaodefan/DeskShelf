; DeskShelf 安装程序脚本（Inno Setup 6）
;
; 设计要点：
; 1. per-user 安装（PrivilegesRequired=lowest）：不需要管理员权限，不弹 UAC，
;    与「开机自启写 HKCU」的设计一致；
; 2. AppMutex 与程序内的单实例互斥体同名：安装/卸载时会自动关闭正在运行的实例；
; 3. 开机自启交给程序自己的设置项（写 HKCU\Run），安装器不代为设置，
;    避免「卸载后残留自启项」；
; 4. 卸载时提示是否保留用户数据（%APPDATA%\DeskShelf），默认保留。

#define AppName "DeskShelf"
#define AppVersion "1.1.0"
#define AppPublisher "zhaodefan"
#define AppUrl "https://github.com/zhaodefan/DeskShelf"
#define ExeName "DeskShelf.exe"
; 仓库根目录：由编译命令以 /DRepoRoot=... 传入绝对路径
; （Iscc 解析文件路径时以「当前工作目录」为基准，不能靠相对路径猜测）
#ifndef RepoRoot
  #define RepoRoot "."
#endif

[Setup]
AppId={{8E4A1C6D-2F3B-4A7E-9C51-6D8B2F4A0E93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; —— 安装范围与权限 ——
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; —— 外观 ——
WizardStyle=modern
SetupIconFile={#RepoRoot}\src\DeskShelf.App\Assets\DeskShelf.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}

; —— 行为 ——
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; 与 App 内的 Mutex 名称一致，安装/卸载时会关闭正在运行的实例
AppMutex=Global\DeskShelf.SingleInstance
CloseApplications=yes
RestartApplications=no

; —— 输出 ——
OutputDir={#RepoRoot}\artifacts\installer
OutputBaseFilename=DeskShelf-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

[Languages]
; Inno Setup 6.7 起不再随包提供简体中文，这里附带官方社区翻译文件（installer\Languages\）
Name: "chinese"; MessagesFile: "{#SourcePath}\Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "开机自动启动（登录后自动运行）"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "{#RepoRoot}\artifacts\publish\win-x64\DeskShelf.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\artifacts\publish\win-x64\DeskShelf.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Registry]
; 只有用户主动勾选「开机自动启动」时才写自启项（HKCU，无需管理员权限）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "DeskShelf"; ValueData: """{app}\{#ExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#ExeName}"; Description: "立即运行 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先请程序正常退出，保证设置与内容落盘
Filename: "{cmd}"; Parameters: "/c taskkill /IM {#ExeName} /F"; Flags: runhidden; RunOnceId: "KillDeskShelf"

[UninstallDelete]
; 只清理安装目录内的运行期残留；用户数据默认保留
Type: filesandordirs; Name: "{app}\logs"

[Code]
// 卸载时询问是否保留用户数据（默认保留，避免误删用户暂存内容）
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\DeskShelf');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除 DeskShelf 的用户数据？' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                '其中包含你暂存的文本 / 图片 / 文件夹路径与设置。' + #13#10 +
                '选择「否」将保留这些数据，便于以后重新安装时继续使用。',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
