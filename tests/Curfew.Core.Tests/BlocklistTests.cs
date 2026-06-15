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

    [Fact]
    public void Exclude_drops_allowed_domains_and_their_subdomains()
    {
        var domains = new[] { "ads.com", "cdn.example.com", "example.com", "evil.net" };
        var allow = new HashSet<string>(new[] { "example.com" }, System.StringComparer.OrdinalIgnoreCase);
        var kept = BlocklistParser.Exclude(domains, allow);
        Assert.Equal(new[] { "ads.com", "evil.net" }, kept); // example.com + cdn.example.com freed
    }

    [Fact]
    public void Exclude_empty_allow_returns_input()
    {
        var domains = new[] { "a.com", "b.com" };
        Assert.Same(domains, BlocklistParser.Exclude(domains, new HashSet<string>()));
    }

    [Fact]
    public void Exclude_does_not_treat_suffix_lookalikes_as_subdomains()
    {
        // "notexample.com" must NOT be freed by allowing "example.com"
        var kept = BlocklistParser.Exclude(new[] { "notexample.com" },
            new HashSet<string>(new[] { "example.com" }, System.StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "notexample.com" }, kept);
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

    [Fact]
    public void ParseCustomUrls_keeps_https_only_and_dedupes()
    {
        var urls = BlocklistSources.ParseCustomUrls(
            "https://a.com/hosts.txt\nhttp://insecure.com/x\nnot a url\nhttps://a.com/hosts.txt\nftp://f.com/l");
        Assert.Equal(new[] { "https://a.com/hosts.txt" }, urls);
    }

    [Fact]
    public void ParseCustomUrls_empty_is_empty() =>
        Assert.Empty(BlocklistSources.ParseCustomUrls(""));
}
