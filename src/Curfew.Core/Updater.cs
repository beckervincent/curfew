using System.Net.Http.Headers;

namespace Curfew.Core;

/// <summary>Check GitHub for a newer Curfew release, build the install script.</summary>
/// <remarks>
/// HTTP fetch injected into <see cref="CheckForUpdateAsync"/> so decision logic is unit-testable without network. Production callers pass <see cref="HttpFetchAsync"/>.
/// </remarks>
public static class Updater
{
    /// <summary>GitHub REST endpoint for most recent published Curfew release (excludes pre-releases).</summary>
    public const string LatestReleaseUrl =
        "https://api.github.com/repos/beckervincent/curfew/releases/latest";

    /// <summary>GitHub REST endpoint for all releases (newest first, includes pre-releases).</summary>
    public const string ReleasesUrl =
        "https://api.github.com/repos/beckervincent/curfew/releases?per_page=30";

    /// <summary>User-Agent for update requests; GitHub rejects requests without one.</summary>
    private const string UserAgent = "curfew-updater";

    /// <summary>How long an update fetch runs before abandoned.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Shared client for <see cref="HttpFetchAsync"/>. One long-lived instance avoids the socket-exhaustion of one client per call.</summary>
    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = FetchTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>Release to install when strictly newer than <paramref name="currentVersion"/>, else <see langword="null"/>.</summary>
    /// <param name="currentVersion">Currently installed version, e.g. "1.2.3" or "v1.2.3". Unparseable returns <see langword="null"/> instead of assuming an update, so a malformed local version never triggers an unwanted reinstall.</param>
    /// <param name="fetchJson">Gets GitHub "latest release" JSON for a URL. Network/HTTP failures = "no update available"; only cancellation propagates.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>Newer <see cref="ReleaseInfo"/>, or <see langword="null"/> when no newer release, response unusable, or fetch fails.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fetchJson"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync(
        string currentVersion,
        Func<string, CancellationToken, Task<string>> fetchJson,
        CancellationToken cancellationToken = default) =>
        await CheckForUpdateAsync(currentVersion, fetchJson, includePrereleases: false, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Like <see cref="CheckForUpdateAsync(string, Func{string, CancellationToken, Task{string}}, CancellationToken)"/>, but <paramref name="includePrereleases"/> true scans the whole release list (pre-releases included), returns newest strictly above current. Else uses the stable "latest" endpoint, already filtered to non-pre-release builds.</summary>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync(
        string currentVersion,
        Func<string, CancellationToken, Task<string>> fetchJson,
        bool includePrereleases,
        CancellationToken cancellationToken = default,
        Action<string>? onCheckFailure = null)
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
            // cancellation = caller decision, not a failed update check: re-throw
            throw;
        }
        catch (Exception ex)
        {
            // offline, DNS failure, HTTP error, rate limit, etc. — no update this
            // pass, but tell caller why: a permanently broken check (expired proxy,
            // TLS misconfig) is otherwise indistinguishable from "already up to date"
            // and would never surface in any log.
            onCheckFailure?.Invoke(ex.Message);
            return null;
        }

        return includePrereleases ? NewestOf(json, current.Value) : NewerLatest(json, current.Value);
    }

    /// <summary>Highest-version release (pre-releases included) strictly above current.</summary>
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

    /// <summary>Single "latest" release when newer than current.</summary>
    private static ReleaseInfo? NewerLatest(string json, SemVer current)
    {
        var release = ReleaseInfo.FromGitHubJson(json);
        if (release is null) return null;

        var latest = SemVer.Parse(release.Value.Tag);
        if (latest is null) return null;

        return latest.Value > current ? release : null;
    }

    /// <summary>Default fetcher: GET <paramref name="url"/> with the User-Agent + Accept headers GitHub expects, returns response body.</summary>
    /// <param name="url">URL to fetch.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Response body as a string.</returns>
    /// <exception cref="HttpRequestException">Request failed or returned a non-success status.</exception>
    /// <exception cref="OperationCanceledException">Request cancelled or timed out.</exception>
    public static Task<string> HttpFetchAsync(string url, CancellationToken cancellationToken) =>
        SharedClient.GetStringAsync(url, cancellationToken);

    /// <summary>Build PowerShell script running the installer through a detached, one-shot SYSTEM scheduled task.</summary>
    /// <param name="installerPath">Full path to the downloaded installer executable.</param>
    /// <returns>PowerShell script that creates, runs, self-deletes the task.</returns>
    /// <exception cref="ArgumentException"><paramref name="installerPath"/> is null, blank, or contains a quote.</exception>
    /// <remarks>
    /// Detached task lets the install survive the calling service stopping mid-update (installer usually stops + restarts that service). Task action also deletes the task afterwards so it doesn't linger and re-fire. Registered via ScheduledTasks cmdlets not schtasks.exe: PowerShell rewrites embedded <c>\"</c> escapes when spawning native executables, silently corrupting the schtasks <c>/tr</c> argument, whereas a cmdlet receives the action string verbatim.
    /// </remarks>
    public static string BuildScheduledInstallScript(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            throw new ArgumentException("Installer path must be provided.", nameof(installerPath));
        }

        // path embedded inside a single-quoted PowerShell string + a cmd.exe command
        // line; a quote of either kind breaks the embedding and can't be escaped
        // safely here.
        if (installerPath.Contains('"') || installerPath.Contains('\''))
        {
            throw new ArgumentException("Installer path must not contain a quote.", nameof(installerPath));
        }

        const string taskName = "CurfewAutoUpdate";

        // task action: run installer silently, record exit code beside it (detached
        // one-shot, so this marker is the only trace a failed silent install leaves —
        // service logs it next pass), then delete the task so it doesn't persist +
        // re-run. cmd /c strips outermost quote pair, so inner quotes survive for
        // paths with spaces. /v:on enables delayed expansion; exit code read with
        // !ERRORLEVEL! not %ERRORLEVEL%: cmd expands percent-vars once when it first
        // parses the line, BEFORE the installer runs — so %ERRORLEVEL% records the
        // value this fresh cmd inherited (always 0), never the installer's result.
        // !ERRORLEVEL! evaluated when the echo runs, after installer exits, so it
        // captures the real exit code.
        var resultPath = Path.Combine(Path.GetDirectoryName(installerPath) ?? string.Empty, InstallResultFileName);
        var cmdArgument =
            $"/v:on /c \"\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART " +
            $"& echo !ERRORLEVEL! > \"{resultPath}\" " +
            $"& schtasks /delete /tn {taskName} /f\"";

        return string.Join('\n', new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '{cmdArgument}'",
            "$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest",
            // default task settings refuse to start (+ kill) tasks on battery —
            // many target devices are laptops, so be explicit about both.
            "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 1)",
            $"Register-ScheduledTask -TaskName '{taskName}' -Action $action -Principal $principal -Settings $settings -Force | Out-Null",
            $"Start-ScheduledTask -TaskName '{taskName}'",
        });
    }

    /// <summary>File the scheduled install writes its exit code to, beside the staged installer. Read + cleared by service next update pass so failed silent installs show in the service log.</summary>
    public const string InstallResultFileName = "install-result.txt";
}
