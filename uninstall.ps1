# Screen Time Manager - Uninstall Script

#Requires -RunAsAdministrator

$ServiceName = "ScreenTimeManager"
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallDir  = Join-Path $env:ProgramFiles "ScreenTimeManager"
$NssmExe     = Join-Path $InstallDir "nssm.exe"
if (-not (Test-Path $NssmExe)) { $NssmExe = Join-Path $ScriptDir "nssm.exe" }

Write-Host "Screen Time Manager - Uninstallation" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

$svc     = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$oldTask = Get-ScheduledTask -TaskName $ServiceName -ErrorAction SilentlyContinue

if (-not $svc -and -not $oldTask -and -not (Test-Path $InstallDir)) {
    Write-Host "Screen Time Manager is not installed." -ForegroundColor Yellow
    exit 0
}

# ── Remove legacy scheduled task ─────────────────────────────────────────────
if ($oldTask) {
    Write-Host "Removing legacy scheduled task..." -ForegroundColor Yellow
    Stop-ScheduledTask -TaskName $ServiceName -EA SilentlyContinue
    Unregister-ScheduledTask -TaskName $ServiceName -Confirm:$false
}

# ── Stop and remove service ───────────────────────────────────────────────────
if ($svc) {
    Write-Host "Stopping service..." -ForegroundColor White
    & $NssmExe stop $ServiceName 2>&1 | Out-Null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Service $ServiceName -EA SilentlyContinue).Status -eq 'Running' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Removing NSSM service..." -ForegroundColor White
    & $NssmExe remove $ServiceName confirm 2>&1 | Out-Null
    Start-Sleep -Seconds 1
}

# ── Kill all remaining processes ──────────────────────────────────────────────
Write-Host "Terminating all running instances..." -ForegroundColor White
Get-Process -Name "screen-time-manager" -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Process -Name "screen-time-manager" -EA SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}

# ── Reset ACLs on install dir so we can delete files ─────────────────────────
if (Test-Path $InstallDir) {
    Write-Host "Resetting ACLs..." -ForegroundColor White
    try {
        $acl = Get-Acl $InstallDir
        $acl.SetAccessRuleProtection($false, $true)
        Set-Acl $InstallDir $acl
    } catch { Write-Host "WARNING: ACL reset failed: $_" -ForegroundColor Yellow }

    Write-Host "Removing install directory: $InstallDir" -ForegroundColor White
    Remove-Item -Path $InstallDir -Recurse -Force -EA SilentlyContinue
    if (Test-Path $InstallDir) {
        Write-Host "WARNING: Could not fully remove $InstallDir" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Uninstallation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Database is kept at: $env:ProgramData\ScreenTimeManager" -ForegroundColor Yellow
Write-Host "Delete that folder to remove all data." -ForegroundColor Yellow
Write-Host ""
