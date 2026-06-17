using System.Security.AccessControl;
using System.Security.Principal;
using Curfew.Core;
using Curfew.Core.Security;

namespace Curfew.Service;

/// <summary>Check for and apply Curfew updates from background worker.</summary>
/// <remarks>
/// <para>Flow: ask <see cref="Updater"/> if newer release exists, download installer to staging, hand off to detached one-shot SYSTEM scheduled task (built by <see cref="Updater.BuildScheduledInstallScript"/>).</para>
/// <para>Install from detached task (not here) is the point: installer stops/restarts this service mid-update, so any process we own dies before install finishes. Scheduled task outlives us and self-cleans.</para>
/// <para>Update is best-effort, never destabilise service. Only cancellation (shutdown) propagates; every other failure logged and swallowed so next six-hourly pass retries.</para>
/// </remarks>
internal static class UpdateService
{
    /// <summary>Settings flag gating auto updates; absent means "enabled".</summary>
    private const string AutoUpdateEnabledKey = "auto_update_enabled";

    /// <summary>Settings key choosing update channel; <see cref="PrereleaseChannel"/> opts into pre-releases.</summary>
    private const string UpdateChannelKey = "update_channel";

    /// <summary>Value of <see cref="UpdateChannelKey"/> including pre-releases.</summary>
    private const string PrereleaseChannel = "prerelease";

    /// <summary>File name installer is staged under in update folder.</summary>
    private const string InstallerFileName = "curfew-update.exe";

    /// <summary>Min bytes a download must reach to count as real installer. GitHub error pages, rate-limit notices, truncated downloads far smaller than genuine multi-MB setup, so below this rejected.</summary>
    private const int MinimumInstallerBytes = 500_000;

    /// <summary>Max download bytes so malformed/hostile <c>Content-Length</c> cannot make us buffer unbounded into memory.</summary>
    private const long MaximumInstallerBytes = 256L * 1024 * 1024;

    /// <summary>How long installer download may run before abandoned.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Two-byte "MZ" signature beginning every Windows PE executable.</summary>
    private static readonly byte[] PortableExecutableMagic = { 0x4D, 0x5A };

    /// <summary>Run one update pass: check for newer release, if found and downloadable schedule silent install. No-op when auto-update disabled or no newer release.</summary>
    /// <param name="settings">Open settings store; read for auto-update flag.</param>
    /// <param name="currentVersion">Currently installed version (e.g. "2.0.0").</param>
    /// <param name="ct">Cancels check, download, and (cooperatively) the pass.</param>
    /// <exception cref="OperationCanceledException">Service shutting down.</exception>
    public static async Task RunAsync(SettingsStore settings, string currentVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.GetBool(AutoUpdateEnabledKey, true)) return;

        ReportPreviousInstallResult();

        // pre-releases auto-installed only when parent opts into that channel
        var includePrereleases = settings.Get(UpdateChannelKey) == PrereleaseChannel;
        var release = await Updater.CheckForUpdateAsync(currentVersion, Updater.HttpFetchAsync, includePrereleases, ct,
                onCheckFailure: reason => ServiceLog.Write($"update check failed: {reason}"))
            .ConfigureAwait(false);
        if (release is null) return;

        ServiceLog.Write($"update available: {release.Value.Tag} (current {currentVersion})");

        var installer = await DownloadInstallerAsync(release.Value.InstallerUrl, ct).ConfigureAwait(false);
        if (installer is null) return;

        // BuildScheduledInstallScript validates path; guard vs throw so bad path cannot take down update pass
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

    /// <summary>Log (and clear) exit code previous scheduled install left behind. Install runs as detached one-shot task, so this marker is the only place a failed silent install (locked files, disk full, another installer running) surfaces.</summary>
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

