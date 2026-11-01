namespace Gac.Shared.Auth;

/// <summary>
/// The Owner-editable role → permission map (CHANGE-001 section 5). Owner is fixed (everything). For the other roles the
/// Owner may toggle any <see cref="RolePermissions.Editable"/> permission; account/server management can never be granted
/// to a non-Owner. Stored on the server; resolved into each session's effective permissions on every request.
/// </summary>
public sealed class PermissionMatrix
{
    public Dictionary<Role, List<Permission>> Roles { get; set; } = new();

    public static PermissionMatrix Default() => new()
    {
        Roles = new()
        {
            [Role.Admin] = [.. RolePermissions.Default(Role.Admin)],
            [Role.Operator] = [.. RolePermissions.Default(Role.Operator)],
            [Role.Viewer] = [.. RolePermissions.Default(Role.Viewer)],
        },
    };

    public IReadOnlySet<Permission> Effective(Role role)
    {
        if (role == Role.Owner) return RolePermissions.Default(Role.Owner);
        var set = Roles.TryGetValue(role, out var list) ? new HashSet<Permission>(list) : new HashSet<Permission>(RolePermissions.Default(role));
        set.RemoveWhere(p => !RolePermissions.Editable.Contains(p)); // never grant Owner-only powers to another role
        return set;
    }

    public void Set(Role role, Permission permission, bool granted)
    {
        if (role == Role.Owner || !RolePermissions.Editable.Contains(permission)) return;
        var list = Roles.TryGetValue(role, out var l) ? l : (Roles[role] = [.. RolePermissions.Default(role)]);
        if (granted) { if (!list.Contains(permission)) list.Add(permission); }
        else list.Remove(permission);
    }
}
