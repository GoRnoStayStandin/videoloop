// Shared prerequisite workflow, exercised by tests/InstallerTests.iss as well.
// Platform operations (detection, extraction and execution) are supplied by the
// including script. Production has no command-line switch to fake a prerequisite.

var
  DotNetBusy: Boolean;
  DotNetCompleted: Boolean;
  DotNetRestartRequired: Boolean;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  PackagePath, LogPath, Parameters: String;
begin
  Result := '';
  if DotNetCompleted or Net48Present then
  begin
    Log('VideoLoop: .NET Framework 4.8 prerequisite satisfied.');
    Exit;
  end;

  LogPath := '';
  DotNetBusy := True;
  try
    try
      SetDotNetStatus(True);
      LogPath := GetDotNetLogPath;
      PackagePath := ExtractDotNetPackage;
      Parameters := '/passive /norestart /log "' + LogPath + '"';
      Log('VideoLoop: installing bundled .NET Framework 4.8. Log: ' + LogPath);
      if not RunDotNetPackage(PackagePath, Parameters, ExitCode) then
        Result := 'Не удалось запустить установку .NET Framework 4.8: ' + SysErrorMessage(ExitCode)
      else
      begin
        Log('VideoLoop: .NET installer exit code: ' + IntToStr(ExitCode));
        case ExitCode of
          0:
            if Net48Present then
              DotNetCompleted := True
            else
              Result := 'Установка .NET завершилась, но версия 4.8 пока не обнаружена. Перезагрузите Windows и повторите запуск установщика.';
          3010, 1641:
            begin
              DotNetCompleted := True;
              DotNetRestartRequired := True;
            end;
          1602:
            Result := 'Установка .NET Framework 4.8 отменена. Для работы VideoLoop необходимо завершить её установку.';
          1618:
            Result := 'В Windows уже выполняется другая установка. Дождитесь её завершения и повторите попытку.';
          5100:
            Result := 'Этот компьютер не соответствует требованиям .NET Framework 4.8. Для Windows 7 нужны Service Pack 1 и необходимые системные обновления. Подробности указаны в журнале.';
        else
          Result := 'Не удалось установить .NET Framework 4.8. Код ошибки: ' + IntToStr(ExitCode) + '.';
        end;
      end;
    except
      Result := 'Ошибка подготовки .NET Framework 4.8: ' + GetExceptionMessage;
    end;
  finally
    DotNetBusy := False;
    SetDotNetStatus(False);
  end;

  if Result <> '' then
  begin
    if LogPath <> '' then Result := Result + #13#10#13#10 + 'Журнал: ' + LogPath;
    Log('VideoLoop prerequisite error: ' + Result);
  end;
end;

function NeedRestart: Boolean;
begin
  // Silent installation must never reboot unattended; report 3010 instead.
  Result := DotNetRestartRequired and not WizardSilent;
end;

function GetCustomSetupExitCode: Integer;
begin
  if DotNetRestartRequired then Result := 3010 else Result := 0;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  // Keep the parent installer alive while the prerequisite is installing.
  // Cancellation is still available in the .NET installer's own progress window.
  if DotNetBusy then
  begin
    Cancel := False;
    Confirm := False;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and DotNetRestartRequired then
    WizardForm.NoRadio.Checked := True;
end;
