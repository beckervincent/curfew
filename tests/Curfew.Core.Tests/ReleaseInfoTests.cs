using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>Tests for <see cref="ReleaseInfo.FromGitHubJson"/>, untrusted boundary turning GitHub "latest release" HTTP response into installer tag + URL. Must never throw, must reject non-usable release; most tests cover malformed + adversarial input.</summary>
public class ReleaseInfoTests
{
    // well-formed GitHub release payload reused across tests
    private const string ValidJson = """
    {
      "tag_name": "v1.2.3",
      "assets": [
        { "name": "other.zip", "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/other.zip" },
        { "name": "curfew-setup-v1.2.3.exe", "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.2.3.exe" }
      ]
    }
    """;

    [Fact]
    public void FromGitHubJson_extracts_tag_and_installer()
    {
        var info = ReleaseInfo.FromGitHubJson(ValidJson);

        Assert.NotNull(info);
        Assert.Equal("v1.2.3", info!.Value.Tag);
        Assert.Equal("https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.2.3.exe", info.Value.InstallerUrl);
    }

    [Fact]
    public void FromGitHubJson_picks_first_matching_installer_when_several_present()
    {
        const string json = """
        {
          "tag_name": "v2.0.0",
          "assets": [
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/notes.txt" },
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-first.exe" },
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-second.exe" }
          ]
        }
        """;

        var info = ReleaseInfo.FromGitHubJson(json);

        Assert.NotNull(info);
        Assert.EndsWith("curfew-setup-first.exe", info!.Value.InstallerUrl);
    }

    [Fact]
    public void FromGitHubJson_skips_non_installer_assets_to_find_the_installer()
    {
        const string json = """
        {
          "tag_name": "v3.1.0",
          "assets": [
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/source.zip" },
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup.exe.sha256" },
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-portable.exe" },
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v3.1.0.exe" }
          ]
        }
        """;

        var info = ReleaseInfo.FromGitHubJson(json);

        Assert.NotNull(info);
        Assert.EndsWith("curfew-setup-v3.1.0.exe", info!.Value.InstallerUrl);
    }

    [Theory]
    [InlineData("https://github.com/beckervincent/curfew/releases/download/v1.0.0/CURFEW-SETUP-v1.0.0.EXE")]
    [InlineData("https://github.com/beckervincent/curfew/releases/download/v1.0.0/Curfew-Setup-v1.0.0.Exe")]
    [InlineData("https://github.com/beckervincent/curfew/releases/download/v1.0.0/path/curfew-setup-final.exe")]
    public void FromGitHubJson_matches_installer_case_insensitively(string url)
    {
        var json = $$"""
        { "tag_name": "v1.0.0", "assets": [
            { "browser_download_url": "{{url}}" } ] }
        """;

        var info = ReleaseInfo.FromGitHubJson(json);

        Assert.NotNull(info);
        Assert.Equal(url, info!.Value.InstallerUrl);
    }

    [Theory]
    // right extension, missing "curfew-setup" marker
    [InlineData("https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-portable.exe")]
    [InlineData("https://github.com/beckervincent/curfew/releases/download/v1.0.0/setup.exe")]
    // right marker, wrong extension (e.g. checksum or signature alongside)
    [InlineData("https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.0.0.exe.sha256")]
    [InlineData("https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.0.0.zip")]
    public void FromGitHubJson_returns_null_when_no_asset_is_an_installer(string url)
    {
        var json = $$"""
        { "tag_name": "v1.0.0", "assets": [
            { "browser_download_url": "{{url}}" } ] }
        """;

        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Fact]
    public void FromGitHubJson_returns_null_without_installer_asset()
    {
        const string json = """
        { "tag_name": "v1.0.0", "assets": [
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/notes.txt" } ] }
        """;

        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Theory]
    // plain HTTP — never accept, even with right name
    [InlineData("http://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.0.0.exe")]
    // different GitHub account's release asset, same name
    [InlineData("https://github.com/attacker/curfew/releases/download/v1.0.0/curfew-setup-v1.0.0.exe")]
    // right host, wrong repo
    [InlineData("https://github.com/beckervincent/evil/releases/download/v1.0.0/curfew-setup-v1.0.0.exe")]
    // look-alike host
    [InlineData("https://github.com.attacker.net/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.0.0.exe")]
    // arbitrary third-party host
    [InlineData("https://evil.example/curfew-setup-v1.0.0.exe")]
    public void FromGitHubJson_rejects_untrusted_installer_hosts(string url)
    {
        var json = $$"""
        { "tag_name": "v1.0.0", "assets": [
            { "browser_download_url": "{{url}}" } ] }
        """;

        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Fact]
    public void IsInstallerUrl_accepts_only_the_pinned_repo_release_path()
    {
        Assert.True(ReleaseInfo.IsInstallerUrl(
            "https://github.com/beckervincent/curfew/releases/download/v1.5.4/curfew-setup-v1.5.4.exe"));
        Assert.False(ReleaseInfo.IsInstallerUrl(
            "https://github.com/attacker/curfew/releases/download/v1.5.4/curfew-setup-v1.5.4.exe"));
        Assert.False(ReleaseInfo.IsInstallerUrl(
            "http://github.com/beckervincent/curfew/releases/download/v1.5.4/curfew-setup-v1.5.4.exe"));
    }

