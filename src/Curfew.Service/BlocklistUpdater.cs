using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Downloads the parent-enabled public blocklists (<see cref="BlocklistSources"/>),
/// parses + caps them (<see cref="BlocklistParser"/>) and caches the merged domain list
/// to a file the hosts applier sinks. The Pi-hole "gravity" model, server-free: the
/// sources are stable public static files fetched like an update.
/// </summary>
/// <remarks>
/// Fail-safe: a failed/oversized/empty download keeps the last good cache rather than
/// dropping protection. Refreshed on the slow (6-hourly) cycle and immediately when the
/// parent changes the blocklist settings. The total is capped (default
/// <see cref="BlocklistParser.DefaultMaxDomains"/>) across all sources so the hosts file
/// stays a size Windows resolves quickly.
/// </remarks>
internal static class BlocklistUpdater
{
    private const string EnabledKey = "blocklist_enabled";
    private const string SourcesKey = "blocklist_sources";
    private const string CustomUrlsKey = "blocklist_custom_urls";
    private const string MaxKey = "blocklist_max_domains";
    private const string CacheFileName = "blocklist-cache.txt";

    /// <summary>Reject absurd downloads (a wrong URL or error page) before buffering.</summary>
    private const long MaxDownloadBytes = 32L * 1024 * 1024;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    private static string CachePath => Path.Combine(CurfewPaths.DataDirectory, CacheFileName);

    /// <summary>Cached blocked domains (one per line), or empty when disabled / never fetched.</summary>
    public static IReadOnlyList<string> LoadCachedDomains()
    {
        try
        {
            var path = CachePath;
            if (!File.Exists(path)) return Array.Empty<string>();
            return File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"blocklist cache read failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>Re-download the enabled sources and rewrite the cache. No-op (clears cache) when disabled.
    /// Never throws; on any failure the previous cache is left untouched.</summary>
    public static async Task RefreshAsync(SettingsStore settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            if (!settings.GetBool(EnabledKey, false))
            {
                // disabled: drop the cache so the hosts applier stops sinking the list
                try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { /* best effort */ }
                return;
            }

            var urls = new List<string>(BlocklistSources.UrlsFor(BlocklistSources.Parse(settings.Get(SourcesKey))));
            foreach (var custom in BlocklistSources.ParseCustomUrls(settings.Get(CustomUrlsKey)))
                if (!urls.Contains(custom)) urls.Add(custom);
            if (urls.Count == 0) return;

            var max = settings.GetInt(MaxKey, BlocklistParser.DefaultMaxDomains);
            if (max <= 0) max = BlocklistParser.DefaultMaxDomains;

            using var http = new HttpClient { Timeout = DownloadTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Curfew-Blocklist/1.0");

            var domains = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var url in urls)
            {
                if (domains.Count >= max) break;
                var text = await DownloadAsync(http, url, ct).ConfigureAwait(false);
                if (text is null) continue; // download failed — skip this source, keep others

                var parsed = BlocklistParser.Parse(text, max - domains.Count, out var capped);
                foreach (var d in parsed)
                    if (seen.Add(d)) domains.Add(d);
                ServiceLog.Write($"blocklist {url}: +{parsed.Count} domain(s){(capped ? " (capped)" : string.Empty)}");
            }

            if (domains.Count == 0)
            {
                ServiceLog.Write("blocklist refresh produced no domains; keeping previous cache");
                return;
            }

            var tmp = CachePath + ".tmp";
            await File.WriteAllLinesAsync(tmp, domains, ct).ConfigureAwait(false);
            File.Move(tmp, CachePath, overwrite: true); // atomic-ish swap so a torn write never half-replaces
            ServiceLog.Write($"blocklist cache updated ({domains.Count} domain(s) from {urls.Count} source(s))");
        }
        catch (OperationCanceledException)
        {
            // shutting down — leave the cache as-is
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"blocklist refresh failed: {ex.Message}");
        }
    }

    /// <summary>Download one source as text, or null on failure / oversized / non-success.</summary>
    private static async Task<string?> DownloadAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { ServiceLog.Write($"blocklist {url}: HTTP {(int)resp.StatusCode}"); return null; }
            if (resp.Content.Headers.ContentLength is > MaxDownloadBytes) { ServiceLog.Write($"blocklist {url}: too large"); return null; }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.LongLength > MaxDownloadBytes) return null;
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"blocklist download {url} failed: {ex.Message}");
            return null;
        }
    }
}
