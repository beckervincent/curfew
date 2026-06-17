; Curfew - Inno Setup installer (.NET / WinUI build)
; needs /DMyAppVersion=x.y.z on ISCC command line

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
; no RestartManager to close our files: cant stop SYSTEM service + aborts silent install. service stopped explicitly in PrepareToInstall
CloseApplications=no
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "service\*"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion
Source: "app\*";     DestDir: "{app}\app";     Flags: recursesubdirs ignoreversion
; headless PIN-gated config CLI, installed alongside the app as {app}\app\curfew-cli.exe
Source: "cli\*";     DestDir: "{app}\app";     Flags: recursesubdirs ignoreversion
Source: "overlay\*"; DestDir: "{app}\overlay"; Flags: recursesubdirs ignoreversion
; uninstall guard: verify parent passcode before uninstall proceeds
; lands in {app}, ACL'd ReadAndExecute for Users, so child cant edit it
Source: "verify-uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{commonprograms}\{#MyAppName}\Curfew Settings";  Filename: "{app}\app\{#AppExeName}"; Parameters: "--settings"; Comment: "Open Curfew settings"
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

{ stop+remove service, kill leftover processes, reset ACLs so files can be replaced/deleted }
procedure RunCleanupScript(const AppDir: String);
var
  ScriptPath, Script: String;
begin
  ScriptPath := ExpandConstant('{tmp}\curfew_cleanup.ps1');

  Script :=
    '$svc  = "{#ServiceName}"' + #13#10 +
    '$dir  = "' + AppDir  + '"' + #13#10 +
    '' + #13#10 +
    '# 1. stop+delete native Windows service' + #13#10 +
    'sc.exe stop   $svc 2>$null | Out-Null' + #13#10 +
    'Start-Sleep -Seconds 2' + #13#10 +
    'sc.exe delete $svc 2>$null | Out-Null' + #13#10 +
    'Start-Sleep -Milliseconds 500' + #13#10 +
    '' + #13#10 +
    '# 2. kill app/overlay/service processes + overlay task so files unlock' + #13#10 +
    'Get-Process -Name "Curfew.App","Curfew.Overlay","Curfew.Service" -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue' + #13#10 +
    'taskkill /f /im "{#ServiceExeName}" 2>$null | Out-Null' + #13#10 +
    'schtasks /delete /tn "CurfewOverlay" /f 2>$null | Out-Null' + #13#10 +
    'Start-Sleep -Milliseconds 500' + #13#10 +
    '' + #13#10 +
    '# 3. strip our hosts-file blocklist section so blocked domains do not persist after removal' + #13#10 +
    '$hostsFile = "$env:SystemRoot\System32\drivers\etc\hosts"' + #13#10 +
    'if (Test-Path $hostsFile) {' + #13#10 +
    '    $keep = @(); $inBlock = $false' + #13#10 +
    '    foreach ($line in Get-Content $hostsFile) {' + #13#10 +
    '        if ($line.Trim() -eq ''# BEGIN Curfew blocklist - do not edit'') { $inBlock = $true; continue }' + #13#10 +
    '        if ($inBlock) { if ($line.Trim() -eq ''# END Curfew blocklist'') { $inBlock = $false }; continue }' + #13#10 +
    '        $keep += $line' + #13#10 +
    '    }' + #13#10 +
    '    Set-Content -Path $hostsFile -Value $keep -Encoding ASCII' + #13#10 +
    '}' + #13#10 +
    '' + #13#10 +
    '# 3b. remove browser private-browsing policies so incognito is not left disabled after removal' + #13#10 +
    'reg delete "HKLM\SOFTWARE\Policies\Google\Chrome" /v IncognitoModeAvailability /f 2>$null | Out-Null' + #13#10 +
    'reg delete "HKLM\SOFTWARE\Policies\Microsoft\Edge" /v InPrivateModeAvailability /f 2>$null | Out-Null' + #13#10 +
    'reg delete "HKLM\SOFTWARE\Policies\Mozilla\Firefox" /v DisablePrivateBrowsing /f 2>$null | Out-Null' + #13#10 +
    'reg delete "HKLM\SOFTWARE\Policies\BraveSoftware\Brave" /v IncognitoModeAvailability /f 2>$null | Out-Null' + #13#10 +
    'reg delete "HKLM\SOFTWARE\Policies\Chromium" /v IncognitoModeAvailability /f 2>$null | Out-Null' + #13#10 +
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

