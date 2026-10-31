; GAC 2.0 Server installer (Admin PC). Built offline by build/build-installers.ps1.
; Silent install:   GAC-Server-Setup.exe /VERYSILENT /PORT=8443
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
UninstallDisplayIcon={app}\Gac.Server.exe
SetupLogging=yes
LicenseFile=..\LICENSE

[Languages]
Name: "korean"; MessagesFile: "lang\Korean.isl"

[Files]
Source: "{#PublishDir}\server\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Code]
var
  DeleteData: Boolean;

function GetPort(Param: String): String;
begin
  Result := ExpandConstant('{param:PORT|8443}');
end;

procedure StopServer();
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop GACServer', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(3000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Gac.Server.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
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
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'GAC 서버를 설정하는 중... (인증서, 데이터베이스, 방화벽, 서비스)';
    if not Exec(ExpandConstant('{app}\Gac.Server.exe'), 'install --port ' + GetPort(''), ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, rc) or (rc <> 0) then
      SuppressibleMsgBox('GAC 서버 설정 중 문제가 발생했습니다 (코드 ' + IntToStr(rc) + '). 설치 로그를 확인하세요.', mbError, MB_OK, IDOK);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Lines: TArrayOfString;
  Text: String;
  I: Integer;
begin
  if CurPageID = wpFinished then
  begin
    Text := '';
    if LoadStringsFromFile(ExpandConstant('{commonappdata}\GAC\Server\enrollment.txt'), Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        Text := Text + Lines[I] + #13#10;
    WizardForm.FinishedLabel.Caption :=
      'GAC 서버가 설치되어 서비스로 실행 중입니다.' + #13#10#13#10 + Text + #13#10 +
      '콘솔과 각 PC의 에이전트를 설치할 때 위의 서버 주소와 등록 코드가 필요합니다.' + #13#10 +
      '이 내용은 ' + ExpandConstant('{commonappdata}') + '\GAC\Server\enrollment.txt 에도 저장되어 있습니다.';
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
    Args := 'uninstall';
    if DeleteData then Args := Args + ' --delete-data';
    Exec(ExpandConstant('{app}\Gac.Server.exe'), Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, rc);
  end;
end;
