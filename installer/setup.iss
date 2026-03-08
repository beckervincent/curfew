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
CloseApplicationsFilter=screen-time-manager.exe
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "screen-time-manager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "nssm.exe";                DestDir: "{app}"; Flags: ignoreversion

[Run]
; Stop any existing service instance
Filename: "{app}\nssm.exe"; Parameters: "stop {#ServiceName}"; \
    Flags: runhidden waituntilterminated; StatusMsg: "Stopping existing service..."
; Remove existing service registration
Filename: "{app}\nssm.exe"; Parameters: "remove {#ServiceName} confirm"; \
    Flags: runhidden waituntilterminated; StatusMsg: "Removing existing service..."
; Kill any lingering processes
Filename: "taskkill.exe"; Parameters: "/f /im {#MyAppExeName}"; \
    Flags: runhidden waituntilterminated

; Install and configure the NSSM service
Filename: "{app}\nssm.exe"; \
    Parameters: "install {#ServiceName} ""{app}\{#MyAppExeName}"" --service"; \
    Flags: runhidden waituntilterminated; StatusMsg: "Installing service..."
Filename: "{app}\nssm.exe"; Parameters: "set {#ServiceName} AppDirectory ""{app}"""; \
    Flags: runhidden waituntilterminated
Filename: "{app}\nssm.exe"; Parameters: "set {#ServiceName} ObjectName LocalSystem"; \
    Flags: runhidden waituntilterminated
Filename: "{app}\nssm.exe"; Parameters: "set {#ServiceName} DisplayName ""Screen Time Manager"""; \
    Flags: runhidden waituntilterminated
Filename: "{app}\nssm.exe"; \
    Parameters: "set {#ServiceName} Description ""Screen Time Manager - Manages daily computer time limits"""; \
    Flags: runhidden waituntilterminated
Filename: "{app}\nssm.exe"; Parameters: "set {#ServiceName} Start SERVICE_AUTO_START"; \
    Flags: runhidden waituntilterminated

; Configure log rotation
Filename: "cmd.exe"; Parameters: "/c mkdir ""{app}\logs"" 2>nul"; \
    Flags: runhidden waituntilterminated
Filename: "{app}\nssm.exe"; Parameters: "set {#ServiceName} AppStdout ""{app}\logs\stdout.log"""; \
    Flags: runhidden waituntilterminated
Filename: "{app}\nssm.exe"; Parameters: "set {#ServiceName} AppStderr ""{app}\logs\stderr.log"""; \
    Flags: runhidden waituntilterminated
Filename: "{app}\nssm.exe"; Parameters: "set {#ServiceName} AppRotateFiles 1"; \
    Flags: runhidden waituntilterminated
Filename: "{app}\nssm.exe"; Parameters: "set {#ServiceName} AppRotateSeconds 86400"; \
    Flags: runhidden waituntilterminated

; Lock down install directory: Admins+SYSTEM full control, Users read-execute only
Filename: "powershell.exe"; \
    Parameters: "-NonInteractive -Command ""$acl = Get-Acl '{app}'; $acl.SetAccessRuleProtection($true, $false); $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Administrators','FullControl','ContainerInherit,ObjectInherit','None','Allow'))); $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM','FullControl','ContainerInherit,ObjectInherit','None','Allow'))); $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Users','ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))); Set-Acl '{app}' $acl"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "Applying security settings..."

; Create and lock down the ProgramData database directory: Admins+SYSTEM only
Filename: "powershell.exe"; \
    Parameters: "-NonInteractive -Command ""$d = Join-Path $env:ProgramData 'ScreenTimeManager'; New-Item -ItemType Directory -Path $d -Force | Out-Null; $acl = Get-Acl $d; $acl.SetAccessRuleProtection($true, $false); $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Administrators','FullControl','ContainerInherit,ObjectInherit','None','Allow'))); $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM','FullControl','ContainerInherit,ObjectInherit','None','Allow'))); Set-Acl $d $acl"""; \
    Flags: runhidden waituntilterminated

; Start the service
Filename: "{app}\nssm.exe"; Parameters: "start {#ServiceName}"; \
    Flags: runhidden waituntilterminated; StatusMsg: "Starting service..."

[Code]

var
  ShouldDeleteData: Boolean;

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