    [Theory]
    // literal ".." segments
    [InlineData("https://github.com/beckervincent/curfew/releases/download/../../../attacker/repo/releases/download/v1.0.0/curfew-setup.exe")]
    // percent-encoded ".." (%2e%2e) — Uri normalization must collapse these the same way
    [InlineData("https://github.com/beckervincent/curfew/releases/download/%2e%2e/%2e%2e/%2e%2e/attacker/repo/releases/download/v1.0.0/curfew-setup.exe")]
    public void IsInstallerUrl_rejects_path_traversal_escaping_the_pinned_repo(string url)
    {
        // starts with the trusted prefix as a raw string, but HttpClient/Uri normalize the ".." segments and
        // would fetch from github.com/attacker/repo — must be rejected by the normalized re-check
        Assert.False(ReleaseInfo.IsInstallerUrl(url));
    }

    [Fact]
    public void FromGitHubJson_returns_null_for_empty_assets_array()
    {
        Assert.Null(ReleaseInfo.FromGitHubJson("""{ "tag_name": "v1.0.0", "assets": [] }"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void FromGitHubJson_returns_null_for_blank_input(string? json)
    {
        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{ \"tag_name\": }")]
    [InlineData("[")]
    [InlineData("\"unterminated")]
    public void FromGitHubJson_returns_null_for_malformed_json(string json)
    {
        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Theory]
    // root not an object can't be release payload
    [InlineData("[]")]
    [InlineData("[ { \"tag_name\": \"v1.0.0\" } ]")]
    [InlineData("\"v1.0.0\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void FromGitHubJson_returns_null_when_root_is_not_an_object(string json)
    {
        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Fact]
    public void FromGitHubJson_returns_null_when_tag_name_is_missing()
    {
        const string json = """
        { "assets": [ { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup.exe" } ] }
        """;

        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Theory]
    // tag_name present but not JSON string
    [InlineData("{ \"tag_name\": 123, \"assets\": [] }")]
    [InlineData("{ \"tag_name\": null, \"assets\": [] }")]
    [InlineData("{ \"tag_name\": true, \"assets\": [] }")]
    [InlineData("{ \"tag_name\": [\"v1\"], \"assets\": [] }")]
    public void FromGitHubJson_returns_null_when_tag_name_is_not_a_string(string json)
    {
        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Fact]
    public void FromGitHubJson_returns_null_for_empty_tag_even_with_valid_installer()
    {
        const string json = """
        { "tag_name": "", "assets": [
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup.exe" } ] }
        """;

        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Fact]
    public void FromGitHubJson_returns_null_when_assets_is_missing()
    {
        Assert.Null(ReleaseInfo.FromGitHubJson("""{ "tag_name": "v1.0.0" }"""));
    }

    [Theory]
    // assets present but not JSON array
    [InlineData("{ \"tag_name\": \"v1.0.0\", \"assets\": {} }")]
    [InlineData("{ \"tag_name\": \"v1.0.0\", \"assets\": \"none\" }")]
    [InlineData("{ \"tag_name\": \"v1.0.0\", \"assets\": null }")]
    public void FromGitHubJson_returns_null_when_assets_is_not_an_array(string json)
    {
        Assert.Null(ReleaseInfo.FromGitHubJson(json));
    }

    [Fact]
    public void FromGitHubJson_tolerates_assets_with_missing_or_non_string_urls()
    {
        // non-object entries + absent/wrong-typed download URLs skipped without throwing; lone valid installer wins
        const string json = """
        {
          "tag_name": "v1.0.0",
          "assets": [
            "not-an-object",
            42,
            { "name": "no-url-here" },
            { "browser_download_url": 999 },
            { "browser_download_url": null },
            { "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v1.0.0.exe" }
          ]
        }
        """;

        var info = ReleaseInfo.FromGitHubJson(json);

        Assert.NotNull(info);
        Assert.EndsWith("curfew-setup-v1.0.0.exe", info!.Value.InstallerUrl);
    }

    [Fact]
    public void FromGitHubJson_ignores_unknown_extra_properties()
    {
        // GitHub payload carries many fields parser doesn't read; presence must not affect result
        const string json = """
        {
          "url": "https://api.github.com/repos/x/y/releases/1",
          "draft": false,
          "prerelease": false,
          "tag_name": "v4.2.0",
          "name": "Curfew 4.2.0",
          "body": "release notes",
          "assets": [
            {
              "id": 7,
              "name": "curfew-setup-v4.2.0.exe",
              "content_type": "application/octet-stream",
              "size": 12345,
              "browser_download_url": "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v4.2.0.exe"
            }
          ]
        }
        """;

        var info = ReleaseInfo.FromGitHubJson(json);

        Assert.NotNull(info);
        Assert.Equal("v4.2.0", info!.Value.Tag);
        Assert.EndsWith("curfew-setup-v4.2.0.exe", info.Value.InstallerUrl);
    }

    [Fact]
    public void ReleaseInfo_record_exposes_constructor_arguments_via_properties()
    {
        var info = new ReleaseInfo("v9.9.9", "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v9.9.9.exe");

        Assert.Equal("v9.9.9", info.Tag);
        Assert.Equal("https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup-v9.9.9.exe", info.InstallerUrl);
    }

    [Fact]
    public void ReleaseInfo_record_uses_value_equality()
    {
        var a = new ReleaseInfo("v1.0.0", "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup.exe");
        var b = new ReleaseInfo("v1.0.0", "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup.exe");
        var different = new ReleaseInfo("v1.0.1", "https://github.com/beckervincent/curfew/releases/download/v1.0.0/curfew-setup.exe");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, different);
    }
}
