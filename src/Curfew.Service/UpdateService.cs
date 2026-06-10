using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Checks for and applies Curfew updates from the background worker.
/// </summary>
/// <remarks>
/// <para>
/// The flow is: ask <see cref="Updater"/> whether a newer release exists, download
/// its installer to the staging folder, then hand the installer off to a detached,
/// one-shot SYSTEM scheduled task (built by <see cref="Updater.BuildScheduledInstallScript"/>).
/// </para>
/// <para>
/// Running the install from a detached task — rather than directly here — is the
/// whole point: the installer stops and restarts this service mid-update, so any
/// process we own would be killed before the install finishes. The scheduled task
/// outlives us and cleans itself up afterwards.
/// </para>
/// <para>
/// Update activity is best-effort and must never destabilise the service. Only
/// cancellation (service shutdown) is allowed to propagate; every other failure is
/// logged and swallowed so the next six-hourly pass can simply retry.
/// </para>
/// </remarks>
internal static class UpdateService
{
    /// <summary>Settings flag that gates automatic updates; absent means "enabled".</summary>
    private const string AutoUpdateEnabledKey = "auto_update_enabled";

    /// <summary>File name the downloaded installer is staged under in the update folder.</summary>
    private const string InstallerFileName = "curfew-update.exe";

    /// <summary>
    /// Minimum size, in bytes, a download must reach to be treated as a real installer.
    /// GitHub error pages, rate-limit notices and truncated downloads are far smaller
    /// than the genuine multi-megabyte setup, so anything below this is rejected.
    /// </summary>
    private const int MinimumInstallerBytes = 500_000;

    /// <summary>
    /// Upper bound on a download, in bytes, so a malformed or hostile
    /// <c>Content-Length</c> can never make us buffer an unbounded amount into memory.
    /// </summary>
    private const long MaximumInstallerBytes = 256L * 1024 * 1024;

    /// <summary>How long the installer download may run before it is abandoned.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The two-byte "MZ" signature that begins every Windows PE executable.</summary>
    private static readonly byte[] PortableExecutableMagic = { 0x4D, 0x5A };

    /// <summary>
    /// Runs one update pass: checks for a newer release and, if found and downloadable,
    /// schedules its silent installation. Does nothing when auto-update is disabled or
    /// no newer release is available.
    /// </summary>
    /// <param name="settings">Open settings store; read for the auto-update flag.</param>
    /// <param name="currentVersion">The currently installed version (e.g. "2.0.0").</param>
    /// <param name="ct">Cancels the check, the download and (cooperatively) the pass.</param>
    /// <exception cref="OperationCanceledException">The service is shutting down.</exception>
    public static async Task RunAsync(SettingsStore settings, string currentVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.GetBool(AutoUpdateEnabledKey, true)) return;

        var release = await Updater.CheckForUpdateAsync(currentVersion, Updater.HttpFetchAsync, ct)
            .ConfigureAwait(false);
        if (release is null) return;

        ServiceLog.Write($"update available: {release.Value.Tag} (current {currentVersion})");

        var installer = await DownloadInstallerAsync(release.Value.InstallerUrl, ct).ConfigureAwait(false);
        if (installer is null) return;

        // BuildScheduledInstallScript validates the path; guard against it throwing
        // so a bad path can't take down the update pass.
        string script;
        try
        {
            script = Updater.BuildScheduledInstallScript(installer);
        }
        catch (ArgumentException ex)
        {
            ServiceLog.Write($"update aborted, unusable installer path: {ex.Message}");
            return;
        }

        var exitCode = PowerShellRunner.Run(script);
        if (exitCode == 0)
            ServiceLog.Write($"update {release.Value.Tag} scheduled for silent install");
        else
            ServiceLog.Write($"update scheduling failed (powershell exit {exitCode})");
    }

    /// <summary>
    /// Downloads the installer at <paramref name="url"/> into the staging folder and
    /// returns its path, or <see langword="null"/> if the download is missing, too
    /// small, too large, or does not look like a Windows executable.
    /// </summary>
    /// <param name="url">The installer asset's download URL.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>The staged installer path, or <see langword="null"/> on any failure.</returns>
    /// <exception cref="OperationCanceledException">The service is shutting down.</exception>
    private static async Task<string?> DownloadInstallerAsync(string url, CancellationToken ct)
    {
        // A dedicated client with an explicit timeout: an updater download must never
        // stall the service for the default (effectively unbounded) duration.
        using var client = new HttpClient { Timeout = DownloadTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("curfew-updater");

        try
        {
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // Reject an over-large body up front using the advertised length, before
            // reading a single byte of it.
            if (response.Content.Headers.ContentLength is long advertised
                && advertised > MaximumInstallerBytes)
            {
                ServiceLog.Write($"update download rejected: {advertised} bytes exceeds cap");
                return null;
            }

            var bytes = await ReadCappedAsync(response, ct).ConfigureAwait(false);
            if (bytes is null) return null;

            if (bytes.Length < MinimumInstallerBytes)
            {
                ServiceLog.Write($"update download rejected: only {bytes.Length} bytes (looks like an error page)");
                return null;
            }

            if (!HasExecutableHeader(bytes))
            {
                ServiceLog.Write("update download rejected: not a Windows executable");
                return null;
            }

            var path = Path.Combine(CurfewPaths.UpdateDirectory, InstallerFileName);
            Directory.CreateDirectory(CurfewPaths.UpdateDirectory);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Service shutdown: a caller decision, not a failed update — let it surface.
            throw;
        }
        catch (Exception ex)
        {
            // Network error, HTTP failure, download timeout, disk error, etc.
            // Any of these simply means "no update this pass"; retry in six hours.
            ServiceLog.Write($"update download failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the response body into memory, stopping (and returning <see langword="null"/>)
    /// if it exceeds <see cref="MaximumInstallerBytes"/>. This protects against a server
    /// that streams more data than its <c>Content-Length</c> claimed, or sends none at all.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];

        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumInstallerBytes)
            {
                ServiceLog.Write("update download rejected: body exceeded size cap mid-stream");
                return null;
            }
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Returns whether <paramref name="bytes"/> begins with the "MZ" Portable Executable
    /// signature, a cheap sanity check that we downloaded a binary and not an HTML or
    /// JSON error response that happened to be large.
    /// </summary>
    private static bool HasExecutableHeader(byte[] bytes) =>
        bytes.Length >= PortableExecutableMagic.Length
        && bytes[0] == PortableExecutableMagic[0]
        && bytes[1] == PortableExecutableMagic[1];
}
