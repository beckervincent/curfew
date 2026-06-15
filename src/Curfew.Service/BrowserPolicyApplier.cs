using System.Diagnostics;
using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Disables private / incognito browsing in the major browsers via their machine-wide
/// policy registry values, so a child can't use a private window to dodge history and
/// some content filtering. SYSTEM writes HKLM policies; removes them when the parent
/// turns the setting off. Applied alongside the DNS/hosts content filter.
/// </summary>
/// <remarks>
/// These are the browsers' documented enterprise policies:
/// Chrome <c>IncognitoModeAvailability=1</c>, Edge <c>InPrivateModeAvailability=1</c>,
/// Firefox <c>DisablePrivateBrowsing=1</c>. Browsers that aren't installed simply ignore
/// the key. Uses <c>reg.exe</c> via <see cref="ProcessStartInfo.ArgumentList"/> (no shell
/// string); paths are constants, never built from untrusted input.
/// </remarks>
internal static class BrowserPolicyApplier
{
    private static readonly (string Path, string Value)[] Policies =
    {
        (@"HKLM\SOFTWARE\Policies\Google\Chrome", "IncognitoModeAvailability"),
        (@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "InPrivateModeAvailability"),
        (@"HKLM\SOFTWARE\Policies\Mozilla\Firefox", "DisablePrivateBrowsing"),
        (@"HKLM\SOFTWARE\Policies\BraveSoftware\Brave", "IncognitoModeAvailability"),
        (@"HKLM\SOFTWARE\Policies\Chromium", "IncognitoModeAvailability"),
    };

    /// <summary>Apply (or clear) the private-browsing-disabled policies for every known browser.</summary>
    public static void Apply(SettingsStore settings)
    {
        var block = settings.GetBool("block_private_browsing", false);
        foreach (var (path, value) in Policies)
            Run(path, value, set: block);
    }

    private static void Run(string path, string value, bool set)
    {
        var psi = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (set)
            foreach (var arg in new[] { "add", path, "/v", value, "/t", "REG_DWORD", "/d", "1", "/f" })
                psi.ArgumentList.Add(arg);
        else
            foreach (var arg in new[] { "delete", path, "/v", value, "/f" })
                psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"browser policy {(set ? "apply" : "clear")} {value} failed: {ex.Message}");
        }
    }
}
