; Klang Soda (WinUI 3) 独立安装程序；与上游 Klang 使用不同安装标识和目录。
; ISCC.exe /DAppVersion=2.8.0.1 /DSourceDir=..\publish\KlangSoda installer\Klang.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\KlangSoda"
#endif

[Setup]
AppId={{27CA8C73-6EE3-49B9-A163-5292DD5E858A}
AppName=Klang Soda
AppVersion={#AppVersion}
AppVerName=Klang Soda {#AppVersion}
AppPublisher=Bigcheese233
AppPublisherURL=https://github.com/Bigcheese233/klang-soda
AppSupportURL=https://github.com/Bigcheese233/klang-soda/issues
AppUpdatesURL=https://github.com/Bigcheese233/klang-soda/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Klang Soda
DefaultGroupName=Klang Soda
DisableProgramGroupPage=yes
; 默认只为当前用户安装（不需要管理员权限）；安装时可以选择为所有用户安装
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist-installer
OutputBaseFilename=KlangSoda-Setup-{#AppVersion}-x64
SetupIconFile=..\Klang.WinUI\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Klang.Soda.exe
UninstallDisplayName=Klang Soda
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.KlangRunning=Klang Soda is running and must be closed before continuing.%n%nClose Klang Soda now?
english.KlangStillRunning=Klang Soda could not be closed. Please exit Klang Soda and try again.
english.KlangExitFirst=Please exit Klang Soda and try again.
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
chinesesimplified.KlangRunning=Klang Soda 正在运行，需要先将其关闭才能继续。%n%n是否现在关闭 Klang Soda？
chinesesimplified.KlangStillRunning=无法关闭 Klang Soda，请手动退出后重试。
chinesesimplified.KlangExitFirst=请先退出 Klang Soda，然后重试。
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Klang Soda"; Filename: "{app}\Klang.Soda.exe"
Name: "{autodesktop}\Klang Soda"; Filename: "{app}\Klang.Soda.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Klang.Soda.exe"; Description: "{cm:LaunchProgram,Klang Soda}"; Flags: nowait postinstall skipifsilent

[Code]
// CloseApplications 走 Restart Manager，只会请求程序自行退出；后台分析子进程（同名 exe，
// 带 --klang-analysis-worker）没有窗口，请求不到。这里在安装和卸载前统一检测并结束所有
// Klang.Soda.exe 进程：先发关闭请求，等不到再强制结束。

function KlangRunning(): Boolean;
var
  ResultCode: Integer;
begin
  // tasklist 无论是否找到都返回 0，用 find 判断：找到返回 0，找不到返回 1
  Result := Exec(ExpandConstant('{cmd}'),
    '/C tasklist /FI "IMAGENAME eq Klang.Soda.exe" /NH | find /I "Klang.Soda.exe" >nul',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function WaitKlangExit(TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while KlangRunning() and (Waited < TimeoutMs) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not KlangRunning();
end;

function StopKlangSoda(): Boolean;
var
  ResultCode: Integer;
begin
  // 先请求关闭主窗口，让它保存播放状态
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Klang.Soda.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not WaitKlangExit(4000) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM Klang.Soda.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    WaitKlangExit(3000);
  end;
  // 给系统一点时间释放文件句柄
  Sleep(300);
  Result := not KlangRunning();
end;

// 返回空字符串表示可以继续；否则返回要显示的错误
function EnsureKlangClosed(Silent: Boolean): String;
begin
  Result := '';
  if not KlangRunning() then Exit;
  if not Silent then
    if MsgBox(CustomMessage('KlangRunning'), mbConfirmation, MB_YESNO) <> IDYES then
    begin
      Result := CustomMessage('KlangExitFirst');
      Exit;
    end;
  if not StopKlangSoda() then
    Result := CustomMessage('KlangStillRunning');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := EnsureKlangClosed(WizardSilent());
end;

function InitializeUninstall(): Boolean;
var
  Err: String;
begin
  Err := EnsureKlangClosed(UninstallSilent());
  Result := Err = '';
  if not Result then
    SuppressibleMsgBox(Err, mbError, MB_OK, IDOK);
end;
