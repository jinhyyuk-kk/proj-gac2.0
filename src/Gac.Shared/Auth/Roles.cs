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
}

/// <summary>The fixed role → permission matrix from CLAUDE.md section 5. Enforced on the server; the console only mirrors it.</summary>
public static class RolePermissions
{
    private static readonly Dictionary<Role, HashSet<Permission>> Map = new()
    {
        [Role.Owner] = [.. Enum.GetValues<Permission>()],
        [Role.Admin] = [.. Enum.GetValues<Permission>().Where(p => p is not (Permission.ManageAccounts or Permission.ManageServerSettings))],
        [Role.Operator] = [Permission.ViewPcs, Permission.ViewReports, Permission.RunModules],
        [Role.Viewer] = [Permission.ViewPcs, Permission.ViewReports],
    };

    public static bool Has(Role role, Permission permission) => Map[role].Contains(permission);

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
