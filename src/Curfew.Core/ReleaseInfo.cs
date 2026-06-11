using System.Text.Json;

namespace Curfew.Core;

/// <summary>A GitHub release tag plus the matching installer asset URL.</summary>
public readonly record struct ReleaseInfo(string Tag, string InstallerUrl)
{
    /// <summary>
    /// Substring an asset's download URL must contain (case-insensitively) to be
    /// recognised as the Curfew installer, distinguishing it from other release
    /// assets such as source archives or checksum files.
    /// </summary>
    private const string InstallerUrlMarker = "curfew-setup";

    /// <summary>File extension of the Windows installer asset (case-insensitive).</summary>
    private const string InstallerExtension = ".exe";

    /// <summary>
    /// Required prefix of a trusted installer download URL: HTTPS, the canonical
    /// GitHub releases host, and this repository's release-asset path. Pinning the
    /// full prefix (not just a "curfew-setup" substring) stops an attacker-hosted
    /// <c>http://evil/curfew-setup.exe</c> — or any other GitHub account's release
    /// asset of the same name — from being accepted as an update by the SYSTEM
    /// service. Kept in sync with the App-side check in SettingsWindow.
    /// </summary>
    public const string TrustedInstallerUrlPrefix =
        "https://github.com/beckervincent/curfew/releases/download/";

    /// <summary>
    /// Parses the GitHub "latest release" JSON, returning the tag and the first
    /// asset whose download URL looks like a Curfew installer.
    /// </summary>
    /// <param name="json">
    /// The raw JSON body returned by the GitHub releases API. May be null,
    /// empty, or malformed; such inputs simply yield <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A populated <see cref="ReleaseInfo"/> when a non-empty tag and a matching
    /// installer asset are both present; otherwise <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// This method never throws for malformed or unexpected input: it is the
    /// untrusted boundary between a remote HTTP response and the update logic, so
    /// any parsing failure is treated the same as "no usable release".
    /// </remarks>
    public static ReleaseInfo? FromGitHubJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("tag_name", out var tagProp)
                || tagProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var tag = tagProp.GetString();
            if (string.IsNullOrEmpty(tag)) return null;

            if (!root.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && asset.TryGetProperty("browser_download_url", out var urlProp)
                    && urlProp.ValueKind == JsonValueKind.String
                    && urlProp.GetString() is { } url
                    && IsInstallerUrl(url))
                {
                    return new ReleaseInfo(tag, url);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="url"/> is a trusted Curfew installer URL:
    /// it must sit under <see cref="TrustedInstallerUrlPrefix"/> (HTTPS + this
    /// repo's GitHub release path), carry the installer extension, and contain the
    /// installer marker. The host/path prefix is matched case-sensitively (a
    /// genuine GitHub URL is already lower-case); the name parts case-insensitively.
    /// </summary>
    public static bool IsInstallerUrl(string url) =>
        url.StartsWith(TrustedInstallerUrlPrefix, StringComparison.Ordinal)
        && url.EndsWith(InstallerExtension, StringComparison.OrdinalIgnoreCase)
        && url.Contains(InstallerUrlMarker, StringComparison.OrdinalIgnoreCase);
}
