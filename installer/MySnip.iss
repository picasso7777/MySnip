#define MyAppName "MySnip"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "MySnip"
#define MyAppExeName "MySnip.exe"

[Setup]
AppId={{8E90D052-20D8-4D6D-BB28-8C9A86521EA3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\MySnip
DefaultGroupName=MySnip
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=MySnip-Setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=MySnip
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=MySnip 安裝程式
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑"; GroupDescription: "其他工作："; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MySnip"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\MySnip"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "啟動 MySnip"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MySnip');
end;
