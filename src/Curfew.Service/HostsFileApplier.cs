using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Reconciles the parent's custom domain blocklist into the system hosts file.
/// Pure parsing + section rendering live in <see cref="HostsBlocklist"/>; this only
/// does the privileged file write (the service runs as SYSTEM, which can write
/// <c>%SystemRoot%\System32\drivers\etc\hosts</c>). Idempotent: only writes when the
/// resulting file actually differs, so re-running on every reconcile is cheap and
/// does not churn the file or its timestamp.
/// </summary>
internal static class HostsFileApplier
{
    /// <summary>Config key holding the newline/comma-separated blocked domains.</summary>
    private const string BlockedDomainsKey = "blocked_domains";

    /// <summary>Config key toggling enforced SafeSearch (Google/Bing/YouTube via hosts).</summary>
    private const string SafeSearchKey = "safesearch_enabled";

    private static string HostsPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    /// <summary>Reconcile the hosts blocklist with <paramref name="settings"/>. Never throws.</summary>
    public static void Apply(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var domains = HostsBlocklist.Parse(settings.Get(BlockedDomainsKey));
            var extra = settings.GetBool(SafeSearchKey, false) ? SafeSearch.HostsLines() : Array.Empty<string>();
            var path = HostsPath;
            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var merged = HostsBlocklist.Merge(existing, domains, extra);

            // compare ignoring newline style so we don't rewrite purely over CRLF/LF
            if (Normalize(existing) == Normalize(merged)) return;

            // hosts files are conventionally CRLF on Windows
            File.WriteAllText(path, merged.Replace("\n", "\r\n"));
            ServiceLog.Write($"hosts blocklist applied ({domains.Count} domain(s))");
        }
        catch (Exception ex)
        {
            EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.FilterFailure, $"hosts blocklist ({ex.GetType().Name})");
            ServiceLog.Write($"hosts blocklist apply failed: {ex.Message}");
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
}
