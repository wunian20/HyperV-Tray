#define MyAppName "Hyper-V 托盘监控"
#define MyAppVersion "1.0.0.0"
#define MyAppExeName "HyperV-Tray.exe"

[Setup]
AppId={{AE45C767-9466-4B43-958A-E2A04A83FD90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=wunian
DefaultDirName={autopf}\HyperV-Tray
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
OutputDir=.
OutputBaseFilename=HyperV-Tray-Setup-{#MyAppVersion}
SetupIconFile=HyperV-Tray.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："; Flags: unchecked

[Files]
Source: "HyperV-Tray.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "HyperV-Tray.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{userstartup}\HyperV-Tray.lnk"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  Shell, Shortcut: Variant;
  StartupDir, LnkPath, ExePath: string;
begin
  if CurStep = ssPostInstall then
  begin
    StartupDir := ExpandConstant('{userstartup}');
    LnkPath := StartupDir + '\HyperV-Tray.lnk';
    ExePath := ExpandConstant('{app}\{#MyAppExeName}');
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(LnkPath);
    Shortcut.TargetPath := ExePath;
    Shortcut.WorkingDirectory := ExpandConstant('{app}');
    Shortcut.IconLocation := ExePath + ',0';
    Shortcut.Description := 'Hyper-V 托盘监控';
    Shortcut.Save;
  end;
end;
