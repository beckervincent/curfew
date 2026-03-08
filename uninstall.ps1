# Screen Time Manager - Uninstall Script

$ServiceName = "ScreenTimeManager"
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallDir  = Join-Path $env:ProgramFiles "ScreenTimeManager"
$NssmExe     = Join-Path $InstallDir "nssm.exe"
# Fall back to script directory nssm if install dir doesn't exist yet
if (-not (Test-Path $NssmExe)) {
    $NssmExe = Join-Path $ScriptDir "nssm.exe"
}

Write-Host "Screen Time Manager - Uninstallation" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

$svc     = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$oldTask = Get-ScheduledTask -TaskName $ServiceName -ErrorAction SilentlyContinue

if (-not $svc -and -not $oldTask) {
    Write-Host "Screen Time Manager is not installed." -ForegroundColor Yellow
    exit 0
}

if ($oldTask) {
    Write-Host "Removing legacy scheduled task..." -ForegroundColor Yellow
    Stop-ScheduledTask -TaskName $ServiceName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $ServiceName -Confirm:$false
}

if ($svc) {
    Write-Host "Stopping service..." -ForegroundColor White
    & $NssmExe stop $ServiceName 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Removing NSSM service..." -ForegroundColor White
    & $NssmExe remove $ServiceName confirm 2>&1 | Out-Null
    Start-Sleep -Seconds 1
}

# Kill ALL running instances (service + tray apps in all user sessions)
$procs = Get-Process -Name "screen-time-manager" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "Terminating all running instances..." -ForegroundColor White
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# Remove install directory
if (Test-Path $InstallDir) {
    Write-Host "Removing install directory: $InstallDir" -ForegroundColor White
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Uninstallation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Database is kept at: $env:ProgramData\ScreenTimeManager" -ForegroundColor Yellow
Write-Host "Delete that folder to remove all data." -ForegroundColor Yellow
Write-Host ""
