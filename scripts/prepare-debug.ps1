$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot

$targets = Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -match 'Torifune.Desktop|dotnet|vsdbg') -and
    ($_.CommandLine -match 'Torifune.Desktop|torifune')
}

if ($targets) {
    $targets | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

$desktopBin = Join-Path $repoRoot 'src/Torifune.Desktop/bin/Debug/net10.0'
$desktopObj = Join-Path $repoRoot 'src/Torifune.Desktop/obj/Debug/net10.0'

if (Test-Path $desktopBin) {
    Get-ChildItem -LiteralPath $desktopBin -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

if (Test-Path $desktopObj) {
    Get-ChildItem -LiteralPath $desktopObj -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

exit 0
