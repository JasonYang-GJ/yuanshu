#define MyAppName "元枢"
#ifndef MyAppVersion
  #define MyAppVersion "0.8.0"
#endif
#ifndef PublishRoot
  #define PublishRoot "..\artifacts\publish\win-x64"
#endif
#ifndef DistributionInclude
  #define DistributionInclude "..\artifacts\staging\distribution-notice-files.iss"
#endif
#define MyAppPublisher "元枢"
#define MyAppExeName "ScreenGuide.DesktopClient.exe"

[Setup]
AppId={{E2B9C242-2965-48BC-B2C6-CF83A2B11953}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\YuanshuDesktop
DefaultGroupName=元枢
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\candidates\v{#MyAppVersion}
OutputBaseFilename=元枢-V{#MyAppVersion}-安装包
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=no
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#include DistributionInclude

[InstallDelete]
; Retire only the previous product-owned cleanup entry; never delete user data.
Type: files; Name: "{group}\删除元枢用户数据.lnk"
Type: files; Name: "{app}\Remove-ScreenGuideUserData.ps1"

[Icons]
Name: "{group}\元枢"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--assistant"; Comment: "打开元枢并开始语音；在可见助手中可随时暂停"
Name: "{userdesktop}\元枢"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--assistant"; Comment: "打开元枢并开始语音；在可见助手中可随时暂停"

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--paused"; Description: "启动元枢（语音暂停）"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E2B9C242-2965-48BC-B2C6-CF83A2B11953}_is1';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function ProductProcessesStopped(): Boolean;
var
  Locator, Service, Processes: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name="ScreenGuide.DesktopClient.exe" OR Name="ScreenGuide.DesktopHost.exe" OR Name="yuanshu-island.exe"');
    Result := Processes.Count = 0;
  except
    Log('Cannot verify that all Yuanshu components have exited.');
  end;
end;

function IsNewerVersion(Installed, Candidate: String): Boolean;
var
  InstalledVersion, CandidateVersion: Int64;
begin
  Result := False;
  if not StrToVersion(Installed, InstalledVersion) then
  begin
    Result := True;
    exit;
  end;
  if not StrToVersion(Candidate, CandidateVersion) then
  begin
    Result := True;
    exit;
  end;
  Result := ComparePackedVersion(InstalledVersion, CandidateVersion) > 0;
end;

function HasWebView2At(RootKey: Integer): Boolean;
var
  VersionText: String;
  Version: Int64;
begin
  Result := False;
  if RegQueryStringValue(RootKey,
    'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', VersionText) then
    if StrToVersion(VersionText, Version) then
      Result := ComparePackedVersion(Version, 0) > 0;
end;

function HasWebView2(): Boolean;
begin
  Result := HasWebView2At(HKCU32) or HasWebView2At(HKLM32);
  if IsWin64 then Result := Result or HasWebView2At(HKCU64) or HasWebView2At(HKLM64);
end;

function InitializeSetup(): Boolean;
var
  InstalledVersion: String;
begin
  Result := False;
  if RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', InstalledVersion) then
    if IsNewerVersion(InstalledVersion, '{#MyAppVersion}') then
    begin
      SuppressibleMsgBox('已安装的元枢版本比此安装包更新，或版本信息无法验证。为保护现有数据，已取消安装。请使用同版本或更新版本的完整安装包。', mbError, MB_OK, IDOK);
      exit;
    end;
  if not HasWebView2() then
  begin
    SuppressibleMsgBox('内置灵动岛需要 Microsoft Edge WebView2 Runtime。当前未检测到该组件，安装尚未开始。请从微软官方网站 https://developer.microsoft.com/microsoft-edge/webview2/ 安装 Evergreen Runtime（64 位），完成后重新运行元枢安装包。本安装器不会联网下载或自动安装系统组件。', mbError, MB_OK, IDOK);
    exit;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not ProductProcessesStopped() then
    Result := '元枢或灵动岛仍在运行。请从元枢托盘菜单选择“退出元枢”，并关闭旧版灵动岛后再继续。安装器不会强制结束程序或开启麦克风。';
end;

function IsLegacyIslandExecutable(ExecutablePath: String): Boolean;
var
  Script, EscapedPath: String;
  ExitCode: Integer;
begin
  EscapedPath := ExecutablePath;
  StringChangeEx(EscapedPath, '''', '''''', True);
  Script := '$i=[Diagnostics.FileVersionInfo]::GetVersionInfo(''' + EscapedPath + '''); ' +
    'if($i.ProductName -ceq ''Yuanshu Island'' -and $i.FileDescription -ceq ''Yuanshu Island''){exit 0}else{exit 1}';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "' + Script + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  if Result then Result := ExitCode = 0;
end;

procedure MigrateExistingIslandAutostart();
var
  Command, ExecutablePath, Suffix, ExistingMain: String;
  ClosingQuote: Integer;
begin
  if not RegQueryStringValue(HKCU, RunKey, 'Yuanshu Island', Command) then exit;
  if (Length(Command) < 3) or (Copy(Command, 1, 1) <> '"') then exit;
  ClosingQuote := Pos('"', Copy(Command, 2, Length(Command)));
  if ClosingQuote = 0 then exit;
  ExecutablePath := Copy(Command, 2, ClosingQuote - 1);
  Suffix := Copy(Command, ClosingQuote + 2, Length(Command));
  if Suffix <> ' --autostart' then exit;
  if CompareText(ExtractFileName(ExecutablePath), 'yuanshu-island.exe') <> 0 then exit;
  if not FileExists(ExecutablePath) then exit;
  if not IsLegacyIslandExecutable(ExecutablePath) then exit;
  Command := '"' + ExpandConstant('{app}\{#MyAppExeName}') + '" --background';
  if RegQueryStringValue(HKCU, RunKey, 'ScreenGuideDesktop', ExistingMain) then
    if ExistingMain <> Command then exit;
  if RegWriteStringValue(HKCU, RunKey, 'ScreenGuideDesktop', Command) then
  begin
    RegDeleteValue(HKCU, RunKey, 'Yuanshu Island');
    Log('Migrated the existing verified Island autostart preference to Yuanshu.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then MigrateExistingIslandAutostart();
end;

function InitializeUninstall(): Boolean;
begin
  Log('Unified product uninstall code 0.8.0 initialized.');
  Result := ProductProcessesStopped();
  if not Result then
  begin
    SuppressibleMsgBox('请先从元枢托盘菜单正常退出，并关闭旧版灵动岛，再卸载。程序和用户数据尚未改动。', mbError, MB_OK, IDOK);
    exit;
  end;
  if not UninstallSilent then
    MsgBox('卸载只删除元枢及内置灵动岛的程序文件。聊天记录、任务历史、设置、密钥和灵动岛位置会保留，重新安装后继续使用。', mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, ExpectedCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Log('Checking the exact product-owned startup entry before uninstall.');
    ExpectedCommand := '"' + ExpandConstant('{app}\{#MyAppExeName}') + '" --background';
    if RegQueryStringValue(HKCU, RunKey, 'ScreenGuideDesktop', Command) then
    begin
      if CompareText(Command, ExpectedCommand) = 0 then
      begin
        if RegDeleteValue(HKCU, RunKey, 'ScreenGuideDesktop') then
          Log('Removed the verified product-owned startup entry.')
        else Log('Could not remove the product-owned startup entry.');
      end
      else Log('Preserved a startup entry pointing to a different command.');
    end
    else Log('No product-owned startup entry exists.');
  end;
end;
