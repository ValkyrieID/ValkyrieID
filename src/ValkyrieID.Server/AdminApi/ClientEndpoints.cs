using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Clients;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;

namespace ValkyrieID.Server.AdminApi;

internal sealed record SecretDto(Guid Id, string? Description, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool Active);

/// <summary>A new secret. <see cref="Secret"/> is only ever returned here; only its hash is stored.</summary>
internal sealed record CreatedSecret(Guid Id, string Secret, DateTimeOffset? ExpiresAt);

/// <summary>Lifetimes are in seconds; <see langword="null"/> uses the global setting.</summary>
internal sealed record ClientDto(
    Guid Id,
    string ClientId,
    string? DisplayName,
    string Type,
    bool Enabled,
    bool BuiltIn,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris,
    IReadOnlyList<string> CorsOrigins,
    string? BackChannelLogoutUri,
    bool RequirePkce,
    bool AllowClientSecretPost,
    int? AccessTokenLifetime,
    int? IdTokenLifetime,
    int? RefreshTokenLifetime,
    int? RefreshTokenAbsoluteLifetime,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SecretDto> Secrets);

/// <summary>The full configuration of a client (PUT replaces it). Omitted lists are empty.</summary>
internal sealed record ClientSettingsRequest(
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> Scopes,
    string? DisplayName = null,
    IReadOnlyList<string>? RedirectUris = null,
    IReadOnlyList<string>? PostLogoutRedirectUris = null,
    IReadOnlyList<string>? CorsOrigins = null,
    string? BackChannelLogoutUri = null,
    bool RequirePkce = true,
    bool AllowClientSecretPost = false,
    int? AccessTokenLifetime = null,
    int? IdTokenLifetime = null,
    int? RefreshTokenLifetime = null,
    int? RefreshTokenAbsoluteLifetime = null)
{
    public ClientSettings ToSettings() => new(
        DisplayName,
        GrantTypes,
        Scopes,
        RedirectUris ?? [],
        PostLogoutRedirectUris ?? [],
        CorsOrigins ?? [],
        BackChannelLogoutUri,
        RequirePkce,
        AllowClientSecretPost,
        AdminRequests.Seconds(AccessTokenLifetime, "accessTokenLifetime"),
        AdminRequests.Seconds(IdTokenLifetime, "idTokenLifetime"),
        AdminRequests.Seconds(RefreshTokenLifetime, "refreshTokenLifetime"),
        AdminRequests.Seconds(RefreshTokenAbsoluteLifetime, "refreshTokenAbsoluteLifetime"));
}

internal sealed record CreateClientRequest(
    string ClientId,
    string Type,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> Scopes,
    string? DisplayName = null,
    IReadOnlyList<string>? RedirectUris = null,
    IReadOnlyList<string>? PostLogoutRedirectUris = null,
    IReadOnlyList<string>? CorsOrigins = null,
    string? BackChannelLogoutUri = null,
    bool RequirePkce = true,
    bool AllowClientSecretPost = false,
    int? AccessTokenLifetime = null,
    int? IdTokenLifetime = null,
    int? RefreshTokenLifetime = null,
    int? RefreshTokenAbsoluteLifetime = null,
    int? SecretExpiresDays = null)
{
    public ClientSettingsRequest Settings => new(
        GrantTypes, Scopes, DisplayName, RedirectUris, PostLogoutRedirectUris, CorsOrigins, BackChannelLogoutUri, RequirePkce,
        AllowClientSecretPost, AccessTokenLifetime, IdTokenLifetime, RefreshTokenLifetime, RefreshTokenAbsoluteLifetime);
}

/// <summary>The created client and, for a confidential client, its first secret.</summary>
internal sealed record CreatedClient(ClientDto Client, CreatedSecret? Secret);

internal sealed record AddSecretRequest(string? Description = null, int? ExpiresDays = null);

