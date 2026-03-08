# Screen Time Manager - Install Script (NSSM)
# Run as Administrator

#Requires -RunAsAdministrator

$ServiceName = "ScreenTimeManager"
$Description = "Screen Time Manager - Manages daily computer time limits"
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallDir  = Join-Path $env:ProgramFiles "ScreenTimeManager"
$SrcExe      = Join-Path $ScriptDir "screen-time-manager.exe"
$SrcNssm     = Join-Path $ScriptDir "nssm.exe"
$NssmExe     = Join-Path $InstallDir "nssm.exe"
$ExePath     = Join-Path $InstallDir "screen-time-manager.exe"
$DbDir       = Join-Path $env:ProgramData "ScreenTimeManager"

Write-Host "Screen Time Manager - Installation" -ForegroundColor Cyan
Write-Host "===================================" -ForegroundColor Cyan
Write-Host ""

# ── Validate sources ──────────────────────────────────────────────────────────
if (-not (Test-Path $SrcExe)) {
    Write-Host "ERROR: screen-time-manager.exe not found in $ScriptDir" -ForegroundColor Red
    Write-Host "Run build.bat first." -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path $SrcNssm)) {
    Write-Host "ERROR: nssm.exe not found in $ScriptDir" -ForegroundColor Red
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
        $rights = if ($includeUsers -eq "Modify") { "Modify" } else { "ReadAndExecute" }
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $s, $rights, "ContainerInherit,ObjectInherit", "None", "Allow")))
    }
    Set-Acl $path $acl
}

function Reset-Acl($path) {
    $acl = Get-Acl $path
    $acl.SetAccessRuleProtection($false, $true)
    Set-Acl $path $acl
}

# ── Stop/remove existing service and processes ────────────────────────────────
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingSvc) {
    Write-Host "Stopping existing service..." -ForegroundColor Yellow
    & $NssmExe stop $ServiceName 2>&1 | Out-Null
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Service $ServiceName -EA SilentlyContinue).Status -eq 'Running' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    & $NssmExe remove $ServiceName confirm 2>&1 | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host "Terminating all running instances..." -ForegroundColor White
Get-Process -Name "screen-time-manager" -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Process -Name "screen-time-manager" -EA SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}

# Remove legacy scheduled task if present
$oldTask = Get-ScheduledTask -TaskName $ServiceName -ErrorAction SilentlyContinue
if ($oldTask) {
    Write-Host "Removing legacy scheduled task..." -ForegroundColor Yellow
    Stop-ScheduledTask  -TaskName $ServiceName -EA SilentlyContinue
    Unregister-ScheduledTask -TaskName $ServiceName -Confirm:$false
}

# ── Reset ACLs on existing install dir so we can write into it ────────────────
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
try { Reset-Acl $InstallDir } catch {}

# ── Copy files ────────────────────────────────────────────────────────────────
Write-Host "Copying files to $InstallDir..." -ForegroundColor White
Copy-Item -Path $SrcExe  -Destination $ExePath  -Force
Copy-Item -Path $SrcNssm -Destination $NssmExe  -Force

if (-not (Test-Path $ExePath) -or -not (Test-Path $NssmExe)) {
    Write-Host "ERROR: File copy failed." -ForegroundColor Red
    exit 1
}
Write-Host "Files copied." -ForegroundColor Green

# ── Lock down install dir (ACLs) ──────────────────────────────────────────────
Set-LockedAcl $InstallDir $true
Write-Host "Install dir locked." -ForegroundColor Green

# ── Create and lock down ProgramData DB dir ───────────────────────────────────
New-Item -ItemType Directory -Path $DbDir -Force | Out-Null
try { Reset-Acl $DbDir } catch {}
Set-LockedAcl $DbDir "Modify"  # Users need Modify to read/write their own DB
Write-Host "DB dir locked: $DbDir" -ForegroundColor Green

# ── Install service ───────────────────────────────────────────────────────────
Write-Host "Installing NSSM service..." -ForegroundColor White
$out = & $NssmExe install $ServiceName $ExePath "--service" 2>&1
Write-Host $out
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: nssm install failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

& $NssmExe set $ServiceName AppDirectory   $InstallDir       2>&1 | Out-Null
& $NssmExe set $ServiceName ObjectName     "LocalSystem"     2>&1 | Out-Null
& $NssmExe set $ServiceName DisplayName    "Screen Time Manager" 2>&1 | Out-Null
& $NssmExe set $ServiceName Description    $Description      2>&1 | Out-Null
& $NssmExe set $ServiceName Start          SERVICE_AUTO_START 2>&1 | Out-Null

$logDir = Join-Path $InstallDir "logs"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
& $NssmExe set $ServiceName AppStdout        (Join-Path $logDir "stdout.log") 2>&1 | Out-Null
& $NssmExe set $ServiceName AppStderr        (Join-Path $logDir "stderr.log") 2>&1 | Out-Null
& $NssmExe set $ServiceName AppRotateFiles   1      2>&1 | Out-Null
& $NssmExe set $ServiceName AppRotateSeconds 86400  2>&1 | Out-Null
Write-Host "Service configured." -ForegroundColor Green

# ── Start service ─────────────────────────────────────────────────────────────
$response = if ([Environment]::UserInteractive) { Read-Host "Start Screen Time Manager now? (Y/n)" } else { "Y" }
if ($response -eq "" -or $response -match "^[Yy]") {
    & $NssmExe start $ServiceName 2>&1 | Out-Null
    Start-Sleep -Seconds 3
    $svc = Get-Service -Name $ServiceName -EA SilentlyContinue
    if ($svc -and $svc.Status -eq "Running") {
        Write-Host "Service started." -ForegroundColor Green
    } else {
        Write-Host "WARNING: Service may not have started. Check $logDir\stderr.log" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host "  Install dir : $InstallDir" -ForegroundColor White
Write-Host "  Database    : $DbDir"      -ForegroundColor White
Write-Host ""
Write-Host "To update : run update.bat"    -ForegroundColor Cyan
Write-Host "To remove : run uninstall.bat" -ForegroundColor Cyan
Write-Host ""
