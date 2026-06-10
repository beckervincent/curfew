namespace Curfew.Core;

/// <summary>
/// Checks GitHub for a newer Curfew release. The HTTP fetch is injected so the
/// decision logic can be unit-tested without network access.
/// </summary>
public static class Updater
{
    public const string LatestReleaseUrl =
        "https://api.github.com/repos/beckervincent/curfew/releases/latest";

    /// <summary>
    /// Returns the release to install when it is newer than <paramref name="currentVersion"/>,
    /// otherwise null. <paramref name="fetchJson"/> retrieves the GitHub
    /// "latest release" JSON.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync(
        string currentVersion,
        Func<string, CancellationToken, Task<string>> fetchJson,
        CancellationToken cancellationToken = default)
    {
        var current = SemVer.Parse(currentVersion);
        if (current is null) return null;

        string json;
        try
        {
            json = await fetchJson(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        var release = ReleaseInfo.FromGitHubJson(json);
        if (release is null) return null;

        var latest = SemVer.Parse(release.Value.Tag);
        if (latest is null) return null;

        return latest > current ? release : null;
    }

    /// <summary>Default fetcher using HttpClient with the User-Agent GitHub requires.</summary>
    public static async Task<string> HttpFetchAsync(string url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("curfew-updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>PowerShell that schedules a silent install via a detached task,
    /// so the install survives the service stopping mid-update.</summary>
    public static string BuildScheduledInstallScript(string installerPath)
    {
        const string taskName = "CurfewAutoUpdate";
        var action = $"\\\"{installerPath}\\\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        return string.Join('\n', new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"schtasks /create /tn \"{taskName}\" /tr \"{action}\" /sc once /st 00:00 /ru SYSTEM /f",
            $"schtasks /run /tn \"{taskName}\"",
        });
    }
}
