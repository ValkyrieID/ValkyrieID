using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Administration;

/// <summary>
/// Role and group management. Names are emitted in the <c>roles</c> and <c>groups</c> claims. The built-in
/// <c>admin</c> role grants access to administration and can't be renamed or deleted.
/// </summary>
internal sealed class RoleAdministration(ValkyrieDbContext db, AuditRecorder audit, TimeProvider timeProvider)
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 200;

    public async Task<Role> CreateRoleAsync(string name, string? description, AuditActor actor, CancellationToken cancellationToken)
    {
        (name, description) = Validate(name, description);
        await EnsureUniqueAsync(db.Roles.Where(r => r.Name.ToLower() == name.ToLower()).Select(r => r.Id), null, "role", cancellationToken);
        var role = new Role { Name = name, Description = description };
        db.Roles.Add(role);
        Audit("role.created", actor, role.Name);
        await db.SaveChangesAsync(cancellationToken);
        return role;
    }

    /// <summary>Renaming a role takes effect immediately: its members' sessions must re-authenticate.</summary>
    public async Task UpdateRoleAsync(Role role, string name, string? description, AuditActor actor, CancellationToken cancellationToken)
    {
        (name, description) = Validate(name, description);
        if (role.Id == Role.AdminRoleId && name != role.Name)
        {
            throw AdministrationException.Conflict($"The built-in '{Role.Admin}' role can't be renamed.");
        }

        await EnsureUniqueAsync(db.Roles.Where(r => r.Name.ToLower() == name.ToLower()).Select(r => r.Id), role.Id, "role", cancellationToken);
        if (name == role.Name && description == role.Description)
        {
            return;
        }

        var renamed = name != role.Name;
        var oldName = role.Name;
        role.Name = name;
        role.Description = description;
        if (renamed)
        {
            await TouchMembersAsync(role.Id, cancellationToken);
        }

        Audit("role.updated", actor, name, renamed ? new() { ["old_name"] = oldName } : null);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Deletes a role and its assignments; its members' sessions must re-authenticate.</summary>
    public async Task DeleteRoleAsync(Role role, AuditActor actor, CancellationToken cancellationToken)
    {
        if (role.Id == Role.AdminRoleId)
        {
            throw AdministrationException.Conflict($"The built-in '{Role.Admin}' role can't be deleted.");
        }

        await TouchMembersAsync(role.Id, cancellationToken);
        db.Roles.Remove(role);
        Audit("role.deleted", actor, role.Name);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Group> CreateGroupAsync(string name, string? description, AuditActor actor, CancellationToken cancellationToken)
    {
        (name, description) = Validate(name, description);
        await EnsureUniqueAsync(db.Groups.Where(g => g.Name.ToLower() == name.ToLower()).Select(g => g.Id), null, "group", cancellationToken);
        var group = new Group { Name = name, Description = description };
        db.Groups.Add(group);
        Audit("group.created", actor, group.Name);
        await db.SaveChangesAsync(cancellationToken);
        return group;
    }

    public async Task UpdateGroupAsync(Group group, string name, string? description, AuditActor actor, CancellationToken cancellationToken)
    {
        (name, description) = Validate(name, description);
        await EnsureUniqueAsync(db.Groups.Where(g => g.Name.ToLower() == name.ToLower()).Select(g => g.Id), group.Id, "group", cancellationToken);
        if (name == group.Name && description == group.Description)
        {
            return;
        }

        var oldName = group.Name;
        group.Name = name;
        group.Description = description;
        Audit("group.updated", actor, name, oldName != name ? new() { ["old_name"] = oldName } : null);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteGroupAsync(Group group, AuditActor actor, CancellationToken cancellationToken)
    {
        db.Groups.Remove(group);
        Audit("group.deleted", actor, group.Name);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task TouchMembersAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var user in await db.Users.Where(u => u.Roles.Any(r => r.Id == roleId)).ToListAsync(cancellationToken))
        {
            user.UpdatedAt = now;
            user.SecurityStamp = User.NewSecurityStamp();
        }
    }

    private static (string Name, string? Description) Validate(string name, string? description)
    {
        List<string> errors = [];
        name = name.Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            errors.Add($"The name must be 1-{MaxNameLength} characters without control characters.");
        }

        if (description?.Length > MaxDescriptionLength || (description?.Any(char.IsControl) ?? false))
        {
            errors.Add($"The description must be at most {MaxDescriptionLength} characters without control characters.");
        }

        return errors.Count > 0 ? throw new AdministrationException(errors) : (name, description);
    }

    private static async Task EnsureUniqueAsync(IQueryable<Guid> matches, Guid? self, string kind, CancellationToken cancellationToken)
    {
        var exists = self is { } id
            ? await matches.AnyAsync(m => m != id, cancellationToken)
            : await matches.AnyAsync(cancellationToken);
        if (exists)
        {
            throw AdministrationException.Conflict($"A {kind} with this name already exists.");
        }
    }

    private void Audit(string eventType, AuditActor actor, string name, Dictionary<string, object?>? details = null)
    {
        details ??= [];
        details["name"] = name;
        audit.Add(eventType, actor: actor.Name, ipAddress: actor.IpAddress, details: details);
    }
}
