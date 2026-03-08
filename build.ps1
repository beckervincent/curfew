# Screen Time Manager - Build Script
# Compiles a release build and copies the exe to the project root.
# Run from the project directory (does not need to be Administrator).

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildExe  = Join-Path $ScriptDir "target\release\screen-time-manager.exe"
$DestExe   = Join-Path $ScriptDir "screen-time-manager.exe"
$CargoExe  = (Get-Command cargo -ErrorAction SilentlyContinue)?.Source

Write-Host "Screen Time Manager - Build" -ForegroundColor Cyan
Write-Host "===========================" -ForegroundColor Cyan
Write-Host ""

if (-not $CargoExe) {
    Write-Host "ERROR: cargo not found in PATH" -ForegroundColor Red
    exit 1
}

Push-Location $ScriptDir
Write-Host "Building release..." -ForegroundColor White
& $CargoExe build --release
$exit = $LASTEXITCODE

if ($exit -eq 0) {
    Write-Host ""
    Write-Host "Linting..." -ForegroundColor White
    & $CargoExe clippy -- -D warnings
    $exit = $LASTEXITCODE
}
Pop-Location

if ($exit -ne 0) {
    Write-Host ""
    Write-Host "ERROR: Build/lint failed (exit $exit)." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $BuildExe)) {
    Write-Host "ERROR: Expected output not found: $BuildExe" -ForegroundColor Red
    exit 1
}

Copy-Item -Path $BuildExe -Destination $DestExe -Force
Write-Host ""
Write-Host "Build successful!" -ForegroundColor Green
Write-Host "Output: $DestExe" -ForegroundColor White
Write-Host ""
