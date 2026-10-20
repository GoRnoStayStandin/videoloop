param(
    [switch]$Test,
    [switch]$Integration,
    [switch]$Installer,
    [string]$VlcPath = "$env:ProgramFiles\VideoLAN\VLC\vlc.exe"
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler was not found.' }

# Use the actual 4.8 reference assemblies, even on a build machine running 4.8.1.
$references = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (-not (Test-Path -LiteralPath (Join-Path $references 'mscorlib.dll'))) {
    $packageRoot = Join-Path $root 'packages\net48'
    $references = Join-Path $packageRoot 'build\.NETFramework\v4.8'
    if (-not (Test-Path -LiteralPath (Join-Path $references 'mscorlib.dll'))) {
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
        $archive = Join-Path $packageRoot 'references.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $client = New-Object Net.WebClient
        try {
            $client.DownloadFile('https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg', $archive)
        } finally { $client.Dispose() }
        Expand-Archive -LiteralPath $archive -DestinationPath $packageRoot -Force
        Remove-Item -LiteralPath $archive
    }
}

$output = Join-Path $root 'artifacts\VideoDisplayChecker'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output 'VideoDisplayChecker.exe'
$referenceArgs = @('mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Xml', 'System.Xml.Linq') | ForEach-Object {
    '/reference:' + (Join-Path $references ($_ + '.dll'))
}
$common = @('/nologo', '/noconfig', '/nostdlib+', '/langversion:5', '/platform:x86', '/optimize+', '/warn:4', '/warnaserror+', '/utf8output')
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @common @referenceArgs '/target:winexe' "/out:$exe" "/win32manifest:$(Join-Path $root 'src\app.manifest')" @sources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'src\App.config') -Destination "$exe.config"
# A BOM lets Windows 7 Notepad display the Russian instructions correctly.
$readme = [IO.File]::ReadAllText((Join-Path $root 'README.md'), [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $output 'README.txt'), $readme, (New-Object Text.UTF8Encoding($true)))

if ($Test -or $Integration) {
    $testOutput = Join-Path $root 'artifacts\tests'
    New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
    $testExe = Join-Path $testOutput 'VideoDisplayChecker.Tests.exe'
    $testSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
    & $compiler @common @referenceArgs "/reference:$exe" '/target:exe' "/out:$testExe" @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    Copy-Item -LiteralPath $exe -Destination $testOutput
    Copy-Item -LiteralPath "$exe.config" -Destination "$testExe.config"
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($Integration) {
        & $testExe '--integration' $VlcPath
        if ($LASTEXITCODE -ne 0) { throw 'VLC integration checks failed.' }
    }
}

$zip = Join-Path $root 'artifacts\VideoDisplayChecker-win-x86.zip'
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force
Write-Output "Built: $exe"
Write-Output "Package: $zip"

if ($Installer) {
    & (Join-Path $root 'installer\Build-Installer.ps1') -Root $root -Test:($Test -or $Integration)
}
