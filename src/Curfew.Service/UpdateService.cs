using Curfew.Core;

namespace Curfew.Service;

/// <summary>Checks for and applies updates: download the installer, then let a
/// detached scheduled task run it silently so the install survives this service
/// stopping mid-update.</summary>
internal static class UpdateService
{
    public static async Task RunAsync(SettingsStore settings, string currentVersion, CancellationToken ct)
    {
        if (!settings.GetBool("auto_update_enabled", true)) return;

        var release = await Updater.CheckForUpdateAsync(currentVersion, Updater.HttpFetchAsync, ct)
            .ConfigureAwait(false);
        if (release is null) return;

        var installer = await DownloadInstallerAsync(release.Value.InstallerUrl, ct).ConfigureAwait(false);
        if (installer is null) return;

        PowerShellRunner.Run(Updater.BuildScheduledInstallScript(installer));
    }

    private static async Task<string?> DownloadInstallerAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("curfew-updater");
            var bytes = await client.GetByteArrayAsync(url, ct).ConfigureAwait(false);

            // A real installer is megabytes; anything smaller is an error page.
            if (bytes.Length < 500_000) return null;

            Directory.CreateDirectory(CurfewPaths.UpdateDirectory);
            var path = Path.Combine(CurfewPaths.UpdateDirectory, "curfew-update.exe");
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
