using System.Net.Http.Headers;

namespace Curfew.Core;

/// <summary>
/// Checks GitHub for a newer Curfew release and builds the script that installs it.
/// </summary>
/// <remarks>
/// The HTTP fetch is injected into <see cref="CheckForUpdateAsync"/> so the decision
/// logic can be unit-tested without network access. Production callers pass
/// <see cref="HttpFetchAsync"/>.
/// </remarks>
public static class Updater
{
    /// <summary>GitHub REST endpoint for the most recent published Curfew release (excludes pre-releases).</summary>
    public const string LatestReleaseUrl =
        "https://api.github.com/repos/beckervincent/curfew/releases/latest";

    /// <summary>GitHub REST endpoint for all releases (newest first, includes pre-releases).</summary>
    public const string ReleasesUrl =
        "https://api.github.com/repos/beckervincent/curfew/releases?per_page=30";

    /// <summary>User-Agent sent with update requests; GitHub rejects requests without one.</summary>
    private const string UserAgent = "curfew-updater";

    /// <summary>How long an update fetch may run before it is abandoned.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Shared client for <see cref="HttpFetchAsync"/>. A single long-lived instance
    /// avoids the socket-exhaustion that results from creating one client per call.
    /// </summary>
    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = FetchTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Returns the release to install when it is strictly newer than
    /// <paramref name="currentVersion"/>, otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="currentVersion">
    /// The version currently installed, e.g. "1.2.3" or "v1.2.3". When this cannot be
    /// parsed as a version the method returns <see langword="null"/> rather than
    /// assuming an update is needed, so a malformed local version never triggers an
    /// unwanted reinstall.
    /// </param>
    /// <param name="fetchJson">
    /// Retrieves the GitHub "latest release" JSON for a given URL. Network or HTTP
    /// failures are expected and treated as "no update available"; only cancellation
    /// is allowed to propagate.
    /// </param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>
    /// The newer <see cref="ReleaseInfo"/>, or <see langword="null"/> when there is no
    /// newer release, the response is unusable, or the fetch fails.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="fetchJson"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync(
        string currentVersion,
        Func<string, CancellationToken, Task<string>> fetchJson,
        CancellationToken cancellationToken = default) =>
        await CheckForUpdateAsync(currentVersion, fetchJson, includePrereleases: false, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// As <see cref="CheckForUpdateAsync(string, Func{string, CancellationToken, Task{string}}, CancellationToken)"/>,
    /// but when <paramref name="includePrereleases"/> is true it considers the whole
    /// release list (pre-releases included) and returns the newest one strictly
    /// above the current version. Otherwise it uses the stable "latest" endpoint,
    /// which GitHub already filters to non-pre-release builds.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync(
        string currentVersion,
        Func<string, CancellationToken, Task<string>> fetchJson,
        bool includePrereleases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchJson);

        var current = SemVer.Parse(currentVersion);
        if (current is null) return null;

        var url = includePrereleases ? ReleasesUrl : LatestReleaseUrl;

        string json;
        try
        {
            json = await fetchJson(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is a caller decision, not a failed update check: re-throw it.
            throw;
        }
        catch
        {
            // Offline, DNS failure, HTTP error, rate limit, etc. — simply no update.
            return null;
        }

        return includePrereleases ? NewestOf(json, current.Value) : NewerLatest(json, current.Value);
    }

    /// <summary>Picks the highest-version release (pre-releases included) strictly above current.</summary>
    private static ReleaseInfo? NewestOf(string json, SemVer current)
    {
        ReleaseInfo? best = null;
        SemVer? bestVersion = null;

        foreach (var release in ReleaseInfo.ListFromGitHubJson(json))
        {
            var version = SemVer.Parse(release.Tag);
            if (version is null || !(version.Value > current)) continue;
            if (bestVersion is null || version.Value > bestVersion.Value)
            {
                best = release;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>Returns the single "latest" release when it is newer than current.</summary>
    private static ReleaseInfo? NewerLatest(string json, SemVer current)
    {
        var release = ReleaseInfo.FromGitHubJson(json);
        if (release is null) return null;

        var latest = SemVer.Parse(release.Value.Tag);
        if (latest is null) return null;

        return latest.Value > current ? release : null;
    }

    /// <summary>
    /// Default fetcher: issues a GET against <paramref name="url"/> with the
    /// User-Agent and Accept headers GitHub expects, and returns the response body.
    /// </summary>
    /// <param name="url">The URL to fetch.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response body as a string.</returns>
    /// <exception cref="HttpRequestException">The request failed or returned a non-success status.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled or timed out.</exception>
    public static Task<string> HttpFetchAsync(string url, CancellationToken cancellationToken) =>
        SharedClient.GetStringAsync(url, cancellationToken);

    /// <summary>
    /// Builds a PowerShell script that runs the installer through a detached, one-shot
    /// SYSTEM scheduled task.
    /// </summary>
    /// <param name="installerPath">Full path to the downloaded installer executable.</param>
    /// <returns>A PowerShell script that creates, runs, and self-deletes the task.</returns>
    /// <exception cref="ArgumentException"><paramref name="installerPath"/> is null, blank, or contains a quote.</exception>
    /// <remarks>
    /// Running the install from a detached task lets it survive the calling service
    /// stopping mid-update (the installer typically stops and restarts that service).
    /// The task action also deletes the task afterwards so it does not linger and
    /// re-fire at its scheduled start time. The task is registered through the
    /// ScheduledTasks cmdlets rather than schtasks.exe: PowerShell rewrites embedded
    /// <c>\"</c> escapes when spawning native executables, which silently corrupted
    /// the schtasks <c>/tr</c> argument, whereas a cmdlet receives the action string
    /// verbatim.
    /// </remarks>
    public static string BuildScheduledInstallScript(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            throw new ArgumentException("Installer path must be provided.", nameof(installerPath));
        }

        // The path is embedded inside a single-quoted PowerShell string and a
        // cmd.exe command line; a quote of either kind would break that embedding
        // and cannot be escaped safely here.
        if (installerPath.Contains('"') || installerPath.Contains('\''))
        {
            throw new ArgumentException("Installer path must not contain a quote.", nameof(installerPath));
        }

        const string taskName = "CurfewAutoUpdate";

        // The task action: run the installer silently, then delete the task so it
        // does not persist and re-run. cmd /c strips the outermost quote pair, so
        // the quotes around the installer path survive for paths with spaces.
        var cmdArgument =
            $"/c \"\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART " +
            $"& schtasks /delete /tn {taskName} /f\"";

        return string.Join('\n', new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '{cmdArgument}'",
            "$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest",
            $"Register-ScheduledTask -TaskName '{taskName}' -Action $action -Principal $principal -Force | Out-Null",
            $"Start-ScheduledTask -TaskName '{taskName}'",
        });
    }
}
