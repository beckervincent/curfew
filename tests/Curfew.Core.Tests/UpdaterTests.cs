using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Unit tests for <see cref="Updater"/>. The HTTP fetch is always injected, so these
/// tests exercise the decision logic and the generated install script without touching
/// the network or the file system.
/// </summary>
public class UpdaterTests
{
    /// <summary>
    /// A well-formed GitHub "latest release" payload advertising v1.5.0 with a single
    /// recognisable Curfew installer asset.
    /// </summary>
    private const string ReleaseJson = """
    {
      "tag_name": "v1.5.0",
      "assets": [
        { "name": "curfew-setup-v1.5.0.exe", "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.5.0.exe" }
      ]
    }
    """;

    private const string InstallerUrl = "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.5.0.exe";

    /// <summary>Builds a fetcher that always returns <paramref name="json"/>.</summary>
    private static Func<string, CancellationToken, Task<string>> Returns(string json) =>
        (_, _) => Task.FromResult(json);

    /// <summary>Builds a fetcher that always faults with <paramref name="error"/>.</summary>
    private static Func<string, CancellationToken, Task<string>> Throws(Exception error) =>
        (_, _) => Task.FromException<string>(error);

    [Fact]
    public async Task CheckForUpdate_returns_release_when_remote_is_newer()
    {
        var result = await Updater.CheckForUpdateAsync("1.0.0", Returns(ReleaseJson));

        Assert.NotNull(result);
        Assert.Equal("v1.5.0", result!.Value.Tag);
        Assert.Equal(InstallerUrl, result.Value.InstallerUrl);
    }

    [Theory]
    [InlineData("1.0.0")]   // older
    [InlineData("v1.0.0")]  // older, with the same leading-v convention the tags use
    [InlineData("1.4.9")]   // older, differing only in patch
    public async Task CheckForUpdate_returns_release_for_any_older_current_version(string current)
    {
        Assert.NotNull(await Updater.CheckForUpdateAsync(current, Returns(ReleaseJson)));
    }