    /// <summary>Download installer at <paramref name="url"/> into staging, return its path, or <see langword="null"/> if download missing, too small, too large, or not a Windows executable.</summary>
    /// <param name="url">Installer asset download URL.</param>
    /// <param name="ct">Cancels download.</param>
    /// <returns>Staged installer path, or <see langword="null"/> on any failure.</returns>
    /// <exception cref="OperationCanceledException">Service shutting down.</exception>
    private static async Task<string?> DownloadInstallerAsync(string url, CancellationToken ct)
    {
        // re-pin URL before this SYSTEM process fetches: must be HTTPS GitHub release path of THIS repo (not just any "curfew-setup.exe"). parse already enforced, but updater runs as SYSTEM and later runs payload, so re-check vs trusting caller
        if (!ReleaseInfo.IsInstallerUrl(url))
        {
            ServiceLog.Write("update download rejected: untrusted installer URL");
            return null;
        }

        // dedicated client with explicit timeout: updater download must never stall service for default (effectively unbounded) duration
        using var client = new HttpClient { Timeout = DownloadTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("curfew-updater");

        try
        {
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // GitHub redirects release path to *.githubusercontent.com; ensure redirect chain never lands on some other host before we save and (later) run payload as SYSTEM
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsTrustedDownloadHost(finalUri))
            {
                ServiceLog.Write($"update download rejected: redirected to untrusted host {finalUri.Host}");
                return null;
            }

            // reject over-large body up front using advertised length, before reading a byte
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

            // stage installer where child cannot tamper. update folder inherits Users=Modify from %ProgramData%\Curfew (installer grants it so state.db + SQLite sidecars stay writable), which would let limited child overwrite staged exe AFTER Verify() but BEFORE detached SYSTEM task opens it — signature TOCTOU landing attacker code with SYSTEM. drop dir inheritance + remove Users write/delete before writing payload, then lock file same way and verify AFTER lockdown, so bytes task runs are exactly bytes that passed signature check
            if (!TryPrepareStagingDir(CurfewPaths.UpdateDirectory))
            {
                ServiceLog.Write("update download rejected: staging folder is a reparse point (possible junction redirect)");
                return null;
            }

            var path = Path.Combine(CurfewPaths.UpdateDirectory, InstallerFileName);

            // CreateNew + FileShare.None: no other handle may write/delete file while we hold it, and child-left decoy rejected not appended. stale exe from previous pass cleared first
            try { File.Delete(path); } catch { /* may not exist; recreated below */ }
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            // lock file with same deny-ACE as dir, no longer child-writable once handle closed. must run before verify: Verify() racing a still-writable file proves nothing. fail closed: if hardening fails the file stays child-writable, so discard rather than verify+run it
            if (!ProtectFromChild(path, isDirectory: false))
            {
                ServiceLog.Write("update download rejected: could not lock down staged installer");
                try { File.Delete(path); } catch { /* best effort */ }
                return null;
            }

            // last defence before SYSTEM schedules installer: must be Authenticode-signed by Curfew's key. anything else (unsigned, tampered, other key) discarded. run AFTER lockdown so verified bytes are bytes detached task opens; child can no longer swap in the window
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
            // shutdown: caller decision, not failed update — let it surface
            throw;
        }
        catch (Exception ex)
        {
            // network error, HTTP fail, timeout, disk error — all mean "no update this pass"; retry in six hours
            ServiceLog.Write($"update download failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Read response body into memory, stop (return <see langword="null"/>) if it exceeds <see cref="MaximumInstallerBytes"/>. Guards a server streaming more than its <c>Content-Length</c> claimed, or none at all.</summary>
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

    /// <summary>Whether <paramref name="uri"/> is HTTPS on GitHub or its asset CDN. Validates final URL after redirects, which land on <c>*.githubusercontent.com</c> so cannot match the repo-path pin.</summary>
    private static bool IsTrustedDownloadHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="bytes"/> begins with "MZ" Portable Executable signature; cheap check we downloaded a binary not a large HTML/JSON error response.</summary>
    private static bool HasExecutableHeader(byte[] bytes) =>
        bytes.Length >= PortableExecutableMagic.Length
        && bytes[0] == PortableExecutableMagic[0]
        && bytes[1] == PortableExecutableMagic[1];

    /// <summary>Prepare the update staging folder as a real, SYSTEM-owned, child-proof directory. A child can
    /// pre-create <c>%ProgramData%\Curfew\update</c> as a junction/symlink so the SYSTEM service stages (and
    /// later runs) the installer through a path the child redirects — LPE to SYSTEM. We delete any reparse
    /// link found (this removes the link only, never a target's contents), recreate a real directory, lock it
    /// down, then re-check and fail closed if it is still/again a reparse point.</summary>
    /// <returns><see langword="true"/> when the folder is a real, locked directory safe to stage into.</returns>
    private static bool TryPrepareStagingDir(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(dir); // removes the junction/symlink link itself, not the target

            Directory.CreateDirectory(dir);
            // fail closed: if ACL hardening fails the dir keeps the inherited Users=Modify ACE, leaving the
            // TOCTOU window open, so abort rather than stage into it
            if (!ProtectFromChild(dir, isDirectory: true))
            {
                ServiceLog.Write("update staging dir prepare: could not lock down staging directory");
                return false;
            }

            // re-check after lockdown: a child racing to swap a junction back in between delete and create
            // would be caught here, so we never stage through a redirected path
            info.Refresh();
            return !(info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"update staging dir prepare: {ex.Message}");
            return false;
        }
    }

    /// <summary>Lock down staging folder (and staged installer) so limited child cannot write/swap/delete the file SYSTEM later runs. Drop ACL inheritance (else folder keeps Users=Modify ACE installer grants on %ProgramData%\Curfew) and grant Users no ACE at all, while SYSTEM + Administrators keep full control. Mirrors <see cref="ConfigFileGuard"/>; best-effort, Windows-only, failure logged.</summary>
    /// <param name="path">Directory or file to protect.</param>
    /// <param name="isDirectory">When true deny is inheritable so files later created in folder (swapped-in payload, install-result marker) cannot be child-written; SYSTEM scheduled task still writes its marker because SYSTEM keeps full control.</param>
    /// <returns><see langword="true"/> when the ACL was applied (or non-Windows, nothing to do);
    /// <see langword="false"/> when hardening failed, so callers can fail closed instead of staging through a
    /// still-child-writable path.</returns>
    private static bool ProtectFromChild(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            // dir allow rules propagate to files inside; a file's do not inherit anywhere. match inheritance scope to target kind
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
                // inheritance is off so the parent dir's Users-write ACE doesn't propagate; granting Users no
                // ACE at all means the child can neither replace the staged exe nor create files to redirect
                // the install. NO explicit Deny on Users: the parent's admin account is a member of Users and
                // a Deny would win over the Administrators Allow above, blocking admin cleanup/uninstall.
                new DirectoryInfo(path).SetAccessControl(security);
            }
            else
            {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.SetOwner(system);
                security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
                // no Users ACE (inheritance off) -> child can't write/swap/delete the staged exe. No explicit
                // Deny on Users, which would also block the parent's admin account (member of Users, Deny wins).
                new FileInfo(path).SetAccessControl(security);
            }
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"update staging guard: {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }
}
