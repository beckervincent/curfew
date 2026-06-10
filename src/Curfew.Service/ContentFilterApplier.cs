using Curfew.Core;

namespace Curfew.Service;

/// <summary>Applies the configured Cloudflare content filter (and optional DoH
/// firewall block) to the machine. Re-run on network changes so it stays
/// pinned when adapters come and go.</summary>
internal static class ContentFilterApplier
{
    public static void Apply(SettingsStore settings)
    {
        var mode = ContentFilter.Parse(settings.Get("dns_filter_mode"));
        PowerShellRunner.Run(ContentFilter.BuildApplyScript(mode));

        var blockDoh = mode != FilterMode.Off && settings.GetBool("block_doh_bypass", true);
        PowerShellRunner.Run(blockDoh ? DohGuard.BuildBlockScript() : DohGuard.BuildClearScript());
    }
}
