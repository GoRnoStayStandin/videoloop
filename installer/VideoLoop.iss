#ifndef ProjectRoot
  #define ProjectRoot AddBackslash(SourcePath) + ".."
#endif
#ifndef DotNetPackage
  #error Build with build.cmd -Installer to provide the verified offline runtime.
#endif
#ifndef DotNetSha256
  #error The offline runtime SHA-256 must be supplied by the build script.
#endif
#define AppFiles AddBackslash(ProjectRoot) + "artifacts\VideoDisplayChecker"
#define AppVersion GetFileVersion(AppFiles + "\VideoDisplayChecker.exe")

[Setup]
; Keep this identifier stable across releases so upgrades use the same installation.
#ifdef InstallerSmokeTest
AppId={{019E8459-D470-4931-BB20-8846472026B1}
AppName=VideoLoop Setup Test
PrivilegesRequired=lowest
OutputDir={#ProjectRoot}\artifacts\installer-tests
OutputBaseFilename=VideoLoop-Setup-Test
DefaultDirName={localappdata}\VideoLoop-Setup-Test
DefaultGroupName=VideoLoop Setup Test
#else
AppId={{37189DF9-FEC8-4C3F-A91C-BC83179AD1C3}
AppName=VideoLoop
PrivilegesRequired=admin
OutputDir={#ProjectRoot}\artifacts
OutputBaseFilename=VideoLoop-Setup
DefaultDirName={autopf}\VideoLoop
DefaultGroupName=VideoLoop
#endif
AppVersion={#AppVersion}
AppPublisher=VideoLoop
VersionInfoProductName=VideoLoop
VersionInfoVersion={#AppVersion}
UninstallDisplayIcon={app}\VideoDisplayChecker.exe
MinVersion=6.1sp1
ArchitecturesAllowed=x86compatible
AppMutex=Local\VideoDisplayChecker.Main
SetupMutex=VideoLoop.Setup
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=yes
Compression=lzma2/normal
SolidCompression=no
SetupLogging=yes
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked

[Files]
; The Microsoft EXE is already compressed. Store it separately, extracting it only
; if .NET is missing. It never becomes a permanent file in the application folder.
Source: "{#DotNetPackage}"; DestName: "dotnet48-offline.exe"; Flags: dontcopy nocompression
Source: "{#AppFiles}\VideoDisplayChecker.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppFiles}\VideoDisplayChecker.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppFiles}\README.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\VideoLoop"; Filename: "{app}\VideoDisplayChecker.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\VideoLoop"; Filename: "{app}\VideoDisplayChecker.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
function Net48Present: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
end;

function GetDotNetLogPath: String;
var
  Directory: String;
begin
  Directory := ExpandConstant('{commonappdata}\VideoLoop\InstallerLogs');
  if not ForceDirectories(Directory) then
    RaiseException('Не удалось создать папку журнала: ' + Directory);
  Result := Directory + '\dotnet48-' + GetDateTimeString('yyyymmdd-hhnnss', '-', ':') + '.txt';
end;

function ExtractDotNetPackage: String;
begin
  ExtractTemporaryFile('dotnet48-offline.exe');
  Result := ExpandConstant('{tmp}\dotnet48-offline.exe');
  if CompareText(GetSHA256OfFile(Result), '{#DotNetSha256}') <> 0 then
    RaiseException('Вложенный пакет .NET повреждён. Скопируйте установщик заново.');
end;

function RunDotNetPackage(const PackagePath, Parameters: String; var ExitCode: Integer): Boolean;
begin
  Result := Exec(PackagePath, Parameters, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode);
end;

procedure SetDotNetStatus(Busy: Boolean);
begin
  if Busy then
    WizardForm.PreparingLabel.Caption := 'Устанавливается .NET Framework 4.8. На слабом компьютере это может занять несколько минут. Дождитесь завершения.';
end;

#ifdef InstallerSmokeTest
// A separate test-only build may install into an isolated user directory. It
// refuses to run if it could modify the machine-wide .NET installation.
function InitializeSetup: Boolean;
begin
  Result := Net48Present;
  if Result then
  begin
    // Also unpack and hash the real bundled Microsoft EXE, without executing it.
    ExtractDotNetPackage;
    Log('VideoLoop test: embedded offline runtime verified.');
  end;
end;
#endif

#include "DotNet.iss"
