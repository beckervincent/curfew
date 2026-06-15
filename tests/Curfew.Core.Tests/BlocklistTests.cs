using System.Linq;
using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class BlocklistParserTests
{
    [Fact]
    public void Parse_reads_hosts_format_and_drops_ips_comments_localhost()
    {
        const string text = @"
# title comment
0.0.0.0 ads.example.com
127.0.0.1 localhost
0.0.0.0 tracker.example.net # inline comment
::1 localhost
0.0.0.0 0.0.0.0
";
        var domains = BlocklistParser.Parse(text);
        Assert.Contains("ads.example.com", domains);
        Assert.Contains("tracker.example.net", domains);
        Assert.DoesNotContain("localhost", domains);
        Assert.DoesNotContain("0.0.0.0", domains);
    }

    [Fact]
    public void Parse_reads_bare_domain_list()
    {
        var domains = BlocklistParser.Parse("evil.com\nbad.org\n");
        Assert.Equal(new[] { "evil.com", "bad.org" }, domains);
    }

    [Fact]
    public void Parse_dedupes_across_lines()
    {
        var domains = BlocklistParser.Parse("0.0.0.0 dup.com\n0.0.0.0 dup.com\n127.0.0.1 dup.com");
        Assert.Single(domains);
    }

    [Fact]
    public void Parse_caps_and_flags_truncation()
    {
        var text = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"0.0.0.0 d{i}.example.com"));
        var domains = BlocklistParser.Parse(text, 10, out var capped);
        Assert.Equal(10, domains.Count);
        Assert.True(capped);
    }

    [Fact]
    public void Parse_not_capped_when_under_limit()
    {
        BlocklistParser.Parse("0.0.0.0 a.com\n0.0.0.0 b.com", 10, out var capped);
        Assert.False(capped);
    }

    [Fact]
    public void Parse_empty_or_nonpositive_max_is_empty()
    {
        Assert.Empty(BlocklistParser.Parse(""));
        Assert.Empty(BlocklistParser.Parse("0.0.0.0 a.com", 0, out _));
    }
}

public class BlocklistSourcesTests
{
    [Fact]
    public void AllKeys_resolve_to_https_urls()
    {
        var urls = BlocklistSources.UrlsFor(BlocklistSources.AllKeys);
        Assert.Equal(BlocklistSources.AllKeys.Count, urls.Count);
        Assert.All(urls, u => Assert.StartsWith("https://", u));
    }

    [Fact]
    public void Parse_keeps_known_keys_only()
    {
        var keys = BlocklistSources.Parse("adult, nonsense ; ADS adult");
        Assert.Equal(new[] { "adult", "ads" }, keys); // de-duped, known-only, lower-cased
    }

    [Fact]
    public void UrlsFor_unknown_is_empty() =>
        Assert.Empty(BlocklistSources.UrlsFor(new[] { "nope" }));
}
