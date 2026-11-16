; Klang Soda (WinUI 3) 独立安装程序；与上游 Klang 使用不同安装标识和目录。
; ISCC.exe /DAppVersion=2.7.6.1 /DSourceDir=..\publish\KlangSoda installer\Klang.iss

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

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Klang Soda"; Filename: "{app}\Klang.Soda.exe"
Name: "{autodesktop}\Klang Soda"; Filename: "{app}\Klang.Soda.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Klang.Soda.exe"; Description: "{cm:LaunchProgram,Klang Soda}"; Flags: nowait postinstall skipifsilent
