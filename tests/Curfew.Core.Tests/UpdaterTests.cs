using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>tests for <see cref="Updater"/>; HTTP fetch injected, exercise decision logic and install script, no network/filesystem</summary>
public class UpdaterTests
{
    /// <summary>well-formed GitHub "latest release" payload: v1.5.0, one Curfew installer asset</summary>
    private const string ReleaseJson = """
    {
      "tag_name": "v1.5.0",
      "assets": [
        { "name": "curfew-setup-v1.5.0.exe", "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.5.0.exe" }
      ]
    }
    """;

    private const string InstallerUrl = "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.5.0.exe";

    /// <summary>fetcher that always returns <paramref name="json"/></summary>
    private static Func<string, CancellationToken, Task<string>> Returns(string json) =>
        (_, _) => Task.FromResult(json);

    /// <summary>fetcher that always faults with <paramref name="error"/></summary>
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
    [InlineData("v1.0.0")]  // older, leading-v like tags
    [InlineData("1.4.9")]   // older, only patch differs
    public async Task CheckForUpdate_returns_release_for_any_older_current_version(string current)
    {
        Assert.NotNull(await Updater.CheckForUpdateAsync(current, Returns(ReleaseJson)));
    }

    [Theory]
    [InlineData("1.5.0")]   // identical: equal is not an update
    [InlineData("v1.5.0")]  // identical, leading v
    [InlineData("2.0.0")]   // strictly newer
    [InlineData("1.5.1")]   // newer by one patch
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
        // unparsable version must not count as "older than release"; corrupt local would trigger reinstall
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
    [InlineData("   ")]              // whitespace body
    [InlineData("{ not json }")]     // malformed JSON
    [InlineData("[]")]               // valid JSON, not a release object
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

        // caller cancel is deliberate; surface cancellation, not silent "no update"
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Updater.CheckForUpdateAsync(
                "1.0.0",
                (_, token) => Task.FromException<string>(new OperationCanceledException(token)),
                cts.Token));
    }

    [Fact]
    public async Task CheckForUpdate_swallows_cancellation_exception_when_not_requested()
    {
        // cancellation reported with none requested is internal fault, not caller decision: treat as no update
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

        // register on-demand SYSTEM task via ScheduledTasks cmdlets (schtasks.exe /create breaks on PowerShell quote rewriting), then trigger now
        Assert.Contains("Register-ScheduledTask -TaskName 'CurfewAutoUpdate'", script, StringComparison.Ordinal);
        Assert.Contains("-UserId 'SYSTEM'", script, StringComparison.Ordinal);
        Assert.Contains("Start-ScheduledTask -TaskName 'CurfewAutoUpdate'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("schtasks /create", script, StringComparison.Ordinal);

        // run installer non-interactively, unattended
        Assert.Contains("/VERYSILENT", script, StringComparison.Ordinal);
        Assert.Contains("/SUPPRESSMSGBOXES", script, StringComparison.Ordinal);
        Assert.Contains("/NORESTART", script, StringComparison.Ordinal);

        // task tears itself down, no linger/re-fire
        Assert.Contains("schtasks /delete /tn CurfewAutoUpdate /f", script, StringComparison.Ordinal);

        // installer path embedded quoted in cmd.exe action so spaces survive
        Assert.Contains($"\"{installerPath}\"", script, StringComparison.Ordinal);

        // laptops: default task settings refuse to start (and kill) on battery
        Assert.Contains("-AllowStartIfOnBatteries", script, StringComparison.Ordinal);
        Assert.Contains("-DontStopIfGoingOnBatteries", script, StringComparison.Ordinal);

        // detached install records exit code so service logs failed silent install next pass, not vanish
        Assert.Contains("!ERRORLEVEL!", script, StringComparison.Ordinal);
        Assert.Contains(Updater.InstallResultFileName, script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScheduledInstallScript_rejects_path_containing_a_single_quote()
    {
        // action in single-quoted PowerShell string; single quote in path breaks out
        var ex = Assert.Throws<ArgumentException>(() =>
            Updater.BuildScheduledInstallScript(@"C:\o'brien\setup.exe"));
        Assert.Equal("installerPath", ex.ParamName);
    }

    [Fact]
    public void BuildScheduledInstallScript_uses_a_single_consistent_task_name()
    {
        var script = Updater.BuildScheduledInstallScript(@"C:\Curfew\setup.exe");

        // same task name to register, start, delete; diverged names leave real task behind
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

    [Theory]
    [InlineData(@"C:\evil"" \payload.exe")]
    [InlineData(@"C:\evil' \payload.exe")]
    public void BuildScheduledInstallScript_rejects_path_containing_a_quote(string installerPath)
    {
        // either quote breaks out of single-quoted PowerShell string or cmd.exe action quoting, can't escape safely: reject, not mis-build
        var ex = Assert.Throws<ArgumentException>(() =>
            Updater.BuildScheduledInstallScript(installerPath));
        Assert.Equal("installerPath", ex.ParamName);
    }

    [Fact]
    public async Task Prerelease_channel_fetches_the_release_list_and_picks_the_newest()
    {
        const string releasesJson = """
        [
          { "tag_name": "v1.6.0", "prerelease": true, "assets": [
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.6.0/curfew-setup-v1.6.0.exe" } ] },
          { "tag_name": "v1.5.0", "prerelease": false, "assets": [
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.5.0/curfew-setup-v1.5.0.exe" } ] }
        ]
        """;

        string? requested = null;
        var result = await Updater.CheckForUpdateAsync(
            "1.5.0",
            (url, _) => { requested = url; return Task.FromResult(releasesJson); },
            includePrereleases: true);

        Assert.Equal(Updater.ReleasesUrl, requested);   // list endpoint, not "latest"
        Assert.NotNull(result);
        Assert.Equal("v1.6.0", result.Value.Tag);
    }

    [Fact]
    public async Task Stable_channel_uses_the_latest_endpoint()
    {
        string? requested = null;
        await Updater.CheckForUpdateAsync(
            "1.5.0",
            (url, _) => { requested = url; return Task.FromResult("{}"); },
            includePrereleases: false);

        Assert.Equal(Updater.LatestReleaseUrl, requested);
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
