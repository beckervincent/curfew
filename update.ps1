# Screen Time Manager - Update Script
# Stops the service, replaces the exe, restarts the service.
# Run build.bat first to produce a fresh screen-time-manager.exe.

$ServiceName = "ScreenTimeManager"
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallDir  = Join-Path $env:ProgramFiles "ScreenTimeManager"
$SrcExe      = Join-Path $ScriptDir "screen-time-manager.exe"
$ExePath     = Join-Path $InstallDir "screen-time-manager.exe"
$NssmExe     = Join-Path $InstallDir "nssm.exe"

Write-Host "Screen Time Manager - Update" -ForegroundColor Cyan
Write-Host "============================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $SrcExe)) {
    Write-Host "ERROR: screen-time-manager.exe not found in $ScriptDir" -ForegroundColor Red
    Write-Host "Run build.bat first to produce the executable." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $InstallDir)) {
    Write-Host "ERROR: Install directory not found: $InstallDir" -ForegroundColor Red
    Write-Host "Run install.bat first." -ForegroundColor Yellow
    exit 1
}

# --- 1. Stop service ---
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Stopping service..." -ForegroundColor White
    & $NssmExe stop $ServiceName 2>&1 | Out-Null
    Start-Sleep -Seconds 2
} else {
    Write-Host "Service not installed. Run install.bat first." -ForegroundColor Yellow
    exit 1
}

# --- 2. Kill ALL running instances (service + tray apps in every user session) ---
Write-Host "Terminating all running instances..." -ForegroundColor White
$procs = Get-Process -Name "screen-time-manager" -ErrorAction SilentlyContinue
if ($procs) {
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Name "screen-time-manager" -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    $remaining = Get-Process -Name "screen-time-manager" -ErrorAction SilentlyContinue
    if ($remaining) {
        Write-Host "WARNING: Could not terminate all instances." -ForegroundColor Yellow
    }
}

# --- 3. Copy updated exe to install directory ---
Write-Host "Copying updated executable to $InstallDir..." -ForegroundColor White
Copy-Item -Path $SrcExe -Destination $ExePath -Force

# --- 4. Start updated service ---
Write-Host "Starting service..." -ForegroundColor White
& $NssmExe start $ServiceName 2>&1 | Out-Null
Start-Sleep -Seconds 2

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") {
    Write-Host ""
    Write-Host "Update complete! Service is running." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "WARNING: Service may not have started. Check $InstallDir\logs\stderr.log" -ForegroundColor Yellow
}
Write-Host ""
