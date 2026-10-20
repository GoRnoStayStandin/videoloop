param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
Write-Output 'Preparing the offline installer (the first build downloads the build tool and .NET runtime)...'
$tools = & (Join-Path $PSScriptRoot 'Prepare-Tools.ps1') -Root $Root
$arguments = @('/Qp', "/DProjectRoot=$Root", "/DDotNetPackage=$($tools.DotNet)", "/DDotNetSha256=$($tools.DotNetSha256)")
& $tools.Compiler @arguments (Join-Path $PSScriptRoot 'VideoLoop.iss')
if ($LASTEXITCODE -ne 0) { throw 'VideoLoop installer compilation failed.' }

if ($Test) {
    & (Join-Path $Root 'tests\Test-Installer.ps1') -Root $Root -Compiler $tools.Compiler -DotNetPackage $tools.DotNet -DotNetSha256 $tools.DotNetSha256
}

$setup = Join-Path $Root 'artifacts\VideoLoop-Setup.exe'
Write-Output "Installer: $setup"
Write-Output ('Installer size: {0:N1} MiB' -f ((Get-Item -LiteralPath $setup).Length / 1MB))
Write-Output ('SHA-256: ' + (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash)
