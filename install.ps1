# Screen Time Manager - Install Script (NSSM)
# Run as Administrator

$ServiceName = "ScreenTimeManager"
$Description = "Screen Time Manager - Manages daily computer time limits"
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallDir  = Join-Path $env:ProgramFiles "ScreenTimeManager"
$SrcExe      = Join-Path $ScriptDir "screen-time-manager.exe"
$SrcNssm     = Join-Path $ScriptDir "nssm.exe"
$NssmExe     = Join-Path $InstallDir "nssm.exe"
$ExePath     = Join-Path $InstallDir "screen-time-manager.exe"

Write-Host "Screen Time Manager - Installation" -ForegroundColor Cyan
Write-Host "===================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $SrcExe)) {
    Write-Host "ERROR: Could not find screen-time-manager.exe in $ScriptDir" -ForegroundColor Red
    Write-Host "Build the project first: cargo build --release" -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $SrcNssm)) {
    Write-Host "ERROR: Could not find nssm.exe in $ScriptDir" -ForegroundColor Red
    exit 1
}

# Create install directory
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Write-Host "Install directory: $InstallDir" -ForegroundColor Green

# Copy files to install directory
Copy-Item -Path $SrcExe  -Destination $ExePath  -Force
Copy-Item -Path $SrcNssm -Destination $NssmExe  -Force
Write-Host "Files copied to $InstallDir" -ForegroundColor Green

# Apply ACLs: Admins + SYSTEM = FullControl; Users = ReadAndExecute only
$acl = Get-Acl $InstallDir
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "Administrators","FullControl","ContainerInherit,ObjectInherit","None","Allow")))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "SYSTEM","FullControl","ContainerInherit,ObjectInherit","None","Allow")))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "Users","ReadAndExecute","ContainerInherit,ObjectInherit","None","Allow")))
Set-Acl $InstallDir $acl
Write-Host "ACLs applied to $InstallDir" -ForegroundColor Green

# Create and lock down the ProgramData database directory
$dbDir = Join-Path $env:ProgramData "ScreenTimeManager"
New-Item -ItemType Directory -Path $dbDir -Force | Out-Null
$dbAcl = Get-Acl $dbDir
$dbAcl.SetAccessRuleProtection($true, $false)
$dbAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "Administrators","FullControl","ContainerInherit,ObjectInherit","None","Allow")))
$dbAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "SYSTEM","FullControl","ContainerInherit,ObjectInherit","None","Allow")))
Set-Acl $dbDir $dbAcl
Write-Host "ACLs applied to $dbDir" -ForegroundColor Green
Write-Host ""

Write-Host "Executable : $ExePath" -ForegroundColor Green
Write-Host "The service runs as LocalSystem to spawn the tray app in every user session." -ForegroundColor White
Write-Host ""

# Remove legacy scheduled task if present
$oldTask = Get-ScheduledTask -TaskName $ServiceName -ErrorAction SilentlyContinue
if ($oldTask) {
    Write-Host "Removing legacy scheduled task..." -ForegroundColor Yellow
    Stop-ScheduledTask  -TaskName $ServiceName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $ServiceName -Confirm:$false
}

# Stop and remove existing service if present
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingSvc) {
    Write-Host "Removing existing service..." -ForegroundColor Yellow
    & $NssmExe stop $ServiceName 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    & $NssmExe remove $ServiceName confirm 2>&1 | Out-Null
    Start-Sleep -Seconds 1
}

# Kill ALL running instances (service + tray apps in all user sessions)
$procs = Get-Process -Name "screen-time-manager" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "Terminating running instances..." -ForegroundColor Yellow
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Install service
Write-Host "Installing NSSM service..." -ForegroundColor White
$out = & $NssmExe install $ServiceName $ExePath "--service" 2>&1
Write-Host $out
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: nssm install failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

& $NssmExe set $ServiceName AppDirectory  $InstallDir  2>&1 | Out-Null
& $NssmExe set $ServiceName ObjectName    "LocalSystem" 2>&1 | Out-Null
& $NssmExe set $ServiceName DisplayName   "Screen Time Manager" 2>&1 | Out-Null
& $NssmExe set $ServiceName Description   $Description 2>&1 | Out-Null
& $NssmExe set $ServiceName Start         SERVICE_AUTO_START 2>&1 | Out-Null

$logDir = Join-Path $InstallDir "logs"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
& $NssmExe set $ServiceName AppStdout       (Join-Path $logDir "stdout.log") 2>&1 | Out-Null
& $NssmExe set $ServiceName AppStderr       (Join-Path $logDir "stderr.log") 2>&1 | Out-Null
& $NssmExe set $ServiceName AppRotateFiles  1     2>&1 | Out-Null
& $NssmExe set $ServiceName AppRotateSeconds 86400 2>&1 | Out-Null

Write-Host ""
Write-Host "Service configured:" -ForegroundColor Green
Write-Host "  Name      : $ServiceName"         -ForegroundColor White
Write-Host "  Executable: $ExePath --service"   -ForegroundColor White
Write-Host "  Directory : $InstallDir"          -ForegroundColor White
Write-Host "  User      : LocalSystem"          -ForegroundColor White
Write-Host ""

$response = Read-Host "Start Screen Time Manager now? (Y/n)"
if ($response -eq "" -or $response -match "^[Yy]") {
    & $NssmExe start $ServiceName 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq "Running") {
        Write-Host "Started!" -ForegroundColor Green
    } else {
        Write-Host "WARNING: Service may not have started. Check $logDir\stderr.log" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "To update : run update.bat"    -ForegroundColor Cyan
Write-Host "To remove : run uninstall.bat" -ForegroundColor Cyan
Write-Host ""
