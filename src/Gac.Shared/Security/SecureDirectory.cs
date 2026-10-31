using System.Security.AccessControl;
using System.Security.Principal;

namespace Gac.Shared.Security;

/// <summary>
/// Creates program-data folders with a protected DACL (SYSTEM and Administrators only, inherited by children).
/// If a folder already exists but is owned by a non-admin (someone pre-created it to plant files or read secrets),
/// it is deleted and recreated. Only applies when elevated; development runs are left alone.
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

        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || !(owner.Equals(System) || owner.Equals(Admins)))
            {
                info.Delete(recursive: true);
                info.Refresh();
            }
        }

        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System, Admins })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

        if (!info.Exists) info.Create(sec);
        else info.SetAccessControl(sec);
    }
}
