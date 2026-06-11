using System.Security.AccessControl;
using System.Security.Principal;

namespace Curfew.Service;

/// <summary>
/// Locks down config.db so ordinary users can read it but cannot write or delete
/// it, while state.db (and the data directory) stay writable for the child-side
/// counters. Applied by the SYSTEM service after it creates config.db on boot.
/// </summary>
/// <remarks>
/// Uses an explicit <see cref="AccessControlType.Deny"/> rule for the Users group
/// rather than relying on the directory ACL, so a child cannot rewrite the file or
/// delete-and-recreate it even though the directory itself permits writes (which
/// state.db and SQLite's sidecar files need). SYSTEM and Administrators keep full
/// control. Best-effort and Windows-only; a failure is logged, not thrown.
/// </remarks>
internal static class ConfigFileGuard
{
    public static void Protect(string configPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            return;

        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            var security = new FileSecurity();
            // Drop inheritance so the directory's Users-write ACE does not apply here.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(system);

            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Read, AccessControlType.Allow));
            // Deny wins over allow: users can read, never write or delete.
            security.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                AccessControlType.Deny));

            new FileInfo(configPath).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"config guard: {ex.Message}");
        }
    }
}
