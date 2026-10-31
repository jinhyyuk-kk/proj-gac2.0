using System.Security.AccessControl;
using System.Security.Principal;

namespace Gac.Shared.Security;

/// <summary>
/// Creates program-data folders with a protected DACL (SYSTEM and Administrators only, inherited by children).
/// A folder that is a reparse point or is owned by a non-admin (someone pre-created it to plant files or read secrets)
/// is removed and recreated. After creation the result is verified; if it is still not trustworthy an exception is thrown
/// and the caller refuses to run. Only applies when elevated; development runs are left alone.
/// </summary>
public static class SecureDirectory
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void Ensure(string path)
    {
        if (!IsElevated()) { Directory.CreateDirectory(path); return; }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var info = new DirectoryInfo(path);
            if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsTrustedOwner(info)))
            {
                // Directory.Delete on a junction/symlink removes only the link itself.
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete();
                else info.Delete(recursive: true);
                info.Refresh();
            }

            var sec = ProtectedSecurity();
            if (!info.Exists) info.Create(sec);
            info.Refresh();
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsTrustedOwner(info)) continue; // lost a race; retry

            try { sec.SetOwner(Admins); info.SetAccessControl(sec); }
            catch (InvalidOperationException) { info.SetAccessControl(ProtectedSecurity()); } // owner change not permitted: DACL only
            catch (UnauthorizedAccessException) { info.SetAccessControl(ProtectedSecurity()); }

            if (Verify(info)) return;
        }
        throw new UnauthorizedAccessException($"Could not secure directory '{path}'. Refusing to use it.");
    }

    private static DirectorySecurity ProtectedSecurity()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System, Admins })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    private static bool IsTrustedOwner(DirectoryInfo info)
    {
        var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner != null && (owner.Equals(System) || owner.Equals(Admins));
    }

    /// <summary>Owner is SYSTEM/Administrators, DACL is protected and only grants access to SYSTEM/Administrators.</summary>
    private static bool Verify(DirectoryInfo info)
    {
        info.Refresh();
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsTrustedOwner(info)) return false;
        var acl = info.GetAccessControl();
        if (!acl.AreAccessRulesProtected) return false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!sid.Equals(System) && !sid.Equals(Admins)) return false;
        }
        return true;
    }
}
