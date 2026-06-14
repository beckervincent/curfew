using Curfew.Core;

namespace Curfew.Service;

/// <summary>Apply Cloudflare content filter (and optional firewall block on third-party DoH resolvers) to machine.</summary>
/// <remarks>
/// <para>Two independent PowerShell scripts run as SYSTEM: one pins/clears Cloudflare DNS on every adapter, one adds/removes DoH bypass firewall rules. Both idempotent (pure helpers in <see cref="Curfew.Core"/>), safe to re-run — re-run on every network change so filter stays pinned.</para>
/// <para>Steps decoupled: DNS filter failure no block DoH guard reconcile, vice versa. Each step logs to <see cref="ServiceLog"/>; caller still wraps whole op in own try/catch.</para>
/// </remarks>
internal static class ContentFilterApplier
{
    // persisted settings keys. cross-component contract shared with WinUI app; must not change
    private const string FilterModeKey = "dns_filter_mode";
    private const string BlockDohKey = "block_doh_bypass";

    /// <summary>Reconcile DNS content filter and DoH bypass block with settings. Never throws on a step's PowerShell failure — logged, next step still runs.</summary>
    /// <param name="settings">Open settings store to read config from.</param>
    public static void Apply(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var mode = ContentFilter.Parse(settings.Get(FilterModeKey));

        // step 1: pin Cloudflare resolvers (or reset adapters to DHCP when filtering Off)
        RunStep(
            $"DNS filter ({ContentFilter.ToSetting(mode)})",
            ContentFilter.BuildApplyScript(mode));

        // step 2: reconcile DoH bypass firewall block. only meaningful while filter active — no filter, nothing to bypass — so always cleared when Off, ignoring persisted pref
        var blockDoh = mode != FilterMode.Off && settings.GetBool(BlockDohKey, true);
        RunStep(
            blockDoh ? "DoH bypass block" : "DoH bypass clear",
            blockDoh ? DohGuard.BuildBlockScript() : DohGuard.BuildClearScript());
    }

    /// <summary>Run one PowerShell reconciliation step; log non-zero/failed exit without aborting remaining steps.</summary>
    private static void RunStep(string description, string script)
    {
        int exitCode = PowerShellRunner.Run(script);
        if (exitCode != 0)
        {
            EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.FilterFailure, $"{description} ({exitCode})");
            // PowerShellRunner returns -1 when process no launch or killed on timeout; other non-zero is script's own exit code. scripts use SilentlyContinue, so non-zero unexpected, worth a log
            ServiceLog.Write($"content filter step '{description}' returned exit code {exitCode}");
        }
    }
}
