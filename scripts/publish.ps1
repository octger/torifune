[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',

    [string]$OutputDirectory,

    [string]$CertificatePath,

    [string]$SignToolPath = 'signtool.exe',

    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\release'
}

if (Get-Process -Name Torifune -ErrorAction SilentlyContinue) {
    throw 'Close all running Torifune instances before publishing.'
}

$project = Join-Path $repositoryRoot 'src\Torifune.Desktop\Torifune.Desktop.csproj'
$publishDirectory = Join-Path $OutputDirectory "Torifune-$Version-win-x64"
$archivePath = "$publishDirectory.zip"
$checksumPath = "$archivePath.sha256"

Remove-Item -Recurse -Force $publishDirectory -ErrorAction SilentlyContinue
Remove-Item -Force $archivePath, $checksumPath -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

dotnet restore (Join-Path $repositoryRoot 'Torifune.slnx') --locked-mode --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet restore failed.'
}

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    --nologo `
    -p:Version=$Version `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

$executable = Join-Path $publishDirectory 'Torifune.exe'
if (-not (Test-Path $executable)) {
    throw "Published executable was not found: $executable"
}

foreach ($requiredFile in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path (Join-Path $publishDirectory $requiredFile))) {
        throw "Required release file was not published: $requiredFile"
    }
}

if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    if (-not (Test-Path $CertificatePath)) {
        throw "Signing certificate was not found: $CertificatePath"
    }
    if ([string]::IsNullOrWhiteSpace($env:TORIFUNE_CERTIFICATE_PASSWORD)) {
        throw 'TORIFUNE_CERTIFICATE_PASSWORD must be set when CertificatePath is provided.'
    }

    & $SignToolPath sign /fd SHA256 /td SHA256 /tr $TimestampUrl `
        /f $CertificatePath /p $env:TORIFUNE_CERTIFICATE_PASSWORD $executable
    if ($LASTEXITCODE -ne 0) {
        throw 'Code signing failed.'
    }
}

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
$checksum = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $(Split-Path -Leaf $archivePath)" | Set-Content -Path $checksumPath -Encoding ascii

$files = Get-ChildItem $publishDirectory -Recurse -File
[pscustomobject]@{
    Version = $Version
    Archive = $archivePath
    Checksum = $checksumPath
    FileCount = $files.Count
    SizeMB = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
    Signed = (Get-AuthenticodeSignature $executable).Status -eq 'Valid'
}
