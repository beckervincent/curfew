; Screen Time Manager - Inno Setup Installer Script
; Requires: /DMyAppVersion=x.y.z passed on the ISCC command line

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName      "Screen Time Manager"
#define MyAppPublisher "Screen Time Manager"
#define MyAppExeName   "screen-time-manager.exe"
#define ServiceName    "ScreenTimeManager"

[Setup]
AppId={{6B3F8E2A-4C71-4D9E-B2A0-8F1D3E7C9A05}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppUpdatesURL=https://github.com/beckervincent/Screen-Time-Manager-for-Windows/releases
DefaultDirName={autopf}\ScreenTimeManager
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=..\
OutputBaseFilename=screen-time-manager-setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "screen-time-manager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "nssm.exe";                DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{commonprograms}\{#MyAppName}\Settings";  Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"; Comment: "Open Screen Time Manager settings"
Name: "{commonprograms}\{#MyAppName}\Uninstall"; Filename: "{uninstallexe}";        Comment: "Uninstall Screen Time Manager"

[Code]

var
  ShouldDeleteData: Boolean;
  IsReinstall: Boolean;

{ Write a string to a file, return true on success }
function WriteScript(const Path, Content: String): Boolean;
var
  Lines: TArrayOfString;
begin
  SetArrayLength(Lines, 1);
  Lines[0] := Content;
  Result := SaveStringsToFile(Path, Lines, False);
end;

{ Run a PowerShell script file and return the exit code }
function RunPS(const ScriptPath: String): Integer;
var
  ResultCode: Integer;
begin
  Exec('powershell.exe',
    '-NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode;
end;

{ Stop and remove the service using NSSM (primary) then sc.exe (fallback),
  reset ACLs on the install dir and ProgramData dir so files can be deleted,
  and kill any remaining processes. }
procedure RunCleanupScript(const AppDir: String);
var
  NssmExe, ScriptPath, Script: String;
begin
  NssmExe    := AppDir + '\nssm.exe';
  ScriptPath := ExpandConstant('{tmp}\stm_cleanup.ps1');

  Script :=
    '$svc  = "ScreenTimeManager"' + #13#10 +
    '$nssm = "' + NssmExe + '"' + #13#10 +
    '$dir  = "' + AppDir  + '"' + #13#10 +
    '' + #13#10 +
    '# Stop and remove service via NSSM first' + #13#10 +
    'if (Test-Path $nssm) {' + #13#10 +
    '    & $nssm stop   $svc 2>$null' + #13#10 +
    '    Start-Sleep -Seconds 2' + #13#10 +
    '    & $nssm remove $svc confirm 2>$null' + #13#10 +
    '    Start-Sleep -Seconds 1' + #13#10 +
    '}' + #13#10 +
    '' + #13#10 +
    '# fallback in case nssm.exe was missing' + #13#10 +
    'if (-not (Test-Path $nssm)) {' + #13#10 +
    '    & (Join-Path $env:windir "System32\nssm.exe") stop   $svc 2>$null' + #13#10 +
    '    & (Join-Path $env:windir "System32\nssm.exe") remove $svc confirm 2>$null' + #13#10 +
    '}' + #13#10 +
    '' + #13#10 +
    '# Kill any remaining processes' + #13#10 +
    'Get-Process -Name "screen-time-manager" -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue' + #13#10 +
    'Start-Sleep -Milliseconds 500' + #13#10 +
    '' + #13#10 +
    '# Reset ACLs on install dir so Inno Setup can delete files' + #13#10 +
    'if (Test-Path $dir) {' + #13#10 +
    '    $acl = Get-Acl $dir' + #13#10 +
    '    $acl.SetAccessRuleProtection($false, $true)' + #13#10 +
    '    Set-Acl $dir $acl' + #13#10 +
    '}' + #13#10 +
    '' + #13#10 +
    '# Reset ACLs on ProgramData dir so it can be deleted if needed' + #13#10 +
    '$dbDir = Join-Path $env:ProgramData "ScreenTimeManager"' + #13#10 +
    'if (Test-Path $dbDir) {' + #13#10 +
    '    $dbAcl = Get-Acl $dbDir' + #13#10 +
    '    $dbAcl.SetAccessRuleProtection($false, $true)' + #13#10 +
    '    Set-Acl $dbDir $dbAcl' + #13#10 +
    '}' + #13#10;

  if WriteScript(ScriptPath, Script) then
  begin
    RunPS(ScriptPath);
    DeleteFile(ScriptPath);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingVersion: String;
  AppDir, DbDir: String;
begin
  Result := '';
  IsReinstall := False;

  if RegQueryStringValue(HKLM,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1',
      'InstallLocation', AppDir) then
  begin
    IsReinstall := True;
    { Strip trailing backslash if present }
    if (Length(AppDir) > 0) and (AppDir[Length(AppDir)] = '\') then
      AppDir := Copy(AppDir, 1, Length(AppDir) - 1);

    { Stop service, reset ACLs }
    RunCleanupScript(AppDir);

    { Wipe existing settings/database so install starts clean }
    DbDir := ExpandConstant('{commonappdata}\ScreenTimeManager');
    if DirExists(DbDir) then
      DelTree(DbDir, True, True, True);
  end;
end;

procedure InstallService();
var
  ScriptPath: String;
  AppDir, NssmExe, AppExe, LogDir: String;
  Script: String;
  ResultCode: Integer;
begin
  AppDir  := ExpandConstant('{app}');
  NssmExe := AppDir + '\nssm.exe';
  AppExe  := AppDir + '\screen-time-manager.exe';
  LogDir  := AppDir + '\logs';
  ScriptPath := ExpandConstant('{tmp}\stm_install_svc.ps1');

  Script :=
    '$svc   = "ScreenTimeManager"' + #13#10 +
    '$nssm  = "' + NssmExe + '"' + #13#10 +
    '$app   = "' + AppExe  + '"' + #13#10 +
    '$dir   = "' + AppDir  + '"' + #13#10 +
    '$logs  = "' + LogDir  + '"' + #13#10 +
    '' + #13#10 +
    '# Ensure no stale service entry exists' + #13#10 +
    '& $nssm stop   $svc 2>$null' + #13#10 +
    '& $nssm remove $svc confirm 2>$null' + #13#10 +
    'Start-Sleep -Seconds 1' + #13#10 +
    '' + #13#10 +
    '# Install via NSSM' + #13#10 +
    '& $nssm install $svc $app "--service"' + #13#10 +
    '& $nssm set $svc AppDirectory  $dir' + #13#10 +
    '& $nssm set $svc ObjectName    "LocalSystem"' + #13#10 +
    '& $nssm set $svc DisplayName   "Screen Time Manager"' + #13#10 +
    '& $nssm set $svc Description   "Screen Time Manager - Manages daily computer time limits"' + #13#10 +
    '& $nssm set $svc Start         SERVICE_AUTO_START' + #13#10 +
    '' + #13#10 +
    '# Log rotation' + #13#10 +
    'New-Item -ItemType Directory -Path $logs -Force | Out-Null' + #13#10 +
    '& $nssm set $svc AppStdout       "$logs\stdout.log"' + #13#10 +
    '& $nssm set $svc AppStderr       "$logs\stderr.log"' + #13#10 +
    '& $nssm set $svc AppRotateFiles  1' + #13#10 +
    '& $nssm set $svc AppRotateSeconds 86400' + #13#10 +
    '' + #13#10 +
    '# Lock down install directory (SID-based, locale-independent)' + #13#10 +
    'function AclRule($sidStr,$rights,$inherit,$prop,$type){' + #13#10 +
    '    $sid=New-Object System.Security.Principal.SecurityIdentifier($sidStr)' + #13#10 +
    '    New-Object System.Security.AccessControl.FileSystemAccessRule($sid,$rights,$inherit,$prop,$type)' + #13#10 +
    '}' + #13#10 +
    '$acl = Get-Acl $dir' + #13#10 +
    '$acl.SetAccessRuleProtection($true, $false)' + #13#10 +
    '$acl.AddAccessRule((AclRule "S-1-5-32-544" "FullControl"    "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    '$acl.AddAccessRule((AclRule "S-1-5-18"     "FullControl"    "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    '$acl.AddAccessRule((AclRule "S-1-5-32-545" "ReadAndExecute" "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    'Set-Acl $dir $acl' + #13#10 +
    '' + #13#10 +
    '# Lock down ProgramData DB directory' + #13#10 +
    '$dbDir = Join-Path $env:ProgramData "ScreenTimeManager"' + #13#10 +
    'New-Item -ItemType Directory -Path $dbDir -Force | Out-Null' + #13#10 +
    '$dbAcl = Get-Acl $dbDir' + #13#10 +
    '$dbAcl.SetAccessRuleProtection($true, $false)' + #13#10 +
    '$dbAcl.AddAccessRule((AclRule "S-1-5-32-544" "FullControl" "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    '$dbAcl.AddAccessRule((AclRule "S-1-5-18"     "FullControl" "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    'Set-Acl $dbDir $dbAcl' + #13#10 +
    '' + #13#10 +
    '# Start the service' + #13#10 +
    '& $nssm start $svc' + #13#10;

  if not WriteScript(ScriptPath, Script) then
  begin
    MsgBox('Failed to write service install script.', mbError, MB_OK);
    Exit;
  end;

  ResultCode := RunPS(ScriptPath);
  if ResultCode <> 0 then
    MsgBox('Service installation completed with warnings (code ' + IntToStr(ResultCode) + '). ' +
           'The application may need to be started manually from Services.', mbInformation, MB_OK);

  DeleteFile(ScriptPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppExe: String;
  ResultCode: Integer;
begin
  case CurStep of
    ssPostInstall:
      InstallService();
    ssDone:
    begin
      AppExe := ExpandConstant('{app}\{#MyAppExeName}');
      if IsReinstall then
        ShellExec('runas', AppExe, '--settings', '', SW_SHOWNORMAL, ewNoWait, ResultCode)
      else
        ShellExec('runas', AppExe, '--setup', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
  end;
end;

function InitializeUninstall(): Boolean;
var
  Answer: Integer;
begin
  Result := True;
  Answer := MsgBox(
    'Do you want to delete all Screen Time Manager data?' + #13#10 +
    '(time limits, passcode, and usage history)' + #13#10#13#10 +
    'Select Yes to remove all data, or No to keep it.',
    mbConfirmation,
    MB_YESNO or MB_DEFBUTTON2
  );
  ShouldDeleteData := (Answer = IDYES);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  case CurUninstallStep of
    usUninstall:
    begin
      { Reset ACLs, stop service, kill processes — must run before Inno Setup deletes files }
      RunCleanupScript(ExpandConstant('{app}'));
    end;

    usPostUninstall:
    begin
      if ShouldDeleteData then
      begin
        DataDir := ExpandConstant('{commonappdata}\ScreenTimeManager');
        if DirExists(DataDir) then
          DelTree(DataDir, True, True, True);
      end;
      { Remove the install directory itself if it still exists }
      if DirExists(ExpandConstant('{app}')) then
        DelTree(ExpandConstant('{app}'), True, True, True);
    end;
  end;
end;