{ eat leading numeric component of dotted version string }
function NextVersionNumber(var S: String): Integer;
var
  P: Integer;
  T: String;
begin
  P := Pos('.', S);
  if P = 0 then
  begin
    T := S;
    S := '';
  end
  else
  begin
    T := Copy(S, 1, P - 1);
    S := Copy(S, P + 1, Length(S));
  end;
  Result := StrToIntDef(T, 0);
end;

{ True when Remote (e.g. "v1.7.0") strictly newer MAJOR.MINOR.PATCH than Local. unparsable components = zero, so garbage never newer }
function IsNewerVersion(Remote, Local: String): Boolean;
var
  I, RN, LN: Integer;
begin
  Result := False;
  if (Remote <> '') and (Remote[1] = 'v') then Delete(Remote, 1, 1);
  if (Local <> '') and (Local[1] = 'v') then Delete(Local, 1, 1);
  for I := 1 to 3 do
  begin
    RN := NextVersionNumber(Remote);
    LN := NextVersionNumber(Local);
    if RN > LN then
    begin
      Result := True;
      Exit;
    end;
    if RN < LN then Exit;
  end;
end;

{ tag of newest stable release on GitHub, or '' when lookup fails (offline, rate-limited, API change). callers must fail open }
function GetLatestReleaseTag(): String;
var
  Http: Variant;
  Body: String;
  P: Integer;
begin
  Result := '';
  try
    Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Http.SetTimeouts(5000, 5000, 5000, 5000);
    Http.Open('GET', 'https://api.github.com/repos/beckervincent/curfew/releases/latest', False);
    Http.SetRequestHeader('User-Agent', 'curfew-installer');
    Http.SetRequestHeader('Accept', 'application/vnd.github+json');
    Http.Send('');
    if Http.Status <> 200 then Exit;
    Body := Http.ResponseText;

    { crude JSON scan: value after "tag_name" is the tag }
    P := Pos('"tag_name"', Body);
    if P = 0 then Exit;
    Body := Copy(Body, P + Length('"tag_name"'), Length(Body));
    P := Pos('"', Body);
    if P = 0 then Exit;
    Body := Copy(Body, P + 1, Length(Body));
    P := Pos('"', Body);
    if P = 0 then Exit;
    Result := Copy(Body, 1, P - 1);
  except
    Result := '';
  end;
end;

{ check if installer still newest stable; if outdated, offer download page instead of installing. False aborts setup. fail-open: unreachable/unparsable API never blocks install }
function ConfirmWhenOutdated(): Boolean;
var
  Latest: String;
  ResultCode: Integer;
