using System.Text.Json;

namespace Curfew.Core;

/// <summary>A GitHub release tag plus the matching installer asset URL.</summary>
public readonly record struct ReleaseInfo(string Tag, string InstallerUrl)
{
    /// <summary>
    /// Parses the GitHub "latest release" JSON, returning the tag and the
    /// first asset whose name looks like a Curfew installer.
    /// </summary>
    public static ReleaseInfo? FromGitHubJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagProp)) return null;
        var tag = tagProp.GetString();
        if (string.IsNullOrEmpty(tag)) return null;

        if (!root.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("browser_download_url", out var urlProp)
                && urlProp.GetString() is { } url
                && url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && url.Contains("curfew-setup", StringComparison.OrdinalIgnoreCase))
            {
                return new ReleaseInfo(tag, url);
            }
        }

        return null;
    }
}
