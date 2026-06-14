using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Curfew.Service;

/// <summary>Toggle per-user <c>DisableTaskMgr</c> so locked child cannot open Task Manager to kill lock; SYSTEM applies, removes on unlock.</summary>
/// <remarks>
/// SID from child-writable settings DB = untrusted: strict SID pattern check, passed to <c>reg.exe</c> via <see cref="ProcessStartInfo.ArgumentList"/> (never shell string), so crafted value cannot inject command nor redirect write.
/// </remarks>
internal static partial class TaskManagerPolicy
{
    private const string PolicyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableTaskMgr";

    [GeneratedRegex(@"^S-1-\d+(-\d+)+$")]
    private static partial Regex SidPattern();

    /// <summary>Disable Task Manager for user SID.</summary>
    public static void Apply(string? sid) => Run(sid, set: true);

    /// <summary>Restore Task Manager for user SID.</summary>
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
