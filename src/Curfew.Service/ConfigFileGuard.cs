using System.Security.AccessControl;
using System.Security.Principal;

namespace Curfew.Service;

/// <summary>lock down config.db: users read but can't write/delete; state.db + data dir stay writable for child counters. applied by SYSTEM service after creating config.db on boot</summary>
/// <remarks>inheritance dropped (so the dir's Users-write ACE doesn't apply) and Users granted Read only, which implicitly denies write/delete without an explicit Deny ACE. SYSTEM + Administrators keep full control. An explicit Deny on Users would also block the parent's admin account (admins are members of Users and Deny wins over Allow), so it is avoided. best-effort + Windows-only; failure logged not thrown</remarks>
internal static class ConfigFileGuard
{
    public static void Protect(string configPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            return;

        // rollback journal sidecar holds recently-committed data + inherits dir's Users-write ACE; unprotected = child gets writable copy of config the main ACL guards. pre-create empty (zero-length journal "not hot" to SQLite) so deny lands before first write. -wal/-shm NOT touched: only exist until WAL→PERSIST conversion, and ACL'ing -shm breaks child read-only opens meanwhile (WAL readers write shared-memory index)
        try
        {
            if (!File.Exists(configPath + "-journal"))
                File.Create(configPath + "-journal").Dispose();
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"config guard: journal precreate: {ex.Message}");
        }

        ProtectFile(configPath);
        ProtectFile(configPath + "-journal");
    }

    private static void ProtectFile(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            var security = new FileSecurity();
            // drop inheritance so dir's Users-write ACE doesn't apply here
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(system);

            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            // inheritance is off (above) so the dir's Users-write ACE doesn't reach here; granting Users
            // only Read means they implicitly cannot write/delete. NO explicit Deny on Users: every account
            // — including the parent's admin account — is a member of Users, and a Deny ACE wins over the
            // Administrators FullControl Allow, which would lock admins out of repairing/removing config.db.
            security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Read, AccessControlType.Allow));

            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"config guard: {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
