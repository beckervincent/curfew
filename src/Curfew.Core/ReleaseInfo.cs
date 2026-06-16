using System.Text.Json;

namespace Curfew.Core;

/// <summary>GitHub release tag + matching installer asset URL</summary>
public readonly record struct ReleaseInfo(string Tag, string InstallerUrl)
{
    /// <summary>substring asset download URL must contain (case-insensitive) to count as Curfew installer, vs source archives or checksum files</summary>
    private const string InstallerUrlMarker = "curfew-setup";

    /// <summary>Windows installer asset extension (case-insensitive)</summary>
    private const string InstallerExtension = ".exe";

    /// <summary>required prefix of trusted installer URL: HTTPS, canonical GitHub releases host, this repo's release-asset path. pinning full prefix (not just "curfew-setup" substring) stops attacker-hosted <c>http://evil/curfew-setup.exe</c> or another account's same-named asset from being accepted by SYSTEM service. kept in sync with App-side check in SettingsWindow</summary>
    public const string TrustedInstallerUrlPrefix =
        "https://github.com/beckervincent/curfew/releases/download/";

    /// <summary>parse GitHub "latest release" JSON; tag + first asset whose URL looks like a Curfew installer</summary>
    /// <param name="json">raw JSON body from GitHub releases API. null/empty/malformed yields <see langword="null"/></param>
    /// <returns>populated <see cref="ReleaseInfo"/> when non-empty tag + matching installer asset both present; else <see langword="null"/></returns>
    /// <remarks>never throws on bad input: untrusted boundary between remote HTTP response and update logic, parse failure = "no usable release"</remarks>
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
            return root.ValueKind == JsonValueKind.Object ? ParseRelease(root) : null;
        }
    }

    /// <summary>parse GitHub "list releases" JSON (array, newest first, includes pre-releases unlike "latest") into every release with a trusted installer asset. caller picks newest by version; no ordering/pre-release filtering here</summary>
    /// <param name="json">raw JSON array body. null/empty/malformed yields empty list</param>
    public static IReadOnlyList<ReleaseInfo> ListFromGitHubJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ReleaseInfo>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<ReleaseInfo>();
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return Array.Empty<ReleaseInfo>();

            var results = new List<ReleaseInfo>();
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object && ParseRelease(element) is { } release)
                    results.Add(release);
            }
            return results;
        }
    }

    /// <summary>extract tag + first trusted installer asset from one release object</summary>
    private static ReleaseInfo? ParseRelease(JsonElement release)
    {
        if (!release.TryGetProperty("tag_name", out var tagProp)
            || tagProp.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var tag = tagProp.GetString();
        if (string.IsNullOrEmpty(tag)) return null;

        if (!release.TryGetProperty("assets", out var assets)
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

    /// <summary>is <paramref name="url"/> a trusted Curfew installer URL: under <see cref="TrustedInstallerUrlPrefix"/> (HTTPS + this repo's GitHub release path), installer extension, installer marker. host/path prefix matched case-sensitively (genuine GitHub URL already lower-case); name parts case-insensitively</summary>
    public static bool IsInstallerUrl(string url) =>
        url.StartsWith(TrustedInstallerUrlPrefix, StringComparison.Ordinal)
        && url.EndsWith(InstallerExtension, StringComparison.OrdinalIgnoreCase)
        && url.Contains(InstallerUrlMarker, StringComparison.OrdinalIgnoreCase)
        && NormalizesUnderPrefix(url);

    /// <summary>guard against path-traversal: the raw-string prefix check above accepts
    /// <c>.../releases/download/../../../attacker/...</c> (it still starts with the prefix), but
    /// <see cref="HttpClient"/> resolves <c>..</c> segments via <see cref="Uri"/> and would fetch from a
    /// different repo. Re-check the normalized absolute URL (which has collapsed any <c>..</c>) still sits
    /// under the pinned prefix, so a traversal that escapes the repo path is rejected.</summary>
    private static bool NormalizesUnderPrefix(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.AbsoluteUri.StartsWith(TrustedInstallerUrlPrefix, StringComparison.Ordinal);
}
