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
    /// Returns whether <paramref name="url"/> points at the Curfew Windows
    /// installer: it must carry the installer extension and contain the
    /// installer marker, both matched case-insensitively.
    /// </summary>
    private static bool IsInstallerUrl(string url) =>
        url.EndsWith(InstallerExtension, StringComparison.OrdinalIgnoreCase)
        && url.Contains(InstallerUrlMarker, StringComparison.OrdinalIgnoreCase);
}