    [Theory]
    [InlineData("1.5.0")]   // identical: an equal version is not an update
    [InlineData("v1.5.0")]  // identical, expressed with a leading v
    [InlineData("2.0.0")]   // strictly newer
    [InlineData("1.5.1")]   // newer by a single patch
    public async Task CheckForUpdate_returns_null_when_current_is_same_or_newer(string current)
    {
        Assert.Null(await Updater.CheckForUpdateAsync(current, Returns(ReleaseJson)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.2")]
    public async Task CheckForUpdate_returns_null_when_current_version_is_unparsable(string current)
    {
        // A version we cannot parse must not be treated as "older than the release";
        // doing so would let a corrupt local version trigger an unwanted reinstall.
        Assert.Null(await Updater.CheckForUpdateAsync(current, Returns(ReleaseJson)));
    }

    [Fact]
    public async Task CheckForUpdate_does_not_fetch_when_current_version_is_unparsable()
    {
        var fetched = false;
        var result = await Updater.CheckForUpdateAsync(
            "garbage",
            (_, _) => { fetched = true; return Task.FromResult(ReleaseJson); });

        Assert.Null(result);
        Assert.False(fetched, "the remote should not be queried when the local version is unusable");
    }

    [Fact]
    public async Task CheckForUpdate_requests_the_latest_release_url()
    {
        string? requestedUrl = null;
        await Updater.CheckForUpdateAsync(
            "1.0.0",
            (url, _) => { requestedUrl = url; return Task.FromResult(ReleaseJson); });

        Assert.Equal(Updater.LatestReleaseUrl, requestedUrl);
    }

    [Theory]
    [InlineData("")]                 // empty body
    [InlineData("   ")]              // whitespace-only body
    [InlineData("{ not json }")]     // malformed JSON
    [InlineData("[]")]               // valid JSON but not a release object
    [InlineData("""{ "assets": [] }""")] // missing tag_name
    public async Task CheckForUpdate_returns_null_when_response_is_unusable(string json)
    {
        Assert.Null(await Updater.CheckForUpdateAsync("1.0.0", Returns(json)));
    }

    [Fact]
    public async Task CheckForUpdate_returns_null_when_release_has_no_installer_asset()
    {
        const string noInstaller = """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/notes.txt" }
          ]
        }
        """;

        Assert.Null(await Updater.CheckForUpdateAsync("1.0.0", Returns(noInstaller)));
    }

    [Fact]
    public async Task CheckForUpdate_returns_null_when_release_tag_is_unparsable()
    {
        const string badTag = """
        {
          "tag_name": "nightly",
          "assets": [
            { "name": "curfew-setup.exe", "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup.exe" }
          ]
        }
        """;

        Assert.Null(await Updater.CheckForUpdateAsync("1.0.0", Returns(badTag)));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task CheckForUpdate_returns_null_when_fetch_throws(Type exceptionType)
    {
        var error = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Null(await Updater.CheckForUpdateAsync("1.0.0", Throws(error)));
    }

    [Fact]
    public async Task CheckForUpdate_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // When the caller cancels, that is a deliberate decision and must surface as a
        // cancellation rather than being swallowed into a silent "no update".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Updater.CheckForUpdateAsync(
                "1.0.0",
                (_, token) => Task.FromException<string>(new OperationCanceledException(token)),
                cts.Token));
    }

    [Fact]
    public async Task CheckForUpdate_swallows_cancellation_exception_when_not_requested()
    {
        // A fetcher that reports cancellation while no cancellation was actually
        // requested is an internal fault, not a caller decision: treat it as no update.
        var result = await Updater.CheckForUpdateAsync(
            "1.0.0",
            (_, _) => Task.FromException<string>(new OperationCanceledException()),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdate_throws_for_null_fetcher()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Updater.CheckForUpdateAsync("1.0.0", null!));
    }

    [Fact]
    public void BuildScheduledInstallScript_creates_runs_and_removes_a_silent_system_task()
    {
        const string installerPath = @"C:\ProgramData\Curfew\update\curfew-update.exe";

        var script = Updater.BuildScheduledInstallScript(installerPath);

        // Creates a one-shot task that runs as SYSTEM, then triggers it immediately.
        Assert.Contains("schtasks /create", script, StringComparison.Ordinal);
        Assert.Contains("/sc once", script, StringComparison.Ordinal);
        Assert.Contains("/ru SYSTEM", script, StringComparison.Ordinal);
        Assert.Contains("schtasks /run", script, StringComparison.Ordinal);

        // Runs the installer non-interactively so it can proceed unattended.
        Assert.Contains("/VERYSILENT", script, StringComparison.Ordinal);
        Assert.Contains("/SUPPRESSMSGBOXES", script, StringComparison.Ordinal);
        Assert.Contains("/NORESTART", script, StringComparison.Ordinal);

        // The task tears itself down so it cannot linger and re-fire at its start time.
        Assert.Contains("schtasks /delete", script, StringComparison.Ordinal);

        // Failures while scripting are ignored so a partial run never blocks the update.
        Assert.Contains("$ErrorActionPreference = 'SilentlyContinue'", script, StringComparison.Ordinal);

        // The installer the caller asked to run is embedded in the task action.
        Assert.Contains(installerPath, script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScheduledInstallScript_uses_a_single_consistent_task_name()
    {
        var script = Updater.BuildScheduledInstallScript(@"C:\Curfew\setup.exe");

        // The same task name must be used to create, run, and delete the task; if the
        // names diverged the cleanup would target a non-existent task and leave the real
        // one behind.
        var occurrences = CountOccurrences(script, "CurfewAutoUpdate");
        Assert.Equal(3, occurrences);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildScheduledInstallScript_rejects_blank_path(string? installerPath)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Updater.BuildScheduledInstallScript(installerPath!));
        Assert.Equal("installerPath", ex.ParamName);
    }

    [Fact]
    public void BuildScheduledInstallScript_rejects_path_containing_a_double_quote()
    {
        // A double quote would break the quoting of the schtasks /tr argument and cannot
        // be escaped safely, so it must be rejected rather than silently mis-built.
        var ex = Assert.Throws<ArgumentException>(() =>
            Updater.BuildScheduledInstallScript(@"C:\evil"" \payload.exe"));
        Assert.Equal("installerPath", ex.ParamName);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
