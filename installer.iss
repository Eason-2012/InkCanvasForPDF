#ifndef Arch
  ; 未指定架构时默认 x64（Inno Setup GUI 直接编译 / 调试用）；x86、arm64 用命令行 /DArch= 覆盖
  #define Arch "x64"
#endif

#define MyAppName "InkCanvasForPDF"
#define MyAppVersion "0.20.1-Dev"
#define MyAppPublisher "@Eason-2012"
#define MyAppURL "https://github.com/Eason-2012/InkCanvasForPDF"
#define MyAppExeName "InkCanvasForPDF.exe"
#define MyAppAssocName MyAppName + " File"
#define MyAppAssocExt ".myp"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt

[Setup]
; NOTE: The value of AppId uniquely identifies this application. Do not use the same AppId value in installers for other applications.
AppId={{1CFCD324-5CE2-42E6-8A47-C235C9EABD09}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
;AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#elif Arch == "x86"
; 32 位程序不进 64 位模式：保证安装器注册表视图与 32 位 app 写入视图一致
ArchitecturesAllowed=x86compatible
#else
#error Arch must be one of: x64 x86 arm64
#endif
ChangesAssociations=yes
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
; Remove the following line to run in administrative install mode (install for all users).
PrivilegesRequired=lowest
OutputDir=C:\Users\ncljh\Documents\ClassPDF\main
OutputBaseFilename=ICPDF_{#Arch}_setup
SolidCompression=yes
WizardStyle=modern dynamic windows11

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "C:\Users\ncljh\Documents\ClassPDF\main\bin\Release\net10.0-windows10.0.19041.0\win-{#Arch}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\ncljh\Documents\ClassPDF\main\bin\Release\net10.0-windows10.0.19041.0\win-{#Arch}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: Don't use "Flags: ignoreversion" on any shared system files
; 覆盖安装保留 Data：本节不登记 Data，安装期也无任何删除逻辑，同 AppId 升级只覆盖已登记文件

[Registry]
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocExt}\OpenWithProgids"; ValueType: string; ValueName: "{#MyAppAssocKey}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}"; ValueType: string; ValueName: ""; ValueData: "{#MyAppAssocName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:ProgramOnTheWeb,{#MyAppName}}"; Filename: "{#MyAppURL}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Code]
var
  AlreadyInstalled: Boolean;
  RestartAfterInstall: Boolean;
  { 持有句柄即持锁（从不关闭），进程退出自动释放 }
  SetupMutexHandle: Cardinal;

procedure SleepMs(Milliseconds: Cardinal);
  external 'Sleep@kernel32.dll stdcall';

function CreateMutexInstance(lpMutexAttributes: Integer; bInitialOwner: Integer;
  lpName: String): Cardinal;
  external 'CreateMutexW@kernel32.dll stdcall';

function GetLastWinError: Cardinal;
  external 'GetLastError@kernel32.dll stdcall';

procedure SHChangeNotify(wEventId: Cardinal; uFlags: Cardinal;
  dwItem1: Cardinal; dwItem2: Cardinal);
  external 'SHChangeNotify@shell32.dll stdcall';

{ 启动：解析 restart 参数 → 检测已安装 → 抢互斥体防多开。
  已安装时由 ShouldSkipPage 跳过全部向导页直达安装、装完自动退出（单进程，不换子实例）。 }
function InitializeSetup(): Boolean;
var
  Dir: String;
begin
  Result := True;
  RestartAfterInstall := (ExpandConstant('{param:restart|0}') = '1') or
                         (ExpandConstant('{param:RESTART|0}') = '1');

  // 已安装检测：读卸载注册表 InstallLocation（InitializeSetup 阶段 app 常量尚未初始化，不能用它）
  AlreadyInstalled := RegQueryStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{1CFCD324-5CE2-42E6-8A47-C235C9EABD09}_is1',
      'InstallLocation', Dir) and FileExists(AddBackslash(Dir) + '{#MyAppExeName}');

  { 防多开（同名互斥体已存在 = 另一个安装实例正在运行） }
  SetupMutexHandle := CreateMutexInstance(0, 0, 'InkCanvasForPDFSetupMutex');
  if GetLastWinError = 183 then { ERROR_ALREADY_EXISTS }
  begin
    SuppressibleMsgBox('安装程序已在运行，请勿重复启动。', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

// 向导是否跳过指定页：已安装时跳过全部前页直达安装进度、跳过完成页实现装完自动退出；
// 首次安装不跳任何页。/SILENT 下向导页本不显示，此函数无副作用（wpPreparing/wpInstalling
// Inno 不会回调，无需列出）
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if not AlreadyInstalled then
    Exit;
  case PageID of
    wpWelcome, wpLicense, wpPassword, wpInfoBefore, wpSelectDir,
    wpSelectComponents, wpSelectTasks, wpSelectProgramGroup,
    wpReady, wpInfoAfter, wpFinished:
      Result := True;
  end;
end;

{ 等待 InkCanvasForPDF.exe 所有实例退出后才开始写文件（静默/交互都走），上限 60 秒 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  I: Integer;
  Rc: Integer;
  Running: Boolean;
begin
  Result := '';
  for I := 1 to 120 do
  begin
    Running := False;
    if Exec(ExpandConstant('{sys}\cmd.exe'),
        '/C "tasklist /NH | findstr /I {#MyAppExeName} >NUL"',
        '', SW_HIDE, ewWaitUntilTerminated, Rc) then
      Running := (Rc = 0);
    if not Running then
      Exit;
    SleepMs(500);
  end;
  Result := '{#MyAppName} 仍在运行，请先退出应用后重试。';
end;

{ /restart=1 → 装完拉起应用（无参数永远不自动启动） }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Rc: Integer;
begin
  if (CurStep = ssPostInstall) and RestartAfterInstall then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '', '', SW_SHOW, ewNoWait, Rc);
end;

{ 卸载：解绑 .pdf 默认关联（仅当指向本应用）+ 清理自注册 ProgID + 删除整个安装文件夹（含 Data） }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ProgId: String;
  Dummy: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { 1) UserChoice 指向本应用才删除——不动用户其他的默认选择 }
    if RegQueryStringValue(HKCU,
         'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice',
         'ProgId', ProgId) and (CompareText(ProgId, 'InkCanvasForPDF.PDF') = 0) then
      RegDeleteKeyIncludingSubkeys(HKCU,
        'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice');

    { 2) HKCU 类 .pdf 默认值指向本应用才删除（否则系统会回落到已删除的 ProgID） }
    if RegQueryStringValue(HKCU, 'Software\Classes\.pdf', '', ProgId) and
       (CompareText(ProgId, 'InkCanvasForPDF.PDF') = 0) then
      RegDeleteValue(HKCU, 'Software\Classes\.pdf', '');

    { 3) 清理本应用运行时自注册的 ProgID 与 .pdf 候选列表项 }
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\InkCanvasForPDF.PDF');
    RegDeleteValue(HKCU, 'Software\Classes\.pdf\OpenWithProgids', 'InkCanvasForPDF.PDF');

    { 4) 通知 Shell 刷新文件图标与打开方式缓存 }
    SHChangeNotify($08000000 {SHCNE_ASSOCCHANGED}, $1000, 0, 0);

    { 5) 删除整个安装文件夹（含 Data、残余文件）；DelTree 返回值忽略，不弹错 }
    Dummy := DelTree(ExpandConstant('{app}'), True, True, True);
  end;
end;
