using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Core.Resources;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Administration;

/// <summary>
/// Creates the Admin API resource and the admin UI client, and keeps them in their required configuration (e.g.
/// after the issuer URL changes). Idempotent: runs at every start, so existing installations get them on upgrade.
/// </summary>
internal sealed partial class BuiltInSeeder(
    ValkyrieDbContext db,
    AuditRecorder audit,
    TimeProvider timeProvider,
    IOptions<IssuerOptions> issuer,
    ILogger<BuiltInSeeder> logger)
{
    public static string AdminUiRedirectUri(IssuerOptions issuer) => issuer.Endpoint(BuiltIn.AdminUiPath + "/callback");

    public static string AdminUiPostLogoutRedirectUri(IssuerOptions issuer) => issuer.Endpoint(BuiltIn.AdminUiPath + "/");

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SeedOnceAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another instance seeded at the same time (unique index); check again against what it saved.
            db.ChangeTracker.Clear();
            await SeedOnceAsync(cancellationToken);
        }
    }

    private async Task SeedOnceAsync(CancellationToken cancellationToken)
    {
        await SeedResourceAsync(cancellationToken);
        await SeedClientAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedResourceAsync(CancellationToken cancellationToken)
    {
        var resource = await db.ApiResources.Include(r => r.Scopes).SingleOrDefaultAsync(r => r.Name == BuiltIn.AdminResource, cancellationToken);
        var scope = await db.ApiScopes.SingleOrDefaultAsync(s => s.Name == BuiltIn.AdminScope, cancellationToken);

        // Fail closed rather than adopting an entity someone else configured under a reserved name.
        if (resource is { BuiltIn: false } || (scope is not null && scope.ApiResourceId != resource?.Id))
        {
            throw new InvalidOperationException(
                $"The API resource name '{BuiltIn.AdminResource}' and scope '{BuiltIn.AdminScope}' are reserved for the Admin API, but are "
                + "used by another resource. Delete or rename it with the CLI (`valkyrieid resource ...`) and start again.");
        }

        if (resource is null)
        {
            resource = new ApiResource
            {
                Name = BuiltIn.AdminResource,
                DisplayName = "ValkyrieID Admin API",
                BuiltIn = true,
                CreatedAt = timeProvider.GetUtcNow(),
                Scopes = [new ApiScope { Name = BuiltIn.AdminScope, Description = "Manage ValkyrieID through the Admin API." }],
            };
            db.ApiResources.Add(resource);
            audit.Add("api_resource.created", actor: AuditRecorder.SystemActor,
                details: new Dictionary<string, object?> { ["name"] = resource.Name, ["scopes"] = new[] { BuiltIn.AdminScope }, ["built_in"] = true });
            LogSeeded(logger, "API resource", resource.Name);
            return;
        }

        if (!resource.Enabled || resource.AllowClientSecretPost || scope is null)
        {
            resource.Enabled = true;
            resource.AllowClientSecretPost = false;
            if (scope is null)
            {
                resource.Scopes.Add(new ApiScope { ApiResourceId = resource.Id, Name = BuiltIn.AdminScope, Description = "Manage ValkyrieID through the Admin API." });
            }

            audit.Add("api_resource.updated", actor: AuditRecorder.SystemActor,
                details: new Dictionary<string, object?> { ["name"] = resource.Name, ["built_in"] = true });
        }
    }

    private async Task SeedClientAsync(CancellationToken cancellationToken)
    {
        var client = await db.Clients.Include(c => c.Secrets).SingleOrDefaultAsync(c => c.ClientId == BuiltIn.AdminUiClientId, cancellationToken);
        if (client is { BuiltIn: false } or { Type: not ClientType.Public })
        {
            throw new InvalidOperationException(
                $"The client id '{BuiltIn.AdminUiClientId}' is reserved for the admin UI, but is used by another client. "
                + "Delete it with the CLI (`valkyrieid client delete`) and start again.");
        }

        var isNew = client is null;
        client ??= new Client { ClientId = BuiltIn.AdminUiClientId, Type = ClientType.Public, BuiltIn = true, CreatedAt = timeProvider.GetUtcNow() };
        var redirectUri = AdminUiRedirectUri(issuer.Value);
        var postLogoutRedirectUri = AdminUiPostLogoutRedirectUri(issuer.Value);
        string[] grants = [GrantTypes.AuthorizationCode];
        string[] scopes = [IdentityScopes.OpenId, IdentityScopes.Profile, BuiltIn.AdminScope];

        // Same-origin SPA: no CORS origins. No refresh tokens: the admin UI renews tokens through the SSO session.
        var upToDate = !isNew
            && client.Enabled
            && client.DisplayName == "ValkyrieID Admin"
            && client.AllowedGrantTypes.SequenceEqual(grants)
            && client.AllowedScopes.SequenceEqual(scopes)
            && client.RedirectUris.SequenceEqual([redirectUri])
            && client.PostLogoutRedirectUris.SequenceEqual([postLogoutRedirectUri])
            && client.AllowedCorsOrigins.Count == 0
            && client.BackChannelLogoutUri is null
            && client.RequirePkce
            && !client.AllowClientSecretPost
            && client.AccessTokenLifetime == BuiltIn.AdminUiAccessTokenLifetime
            && client.IdTokenLifetime is null
            && client.RefreshTokenSlidingLifetime is null
            && client.RefreshTokenAbsoluteLifetime is null
            && client.Secrets.Count == 0;
        if (upToDate)
        {
            return;
        }

        client.Enabled = true;
        client.DisplayName = "ValkyrieID Admin";
        client.AllowedGrantTypes = [.. grants];
        client.AllowedScopes = [.. scopes];
        client.RedirectUris = [redirectUri];
        client.PostLogoutRedirectUris = [postLogoutRedirectUri];
        client.AllowedCorsOrigins = [];
        client.BackChannelLogoutUri = null;
        client.RequirePkce = true;
        client.AllowClientSecretPost = false;
        client.AccessTokenLifetime = BuiltIn.AdminUiAccessTokenLifetime;
        client.IdTokenLifetime = null;
        client.RefreshTokenSlidingLifetime = null;
        client.RefreshTokenAbsoluteLifetime = null;
        client.Secrets.Clear();
        if (isNew)
        {
            db.Clients.Add(client);
        }

        audit.Add(isNew ? "client.created" : "client.updated", actor: AuditRecorder.SystemActor, clientId: client.ClientId,
            details: new Dictionary<string, object?> { ["built_in"] = true, ["redirect_uris"] = client.RedirectUris });
        LogSeeded(logger, "client", client.ClientId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Configured the built-in {Kind} {Name}")]
    private static partial void LogSeeded(ILogger logger, string kind, string name);
}