begin
  Result := True;
  Latest := GetLatestReleaseTag();
  if (Latest = '') or not IsNewerVersion(Latest, '{#MyAppVersion}') then Exit;

  if MsgBox('A newer version of Curfew (' + Latest + ') is available.' + #13#10 +
            'This installer contains version {#MyAppVersion}.' + #13#10#13#10 +
            'Open the download page for the newest version and cancel this install?',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExecAsOriginalUser('open',
      'https://github.com/beckervincent/curfew/releases/latest', '', '',
      SW_SHOWNORMAL, ewNoWait, ResultCode);
    Result := False;
  end;
end;

{ verify installer current (interactive only; service silent auto-update always feeds newest + must never block), then stop prior install service+processes before file work so binaries not locked during copy. runs first, before wizard }
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not WizardSilent then
    Result := ConfirmWhenOutdated();
  if not Result then Exit;

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
  AppDir, ServiceExe: String;
  Script: String;
  ResultCode: Integer;
begin
  AppDir     := ExpandConstant('{app}');
  ServiceExe := AppDir + '\service\{#ServiceExeName}';
  ScriptPath := ExpandConstant('{tmp}\curfew_install_svc.ps1');

  Script :=
    '$svc   = "{#ServiceName}"' + #13#10 +
    '$exe   = "' + ServiceExe + '"' + #13#10 +
    '$dir   = "' + AppDir     + '"' + #13#10 +
    '' + #13#10 +
    '# remove prior registration, then create .NET app as native' + #13#10 +
    '# LocalSystem auto-start Windows service (uses AddWindowsService())' + #13#10 +
    'sc.exe stop   $svc 2>$null | Out-Null' + #13#10 +
    'Start-Sleep -Seconds 1' + #13#10 +
    'sc.exe delete $svc 2>$null | Out-Null' + #13#10 +
    'Start-Sleep -Seconds 1' + #13#10 +
    '' + #13#10 +
    'New-Service -Name $svc -BinaryPathName ("`"" + $exe + "`"") -DisplayName "Curfew" -Description "Curfew - Manages daily computer time limits" -StartupType Automatic | Out-Null' + #13#10 +
    '' + #13#10 +
    '# auto-restart on crash (5s, 5s, then every 60s); reset count daily' + #13#10 +
    'sc.exe failure $svc reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null' + #13#10 +
    '' + #13#10 +
    '# lock down install dir (read-only for users); DB dir writable for app' + #13#10 +
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
    'Start-Service $svc' + #13#10 +
    '' + #13#10 +
    '# overlay launches via logon scheduled task: .NET app fails to start' + #13#10 +
    '# under service CreateProcessAsUser, but starts cleanly from Task' + #13#10 +
    '# Scheduler. at-logon trigger, interactive Users principal, auto-restart' + #13#10 +
    '$overlay = Join-Path $dir "overlay\Curfew.Overlay.exe"' + #13#10 +
    '$act = New-ScheduledTaskAction -Execute $overlay' + #13#10 +
    '# triggers: at-logon covers each user''s logon (incl. fast-user-switch sign-in).' + #13#10 +
    '# console/remote CONNECT also relaunch on switching back to / reconnecting an' + #13#10 +
    '# already-logged-on session, which fires no logon event. empty UserId = any user;' + #13#10 +
    '# the overlay''s per-session mutex blocks duplicates when one is already running' + #13#10 +
    '$logon = New-ScheduledTaskTrigger -AtLogOn' + #13#10 +
    '$cls = Get-CimClass -ClassName MSFT_TaskSessionStateChangeTrigger -Namespace Root/Microsoft/Windows/TaskScheduler' + #13#10 +
    '$conn = New-CimInstance -CimClass $cls -ClientOnly; $conn.Enabled = $true; $conn.StateChange = 1' + #13#10 +
    '$rconn = New-CimInstance -CimClass $cls -ClientOnly; $rconn.Enabled = $true; $rconn.StateChange = 3' + #13#10 +
    '$trg = @($logon, $conn, $rconn)' + #13#10 +
    '$prn = New-ScheduledTaskPrincipal -GroupId "S-1-5-32-545" -RunLevel Limited' + #13#10 +
    '# MultipleInstances Parallel: overlay runs one instance per interactive' + #13#10 +
    '# session, each never exits its message loop. IgnoreNew would let' + #13#10 +
    '# first session''s instance suppress every later logon trigger, leaving a' + #13#10 +
    '# second concurrent user (fast-user-switch / lingering disconnected session)' + #13#10 +
    '# with no overlay. overlay''s per-session mutex still blocks duplicates' + #13#10 +
    '$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances Parallel -RestartCount 99 -RestartInterval (New-TimeSpan -Minutes 1)' + #13#10 +
    '$set.ExecutionTimeLimit = "PT0S"' + #13#10 +
    'Register-ScheduledTask -TaskName "CurfewOverlay" -Action $act -Trigger $trg -Principal $prn -Settings $set -Force | Out-Null' + #13#10 +
    'Start-ScheduledTask -TaskName "CurfewOverlay"' + #13#10 +
    '' + #13#10 +
    'schtasks /delete /tn "CurfewAutoUpdate" /f 2>$null | Out-Null' + #13#10 +
    'Remove-Item -Recurse -Force (Join-Path $dbDir "update") -EA SilentlyContinue' + #13#10 +
    '' + #13#10 +
    '# stage auto-updater''s download dir with protected ACL: data dir' + #13#10 +
    '# grants Users=Modify (so app can write state.db), and that ACE inherits' + #13#10 +
    '# down. left inherited, child could overwrite staged, signature-checked' + #13#10 +
    '# curfew-update.exe in TOCTOU gap before SYSTEM install task fires.' + #13#10 +
    '# break inheritance here so only SYSTEM+Admins can write update folder' + #13#10 +
    '$up = Join-Path $dbDir "update"' + #13#10 +
    'New-Item -ItemType Directory -Path $up -Force | Out-Null' + #13#10 +
    '$upAcl = Get-Acl $up' + #13#10 +
    '$upAcl.SetAccessRuleProtection($true, $false)' + #13#10 +
    '$upAcl.AddAccessRule((AclRule "S-1-5-32-544" "FullControl" "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    '$upAcl.AddAccessRule((AclRule "S-1-5-18"     "FullControl" "ContainerInherit,ObjectInherit" "None" "Allow"))' + #13#10 +
    'Set-Acl $up $upAcl' + #13#10;

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

{ True if named switch passed on installer command line }
function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

{ launch first-run wizard in interactive user session via one-shot scheduled task. works even for silent install over SSH/SYSTEM (session 0), where ShellExec would open GUI on invisible desktop }
procedure LaunchSetupInUserSession(const AppDir: String);
var
  ScriptPath, Script: String;
begin
  ScriptPath := ExpandConstant('{tmp}\curfew_setup_launch.ps1');
  Script :=
    '$exe = "' + AppDir + '\app\{#AppExeName}"' + #13#10 +
    '$act = New-ScheduledTaskAction -Execute $exe -Argument "--setup"' + #13#10 +
    '$trg = New-ScheduledTaskTrigger -AtLogOn' + #13#10 +
    '$prn = New-ScheduledTaskPrincipal -GroupId "S-1-5-32-545" -RunLevel Limited' + #13#10 +
    '$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries' + #13#10 +
    'Register-ScheduledTask -TaskName "CurfewSetup" -Action $act -Trigger $trg -Principal $prn -Settings $set -Force | Out-Null' + #13#10 +
    'Start-ScheduledTask -TaskName "CurfewSetup"' + #13#10 +
    'Start-Sleep -Seconds 3' + #13#10 +
    'Unregister-ScheduledTask -TaskName "CurfewSetup" -Confirm:$false 2>$null | Out-Null' + #13#10;

  if WriteScript(ScriptPath, Script) then
  begin
    RunPS(ScriptPath);
    DeleteFile(ScriptPath);
  end;
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
      begin
        { /RUNSETUP forces wizard even for silent/remote install; otherwise only show for interactive install }
        if HasParam('/RUNSETUP') then
          LaunchSetupInUserSession(ExpandConstant('{app}'))
        else if not WizardSilent then
          ShellExec('runas', ExpandConstant('{app}\app\{#AppExeName}'), '--setup', '',
                    SW_SHOWNORMAL, ewNoWait, ResultCode);
      end;
  end;
end;

{ run shipped uninstall guard: reads stored parent passcode from config.db; when set, requires it entered before uninstall proceeds. True allows, False blocks. fails closed (blocks) if PowerShell cant launch since passcode may be set; allows only when guard genuinely absent (older install without it) }
function VerifyUninstallPasscode(): Boolean;
var
  ScriptPath: String;
  Interactive, ResultCode: Integer;
begin
  ScriptPath := ExpandConstant('{app}\verify-uninstall.ps1');
  if not FileExists(ScriptPath) then
  begin
    Result := True;
    Exit;
  end;

  if UninstallSilent then Interactive := 0 else Interactive := 1;

  if not Exec('powershell.exe',
       '-NonInteractive -NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"' +
       ' -AppDir "' + ExpandConstant('{app}') + '" -Interactive ' + IntToStr(Interactive),
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := False; { couldnt launch check -> fail closed }
    Exit;
  end;

  Result := (ResultCode = 0);
end;

function InitializeUninstall(): Boolean;
var
  Answer: Integer;
begin
  Result := VerifyUninstallPasscode();
  if not Result then
  begin
    if not UninstallSilent then
      MsgBox('The parent passcode is required to uninstall Curfew.' + #13#10 +
             'Uninstall cancelled.', mbError, MB_OK);
    Exit;
  end;

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
