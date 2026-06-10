; Curfew - Inno Setup installer (.NET / WinUI build)
; Requires: /DMyAppVersion=x.y.z passed on the ISCC command line

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName      "Curfew"
#define MyAppPublisher "Curfew"
#define AppExeName     "Curfew.App.exe"
#define ServiceExeName "Curfew.Service.exe"
#define ServiceName    "Curfew"
#define DataFolder     "Curfew"

[Setup]
AppId={{6B3F8E2A-4C71-4D9E-B2A0-8F1D3E7C9A05}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppUpdatesURL=https://github.com/beckervincent/curfew/releases
DefaultDirName={autopf}\Curfew
SetupIconFile=..\resources\app.ico
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
UninstallDisplayIcon={app}\app\{#AppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=..\
OutputBaseFilename=curfew-setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
; Do NOT use RestartManager to close our files: it cannot stop the SYSTEM
; service and aborts a silent install. The service is stopped explicitly in
; PrepareToInstall instead.
CloseApplications=no
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "service\*"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion
Source: "app\*";     DestDir: "{app}\app";     Flags: recursesubdirs ignoreversion
Source: "overlay\*"; DestDir: "{app}\overlay"; Flags: recursesubdirs ignoreversion
Source: "nssm.exe";  DestDir: "{app}";         Flags: ignoreversion

[Icons]
Name: "{commonprograms}\{#MyAppName}\Settings";  Filename: "{app}\app\{#AppExeName}"; Parameters: "--settings"; Comment: "Open Curfew settings"
Name: "{commonprograms}\{#MyAppName}\Uninstall"; Filename: "{uninstallexe}";          Comment: "Uninstall Curfew"

[Code]

var
  ShouldDeleteData: Boolean;
  IsUpdate: Boolean;

function WriteScript(const Path, Content: String): Boolean;
var
  Lines: TArrayOfString;
begin
  SetArrayLength(Lines, 1);
  Lines[0] := Content;
  Result := SaveStringsToFile(Path, Lines, False);
end;

function RunPS(const ScriptPath: String): Integer;
var
  ResultCode: Integer;
begin
  Exec('powershell.exe',
    '-NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode;
end;

{ Stop and remove the service, kill leftover processes, reset ACLs so files
  can be replaced or deleted. }
procedure RunCleanupScript(const AppDir: String);
var
  NssmExe, ScriptPath, Script: String;
begin
  NssmExe    := AppDir + '\nssm.exe';
  ScriptPath := ExpandConstant('{tmp}\curfew_cleanup.ps1');

  Script :=
    '$svc  = "{#ServiceName}"' + #13#10 +
    '$nssm = "' + NssmExe + '"' + #13#10 +
    '$dir  = "' + AppDir  + '"' + #13#10 +
    '' + #13#10 +
    'if (Test-Path $nssm) {' + #13#10 +
    '    & $nssm stop   $svc 2>$null' + #13#10 +
    '    Start-Sleep -Seconds 2' + #13#10 +
    '    & $nssm remove $svc confirm 2>$null' + #13#10 +
    '    Start-Sleep -Seconds 1' + #13#10 +
    '} else {' + #13#10 +
    '    sc.exe stop   $svc 2>$null | Out-Null' + #13#10 +
    '    Start-Sleep -Seconds 2' + #13#10 +
    '    sc.exe delete $svc 2>$null | Out-Null' + #13#10 +
    '}' + #13#10 +
    '' + #13#10 +
    'Get-Process -Name "Curfew.App","Curfew.Overlay","Curfew.Service" -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue' + #13#10 +
    'Start-Sleep -Milliseconds 500' + #13#10 +
    '' + #13#10 +
    'foreach ($p in @($dir, (Join-Path $env:ProgramData "{#DataFolder}"))) {' + #13#10 +
    '    if (Test-Path $p) {' + #13#10 +
    '        $acl = Get-Acl $p' + #13#10 +
    '        $acl.SetAccessRuleProtection($false, $true)' + #13#10 +
    '        Set-Acl $p $acl' + #13#10 +
    '    }' + #13#10 +
    '}' + #13#10;

  if WriteScript(ScriptPath, Script) then
  begin
    RunPS(ScriptPath);
    DeleteFile(ScriptPath);
  end;
end;

{ Stop a previous install's service and processes before any file work, so its
  binaries are not locked during copy. Runs first, before the wizard. }
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(3000);
  Exec('taskkill.exe', '/f /im {#ServiceExeName} /im {#AppExeName} /im Curfew.Overlay.exe',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppDir: String;
begin
  Result := '';
  IsUpdate := False;

  if RegQueryStringValue(HKLM,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1',
      'InstallLocation', AppDir) then
  begin
    IsUpdate := True;
    if (Length(AppDir) > 0) and (AppDir[Length(AppDir)] = '\') then
      AppDir := Copy(AppDir, 1, Length(AppDir) - 1);
    RunCleanupScript(AppDir);
  end;
end;

procedure InstallService();
var
  ScriptPath: String;
  AppDir, NssmExe, ServiceExe, LogDir: String;
  Script: String;
  ResultCode: Integer;
begin
  AppDir     := ExpandConstant('{app}');
  NssmExe    := AppDir + '\nssm.exe';
  ServiceExe := AppDir + '\service\{#ServiceExeName}';
  LogDir     := AppDir + '\logs';
  ScriptPath := ExpandConstant('{tmp}\curfew_install_svc.ps1');

  Script :=
    '$svc   = "{#ServiceName}"' + #13#10 +
    '$nssm  = "' + NssmExe    + '"' + #13#10 +
    '$exe   = "' + ServiceExe + '"' + #13#10 +
    '$dir   = "' + AppDir     + '"' + #13#10 +
    '$logs  = "' + LogDir     + '"' + #13#10 +
    '' + #13#10 +
    '& $nssm stop   $svc 2>$null' + #13#10 +
    '& $nssm remove $svc confirm 2>$null' + #13#10 +
    'Start-Sleep -Seconds 1' + #13#10 +
    '' + #13#10 +
    '& $nssm install $svc $exe' + #13#10 +
    '& $nssm set $svc AppDirectory  (Split-Path $exe)' + #13#10 +
    '& $nssm set $svc ObjectName    "LocalSystem"' + #13#10 +
    '& $nssm set $svc DisplayName   "Curfew"' + #13#10 +
    '& $nssm set $svc Description   "Curfew - Manages daily computer time limits"' + #13#10 +
    '& $nssm set $svc Start         SERVICE_AUTO_START' + #13#10 +
    '' + #13#10 +
    'New-Item -ItemType Directory -Path $logs -Force | Out-Null' + #13#10 +
    '& $nssm set $svc AppStdout       "$logs\stdout.log"' + #13#10 +
    '& $nssm set $svc AppStderr       "$logs\stderr.log"' + #13#10 +
    '& $nssm set $svc AppRotateFiles  1' + #13#10 +
    '& $nssm set $svc AppRotateSeconds 86400' + #13#10 +
    '' + #13#10 +
    '# Lock down install dir (read-only for users); DB dir writable for the app' + #13#10 +
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
    '$dbDir = Join-Path $env:ProgramData "{#DataFolder}"' + #13#10 +
    'New-Item -ItemType Directory -Path $dbDir -Force | Out-Null' + #13#10 +
    '$dbAcl = Get-Acl $dbDir' + #13#10 +
    '$dbAcl.SetAccessRuleProtection($true, $false)' + #13#10 +
    '$dbAcl.AddAccessRule((AclRule "S-1-5-32-544" "FullControl" "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    '$dbAcl.AddAccessRule((AclRule "S-1-5-18"     "FullControl" "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    '$dbAcl.AddAccessRule((AclRule "S-1-5-32-545" "Modify"      "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    'Set-Acl $dbDir $dbAcl' + #13#10 +
    '' + #13#10 +
    '& $nssm start $svc' + #13#10 +
    '' + #13#10 +
    'schtasks /delete /tn "CurfewAutoUpdate" /f 2>$null | Out-Null' + #13#10 +
    'Remove-Item -Recurse -Force (Join-Path $dbDir "update") -EA SilentlyContinue' + #13#10;

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
  ResultCode: Integer;
begin
  case CurStep of
    ssPostInstall:
      InstallService();
    ssDone:
      if not IsUpdate then
        ShellExec('runas', ExpandConstant('{app}\app\{#AppExeName}'), '--setup', '',
                  SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

function InitializeUninstall(): Boolean;
var
  Answer: Integer;
begin
  Result := True;
  ShouldDeleteData := False;

  if not UninstallSilent then
  begin
    Answer := MsgBox(
      'Do you want to delete all Curfew data?' + #13#10 +
      '(time limits, passcode, and usage history)' + #13#10#13#10 +
      'Select Yes to remove all data, or No to keep it.',
      mbConfirmation,
      MB_YESNO or MB_DEFBUTTON2
    );
    ShouldDeleteData := (Answer = IDYES);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  case CurUninstallStep of
    usUninstall:
      RunCleanupScript(ExpandConstant('{app}'));

    usPostUninstall:
    begin
      if ShouldDeleteData then
      begin
        DataDir := ExpandConstant('{commonappdata}\{#DataFolder}');
        if DirExists(DataDir) then
          DelTree(DataDir, True, True, True);
      end;
      if DirExists(ExpandConstant('{app}')) then
        DelTree(ExpandConstant('{app}'), True, True, True);
    end;
  end;
end;
