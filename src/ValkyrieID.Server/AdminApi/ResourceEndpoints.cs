using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Resources;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;

namespace ValkyrieID.Server.AdminApi;

internal sealed record ScopeDto(Guid Id, string Name, string? Description);

internal sealed record ResourceDto(
    Guid Id,
    string Name,
    string? DisplayName,
    bool Enabled,
    bool BuiltIn,
    bool AllowClientSecretPost,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ScopeDto> Scopes,
    IReadOnlyList<SecretDto> Secrets);

internal sealed record ScopeRequest(string Name, string? Description = null);

internal sealed record CreateResourceRequest(string Name, IReadOnlyList<ScopeRequest> Scopes, string? DisplayName = null);

internal sealed record UpdateResourceRequest(string? DisplayName, bool AllowClientSecretPost);

internal sealed record UpdateScopeRequest(string? Description);

internal static class ResourceEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var resources = api.MapGroup("/resources").WithTags("API resources");
        resources.MapGet("/", ListAsync);
        resources.MapPost("/", async Task<Created<ResourceDto>> (CreateResourceRequest request, AdminCaller caller, ResourceAdministration admin, TimeProvider time, CancellationToken ct) =>
        {
            var resource = await admin.CreateAsync(request.Name, request.DisplayName, [.. request.Scopes.Select(s => new NewScope(s.Name, s.Description))], caller.Actor, ct);
            return TypedResults.Created($"{AdminApiAuthentication.PathPrefix}/resources/{resource.Id}", Dto(resource, time.GetUtcNow()));
        });
        resources.MapGet("/{id:guid}", async Task<Ok<ResourceDto>> (Guid id, ResourceAdministration admin, TimeProvider time, CancellationToken ct) =>
            TypedResults.Ok(Dto(await FindAsync(admin, id, ct), time.GetUtcNow())));
        resources.MapPut("/{id:guid}", (Guid id, UpdateResourceRequest request, AdminCaller caller, ResourceAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, r => admin.UpdateAsync(r, request.DisplayName, request.AllowClientSecretPost, caller.Actor, ct)));
        resources.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, AdminCaller caller, ResourceAdministration admin, CancellationToken ct) =>
        {
            await admin.DeleteAsync(await FindAsync(admin, id, ct), caller.Actor, ct);
            return TypedResults.NoContent();
        });
        resources.MapPost("/{id:guid}/enable", (Guid id, AdminCaller caller, ResourceAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, r => admin.SetEnabledAsync(r, true, caller.Actor, ct)));
        resources.MapPost("/{id:guid}/disable", (Guid id, AdminCaller caller, ResourceAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, r => admin.SetEnabledAsync(r, false, caller.Actor, ct)));
        resources.MapPost("/{id:guid}/scopes", (Guid id, ScopeRequest request, AdminCaller caller, ResourceAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, r => admin.AddScopeAsync(r, new NewScope(request.Name, request.Description), caller.Actor, ct)));
        resources.MapPut("/{id:guid}/scopes/{scopeId:guid}", (Guid id, Guid scopeId, UpdateScopeRequest request, AdminCaller caller, ResourceAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, r => admin.UpdateScopeAsync(r, scopeId, request.Description, caller.Actor, ct)));
        resources.MapDelete("/{id:guid}/scopes/{scopeId:guid}", (Guid id, Guid scopeId, AdminCaller caller, ResourceAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, r => admin.RemoveScopeAsync(r, scopeId, caller.Actor, ct)));
        resources.MapPost("/{id:guid}/secrets", async Task<Created<CreatedSecret>> (Guid id, AddSecretRequest request, AdminCaller caller, ResourceAdministration admin, CancellationToken ct) =>
        {
            var secret = await admin.AddSecretAsync(await FindAsync(admin, id, ct), request.Description, request.ExpiresDays, caller.Actor, ct);
            return TypedResults.Created($"{AdminApiAuthentication.PathPrefix}/resources/{id}", new CreatedSecret(secret.Id, secret.Plaintext, secret.ExpiresAt));
        });
        resources.MapDelete("/{id:guid}/secrets/{secretId:guid}", async Task<NoContent> (Guid id, Guid secretId, AdminCaller caller, ResourceAdministration admin, CancellationToken ct) =>
        {
            await admin.RemoveSecretAsync(await FindAsync(admin, id, ct), secretId, caller.Actor, ct);
            return TypedResults.NoContent();
        });
    }

    private static async Task<Ok<Page<ResourceDto>>> ListAsync(ValkyrieDbContext db, TimeProvider time, int? limit, int? offset, string? q, CancellationToken ct)
    {
        var (take, skip, query) = AdminRequests.Paging(limit, offset, q);
        var resources = db.ApiResources.AsNoTracking();
        if (query is not null)
        {
            var lower = query.ToLowerInvariant();
            resources = resources.Where(r => r.Name.ToLower().Contains(lower) || (r.DisplayName != null && r.DisplayName.ToLower().Contains(lower)));
        }

        var total = await resources.CountAsync(ct);
        var page = await resources.Include(r => r.Scopes).Include(r => r.Secrets).AsSplitQuery()
            .OrderBy(r => r.Name).Skip(skip).Take(take).ToListAsync(ct);
        var now = time.GetUtcNow();
        return TypedResults.Ok(new Page<ResourceDto>([.. page.Select(r => Dto(r, now))], total));
    }

    private static async Task<Ok<ResourceDto>> ActAsync(Guid id, ResourceAdministration admin, TimeProvider time, CancellationToken ct, Func<ApiResource, Task> action)
    {
        var resource = await FindAsync(admin, id, ct);
        await action(resource);
        return TypedResults.Ok(Dto(resource, time.GetUtcNow()));
    }

    private static async Task<ApiResource> FindAsync(ResourceAdministration admin, Guid id, CancellationToken ct) =>
        AdminRequests.Found(await admin.FindAsync(id, ct), "API resource");

    private static ResourceDto Dto(ApiResource resource, DateTimeOffset now) => new(
        resource.Id,
        resource.Name,
        resource.DisplayName,
        resource.Enabled,
        resource.BuiltIn,
        resource.AllowClientSecretPost,
        resource.CreatedAt,
        [.. resource.Scopes.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => new ScopeDto(s.Id, s.Name, s.Description))],
        [.. resource.Secrets.OrderBy(s => s.CreatedAt).Select(s => new SecretDto(s.Id, s.Description, s.CreatedAt, s.ExpiresAt, s.IsActive(now)))]);
}
