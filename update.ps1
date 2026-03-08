# Screen Time Manager - Update Script
# Stops the service, replaces the exe, restarts the service.
# Run build.bat first to produce a fresh screen-time-manager.exe.

#Requires -RunAsAdministrator

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
    Write-Host "Run build.bat first." -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path $InstallDir)) {
    Write-Host "ERROR: Install directory not found: $InstallDir" -ForegroundColor Red
    Write-Host "Run install.bat first." -ForegroundColor Yellow
    exit 1
}
if (-not (Get-Service -Name $ServiceName -EA SilentlyContinue)) {
    Write-Host "ERROR: Service '$ServiceName' not found. Run install.bat first." -ForegroundColor Red
    exit 1
}

# ── SID-based ACL helpers (locale-independent) ────────────────────────────────
function Set-LockedAcl($path, $includeUsers) {
    $acl = Get-Acl $path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($entry in $acl.Access) { $acl.RemoveAccessRule($entry) | Out-Null }
    foreach ($sid in @("S-1-5-32-544", "S-1-5-18")) {
        $s = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $s, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
    }
    if ($includeUsers) {
        $s = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-545")
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $s, "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")))
    }
    Set-Acl $path $acl
}

function Reset-Acl($path) {
    $acl = Get-Acl $path
    $acl.SetAccessRuleProtection($false, $true)
    Set-Acl $path $acl
}

# ── 1. Stop service ───────────────────────────────────────────────────────────
Write-Host "Stopping service..." -ForegroundColor White
& $NssmExe stop $ServiceName 2>&1 | Out-Null
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Service $ServiceName -EA SilentlyContinue).Status -eq 'Running' -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}

# ── 2. Kill all remaining processes ───────────────────────────────────────────
Write-Host "Terminating all running instances..." -ForegroundColor White
Get-Process -Name "screen-time-manager" -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Process -Name "screen-time-manager" -EA SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if (Get-Process -Name "screen-time-manager" -EA SilentlyContinue) {
    Write-Host "WARNING: Could not terminate all instances." -ForegroundColor Yellow
}

# ── 3. Reset ACLs so we can replace the exe ───────────────────────────────────
Write-Host "Resetting ACLs..." -ForegroundColor White
try { Reset-Acl $InstallDir } catch { Write-Host "WARNING: ACL reset failed: $_" -ForegroundColor Yellow }

# ── 4. Copy updated exe ───────────────────────────────────────────────────────
Write-Host "Copying updated executable..." -ForegroundColor White
Copy-Item -Path $SrcExe -Destination $ExePath -Force
if (-not (Test-Path $ExePath)) {
    Write-Host "ERROR: Failed to copy exe." -ForegroundColor Red
    exit 1
}
Write-Host "Exe updated." -ForegroundColor Green

# ── 5. Re-lock ACLs ───────────────────────────────────────────────────────────
Set-LockedAcl $InstallDir $true
Write-Host "ACLs re-locked." -ForegroundColor Green

# ── 6. Start updated service ──────────────────────────────────────────────────
Write-Host "Starting service..." -ForegroundColor White
& $NssmExe start $ServiceName 2>&1 | Out-Null
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName -EA SilentlyContinue
if ($svc -and $svc.Status -eq "Running") {
    Write-Host ""
    Write-Host "Update complete! Service is running." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "WARNING: Service may not have started. Check $InstallDir\logs\stderr.log" -ForegroundColor Yellow
}
Write-Host ""
