#ifndef ProjectRoot
  #define ProjectRoot AddBackslash(SourcePath) + ".."
#endif

[Setup]
AppName=VideoLoop Prerequisite Checks
AppVersion=1.0
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
MinVersion=6.1sp1
OutputDir={#ProjectRoot}\artifacts\installer-tests
OutputBaseFilename=PrerequisiteChecks
SetupLogging=yes

[Code]
var
  FakeInstalled, FakeInstallOnExit, FakeLaunch, FakeExtractionError: Boolean;
  FakeExitCode, Executions, Extractions, Checks: Integer;
  LastParameters, ReportPath: String;

function Net48Present: Boolean;
begin
  Result := FakeInstalled;
end;

function GetDotNetLogPath: String;
begin
  Result := 'C:\test logs\dotnet48.txt';
end;

function ExtractDotNetPackage: String;
begin
  Extractions := Extractions + 1;
  if FakeExtractionError then RaiseException('Simulated extraction failure');
  Result := 'C:\test runtime\dotnet48.exe';
end;

function RunDotNetPackage(const PackagePath, Parameters: String; var ExitCode: Integer): Boolean;
begin
  Executions := Executions + 1;
  LastParameters := Parameters;
  ExitCode := FakeExitCode;
  if FakeInstallOnExit then FakeInstalled := True;
  Result := FakeLaunch;
end;

procedure SetDotNetStatus(Busy: Boolean);
begin
end;

#include "..\installer\DotNet.iss"

procedure Assert(Condition: Boolean; const Message: String);
begin
  if not Condition then RaiseException(Message);
end;

procedure Reset;
begin
  FakeInstalled := False;
  FakeInstallOnExit := False;
  FakeLaunch := True;
  FakeExtractionError := False;
  FakeExitCode := 0;
  Executions := 0;
  Extractions := 0;
  LastParameters := '';
  DotNetCompleted := False;
  DotNetRestartRequired := False;
  DotNetBusy := False;
end;

function Prepare: String;
var
  Restart: Boolean;
begin
  Restart := False;
  Result := PrepareToInstall(Restart);
  Assert(not DotNetBusy, 'The busy state must be reset even on errors');
end;

procedure ExpectFailure(Code: Integer);
var
  Message: String;
begin
  Reset;
  FakeExitCode := Code;
  Message := Prepare;
  Assert((Message <> '') and (Pos(GetDotNetLogPath, Message) > 0), 'An unsuccessful prerequisite must stop the application install and identify the log');
  Assert(not DotNetCompleted and not DotNetRestartRequired, 'Failure cannot count as a completed prerequisite');
  Checks := Checks + 1;
end;

function InitializeSetup: Boolean;
var
  Message: String;
  Cancel, Confirm: Boolean;
begin
  // Returning False prevents this harness from installing anything. The caller
  // verifies the report; an aborted setup exit code is expected for this EXE.
  Result := False;
  ReportPath := ExpandConstant('{param:Report|}');
  if ReportPath = '' then Exit;
  try
    Reset;
    FakeInstalled := True;
    Assert((Prepare = '') and (Executions = 0) and (Extractions = 0), 'Existing compatible .NET must skip the bundled installer');
    Checks := Checks + 1;

    Reset;
    FakeInstallOnExit := True;
    Assert((Prepare = '') and DotNetCompleted and (Executions = 1), 'Successful installation should allow application files');
    Assert((Pos('/norestart', LastParameters) > 0) and (Pos('/log "C:\test logs\dotnet48.txt"', LastParameters) > 0), 'Prerequisite command must suppress reboot and quote its log path');
    Assert(not DotNetRestartRequired and (GetCustomSetupExitCode = 0), 'Success without reboot');
    Checks := Checks + 1;

    Reset;
    FakeExitCode := 3010;
    Assert((Prepare = '') and DotNetRestartRequired, '3010 is successful installation requiring reboot');
    Assert((GetCustomSetupExitCode = 3010) and not NeedRestart, 'Silent install reports 3010 and never reboots');
    Assert((Prepare = '') and (Executions = 1), 'Repeated Prepare after 3010 must not run .NET again');
    Checks := Checks + 1;

    Reset;
    FakeExitCode := 1641;
    Assert((Prepare = '') and DotNetRestartRequired, 'Handle the reboot-initiated result defensively');
    Checks := Checks + 1;

    ExpectFailure(1602);
    ExpectFailure(1618);
    ExpectFailure(5100);
    ExpectFailure(1603);

    Reset;
    FakeLaunch := False;
    FakeExitCode := 5;
    Assert((Prepare <> '') and not DotNetCompleted, 'Failure to create prerequisite process');
    Checks := Checks + 1;

    Reset;
    Assert((Prepare <> '') and not DotNetCompleted, 'Exit zero alone is not enough if .NET is still absent');
    Checks := Checks + 1;

    Reset;
    FakeExtractionError := True;
    Message := Prepare;
    Assert((Pos('Simulated extraction failure', Message) > 0) and (Executions = 0), 'Extraction error is surfaced without launching a process');
    Checks := Checks + 1;

    Reset;
    FakeExitCode := 1602;
    Assert(Prepare <> '', 'Cancellation before retry');
    FakeExitCode := 0;
    FakeInstallOnExit := True;
    Assert((Prepare = '') and (Executions = 2), 'Retry after cancellation must be possible');
    Checks := Checks + 1;

    Cancel := True;
    Confirm := True;
    DotNetBusy := True;
    CancelButtonClick(wpPreparing, Cancel, Confirm);
    Assert(not Cancel and not Confirm, 'Parent must wait for prerequisite child');
    DotNetBusy := False;
    Cancel := True;
    Confirm := True;
    CancelButtonClick(wpPreparing, Cancel, Confirm);
    Assert(Cancel and Confirm, 'Normal cancellation remains available outside prerequisite setup');
    Checks := Checks + 1;

    SaveStringToFile(ReportPath, 'PASS: ' + IntToStr(Checks) + ' prerequisite scenarios', False);
  except
    SaveStringToFile(ReportPath, 'FAIL: ' + GetExceptionMessage, False);
  end;
end;
