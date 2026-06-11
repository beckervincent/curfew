using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Curfew.Service;

/// <summary>
/// Toggles the per-user <c>DisableTaskMgr</c> policy so a child cannot open Task
/// Manager to kill the lock while a session is locked. Applied by the SYSTEM
/// service (which can write any loaded user hive) and removed on unlock.
/// </summary>
/// <remarks>
/// The SID is read from the settings DB, which is currently child-writable, so it
/// is treated as untrusted: it is matched against a strict SID pattern before use,
/// and passed to <c>reg.exe</c> via <see cref="ProcessStartInfo.ArgumentList"/>
/// (never a shell string), so a crafted value can neither inject a command nor
/// redirect the write to another key.
/// </remarks>
internal static partial class TaskManagerPolicy
{
    private const string PolicyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableTaskMgr";

    [GeneratedRegex(@"^S-1-\d+(-\d+)+$")]
    private static partial Regex SidPattern();

    /// <summary>Disables Task Manager for the given user SID.</summary>
    public static void Apply(string? sid) => Run(sid, set: true);

    /// <summary>Restores Task Manager for the given user SID.</summary>
    public static void Clear(string? sid) => Run(sid, set: false);

    private static void Run(string? sid, bool set)
    {
        if (sid is null || !SidPattern().IsMatch(sid)) return;

        var keyPath = $@"HKU\{sid}\{PolicyPath}";
        var psi = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (set)
        {
            foreach (var arg in new[] { "add", keyPath, "/v", ValueName, "/t", "REG_DWORD", "/d", "1", "/f" })
                psi.ArgumentList.Add(arg);
        }
        else
        {
            foreach (var arg in new[] { "delete", keyPath, "/v", ValueName, "/f" })
                psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"taskmgr policy {(set ? "apply" : "clear")} failed: {ex.Message}");
        }
    }
}
