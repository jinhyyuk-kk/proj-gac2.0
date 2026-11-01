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

[Code]
// CHANGE-001 1.4 — the Console leaves install-order markers. A machine that had the Console first can never become a
// client. The InstallTime (unix ms) lets the server compare install order; a tombstone survives uninstall.
function UnixNowMs(): String;
var
  TempFile, S: AnsiString;
  rc: Integer;
begin
  Result := '';
  TempFile := ExpandConstant('{tmp}\gac_now.txt');
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -Command "[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() | Out-File -Encoding ASCII ''' + TempFile + '''"',
       '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0) and LoadStringFromFile(TempFile, S) then
    Result := Trim(String(S));
  DeleteFile(TempFile);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RegWriteStringValue(HKLM64, 'SOFTWARE\GAC\Console', 'Machine', GetComputerNameString());
    if not RegValueExists(HKLM64, 'SOFTWARE\GAC\Console', 'InstallTime') then
      RegWriteStringValue(HKLM64, 'SOFTWARE\GAC\Console', 'InstallTime', UnixNowMs());
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    // Keep a permanent tombstone: the order cannot be reset by uninstalling (only the Owner can clear it).
    RegWriteStringValue(HKLM64, 'SOFTWARE\GAC\Console', 'EverInstalled', GetDateTimeString('yyyy-mm-dd', #0, #0));
end;
