using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class UpdaterTests
{
    private const string ReleaseJson = """
    {
      "tag_name": "v1.5.0",
      "assets": [
        { "name": "curfew-setup-v1.5.0.exe", "browser_download_url": "https://example.com/curfew-setup-v1.5.0.exe" }
      ]
    }
    """;

    [Fact]
    public async Task Returns_release_when_newer()
    {
        var result = await Updater.CheckForUpdateAsync("1.0.0", (_, _) => Task.FromResult(ReleaseJson));
        Assert.NotNull(result);
        Assert.Equal("v1.5.0", result!.Value.Tag);
    }

    [Fact]
    public async Task Returns_null_when_same_or_older()
    {
        Assert.Null(await Updater.CheckForUpdateAsync("1.5.0", (_, _) => Task.FromResult(ReleaseJson)));
        Assert.Null(await Updater.CheckForUpdateAsync("2.0.0", (_, _) => Task.FromResult(ReleaseJson)));
    }

    [Fact]
    public async Task Returns_null_when_fetch_throws()
    {
        var result = await Updater.CheckForUpdateAsync(
            "1.0.0", (_, _) => Task.FromException<string>(new HttpRequestException("offline")));
        Assert.Null(result);
    }

    [Fact]
    public void Install_script_schedules_silent_task()
    {
        var script = Updater.BuildScheduledInstallScript(@"C:\ProgramData\Curfew\update\curfew-update.exe");
        Assert.Contains("schtasks /create", script);
        Assert.Contains("/ru SYSTEM", script);
        Assert.Contains("/VERYSILENT", script);
        Assert.Contains("schtasks /run", script);
    }
}
