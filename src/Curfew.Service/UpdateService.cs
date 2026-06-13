using System.Security.AccessControl;
using System.Security.Principal;
using Curfew.Core;
using Curfew.Core.Security;

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

    /// <summary>Settings key choosing the update channel; <see cref="PrereleaseChannel"/> opts into pre-releases.</summary>
    private const string UpdateChannelKey = "update_channel";

    /// <summary>Value of <see cref="UpdateChannelKey"/> that includes pre-releases.</summary>
    private const string PrereleaseChannel = "prerelease";

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

        ReportPreviousInstallResult();

        // Pre-releases are only auto-installed when the parent opts into that channel.
        var includePrereleases = settings.Get(UpdateChannelKey) == PrereleaseChannel;
        var release = await Updater.CheckForUpdateAsync(currentVersion, Updater.HttpFetchAsync, includePrereleases, ct,
                onCheckFailure: reason => ServiceLog.Write($"update check failed: {reason}"))
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
        {
            ServiceLog.Write($"update {release.Value.Tag} scheduled for silent install");
            EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.UpdateInstalled, release.Value.Tag);
        }
        else
            ServiceLog.Write($"update scheduling failed (powershell exit {exitCode})");
    }

    /// <summary>
    /// Logs (and clears) the exit code the previous scheduled install left behind.
    /// The install runs as a detached one-shot task, so this marker is the only
    /// place a failed silent install (locked files, disk full, another installer
    /// running) ever surfaces.
    /// </summary>
    private static void ReportPreviousInstallResult()
    {
        var marker = Path.Combine(CurfewPaths.UpdateDirectory, Updater.InstallResultFileName);
        try
        {
            if (!File.Exists(marker)) return;

            var raw = File.ReadAllText(marker).Trim();
            File.Delete(marker);

            if (raw == "0")
                ServiceLog.Write("previous update install completed (exit 0)");
            else
                ServiceLog.Write($"previous update install FAILED (installer exit {raw})");
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"update result marker: {ex.Message}");
        }
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
        // Re-pin the URL before this SYSTEM process fetches anything: it must be the
        // HTTPS GitHub release path of THIS repo (not just any "curfew-setup.exe").
        // The parse step already enforced this, but the updater runs as SYSTEM and
        // later runs the payload, so it re-checks rather than trusting its caller.
        if (!ReleaseInfo.IsInstallerUrl(url))
        {
            ServiceLog.Write("update download rejected: untrusted installer URL");
            return null;
        }

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

            // GitHub redirects the release path to *.githubusercontent.com; make sure
            // a redirect chain never lands us on some other host before we save and
            // (later) execute the payload as SYSTEM.
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsTrustedDownloadHost(finalUri))
            {
                ServiceLog.Write($"update download rejected: redirected to untrusted host {finalUri.Host}");
                return null;
            }

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

            // Stage the installer where the child cannot tamper with it. The update
            // folder inherits Users=Modify from %ProgramData%\Curfew (the installer
            // grants it so state.db and SQLite's sidecars stay writable), which would
            // otherwise let a limited child overwrite the staged exe AFTER Verify()
            // returns but BEFORE the detached SYSTEM task opens it — a signature TOCTOU
            // that lands attacker code with SYSTEM rights. Drop the directory's
            // inheritance and deny Users write/delete before we write the payload, then
            // lock the file the same way and verify AFTER the lockdown, so the bytes the
            // task later executes are exactly the bytes that passed the signature check.
            Directory.CreateDirectory(CurfewPaths.UpdateDirectory);
            ProtectFromChild(CurfewPaths.UpdateDirectory, isDirectory: true);

            var path = Path.Combine(CurfewPaths.UpdateDirectory, InstallerFileName);

            // CreateNew + FileShare.None: no other handle may write or delete the file
            // while we hold it, and a pre-created decoy left by the child is rejected
            // rather than appended to. A stale exe from a previous pass is cleared first.
            try { File.Delete(path); } catch { /* may not exist; recreated below */ }
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            // Lock the file down with the same deny-ACE as the directory, so it is no
            // longer child-writable once our handle is closed. This must happen before
            // verification: a Verify() that races a still-writable file proves nothing.
            ProtectFromChild(path, isDirectory: false);

            // Last line of defence before this SYSTEM process schedules the installer
            // to run: it must be Authenticode-signed by Curfew's own key. Anything
            // else — unsigned, tampered, or signed by a different key — is discarded.
            // Run AFTER the lockdown so the verified bytes are the bytes the detached
            // task will open; the child can no longer swap them in the window between.
            if (!InstallerSignature.Verify(path))
            {
                ServiceLog.Write("update download rejected: installer is not signed by Curfew's key");
                try { File.Delete(path); } catch { /* best effort */ }
                return null;
            }

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
    /// Whether <paramref name="uri"/> is HTTPS on GitHub or its asset CDN. Used to
    /// validate the final URL after redirects, which land on
    /// <c>*.githubusercontent.com</c> and so cannot be matched by the repo-path pin.
    /// </summary>
    private static bool IsTrustedDownloadHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns whether <paramref name="bytes"/> begins with the "MZ" Portable Executable
    /// signature, a cheap sanity check that we downloaded a binary and not an HTML or
    /// JSON error response that happened to be large.
    /// </summary>
    private static bool HasExecutableHeader(byte[] bytes) =>
        bytes.Length >= PortableExecutableMagic.Length
        && bytes[0] == PortableExecutableMagic[0]
        && bytes[1] == PortableExecutableMagic[1];

    /// <summary>
    /// Locks down the staging folder (and the staged installer) so a limited child
    /// cannot write, swap or delete the file this SYSTEM process will later execute.
    /// Drops ACL inheritance — otherwise the update folder keeps the Users=Modify ACE
    /// the installer grants on %ProgramData%\Curfew — and adds an explicit deny for the
    /// Users group, while SYSTEM and Administrators keep full control. Mirrors
    /// <see cref="ConfigFileGuard"/>; best-effort and Windows-only, a failure is logged.
    /// </summary>
    /// <param name="path">The directory or file to protect.</param>
    /// <param name="isDirectory">
    /// When true the deny is made inheritable so files later created in the folder
    /// (a swapped-in payload, the install-result marker) cannot be child-written; the
    /// SYSTEM scheduled task still writes its marker because SYSTEM keeps full control.
    /// </param>
    private static void ProtectFromChild(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            // A directory's allow/deny rules propagate to the files inside it; a file's
            // do not inherit anywhere. Match the inheritance scope to the target kind.
            var inherit = isDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;

            if (isDirectory)
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.SetOwner(system);
                security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                // Deny wins over allow: the child can neither replace the staged exe nor
                // create new files in the folder to redirect the install.
                security.AddAccessRule(new FileSystemAccessRule(
                    users,
                    FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                    inherit, PropagationFlags.None, AccessControlType.Deny));
                new DirectoryInfo(path).SetAccessControl(security);
            }
            else
            {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.SetOwner(system);
                security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    users,
                    FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                    AccessControlType.Deny));
                new FileInfo(path).SetAccessControl(security);
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"update staging guard: {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
