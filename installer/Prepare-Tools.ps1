param([string]$Root = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Assert-Publisher([string]$Path, [string]$Publisher) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike "*O=$Publisher,*") {
        throw "Invalid signature or unexpected publisher: $Path ($($signature.Status))"
    }
}

function Save-Download([string]$Url, [string]$Path) {
    $offset = if (Test-Path -LiteralPath $Path) { (Get-Item -LiteralPath $Path).Length } else { 0 }
    $request = [Net.HttpWebRequest]::Create($Url)
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 60000
    if ($offset -gt 0) { $request.AddRange($offset) }
    try { $response = $request.GetResponse() }
    catch [Net.WebException] {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 416) {
            $_.Exception.Response.Dispose()
            Remove-Item -LiteralPath $Path
            Save-Download $Url $Path
            return
        }
        throw
    }
    try {
        if ([int]$response.StatusCode -ne 206) { $offset = 0 }
        $mode = if ($offset -gt 0) { [IO.FileMode]::Append } else { [IO.FileMode]::Create }
        $target = [IO.File]::Open($Path, $mode, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try {
            $stream = $response.GetResponseStream()
            $buffer = New-Object byte[] 65536
            $received = $offset
            $clock = [Diagnostics.Stopwatch]::StartNew()
            while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $target.Write($buffer, 0, $count)
                $received += $count
                if ($clock.Elapsed.TotalSeconds -ge 15) {
                    Write-Information ('Downloading {0}: {1:N1} MiB' -f [IO.Path]::GetFileName($Path), ($received / 1MB)) -InformationAction Continue
                    $clock.Restart()
                }
            }
        } finally { $target.Dispose() }
    } finally { $response.Dispose() }
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "Unexpected SHA-256: $Path. Delete this cached package and rebuild."
    }
}

function Get-SignedPackage([string]$Url, [string]$Path, [string]$Publisher, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path)) {
        $partial = "$Path.download"
        $candidate = "$Path.pending.exe"
        try {
            Write-Information ("Downloading " + [IO.Path]::GetFileName($Path)) -InformationAction Continue
            Save-Download $Url $partial
            # Authenticode needs a recognized extension, even for a temporary file.
            Move-Item -LiteralPath $partial -Destination $candidate -Force
            Assert-Publisher $candidate $Publisher
            Assert-Hash $candidate $Hash
            Move-Item -LiteralPath $candidate -Destination $Path -Force
        } finally {
            # Keep interrupted downloads for the next build (HTTP range resume).
            if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate }
        }
    }
    Assert-Hash $Path $Hash
    Assert-Publisher $Path $Publisher
}

$cache = Join-Path $Root 'packages\installer'
New-Item -ItemType Directory -Path $cache -Force | Out-Null

# The official Inno installer supports portable extraction without registration,
# shortcuts, file associations or elevation. It is only a build-time tool.
$innoPackage = Join-Path $cache 'innosetup-6.7.3.exe'
Get-SignedPackage 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' $innoPackage 'Pyrsys B.V.' '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
$innoDirectory = Join-Path $cache 'inno-6.7.3'
$iscc = Join-Path $innoDirectory 'ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc)) {
    $arguments = '/PORTABLE=1 /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /TASKS="" /DIR="' + $innoDirectory + '" /LOG="' + (Join-Path $cache 'inno-extract.log') + '"'
    $process = Start-Process -FilePath $innoPackage -ArgumentList $arguments -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Inno Setup preparation failed: $($process.ExitCode). See packages\installer\inno-extract.log." }
}
if (-not (Test-Path -LiteralPath $iscc)) { throw 'Inno Setup compiler was not extracted.' }
Assert-Publisher $iscc 'Pyrsys B.V.'

# Runtime, not the Developer Pack and not the web bootstrapper. This package
# contains both x86 and x64 payloads and supports Windows 7 SP1.
$dotNet = Join-Path $cache 'ndp48-x86-x64-allos-enu.exe'
Get-SignedPackage 'https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe' $dotNet 'Microsoft Corporation' '0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'

[pscustomobject]@{
    Compiler = $iscc
    DotNet = $dotNet
    DotNetSha256 = (Get-FileHash -LiteralPath $dotNet -Algorithm SHA256).Hash
    InnoSha256 = (Get-FileHash -LiteralPath $innoPackage -Algorithm SHA256).Hash
}
