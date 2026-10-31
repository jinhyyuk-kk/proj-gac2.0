; GAC 2.0 Agent installer (every school PC): Agent service + teacher tray app.
; Silent mass rollout:
;   GAC-Agent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /SERVER=ADMIN-PC:8443 /CODE=K7QM-2XPA-9RTD
; Silent uninstall: "C:\Program Files\GAC 2.0\Agent\unins000.exe" /VERYSILENT

#define AppVersion "2.0.0"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{9B3E7A51-6C2D-4F8B-A1E4-5D0C3B7F2A92}
AppName=GAC 2.0 에이전트
AppVersion={#AppVersion}
AppPublisher=JH
AppCopyright=Copyright (c) 2026 JH (MIT License)
DefaultDirName={autopf}\GAC 2.0\Agent
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=GAC-Agent-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
WizardStyle=modern
CloseApplications=no
UninstallDisplayName=GAC 2.0 에이전트
UninstallDisplayIcon={app}\Tray\Gac.Tray.exe
SetupLogging=yes

[Languages]
Name: "korean"; MessagesFile: "lang\Korean.isl"

[Files]
Source: "{#PublishDir}\agent\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\tray\*"; DestDir: "{app}\Tray"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Start the tray app at every user logon (runs with the user's own rights).
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GAC Tray"; ValueData: """{app}\Tray\Gac.Tray.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Tray\Gac.Tray.exe"; Description: "선생님용 GAC 트레이 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  ServerPage: TInputQueryWizardPage;

function ParamServer(): String;
begin
  Result := Trim(ExpandConstant('{param:SERVER|}'));
end;

function ParamCode(): String;
begin
  Result := Trim(ExpandConstant('{param:CODE|}'));
end;

procedure InitializeWizard();
begin
  ServerPage := CreateInputQueryPage(wpSelectDir, 'GAC 서버에 등록', '이 PC를 학교 GAC 서버에 등록합니다.',
    '관리 PC(GAC 서버) 설치 마지막 화면에 나온 서버 주소와 등록 코드를 입력하세요.' + #13#10 +
    '이미 등록된 PC를 업그레이드할 때는 비워 두어도 됩니다.');
  ServerPage.Add('서버 주소 (예: ADMIN-PC:8443):', False);
  ServerPage.Add('등록 코드 (예: K7QM-2XPA-9RTD):', False);
  ServerPage.Values[0] := ParamServer();
  ServerPage.Values[1] := ParamCode();
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = ServerPage.ID) and (ParamServer() <> '') and (ParamCode() <> '');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ServerPage.ID) and ((Trim(ServerPage.Values[0]) = '') <> (Trim(ServerPage.Values[1]) = '')) then
  begin
    MsgBox('서버 주소와 등록 코드를 모두 입력하거나, 둘 다 비워 두세요.', mbError, MB_OK);
    Result := False;
  end;
end;

function SafeArg(S: String): String;
begin
  { Only letters, digits and . : - _ are meaningful in an address or code; drop anything else. }
  Result := S;
  StringChangeEx(Result, '"', '', True);
  StringChangeEx(Result, ' ', '', True);
  StringChangeEx(Result, '&', '', True);
  StringChangeEx(Result, '|', '', True);
end;

procedure StopAgent();
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop GACAgent', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(2000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Gac.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Gac.Tray.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAgent(); { upgrade in place keeps C:\ProgramData\GAC\Agent (identity, queue, settings) }
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  rc: Integer;
  Server, Code: String;
begin
  if CurStep = ssPostInstall then
  begin
    Server := SafeArg(Trim(ServerPage.Values[0]));
    Code := SafeArg(Trim(ServerPage.Values[1]));
    if (Server <> '') and (Code <> '') then
    begin
      WizardForm.StatusLabel.Caption := 'GAC 서버에 이 PC를 등록하는 중...';
      if not Exec(ExpandConstant('{app}\Agent\Gac.Agent.exe'), 'enroll --server "' + Server + '" --code "' + Code + '"', ExpandConstant('{app}\Agent'), SW_HIDE, ewWaitUntilTerminated, rc) or (rc <> 0) then
        SuppressibleMsgBox('GAC 서버에 등록하지 못했습니다 (코드 ' + IntToStr(rc) + ').' + #13#10 +
          '서버 주소와 등록 코드, 관리 PC가 켜져 있는지 확인한 뒤 설치 프로그램을 다시 실행하세요.' + #13#10 +
          '에이전트는 설치되었으며, 등록 전까지는 예약 작업만 실행합니다.', mbError, MB_OK, IDOK);
    end;
    WizardForm.StatusLabel.Caption := 'GAC 에이전트 서비스를 등록하는 중...';
    Exec(ExpandConstant('{app}\Agent\Gac.Agent.exe'), 'install', ExpandConstant('{app}\Agent'), SW_HIDE, ewWaitUntilTerminated, rc);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  rc: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Gac.Tray.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
    { Stops and deletes the service and removes C:\ProgramData\GAC\Agent. }
    Exec(ExpandConstant('{app}\Agent\Gac.Agent.exe'), 'uninstall', ExpandConstant('{app}\Agent'), SW_HIDE, ewWaitUntilTerminated, rc);
  end;
end;
