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
#define AppPublisher "zhaodefan"
#define AppUrl "https://github.com/zhaodefan/DeskShelf"
#define ExeName "DeskShelf.exe"
; 仓库根目录：由编译命令以 /DRepoRoot=... 传入绝对路径
; （Iscc 解析文件路径时以「当前工作目录」为基准，不能靠相对路径猜测）
#ifndef RepoRoot
  #define RepoRoot "."
#endif

; 版本号从 Directory.Build.props 读取（单一来源）。
;
; 为什么不再硬编码：程序集版本由 Directory.Build.props 的 <Version> 决定，
; 而安装包文件名/注册表版本由这里的 AppVersion 决定；两处手写必然会漂移
; （实际发生过：构建 1.1.0 却产出 DeskShelf-1.0.99-setup.exe）。
; 命令行仍然可以用 /DAppVersion=x.y.z 覆盖。
; 版本号：由发布脚本以 /DAppVersion=x.y.z 传入（与 Directory.Build.props 同源）。
;
; 为什么不硬编码：程序集版本由 Directory.Build.props 的 <Version> 决定，
; 安装包文件名与注册表版本由这里的 AppVersion 决定；两处手写必然会漂移
; （实际发生过：构建 1.1.0 却产出 DeskShelf-1.0.99-setup.exe）。
; 这里保留兜底值，并用 #ifndef 让命令行传值优先，同时让脚本单独编译时也能出包。
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
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
; 严格 per-user 安装：不提供「为所有用户安装」选项。
; 原因：允许提权会把程序装进 Program Files / ProgramData，而卸载项与快捷方式分散到
; 机器级位置，一旦注册表项被清理就会留下「删不掉的孤儿目录」（v1.0.0 实际踩到过）。
; 与「开机自启写 HKCU、数据写 %APPDATA%」的设计保持一致。
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; —— 外观 ——
WizardStyle=modern
SetupIconFile={#RepoRoot}\src\DeskShelf.App\Assets\deskshelf.ico
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
Source: "{#RepoRoot}\artifacts\publish\win-x64\deskshelf.ico"; DestDir: "{app}"; Flags: ignoreversion
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
// 卸载时处理用户数据（%APPDATA%\DeskShelf）。
//
// 三条规则，按优先级：
//   1. 静默卸载（/SILENT 或 /VERYSILENT）—— 一律【保留】数据。
//      原因：静默模式下 MsgBox 不会真正询问，返回的按钮不可靠；而「卸载时误删用户
//      暂存内容」属于不可接受的数据损失。需要清理请显式加 /DELETEUSERDATA。
//   2. 交互卸载 —— 弹窗询问，默认按钮是「否」（保留）。
//   3. 显式 /DELETEUSERDATA —— 无论静默与否都删除，供自动化清理使用。
function DeleteUserDataRequested(): Boolean;
begin
  Result := ExpandConstant('{param:DELETEUSERDATA|0}') = '1';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  ShouldDelete: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  DataDir := ExpandConstant('{userappdata}\DeskShelf');
  if not DirExists(DataDir) then
    Exit;

  if DeleteUserDataRequested() then
  begin
    ShouldDelete := True;
  end
  else if UninstallSilent() then
  begin
    // 静默卸载：保留数据（并在日志里说明，便于排查）
    Log('保留用户数据目录（静默卸载默认保留）：' + DataDir);
    ShouldDelete := False;
  end
  else
  begin
    ShouldDelete := MsgBox('是否同时删除 DeskShelf 的用户数据？' + #13#10 + #13#10 +
                           DataDir + #13#10 + #13#10 +
                           '其中包含你暂存的文本 / 图片 / 文件夹路径与设置。' + #13#10 +
                           '选择「否」将保留这些数据，便于以后重新安装时继续使用。',
                           mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;

  if ShouldDelete then
  begin
    DelTree(DataDir, True, True, True);
    Log('已删除用户数据目录：' + DataDir);
  end;
end;
