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

{ Stops and removes any existing service installation so files are not locked }
procedure StopExistingService();
var
  NssmExe: String;
  ResultCode: Integer;
begin
  { Try NSSM from the current install dir first }
  NssmExe := ExpandConstant('{app}\nssm.exe');
  if FileExists(NssmExe) then
  begin
    Exec(NssmExe, 'stop {#ServiceName}',          '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
    Exec(NssmExe, 'remove {#ServiceName} confirm', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
  { sc.exe fallback — handles cases where NSSM wasn't present }
  Exec('sc.exe', 'stop {#ServiceName}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingVersion: String;
begin
  Result := '';
  IsReinstall := False;
  { Check whether a previous installation exists in the registry }
  if RegQueryStringValue(HKLM,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1',
      'DisplayVersion', ExistingVersion) then
  begin
    IsReinstall := True;
    StopExistingService();
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
    '# Thorough cleanup of any existing installation' + #13#10 +
    'Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue' + #13#10 +
    '& $nssm stop   $svc 2>$null' + #13#10 +
    '& $nssm remove $svc confirm 2>$null' + #13#10 +
    'sc.exe stop   $svc 2>$null | Out-Null' + #13#10 +
    'sc.exe delete $svc 2>$null | Out-Null' + #13#10 +
    'Start-Sleep -Seconds 2' + #13#10 +
    '' + #13#10 +
    '# Kill any stray processes' + #13#10 +
    'Get-Process -Name "screen-time-manager" -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue' + #13#10 +
    'Start-Sleep -Milliseconds 500' + #13#10 +
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
    '# Lock down install directory' + #13#10 +
    '$acl = Get-Acl $dir' + #13#10 +
    '$acl.SetAccessRuleProtection($true, $false)' + #13#10 +
    '$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("Administrators","FullControl","ContainerInherit,ObjectInherit","None","Allow")))' + #13#10 +
    '$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("SYSTEM","FullControl","ContainerInherit,ObjectInherit","None","Allow")))' + #13#10 +
    '$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("Users","ReadAndExecute","ContainerInherit,ObjectInherit","None","Allow")))' + #13#10 +
    'Set-Acl $dir $acl' + #13#10 +
    '' + #13#10 +
    '# Lock down ProgramData DB directory' + #13#10 +
    '$dbDir = Join-Path $env:ProgramData "ScreenTimeManager"' + #13#10 +
    'New-Item -ItemType Directory -Path $dbDir -Force | Out-Null' + #13#10 +
    '$dbAcl = Get-Acl $dbDir' + #13#10 +
    '$dbAcl.SetAccessRuleProtection($true, $false)' + #13#10 +
    '$dbAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("Administrators","FullControl","ContainerInherit,ObjectInherit","None","Allow")))' + #13#10 +
    '$dbAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("SYSTEM","FullControl","ContainerInherit,ObjectInherit","None","Allow")))' + #13#10 +
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
        { Upgrade: skip setup wizard, open settings directly }
        ShellExec('runas', AppExe, '--settings', '', SW_SHOWNORMAL, ewNoWait, ResultCode)
      else
        { Fresh install: run PIN setup + initial settings }
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
  ResultCode: Integer;
  NssmExe: String;
  DataDir: String;
begin
  case CurUninstallStep of
    usUninstall:
    begin
      NssmExe := ExpandConstant('{app}\nssm.exe');
      if FileExists(NssmExe) then
      begin
        Exec(NssmExe, 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Sleep(2000);
        Exec(NssmExe, 'remove {#ServiceName} confirm', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Sleep(1000);
      end;
      { Fallback: sc.exe in case NSSM didn't clean up }
      Exec('sc.exe', 'stop {#ServiceName}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec('taskkill.exe', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(500);
    end;

    usPostUninstall:
    begin
      if ShouldDeleteData then
      begin
        DataDir := ExpandConstant('{commonappdata}\ScreenTimeManager');
        if DirExists(DataDir) then
          DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
