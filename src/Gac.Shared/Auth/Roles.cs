namespace Gac.Shared.Auth;

public enum Role { Owner, Admin, Operator, Viewer }

public enum Permission
{
    ViewPcs,
    ViewReports,
    ViewAudit,
    RunModules,
    RunDeployScripts,
    UninstallPrograms,
    ManageGroups,
    ManagePolicies,
    ManagePresets,
    ManagePackages,
    ManageAccounts,
    ManageServerSettings,
    /// <summary>CHANGE-001 2.4: unlock a PC that is locked for a period. Default Owner/Admin/Operator.</summary>
    UnlockPc,
    /// <summary>CHANGE-001 1.2/1.4: approve/revoke/block devices, clear console-first. Default Owner/Admin.</summary>
    ManageDevices,
    /// <summary>CHANGE-002 §2.2: add/edit/delete lock exemption days (캘린더 관리). Default Owner/Admin.</summary>
    ManageCalendar,
    /// <summary>CHANGE-002 §4.2: change the server PC auto-shutdown schedule, skip today, extend (서버 PC 종료 설정). Default Owner/Admin.</summary>
    ManageServerShutdown,
}

/// <summary>The fixed role → permission matrix from CLAUDE.md section 5. Enforced on the server; the console only mirrors it.</summary>
public static class RolePermissions
{
    private static readonly Dictionary<Role, HashSet<Permission>> Map = new()
    {
        [Role.Owner] = [.. Enum.GetValues<Permission>()],
        [Role.Admin] = [.. Enum.GetValues<Permission>().Where(p => p is not (Permission.ManageAccounts or Permission.ManageServerSettings))],
        [Role.Operator] = [Permission.ViewPcs, Permission.ViewReports, Permission.RunModules, Permission.UnlockPc],
        [Role.Viewer] = [Permission.ViewPcs, Permission.ViewReports],
    };

    public static bool Has(Role role, Permission permission) => Map[role].Contains(permission);

    /// <summary>The default permission set for a role (starting point the Owner can edit, except Owner which is fixed).</summary>
    public static IReadOnlySet<Permission> Default(Role role) => Map[role];

    /// <summary>Permissions the Owner is allowed to toggle for non-Owner roles (account/server management stay Owner-only).</summary>
    public static readonly IReadOnlyList<Permission> Editable =
        [.. Enum.GetValues<Permission>().Where(p => p is not (Permission.ManageAccounts or Permission.ManageServerSettings))];

    /// <summary>Operators are limited to their assigned groups; everyone else sees all PCs.</summary>
    public static bool IsGroupScoped(Role role) => role == Role.Operator;

    public static string KoreanLabel(Role role) => role switch
    {
        Role.Owner => "최고 관리자",
        Role.Admin => "관리자",
        Role.Operator => "운영자",
        Role.Viewer => "조회 전용",
        _ => role.ToString(),
    };

    /// <summary>Permission required to send a given module action.</summary>
    public static Permission RequiredFor(string module, string action) => (module, action) switch
    {
        (ModuleIds.Deploy, ModuleActions.DeployRunScript) => Permission.RunDeployScripts,
        (ModuleIds.Inspect, ModuleActions.InspectUninstall) => Permission.UninstallPrograms,
        _ => Permission.RunModules,
    };
}
