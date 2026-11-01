; GAC 2.0 Server installer (Admin PC). Built offline by build/build-installers.ps1.
; The install is finished only after the firewall step (CHANGE-002 §3): files + service → [다음] → 방화벽 열기 → 설치 완료.
; Silent install:   GAC-Server-Setup.exe /VERYSILENT /PORT=8443 /RANGE=10.20.0.0/22
;                   (without /RANGE the server stays "설치 미완료" until the firewall is opened in GAC 서버 관리)
; Silent uninstall: unins000.exe /VERYSILENT [/DELETEDATA=1]

#define AppVersion "2.0.0"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{6F0C2B4E-3D8A-4B6E-9C1F-2A7D5E8B9C01}
AppName=GAC 2.0 서버
AppVersion={#AppVersion}
AppPublisher=JH
AppCopyright=Copyright (c) 2026 JH (MIT License)
DefaultDirName={autopf}\GAC 2.0\Server
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=GAC-Server-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
WizardStyle=modern
CloseApplications=no
UninstallDisplayName=GAC 2.0 서버
; CHANGE-002 §1.4: the server installer and its uninstall entry use the GAC icon.
SetupIconFile=..\assets\icons\gac.ico
UninstallDisplayIcon={app}\Gac.ServerStatus.exe
SetupLogging=yes
LicenseFile=..\LICENSE

[Languages]
Name: "korean"; MessagesFile: "lang\Korean.isl"

[Files]
Source: "{#PublishDir}\server\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GAC 서버 관리"; Filename: "{app}\Gac.ServerStatus.exe"
Name: "{commonstartup}\GAC 서버 관리"; Filename: "{app}\Gac.ServerStatus.exe"; Parameters: "--tray"

[Run]
Filename: "{app}\Gac.ServerStatus.exe"; Description: "GAC 서버 관리 열기"; Flags: nowait postinstall skipifsilent runascurrentuser

[Code]
var
  DeleteData: Boolean;
  FirewallDone: Boolean;
  InstalledPage: TOutputMsgWizardPage;
  FirewallPage: TWizardPage;
  RangeEdit, PortEdit: TNewEdit;
  ResultLabel: TNewStaticText;

function ServerExe(): String;
begin
  Result := ExpandConstant('{app}\Gac.Server.exe');
end;

function InstallPort(): String;
begin
  Result := ExpandConstant('{param:PORT|8443}');
end;

{ Runs a Gac.Server.exe verb that writes "OK" / "ERROR: ..." (UTF-8) to a result file, and returns that text. }
function RunVerb(Args: String): String;
var
  ResultFile: String;
  S: AnsiString;
  rc: Integer;
begin
  ResultFile := ExpandConstant('{tmp}\gac_result.txt');
  DeleteFile(ResultFile);
  Result := '';
  if not Exec(ServerExe(), Args + ' --result "' + ResultFile + '"', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, rc) then
    Result := 'ERROR: Gac.Server.exe 를 실행하지 못했습니다.'
  else if LoadStringFromFile(ResultFile, S) then
    Result := Trim(UTF8Decode(S))
  else
    Result := 'ERROR: 결과를 읽지 못했습니다 (코드 ' + IntToStr(rc) + ').';
end;

function DetectRange(): String;
begin
  Result := RunVerb('detect-range');
  if Pos('ERROR', Result) = 1 then Result := '';
end;

procedure OpenFirewallClick(Sender: TObject);
var
  R: String;
begin
  WizardForm.NextButton.Enabled := False;
  ResultLabel.Font.Color := clWindowText;
  ResultLabel.Caption := '방화벽 규칙을 만드는 중...';
  R := RunVerb('firewall --range "' + Trim(RangeEdit.Text) + '" --port ' + Trim(PortEdit.Text));
  FirewallDone := R = 'OK';
  if FirewallDone then
  begin
    ResultLabel.Font.Color := clGreen;
    ResultLabel.Caption := '✓ 방화벽 규칙을 만들고 다시 읽어 확인했습니다.' + #13#10 + '(TCP ' + Trim(PortEdit.Text) + ', ' + Trim(RangeEdit.Text) + ' 대역만 허용, 모든 프로필)';
  end
  else
  begin
    ResultLabel.Font.Color := clRed;
    if Pos('ERROR: ', R) = 1 then R := Copy(R, 8, Length(R));
    ResultLabel.Caption := '✗ ' + R;
  end;
  WizardForm.NextButton.Enabled := FirewallDone;
end;

procedure InitializeWizard();
var
  Lbl: TNewStaticText;
  Btn: TNewButton;
begin
  { CHANGE-002 §3.1: the page after installation says [다음], never [마침]. }
  InstalledPage := CreateOutputMsgPage(wpInfoAfter, '파일 설치가 끝났습니다', '아직 설치가 끝나지 않았습니다.',
    '프로그램 파일과 GAC 서버 서비스가 설치되었습니다.' + #13#10#13#10 +
    '다음 단계에서 방화벽을 열어야 교실 PC가 서버에 연결할 수 있습니다.' + #13#10 + '[다음]을 누르세요.');

  FirewallPage := CreateCustomPage(InstalledPage.ID, '방화벽 열기', '교내망에서만 서버에 접속할 수 있도록 방화벽을 엽니다.');
  Lbl := TNewStaticText.Create(FirewallPage);
  Lbl.Parent := FirewallPage.Surface;
  Lbl.Caption := '감지된 교내망 대역과 포트를 확인하고 [방화벽 열기]를 누르세요. 이 대역과 포트만 열립니다.';
  Lbl.Width := FirewallPage.SurfaceWidth;
  Lbl.WordWrap := True;

  Lbl := TNewStaticText.Create(FirewallPage);
  Lbl.Parent := FirewallPage.Surface;
  Lbl.Caption := '교내망 대역 (CIDR, 예: 10.20.0.0/22):';
  Lbl.Top := ScaleY(40);
  RangeEdit := TNewEdit.Create(FirewallPage);
  RangeEdit.Parent := FirewallPage.Surface;
  RangeEdit.Top := ScaleY(58);
  RangeEdit.Width := ScaleX(220);

  Lbl := TNewStaticText.Create(FirewallPage);
  Lbl.Parent := FirewallPage.Surface;
  Lbl.Caption := '포트:';
  Lbl.Top := ScaleY(90);
  PortEdit := TNewEdit.Create(FirewallPage);
  PortEdit.Parent := FirewallPage.Surface;
  PortEdit.Top := ScaleY(108);
  PortEdit.Width := ScaleX(80);
  PortEdit.Text := InstallPort();

  Btn := TNewButton.Create(FirewallPage);
  Btn.Parent := FirewallPage.Surface;
  Btn.Caption := '방화벽 열기';
  Btn.Top := ScaleY(145);
  Btn.Width := ScaleX(120);
  Btn.Height := ScaleY(28);
  Btn.OnClick := @OpenFirewallClick;

  ResultLabel := TNewStaticText.Create(FirewallPage);
  ResultLabel.Parent := FirewallPage.Surface;
  ResultLabel.Top := ScaleY(185);
  ResultLabel.Width := FirewallPage.SurfaceWidth;
  ResultLabel.WordWrap := True;
  ResultLabel.Caption := '';
end;

procedure StopServer();
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop GACServer', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(3000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Gac.Server.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Gac.ServerStatus.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Upgrade in place: stop the running service so files can be replaced. Data in ProgramData is kept. }
  StopServer();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  rc: Integer;
  R: String;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'GAC 서버를 설정하는 중... (인증서, 데이터베이스, 서비스)';
    if not Exec(ServerExe(), 'install --port ' + InstallPort(), ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, rc) or (rc <> 0) then
      SuppressibleMsgBox('GAC 서버 설정 중 문제가 발생했습니다 (코드 ' + IntToStr(rc) + '). 설치 로그를 확인하세요.', mbError, MB_OK, IDOK);
    { Silent rollout: /RANGE= performs the firewall step without the pages. }
    if WizardSilent() and (ExpandConstant('{param:RANGE|}') <> '') then
    begin
      R := RunVerb('firewall --range "' + ExpandConstant('{param:RANGE|}') + '" --port ' + InstallPort());
      FirewallDone := R = 'OK';
      if not FirewallDone then Log('Firewall step failed: ' + R);
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (FirewallPage <> nil) and (CurPageID = FirewallPage.ID) then
    Result := FirewallDone; { [다음] stays disabled until the rule exists and was verified }
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Lines: TArrayOfString;
  Text: String;
  I: Integer;
begin
  if (CurPageID = InstalledPage.ID) or (CurPageID = FirewallPage.ID) or (CurPageID = wpFinished) then
  begin
    { No way to skip the firewall step: Cancel and Back are hidden after installation. }
    WizardForm.CancelButton.Visible := False;
    WizardForm.BackButton.Visible := False;
  end;
  if CurPageID = FirewallPage.ID then
  begin
    if Trim(RangeEdit.Text) = '' then RangeEdit.Text := DetectRange();
    WizardForm.NextButton.Enabled := FirewallDone;
  end;
  if CurPageID = wpFinished then
  begin
    WizardForm.FinishedHeadingLabel.Caption := '설치 완료';
    Text := '';
    if LoadStringsFromFile(ExpandConstant('{commonappdata}\GAC\Server\enrollment.txt'), Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        Text := Text + Lines[I] + #13#10;
    WizardForm.FinishedLabel.Caption :=
      'GAC 서버 설치가 끝났습니다.' + #13#10#13#10 + Text + #13#10 +
      '설치 코드는 콘솔에서 처음 회원가입할 때 필요합니다. 꼭 적어 두세요.' + #13#10 +
      '이 내용은 ' + ExpandConstant('{commonappdata}') + '\GAC\Server\enrollment.txt 에도 저장되어 있습니다.';
  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  { Also covers the window's close box. Before installation Cancel works as usual. }
  if (CurPageID = InstalledPage.ID) or (CurPageID = FirewallPage.ID) then
  begin
    Cancel := False;
    Confirm := False;
  end;
end;

function InitializeUninstall(): Boolean;
var
  Form: TSetupForm;
  Lbl: TNewStaticText;
  Check: TNewCheckBox;
  OkButton, CancelButton: TNewButton;
begin
  Result := True;
  DeleteData := ExpandConstant('{param:DELETEDATA|0}') = '1';
  if UninstallSilent then Exit;
  Form := CreateCustomForm();
  try
    Form.ClientWidth := ScaleX(400);
    Form.ClientHeight := ScaleY(150);
    Form.Caption := 'GAC 2.0 서버 제거';
    Lbl := TNewStaticText.Create(Form);
    Lbl.Parent := Form;
    Lbl.Left := ScaleX(16);
    Lbl.Top := ScaleY(14);
    Lbl.Width := ScaleX(370);
    Lbl.WordWrap := True;
    Lbl.Caption := 'GAC 서버 서비스, 방화벽 규칙, 프로그램 파일을 삭제합니다.' + #13#10 + 'PC 목록·계정·기록(데이터베이스)은 기본적으로 남겨 두어 다시 설치하면 그대로 쓸 수 있습니다.';
    Check := TNewCheckBox.Create(Form);
    Check.Parent := Form;
    Check.Left := ScaleX(16);
    Check.Top := ScaleY(72);
    Check.Width := ScaleX(360);
    Check.Caption := '데이터도 삭제';
    OkButton := TNewButton.Create(Form);
    OkButton.Parent := Form;
    OkButton.Caption := '제거';
    OkButton.ModalResult := mrOk;
    OkButton.Left := Form.ClientWidth - ScaleX(184);
    OkButton.Top := ScaleY(110);
    OkButton.Width := ScaleX(80);
    OkButton.Height := ScaleY(26);
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.Caption := '취소';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Left := Form.ClientWidth - ScaleX(96);
    CancelButton.Top := ScaleY(110);
    CancelButton.Width := ScaleX(80);
    CancelButton.Height := ScaleY(26);
    Form.ActiveControl := OkButton;
    if Form.ShowModal() = mrOk then
      DeleteData := Check.Checked
    else
      Result := False;
  finally
    Form.Free();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  rc: Integer;
  Args: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Gac.ServerStatus.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Args := 'uninstall';
    if DeleteData then Args := Args + ' --delete-data';
    Exec(ExpandConstant('{app}\Gac.Server.exe'), Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, rc);
  end;
end;
