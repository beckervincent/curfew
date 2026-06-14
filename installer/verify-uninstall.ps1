# Curfew uninstall guard. Verifies the parent passcode before an uninstall is
# allowed to proceed. Invoked by the installer's InitializeUninstall (Inno Setup).
#
# Exit codes:
#   0  allow uninstall  (no passcode configured, or the entered passcode verified)
#   1  block uninstall  (wrong passcode, cancelled, or a silent uninstall while a
#                        passcode is set and cannot be prompted for)
#
# This script lives in the install dir, which is ACL'd ReadAndExecute for Users,
# so a standard (child) account cannot modify it to weaken the check. It reads the
# stored passcode hash from the write-protected config.db (read-only open, so it
# never contends with the running service) and verifies it the same way the app
# does: PBKDF2-SHA256, or a legacy plaintext value for older installs.

param(
    [Parameter(Mandatory = $true)] [string] $AppDir,
    [int] $Interactive = 1
)

$ErrorActionPreference = 'Stop'

function Fail-Closed {
    # Any unexpected error while a passcode might be set must NOT open the gate.
    param([string] $Message)
    exit 1
}

trap { Fail-Closed $_.Exception.Message }

$configDb = Join-Path $env:ProgramData 'Curfew\config.db'
$appBin = Join-Path $AppDir 'app'
$sqliteDll = Join-Path $appBin 'e_sqlite3.dll'

# Nothing installed/configured to protect: allow. A child cannot reach this state
# by deleting config.db — it is deny-ACL'd against Users for delete/write.
if (-not (Test-Path -LiteralPath $configDb)) { exit 0 }
if (-not (Test-Path -LiteralPath $sqliteDll)) { Fail-Closed 'sqlite missing' }

# Resolve e_sqlite3 (and its dependencies) by name from the install's app dir,
# which is ACL'd ReadAndExecute for Users — a child cannot plant a DLL there.
# Pointing the process current directory at that trusted dir lets the loader find
# it without copying to (and loading from) a user-writable location.
[Environment]::CurrentDirectory = $appBin

$interop = @"
using System;
using System.Runtime.InteropServices;
public static class CurfewUninstallSqlite {
    [DllImport("e_sqlite3", CharSet = CharSet.Ansi)]
    public static extern int sqlite3_open_v2(string filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("e_sqlite3")]
    public static extern int sqlite3_close(IntPtr db);
    [DllImport("e_sqlite3", CharSet = CharSet.Ansi)]
    public static extern int sqlite3_prepare_v2(IntPtr db, string sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport("e_sqlite3")]
    public static extern int sqlite3_step(IntPtr stmt);
    [DllImport("e_sqlite3")]
    public static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
    [DllImport("e_sqlite3")]
    public static extern int sqlite3_finalize(IntPtr stmt);
}
"@
Add-Type -TypeDefinition $interop

# Read the stored passcode with a READONLY open (flags = SQLITE_OPEN_READONLY = 1)
# so the running service's open handle never causes a write-lock conflict.
$stored = $null
$db = [IntPtr]::Zero
if ([CurfewUninstallSqlite]::sqlite3_open_v2($configDb, [ref] $db, 1, [IntPtr]::Zero) -ne 0) {
    Fail-Closed 'could not open config.db'
}
try {
    $stmt = [IntPtr]::Zero
    if ([CurfewUninstallSqlite]::sqlite3_prepare_v2($db, "SELECT value FROM settings WHERE key = 'passcode'", -1, [ref] $stmt, [IntPtr]::Zero) -eq 0) {
        try {
            if ([CurfewUninstallSqlite]::sqlite3_step($stmt) -eq 100) {
                $stored = [Runtime.InteropServices.Marshal]::PtrToStringAnsi([CurfewUninstallSqlite]::sqlite3_column_text($stmt, 0))
            }
        } finally {
            [void][CurfewUninstallSqlite]::sqlite3_finalize($stmt)
        }
    }
} finally {
    [void][CurfewUninstallSqlite]::sqlite3_close($db)
}

# No passcode set -> nothing to protect, allow uninstall.
if ([string]::IsNullOrEmpty($stored)) { exit 0 }

# A passcode IS set but this is a silent uninstall: there is no way to prompt, and
# allowing it would let `unins000.exe /SILENT` bypass the guard. Block it.
if ($Interactive -ne 1) { exit 1 }

function Test-Passcode {
    param([string] $Entered, [string] $Stored)

    # Legacy plaintext (anything without the pbkdf2$ prefix), compared case-sensitively.
    if (-not $Stored.StartsWith('pbkdf2$')) {
        return [string]::Equals($Entered, $Stored, [System.StringComparison]::Ordinal)
    }

    $parts = $Stored.Substring('pbkdf2$'.Length).Split('$')
    if ($parts.Length -ne 3) { return $false }

    $iterations = 0
    if (-not [int]::TryParse($parts[0], [ref] $iterations) -or $iterations -lt 1) { return $false }

    try {
        $salt = [Convert]::FromBase64String($parts[1])
        $expected = [Convert]::FromBase64String($parts[2])
    } catch { return $false }
    if ($expected.Length -eq 0) { return $false }

    # Rfc2898DeriveBytes with HashAlgorithmName.SHA256 is PBKDF2-HMAC-SHA256 — the
    # same derivation the app's PasscodeHash uses.
    $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        [System.Text.Encoding]::UTF8.GetBytes($Entered), $salt, $iterations,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $actual = $kdf.GetBytes($expected.Length)
    } finally {
        $kdf.Dispose()
    }

    # Constant-time comparison.
    $diff = 0
    for ($i = 0; $i -lt $expected.Length; $i++) { $diff = $diff -bor ($actual[$i] -bxor $expected[$i]) }
    return ($diff -eq 0)
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

for ($attempt = 1; $attempt -le 3; $attempt++) {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Curfew'
    $form.ClientSize = New-Object System.Drawing.Size(380, 150)
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.TopMost = $true

    $label = New-Object System.Windows.Forms.Label
    $label.SetBounds(15, 15, 350, 40)
    if ($attempt -eq 1) {
        $label.Text = 'Enter the parent passcode to uninstall Curfew:'
    } else {
        $label.Text = 'Incorrect passcode. Enter the parent passcode to uninstall Curfew:'
        $label.ForeColor = [System.Drawing.Color]::Firebrick
    }

    $textBox = New-Object System.Windows.Forms.TextBox
    $textBox.SetBounds(15, 60, 350, 25)
    $textBox.UseSystemPasswordChar = $true

    $okButton = New-Object System.Windows.Forms.Button
    $okButton.Text = 'OK'
    $okButton.SetBounds(195, 105, 80, 30)
    $okButton.DialogResult = [System.Windows.Forms.DialogResult]::OK

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = 'Cancel'
    $cancelButton.SetBounds(285, 105, 80, 30)
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

    $form.Controls.AddRange(@($label, $textBox, $okButton, $cancelButton))
    $form.AcceptButton = $okButton
    $form.CancelButton = $cancelButton
    $form.Add_Shown({ $form.Activate(); $textBox.Focus() })

    $result = $form.ShowDialog()
    $entered = $textBox.Text
    $form.Dispose()

    if ($result -ne [System.Windows.Forms.DialogResult]::OK) { exit 1 }
    if (Test-Passcode -Entered $entered -Stored $stored) { exit 0 }
}

exit 1
