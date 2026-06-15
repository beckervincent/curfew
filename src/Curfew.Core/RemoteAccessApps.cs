using System.Collections.Immutable;

namespace Curfew.Core;

/// <summary>
/// Curated set of well-known remote-access / remote-control client process names. When
/// the parent enables "block remote-access apps", the overlay folds these into the
/// enforced app blocklist so a stranger or scammer can't talk the child into handing
/// over control of the PC (a common social-engineering vector). Same proven
/// terminate-foreground path as <see cref="VpnApps"/>.
/// </summary>
/// <remarks>
/// Bare image names (no path, no <c>.exe</c>), normalized via <see cref="AppAllowlist.Normalize"/>
/// so they match the overlay's foreground check exactly. Includes Windows Quick Assist
/// (<c>quickassist</c>) and Remote Assistance (<c>msra</c>), which scammers frequently use.
/// </remarks>
public static class RemoteAccessApps
{
    /// <summary>Normalized remote-access client image names to terminate when the toggle is on.</summary>
    public static readonly IReadOnlySet<string> Names = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "teamviewer", "anydesk", "rustdesk", "vncviewer", "tvnviewer", "tvnserver",
        "winvnc", "uvnc_service", "vncserver", "remotepc", "logmein", "gotomypc",
        "splashtop", "srserver", "ammyy", "supremo", "quickassist", "msra",
        "dwagent", "screenconnect", "connectwisecontrol", "ateraagent");
}
