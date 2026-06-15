using System.Linq;
using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class HostsBlocklistTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM  ", "example.com")]
    [InlineData("https://example.com/path?q=1", "example.com")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("example.com:8080", "example.com")]
    [InlineData("sub.example.co.uk", "sub.example.co.uk")]
    public void Parse_normalizes_to_bare_domain(string input, string expected) =>
        Assert.Equal(new[] { expected }, HostsBlocklist.Parse(input));

    [Theory]
    [InlineData("notadomain")]   // no dot
    [InlineData("bad_domain.com")] // underscore illegal in a label
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_drops_invalid_entries(string input) =>
        Assert.Empty(HostsBlocklist.Parse(input));

    [Fact]
    public void Parse_splits_on_newlines_commas_spaces_and_dedupes()
    {
        var list = HostsBlocklist.Parse("a.com, b.com\nA.COM  c.com;b.com");
        Assert.Equal(new[] { "a.com", "b.com", "c.com" }, list);
    }

    [Fact]
    public void RenderSection_blocks_apex_and_www_between_markers()
    {
        var section = HostsBlocklist.RenderSection(HostsBlocklist.Parse("game.com"));
        Assert.StartsWith(HostsBlocklist.BeginMarker, section);
        Assert.EndsWith(HostsBlocklist.EndMarker, section);
        Assert.Contains("0.0.0.0 game.com", section);
        Assert.Contains("0.0.0.0 www.game.com", section);
    }

    [Fact]
    public void RenderSection_empty_for_no_domains() =>
        Assert.Equal(string.Empty, HostsBlocklist.RenderSection(HostsBlocklist.Parse("")));

    [Fact]
    public void Merge_appends_section_and_preserves_existing_hosts()
    {
        const string hosts = "127.0.0.1 localhost\n::1 localhost";
        var merged = HostsBlocklist.Merge(hosts, HostsBlocklist.Parse("x.com"));
        Assert.Contains("127.0.0.1 localhost", merged);
        Assert.Contains("0.0.0.0 x.com", merged);
    }

    [Fact]
    public void Merge_is_idempotent()
    {
        const string hosts = "127.0.0.1 localhost";
        var domains = HostsBlocklist.Parse("x.com y.com");
        var once = HostsBlocklist.Merge(hosts, domains);
        var twice = HostsBlocklist.Merge(once, domains);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Merge_with_empty_list_removes_a_prior_section()
    {
        const string hosts = "127.0.0.1 localhost";
        var withBlock = HostsBlocklist.Merge(hosts, HostsBlocklist.Parse("x.com"));
        var cleared = HostsBlocklist.Merge(withBlock, HostsBlocklist.Parse(""));
        Assert.DoesNotContain(HostsBlocklist.BeginMarker, cleared);
        Assert.Contains("127.0.0.1 localhost", cleared);
    }

    [Fact]
    public void Merge_includes_extra_lines_in_the_section()
    {
        var merged = HostsBlocklist.Merge("127.0.0.1 localhost",
            HostsBlocklist.Parse("x.com"), new[] { "1.2.3.4 example.org" });
        Assert.Contains("0.0.0.0 x.com", merged);
        Assert.Contains("1.2.3.4 example.org", merged);
        Assert.Contains(HostsBlocklist.BeginMarker, merged);
    }

    [Fact]
    public void SafeSearch_lines_cover_google_bing_youtube()
    {
        var lines = SafeSearch.HostsLines();
        Assert.Contains(lines, l => l.EndsWith(" google.com"));
        Assert.Contains(lines, l => l.EndsWith(" google.de"));      // country domain covered
        Assert.Contains(lines, l => l.EndsWith(" www.google.co.uk"));
        Assert.Contains(lines, l => l.EndsWith(" bing.com"));
        Assert.Contains(lines, l => l.EndsWith(" youtube.com"));
        Assert.All(lines, l => Assert.Matches(@"^\d+\.\d+\.\d+\.\d+ \S+$", l));
    }

    [Fact]
    public void StripSection_handles_crlf_and_keeps_other_lines()
    {
        var hosts = "127.0.0.1 localhost\r\n" + HostsBlocklist.BeginMarker + "\r\n0.0.0.0 x.com\r\n" +
                    HostsBlocklist.EndMarker + "\r\n10.0.0.1 intranet";
        var stripped = HostsBlocklist.StripSection(hosts);
        Assert.DoesNotContain("x.com", stripped);
        Assert.Contains("127.0.0.1 localhost", stripped);
        Assert.Contains("10.0.0.1 intranet", stripped);
    }

    [Fact]
    public void StripSection_preserves_user_lines_when_section_is_unterminated()
    {
        // BEGIN with no matching END (torn write / manual edit) must NOT delete the user's entries below it
        var hosts = "127.0.0.1 localhost\n" + HostsBlocklist.BeginMarker +
                    "\n0.0.0.0 x.com\n10.0.0.1 intranet";
        var stripped = HostsBlocklist.StripSection(hosts);
        Assert.Contains("127.0.0.1 localhost", stripped);
        Assert.Contains("10.0.0.1 intranet", stripped);            // user line below the orphan section preserved
        Assert.DoesNotContain(HostsBlocklist.BeginMarker, stripped); // orphan marker dropped (no re-trigger)
    }
}