internal static class ClientEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var clients = api.MapGroup("/clients").WithTags("Clients");
        clients.MapGet("/", ListAsync);
        clients.MapPost("/", CreateAsync);
        clients.MapGet("/{id:guid}", async Task<Ok<ClientDto>> (Guid id, ClientAdministration admin, TimeProvider time, CancellationToken ct) =>
            TypedResults.Ok(Dto(await FindAsync(admin, id, ct), time.GetUtcNow())));
        clients.MapPut("/{id:guid}", (Guid id, ClientSettingsRequest request, AdminCaller caller, ClientAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, client => admin.UpdateAsync(client, request.ToSettings(), caller.Actor, ct)));
        clients.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, AdminCaller caller, ClientAdministration admin, CancellationToken ct) =>
        {
            await admin.DeleteAsync(await FindAsync(admin, id, ct), caller.Actor, ct);
            return TypedResults.NoContent();
        });
        clients.MapPost("/{id:guid}/enable", (Guid id, AdminCaller caller, ClientAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, client => admin.SetEnabledAsync(client, true, caller.Actor, ct)));
        clients.MapPost("/{id:guid}/disable", (Guid id, AdminCaller caller, ClientAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, client => admin.SetEnabledAsync(client, false, caller.Actor, ct)));
        clients.MapPost("/{id:guid}/secrets", async Task<Created<CreatedSecret>> (Guid id, AddSecretRequest request, AdminCaller caller, ClientAdministration admin, CancellationToken ct) =>
        {
            var secret = await admin.AddSecretAsync(await FindAsync(admin, id, ct), request.Description, request.ExpiresDays, caller.Actor, ct);
            return TypedResults.Created($"{AdminApiAuthentication.PathPrefix}/clients/{id}", new CreatedSecret(secret.Id, secret.Plaintext, secret.ExpiresAt));
        });
        clients.MapDelete("/{id:guid}/secrets/{secretId:guid}", async Task<NoContent> (Guid id, Guid secretId, AdminCaller caller, ClientAdministration admin, CancellationToken ct) =>
        {
            await admin.RemoveSecretAsync(await FindAsync(admin, id, ct), secretId, caller.Actor, ct);
            return TypedResults.NoContent();
        });
    }

    private static async Task<Ok<Page<ClientDto>>> ListAsync(ValkyrieDbContext db, TimeProvider time, int? limit, int? offset, string? q, CancellationToken ct)
    {
        var (take, skip, query) = AdminRequests.Paging(limit, offset, q);
        var clients = db.Clients.AsNoTracking();
        if (query is not null)
        {
            var lower = query.ToLowerInvariant();
            clients = clients.Where(c => c.ClientId.ToLower().Contains(lower) || (c.DisplayName != null && c.DisplayName.ToLower().Contains(lower)));
        }

        var total = await clients.CountAsync(ct);
        var page = await clients.Include(c => c.Secrets).AsSplitQuery().OrderBy(c => c.ClientId).Skip(skip).Take(take).ToListAsync(ct);
        var now = time.GetUtcNow();
        return TypedResults.Ok(new Page<ClientDto>([.. page.Select(c => Dto(c, now))], total));
    }

    private static async Task<Created<CreatedClient>> CreateAsync(
        CreateClientRequest request, AdminCaller caller, ClientAdministration admin, TimeProvider time, CancellationToken ct)
    {
        var type = request.Type switch
        {
            "confidential" => ClientType.Confidential,
            "public" => ClientType.Public,
            _ => throw new AdministrationException("type must be 'confidential' or 'public'."),
        };
        var (client, secret) = await admin.CreateAsync(request.ClientId, type, request.Settings.ToSettings(), request.SecretExpiresDays, caller.Actor, ct);
        return TypedResults.Created(
            $"{AdminApiAuthentication.PathPrefix}/clients/{client.Id}",
            new CreatedClient(Dto(client, time.GetUtcNow()), secret is null ? null : new CreatedSecret(secret.Id, secret.Plaintext, secret.ExpiresAt)));
    }

    private static async Task<Ok<ClientDto>> ActAsync(Guid id, ClientAdministration admin, TimeProvider time, CancellationToken ct, Func<Client, Task> action)
    {
        var client = await FindAsync(admin, id, ct);
        await action(client);
        return TypedResults.Ok(Dto(client, time.GetUtcNow()));
    }

    private static async Task<Client> FindAsync(ClientAdministration admin, Guid id, CancellationToken ct) =>
        AdminRequests.Found(await admin.FindAsync(id, ct), "client");

    private static ClientDto Dto(Client client, DateTimeOffset now) => new(
        client.Id,
        client.ClientId,
        client.DisplayName,
        client.Type == ClientType.Public ? "public" : "confidential",
        client.Enabled,
        client.BuiltIn,
        client.AllowedGrantTypes,
        client.AllowedScopes,
        client.RedirectUris,
        client.PostLogoutRedirectUris,
        client.AllowedCorsOrigins,
        client.BackChannelLogoutUri,
        client.PkceRequired,
        client.AllowClientSecretPost,
        AdminRequests.Seconds(client.AccessTokenLifetime),
        AdminRequests.Seconds(client.IdTokenLifetime),
        AdminRequests.Seconds(client.RefreshTokenSlidingLifetime),
        AdminRequests.Seconds(client.RefreshTokenAbsoluteLifetime),
        client.CreatedAt,
        [.. client.Secrets.OrderBy(s => s.CreatedAt).Select(s => new SecretDto(s.Id, s.Description, s.CreatedAt, s.ExpiresAt, s.IsActive(now)))]);
}
