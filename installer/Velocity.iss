#define AppName "Velocity"
#define AppVersion "0.1.0"
#define AppPublisher "Velocity Project"
#define AppExeName "Velocity.App.exe"

[Setup]
AppId={{B9B4D4F3-0D2C-4C0E-9C6F-001000000001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Velocity
DefaultGroupName=Velocity
OutputDir=..\artifacts\installer
OutputBaseFilename=Velocity-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExeName}
; User downloads and %LocalAppData%\Velocity are intentionally preserved on uninstall.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\artifacts\velocity-app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\browser-extension\*"; DestDir: "{app}\browser-extension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Velocity"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Velocity"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch Velocity"; Flags: nowait postinstall skipifsilent
