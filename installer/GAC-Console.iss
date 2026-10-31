; GAC 2.0 Console installer (operator PC).
; Silent: GAC-Console-Setup.exe /VERYSILENT

#define AppVersion "2.0.0"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{2D8F4C67-1A3B-4E9D-B5C2-7E6A0F1D3B84}
AppName=GAC 2.0 콘솔
AppVersion={#AppVersion}
AppPublisher=JH
AppCopyright=Copyright (c) 2026 JH (MIT License)
DefaultDirName={autopf}\GAC 2.0\Console
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=GAC-Console-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
WizardStyle=modern
CloseApplications=yes
UninstallDisplayName=GAC 2.0 콘솔
UninstallDisplayIcon={app}\Gac.Console.exe
SetupLogging=yes

[Languages]
Name: "korean"; MessagesFile: "lang\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로 가기 만들기"; GroupDescription: "추가 작업:"

[Files]
Source: "{#PublishDir}\console\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GAC 2.0 콘솔"; Filename: "{app}\Gac.Console.exe"
Name: "{autodesktop}\GAC 2.0 콘솔"; Filename: "{app}\Gac.Console.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Gac.Console.exe"; Description: "GAC 2.0 콘솔 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; Saved server address / pinned certificate thumbprint of the user who uninstalls.
Type: files; Name: "{userappdata}\GAC\console.json"
Type: dirifempty; Name: "{userappdata}\GAC"
