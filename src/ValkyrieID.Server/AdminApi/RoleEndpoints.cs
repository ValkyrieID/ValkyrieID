using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;

namespace ValkyrieID.Server.AdminApi;

internal sealed record RoleDto(Guid Id, string Name, string? Description, bool BuiltIn, int Members);

internal sealed record GroupDto(Guid Id, string Name, string? Description, int Members);

internal sealed record NameRequest(string Name, string? Description = null);

internal static class RoleEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var roles = api.MapGroup("/roles").WithTags("Roles");
        roles.MapGet("/", ListRolesAsync);
        roles.MapPost("/", async Task<Created<RoleDto>> (NameRequest request, AdminCaller caller, RoleAdministration admin, ValkyrieDbContext db, CancellationToken ct) =>
        {
            var role = await admin.CreateRoleAsync(request.Name, request.Description, caller.Actor, ct);
            return TypedResults.Created($"{AdminApiAuthentication.PathPrefix}/roles/{role.Id}", await RoleAsync(db, role.Id, ct));
        });
        roles.MapGet("/{id:guid}", async Task<Ok<RoleDto>> (Guid id, ValkyrieDbContext db, CancellationToken ct) =>
            TypedResults.Ok(await RoleAsync(db, id, ct)));
        roles.MapPut("/{id:guid}", async Task<Ok<RoleDto>> (Guid id, NameRequest request, AdminCaller caller, RoleAdministration admin, ValkyrieDbContext db, CancellationToken ct) =>
        {
            var role = AdminRequests.Found(await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct), "role");
            await admin.UpdateRoleAsync(role, request.Name, request.Description, caller.Actor, ct);
            return TypedResults.Ok(await RoleAsync(db, id, ct));
        });
        roles.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, AdminCaller caller, RoleAdministration admin, ValkyrieDbContext db, CancellationToken ct) =>
        {
            var role = AdminRequests.Found(await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct), "role");
            await admin.DeleteRoleAsync(role, caller.Actor, ct);
            return TypedResults.NoContent();
        });

        var groups = api.MapGroup("/groups").WithTags("Groups");
        groups.MapGet("/", ListGroupsAsync);
        groups.MapPost("/", async Task<Created<GroupDto>> (NameRequest request, AdminCaller caller, RoleAdministration admin, ValkyrieDbContext db, CancellationToken ct) =>
        {
            var group = await admin.CreateGroupAsync(request.Name, request.Description, caller.Actor, ct);
            return TypedResults.Created($"{AdminApiAuthentication.PathPrefix}/groups/{group.Id}", await GroupAsync(db, group.Id, ct));
        });
        groups.MapGet("/{id:guid}", async Task<Ok<GroupDto>> (Guid id, ValkyrieDbContext db, CancellationToken ct) =>
            TypedResults.Ok(await GroupAsync(db, id, ct)));
        groups.MapPut("/{id:guid}", async Task<Ok<GroupDto>> (Guid id, NameRequest request, AdminCaller caller, RoleAdministration admin, ValkyrieDbContext db, CancellationToken ct) =>
        {
            var group = AdminRequests.Found(await db.Groups.SingleOrDefaultAsync(g => g.Id == id, ct), "group");
            await admin.UpdateGroupAsync(group, request.Name, request.Description, caller.Actor, ct);
            return TypedResults.Ok(await GroupAsync(db, id, ct));
        });
        groups.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, AdminCaller caller, RoleAdministration admin, ValkyrieDbContext db, CancellationToken ct) =>
        {
            var group = AdminRequests.Found(await db.Groups.SingleOrDefaultAsync(g => g.Id == id, ct), "group");
            await admin.DeleteGroupAsync(group, caller.Actor, ct);
            return TypedResults.NoContent();
        });
    }

    private static async Task<Ok<Page<RoleDto>>> ListRolesAsync(ValkyrieDbContext db, int? limit, int? offset, string? q, CancellationToken ct)
    {
        var (take, skip, query) = AdminRequests.Paging(limit, offset, q);
        var roles = db.Roles.AsNoTracking();
        if (query is not null)
        {
            var lower = query.ToLowerInvariant();
            roles = roles.Where(r => r.Name.ToLower().Contains(lower));
        }

        var total = await roles.CountAsync(ct);
        var page = await RoleDtos(db, roles.OrderBy(r => r.Name).Skip(skip).Take(take)).ToListAsync(ct);
        return TypedResults.Ok(new Page<RoleDto>(page, total));
    }

    private static async Task<Ok<Page<GroupDto>>> ListGroupsAsync(ValkyrieDbContext db, int? limit, int? offset, string? q, CancellationToken ct)
    {
        var (take, skip, query) = AdminRequests.Paging(limit, offset, q);
        var groups = db.Groups.AsNoTracking();
        if (query is not null)
        {
            var lower = query.ToLowerInvariant();
            groups = groups.Where(g => g.Name.ToLower().Contains(lower));
        }

        var total = await groups.CountAsync(ct);
        var page = await GroupDtos(db, groups.OrderBy(g => g.Name).Skip(skip).Take(take)).ToListAsync(ct);
        return TypedResults.Ok(new Page<GroupDto>(page, total));
    }

    private static async Task<RoleDto> RoleAsync(ValkyrieDbContext db, Guid id, CancellationToken ct) =>
        AdminRequests.Found(await RoleDtos(db, db.Roles.AsNoTracking().Where(r => r.Id == id)).SingleOrDefaultAsync(ct), "role");

    private static async Task<GroupDto> GroupAsync(ValkyrieDbContext db, Guid id, CancellationToken ct) =>
        AdminRequests.Found(await GroupDtos(db, db.Groups.AsNoTracking().Where(g => g.Id == id)).SingleOrDefaultAsync(ct), "group");

    private static IQueryable<RoleDto> RoleDtos(ValkyrieDbContext db, IQueryable<Role> roles) =>
        roles.Select(r => new RoleDto(r.Id, r.Name, r.Description, r.Id == Role.AdminRoleId, db.Users.Count(u => u.Roles.Any(x => x.Id == r.Id))));

    private static IQueryable<GroupDto> GroupDtos(ValkyrieDbContext db, IQueryable<Group> groups) =>
        groups.Select(g => new GroupDto(g.Id, g.Name, g.Description, db.Users.Count(u => u.Groups.Any(x => x.Id == g.Id))));
}
