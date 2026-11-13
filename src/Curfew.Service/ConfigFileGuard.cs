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

    /// <summary>Create (if needed) and lock the service-private directory: SYSTEM owner, SYSTEM + Administrators only, no inheritance. Ordinary users can't read secrets.db or touch the blocklist cache. Returns false if isolation couldn't be established (caller must not store secrets there).</summary>
    public static bool ProtectSecureDirectory(string dir)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateDirectory(dir); return true; }
        try
        {
            var info = new DirectoryInfo(dir);
            // a pre-existing reparse point (child-planted junction) is never trusted
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
                info.Refresh();
            }

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(system);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            if (!info.Exists) info = System.IO.FileSystemAclExtensions.CreateDirectory(security, dir);
            info.SetAccessControl(security);

            // children (anything a child pre-planted) re-inherit the fresh DACL + get SYSTEM owner
            foreach (var entry in info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry is FileInfo f)
                {
                    var fs = new FileSecurity();
                    fs.SetOwner(system);
                    fs.SetAccessRuleProtection(false, false);
                    f.SetAccessControl(fs);
                }
            }

            // verify: owner + no ACE for anyone but SYSTEM/Administrators
            var check = info.GetAccessControl();
            if (check.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !owner.Equals(system)) return false;
            foreach (FileSystemAccessRule rule in check.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(system) && !rule.IdentityReference.Equals(admins))
                    return false;
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"config guard: secure dir: {ex.Message}");
            return false;
        }
    }

    /// <summary>events.log: users may READ and APPEND (overlay/app record events) but not overwrite, truncate, rename or delete - the audited account can't erase its own tamper history. Inheritance dropped so the data dir's Users-Modify (which carries Delete) doesn't apply.</summary>
    public static void ProtectEventLog(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!File.Exists(path)) File.Create(path).Dispose();

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(system);
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users,
                FileSystemRights.Read | FileSystemRights.AppendData | FileSystemRights.Synchronize, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"config guard: events.log: {ex.Message}");
        }
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
