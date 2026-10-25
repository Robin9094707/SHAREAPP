#define MyAppVersion "1.0.1"
[Setup]
AppId={{EB1DC50A-D27A-4164-97B3-050550550001}
AppName=Robins Share
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\RobinsShare
DefaultGroupName=Robins Share
UninstallDisplayIcon={app}\RobinsShare.exe
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=RobinsShare-Windows-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
SetupIconFile=RobinShare\Assets\RobinShare.ico
[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; Flags: unchecked
[Files]
Source: "..\dist\windows\RobinsShare.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\Lokale-Verbindung.ps1"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{group}\Robins Share"; Filename: "{app}\RobinsShare.exe"
Name: "{autodesktop}\Robins Share"; Filename: "{app}\RobinsShare.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\RobinsShare.exe"; Description: "Robins Share starten"; Flags: nowait postinstall skipifsilent
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "RobinsShare"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\*\shell\RobinsShare"; Flags: uninsdeletekey
