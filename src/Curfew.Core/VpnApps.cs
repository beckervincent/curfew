using System.Collections.Immutable;

namespace Curfew.Core;

/// <summary>
/// Curated set of well-known VPN / Tor / circumvention client process names. When the
/// parent enables "block VPN apps", the overlay merges these into the enforced app
/// blocklist so an already-installed tunnel can't be used to bypass the content filter
/// (the proxy/VPN <see cref="BlockCategories"/> entry only blocks the download sites).
/// </summary>
/// <remarks>
/// Bare image names (no path, no <c>.exe</c>), normalized via <see cref="AppAllowlist.Normalize"/>
/// so they match the overlay's foreground-process check exactly. Conservative list of
/// flagship clients — not exhaustive; the parent can still add more by name.
/// </remarks>
public static class VpnApps
{
    /// <summary>Normalized VPN/Tor/proxy client image names to terminate when the toggle is on.</summary>
    public static readonly IReadOnlySet<string> Names = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "nordvpn", "expressvpn", "protonvpn", "surfshark", "openvpn", "openvpn-gui",
        "wireguard", "tunnelbear", "windscribe", "hotspotshield", "cyberghost",
        "psiphon", "psiphon3", "ultrasurf", "hola", "torbrowser", "tor", "mullvad");
}
