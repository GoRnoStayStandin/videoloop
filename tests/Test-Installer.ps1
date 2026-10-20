param(
    [Parameter(Mandatory=$true)][string]$Root,
    [Parameter(Mandatory=$true)][string]$Compiler,
    [Parameter(Mandatory=$true)][string]$DotNetPackage,
    [Parameter(Mandatory=$true)][string]$DotNetSha256
)

$ErrorActionPreference = 'Stop'
$output = Join-Path $Root 'artifacts\installer-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Installer test failed: $Message" }
}

function Run-Setup([string]$Exe, [string]$Arguments) {
    $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(120000)) {
        throw "Installer test timed out; inspect $output before trying again."
    }
    $process.Refresh()
    return $process.ExitCode
}

# The bootstrapper itself must run on a 32-bit machine without a CLR installed.
$release = Join-Path $Root 'artifacts\VideoLoop-Setup.exe'
$reader = New-Object IO.BinaryReader([IO.File]::OpenRead($release))
try {
    $reader.BaseStream.Position = 0x3c
    $pe = $reader.ReadInt32()
    $reader.BaseStream.Position = $pe
    Assert ($reader.ReadUInt32() -eq 0x00004550) 'PE signature'
    Assert ($reader.ReadUInt16() -eq 0x014c) 'x86 setup for Windows 7 32 bit'
    $reader.BaseStream.Position = $pe + 24
    Assert ($reader.ReadUInt16() -eq 0x010b) 'PE32 optional header'
    $reader.BaseStream.Position = $pe + 24 + 96 + 14 * 8
    Assert ($reader.ReadUInt32() -eq 0) 'native bootstrapper must not require .NET'
} finally { $reader.Dispose() }
Write-Output 'OK: release bootstrapper is native x86, with no CLR dependency'

& $Compiler '/Qp' "/DProjectRoot=$Root" (Join-Path $PSScriptRoot 'InstallerTests.iss')
if ($LASTEXITCODE -ne 0) { throw 'Prerequisite test harness compilation failed.' }
$report = Join-Path $output ('prerequisite-' + [Guid]::NewGuid().ToString('N') + '.txt')
$null = Run-Setup (Join-Path $output 'PrerequisiteChecks.exe') ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /Report="' + $report + '"')
Assert (Test-Path -LiteralPath $report) 'prerequisite report exists'
$result = [IO.File]::ReadAllText($report)
Assert ($result -eq 'PASS: 13 prerequisite scenarios') $result
Write-Output $result

# Compile the same install/uninstall script in user mode with a separate AppId.
# This variant is only in artifacts/installer-tests, never the release artifact.
& $Compiler '/Qp' "/DProjectRoot=$Root" "/DDotNetPackage=$DotNetPackage" "/DDotNetSha256=$DotNetSha256" '/DInstallerSmokeTest' (Join-Path $Root 'installer\VideoLoop.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer smoke build failed.' }
$setup = Join-Path $output 'VideoLoop-Setup-Test.exe'
$installDirectory = Join-Path $output ('installed-' + [Guid]::NewGuid().ToString('N'))
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{019E8459-D470-4931-BB20-8846472026B1}_is1'
Assert (-not (Test-Path -LiteralPath $key)) 'remove any previous VideoLoop Setup Test installation before rerunning'
$arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="' + $installDirectory + '" /GROUP="VideoLoop Setup Test" /TASKS=""'
$installed = $false
try {
    $code = Run-Setup $setup ($arguments + ' /LOG="' + (Join-Path $output 'install.log') + '"')
    $installed = Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe')
    Assert ($code -eq 0) "fresh user-mode test installation (exit $code)"
    foreach ($file in @('VideoDisplayChecker.exe', 'VideoDisplayChecker.exe.config', 'README.txt')) {
        $expected = (Get-FileHash -LiteralPath (Join-Path $Root "artifacts\VideoDisplayChecker\$file") -Algorithm SHA256).Hash
        Assert ((Get-FileHash -LiteralPath (Join-Path $installDirectory $file) -Algorithm SHA256).Hash -eq $expected) "installed $file matches release"
    }
    Assert (Test-Path -LiteralPath $key) 'Add/Remove Programs registration'
    $programs = [Environment]::GetFolderPath('Programs')
    $shortcut = Join-Path $programs 'VideoLoop Setup Test\VideoLoop.lnk'
    Assert (Test-Path -LiteralPath $shortcut) 'Start menu shortcut'
    Assert (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'dotnet48-offline.exe'))) 'runtime installer is temporary, not copied into the application directory'
    Write-Output 'OK: embedded runtime hash, installed files, shortcut and uninstall registration'

    $code = Run-Setup $setup ($arguments + ' /LOG="' + (Join-Path $output 'upgrade.log') + '"')
    Assert ($code -eq 0) "repeat installation/upgrade (exit $code)"
    Write-Output 'OK: repeat installation uses the existing application directory'

    $mutex = New-Object Threading.Mutex($false, 'Local\VideoDisplayChecker.Main')
    try {
        $code = Run-Setup $setup ($arguments + ' /LOG="' + (Join-Path $output 'running-app.log') + '"')
        Assert ($code -ne 0) 'setup must stop while VideoLoop is running'
    } finally { $mutex.Dispose() }
    Write-Output 'OK: running-application mutex blocks replacement'
} finally {
    if ($installed) {
        $uninstaller = Join-Path $installDirectory 'unins000.exe'
        $code = Run-Setup $uninstaller ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + (Join-Path $output 'uninstall.log') + '"')
        Assert ($code -eq 0) "test uninstallation (exit $code)"
    }
}
# Inno's uninstaller can finish deleting itself after the initial process exits.
for ($i = 0; $i -lt 50 -and (Test-Path -LiteralPath $installDirectory); $i++) { Start-Sleep -Milliseconds 100 }
Assert (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'VideoDisplayChecker.exe'))) 'application removed'
Assert (-not (Test-Path -LiteralPath $key)) 'uninstall registration removed'
Assert (-not (Test-Path -LiteralPath $shortcut)) 'shortcut removed'
Write-Output 'OK: test uninstallation removes application, shortcut and registration'
