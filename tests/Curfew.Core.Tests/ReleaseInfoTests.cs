using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class ReleaseInfoTests
{
    [Fact]
    public void FromGitHubJson_extracts_tag_and_installer()
    {
        const string json = """
        {
          "tag_name": "v1.2.3",
          "assets": [
            { "name": "other.zip", "browser_download_url": "https://example.com/other.zip" },
            { "name": "curfew-setup-v1.2.3.exe", "browser_download_url": "https://example.com/curfew-setup-v1.2.3.exe" }
          ]
        }
        """;

        var info = ReleaseInfo.FromGitHubJson(json);

        Assert.NotNull(info);
        Assert.Equal("v1.2.3", info!.Value.Tag);
        Assert.EndsWith("curfew-setup-v1.2.3.exe", info.Value.InstallerUrl);
    }

    [Fact]
    public void FromGitHubJson_returns_null_without_installer_asset()
    {
        const string json = """
        { "tag_name": "v1.0.0", "assets": [
            { "name": "notes.txt", "browser_download_url": "https://example.com/notes.txt" } ] }
        """;

        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }
}
