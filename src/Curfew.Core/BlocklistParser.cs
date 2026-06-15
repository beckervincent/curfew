using System.Net;

namespace Curfew.Core;

/// <summary>
/// Parses a downloaded Pi-hole / StevenBlack-style blocklist (hosts format or a bare
/// domain list) into normalized, de-duplicated domains. Pure + testable; the service
/// fetches the text and the hosts applier sinks the domains.
/// </summary>
/// <remarks>
/// <para>Accepts both <c>0.0.0.0 example.com</c> / <c>127.0.0.1 example.com</c> hosts
/// lines and bare <c>example.com</c> lines; strips comments (<c>#</c>), the sink IP, and
/// <c>localhost</c>-type entries, then validates each domain via
/// <see cref="HostsBlocklist.Parse"/> (same rules as the custom blocklist).</para>
/// <para><b>Capped on purpose.</b> Windows resolves names through the DNS Client service
/// against the hosts file; a list of hundreds of thousands of entries (a full unified
/// list) measurably slows every lookup. The cap bounds how many domains reach the hosts
/// file so the machine stays responsive — the Cloudflare family DNS filter remains the
/// performant primary, this list is supplementary. The caller logs when the cap bites
/// (never a silent truncation).</para>
/// </remarks>
public static class BlocklistParser
{
    /// <summary>Default ceiling on parsed domains, to keep the hosts file a size Windows resolves quickly.</summary>
    public const int DefaultMaxDomains = 50_000;

    /// <summary>True when <paramref name="text"/> yielded exactly <paramref name="max"/> domains and parsing
    /// stopped at the cap (i.e. the source likely had more). Lets the caller log the truncation.</summary>
    public static IReadOnlyList<string> Parse(string? text, int max, out bool capped)
    {
        capped = false;
        var result = new List<string>();
        if (string.IsNullOrEmpty(text) || max <= 0) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        for (string? raw; (raw = reader.ReadLine()) is not null;)
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // hosts format is "<ip> <domain>"; a bare list is just "<domain>"
            var candidate = tokens.Length >= 2 ? tokens[1] : tokens[0];
            if (IPAddress.TryParse(candidate, out _)) continue;                      // the token is itself an IP
            if (candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase)) continue;
            if (candidate.Equals("local", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var domain in HostsBlocklist.Parse(candidate))
            {
                if (!seen.Add(domain)) continue;
                result.Add(domain);
                if (result.Count >= max) { capped = true; return result; }
            }
        }
        return result;
    }

    /// <summary>Parse with the default cap.</summary>
    public static IReadOnlyList<string> Parse(string? text) => Parse(text, DefaultMaxDomains, out _);
}
