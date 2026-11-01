using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Core.Resources;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Administration;

internal sealed record NewScope(string Name, string? Description = null);

/// <summary>API resource and scope management shared by the CLI and the Admin API. Every change is audited.</summary>
internal sealed class ResourceAdministration(ValkyrieDbContext db, AuditRecorder audit, TimeProvider timeProvider)
{
    public const int MaxDisplayNameLength = 200;
    public const int MaxScopeDescriptionLength = 500;
    public const int MaxSecretDescriptionLength = 200;

    public Task<ApiResource?> FindAsync(string name, CancellationToken cancellationToken) =>
        Query().SingleOrDefaultAsync(r => r.Name == name, cancellationToken);

    public Task<ApiResource?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Query().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<ApiResource> CreateAsync(
        string name, string? displayName, IReadOnlyList<NewScope> scopes, AuditActor actor, CancellationToken cancellationToken)
    {
        if (!ProtocolSyntax.IsValidResourceName(name))
        {
            throw new AdministrationException("The resource name must be 1-200 printable ASCII characters without spaces, '\"' or '\\'.");
        }

        if (name == BuiltIn.AdminResource)
        {
            throw AdministrationException.Conflict($"The resource name '{name}' is reserved.");
        }

        if (await db.ApiResources.AnyAsync(r => r.Name == name, cancellationToken))
        {
            throw AdministrationException.Conflict($"API resource '{name}' already exists.");
        }

        displayName = ValidateDisplayName(displayName);
        if (scopes.Count == 0)
        {
            throw new AdministrationException("At least one scope is required.");
        }

        if (scopes.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != scopes.Count)
        {
            throw new AdministrationException("Scope names must be unique.");
        }

        List<ApiScope> created = [];
        foreach (var scope in scopes)
        {
            created.Add(new ApiScope { Name = scope.Name, Description = await ValidateNewScopeAsync(scope, cancellationToken) });
        }

        var resource = new ApiResource { Name = name, DisplayName = displayName, CreatedAt = timeProvider.GetUtcNow(), Scopes = created };
        db.ApiResources.Add(resource);
        Audit("api_resource.created", resource, actor, new() { ["scopes"] = scopes.Select(s => s.Name).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return resource;
    }

    public async Task UpdateAsync(ApiResource resource, string? displayName, bool allowClientSecretPost, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(resource);
        displayName = ValidateDisplayName(displayName);
        List<string> changed = [];
        if (displayName != resource.DisplayName)
        {
            changed.Add("display_name");
        }

        if (allowClientSecretPost != resource.AllowClientSecretPost)
        {
            changed.Add("allow_client_secret_post");
        }

        if (changed.Count == 0)
        {
            return;
        }

        resource.DisplayName = displayName;
        resource.AllowClientSecretPost = allowClientSecretPost;
        Audit("api_resource.updated", resource, actor, new() { ["fields"] = string.Join(' ', changed) });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ApiScope> AddScopeAsync(ApiResource resource, NewScope scope, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(resource);
        var created = new ApiScope { ApiResourceId = resource.Id, Name = scope.Name, Description = await ValidateNewScopeAsync(scope, cancellationToken) };
        resource.Scopes.Add(created);
        Audit("api_resource.scope_added", resource, actor, new() { ["scope"] = scope.Name });
        await db.SaveChangesAsync(cancellationToken);
        return created;
    }

    public async Task UpdateScopeAsync(ApiResource resource, Guid scopeId, string? description, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(resource);
        var scope = FindScope(resource, scopeId);
        scope.Description = ValidateScopeDescription(description);
        Audit("api_resource.scope_updated", resource, actor, new() { ["scope"] = scope.Name });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Removes a scope. Refused while clients are allowed to request it, so no client is left with an unknown scope.</summary>
    public async Task RemoveScopeAsync(ApiResource resource, Guid scopeId, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(resource);
        var scope = FindScope(resource, scopeId);
        if (resource.Scopes.Count == 1)
        {
            throw AdministrationException.Conflict("An API resource needs at least one scope.");
        }

        await EnsureUnusedAsync([scope.Name], cancellationToken);
        resource.Scopes.Remove(scope);
        Audit("api_resource.scope_removed", resource, actor, new() { ["scope"] = scope.Name });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetSecretPostAsync(ApiResource resource, bool allow, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(resource);
        resource.AllowClientSecretPost = allow;
        Audit(allow ? "api_resource.secret_post_enabled" : "api_resource.secret_post_disabled", resource, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(ApiResource resource, bool enabled, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(resource);
        resource.Enabled = enabled;
        Audit(enabled ? "api_resource.enabled" : "api_resource.disabled", resource, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Deletes a resource. Refused while clients are allowed to request its scopes.</summary>
    public async Task DeleteAsync(ApiResource resource, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(resource);
        await EnsureUnusedAsync([.. resource.Scopes.Select(s => s.Name)], cancellationToken);
        db.ApiResources.Remove(resource);
        Audit("api_resource.deleted", resource, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A resource secret lets the API call the introspection endpoint (RFC 7662).</summary>
    public async Task<NewSecret> AddSecretAsync(ApiResource resource, string? description, int? expiresDays, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(resource);
        if (expiresDays is <= 0)
        {
            throw new AdministrationException("The secret expiry must be a positive number of days.");
        }

        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (description?.Length > MaxSecretDescriptionLength)
        {
            throw new AdministrationException($"The description must be at most {MaxSecretDescriptionLength} characters.");
        }

        var now = timeProvider.GetUtcNow();
        var plaintext = SecretHasher.Generate();
        var secret = new ApiResourceSecret
        {
            ApiResourceId = resource.Id,
            Hash = SecretHasher.Hash(plaintext),
            Description = description,
            CreatedAt = now,
            ExpiresAt = expiresDays is { } days ? now.AddDays(days) : null,
        };
        resource.Secrets.Add(secret);
        Audit("api_resource.secret_added", resource, actor, new() { ["secret_id"] = secret.Id, ["expires_at"] = secret.ExpiresAt });
        await db.SaveChangesAsync(cancellationToken);
        return new NewSecret(secret.Id, plaintext, secret.ExpiresAt);
    }

    public async Task RemoveSecretAsync(ApiResource resource, Guid secretId, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(resource);
        var secret = resource.Secrets.SingleOrDefault(s => s.Id == secretId)
            ?? throw AdministrationException.NotFound($"API resource '{resource.Name}' has no secret '{secretId}'.");
        resource.Secrets.Remove(secret);
        Audit("api_resource.secret_removed", resource, actor, new() { ["secret_id"] = secret.Id });
        await db.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<ApiResource> Query() => db.ApiResources.Include(r => r.Scopes).Include(r => r.Secrets).AsSplitQuery();

    private static ApiScope FindScope(ApiResource resource, Guid scopeId) =>
        resource.Scopes.SingleOrDefault(s => s.Id == scopeId)
        ?? throw AdministrationException.NotFound($"API resource '{resource.Name}' has no scope '{scopeId}'.");

    private async Task EnsureUnusedAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        var clients = await db.Clients.AsNoTracking().Select(c => new { c.ClientId, c.AllowedScopes }).ToListAsync(cancellationToken);
        var users = clients.Where(c => c.AllowedScopes.Any(s => scopes.Contains(s, StringComparer.Ordinal))).Select(c => c.ClientId).Order(StringComparer.Ordinal).ToList();
        if (users.Count > 0)
        {
            throw AdministrationException.Conflict($"The scope is allowed for client(s) {string.Join(", ", users)}; remove it from them first.");
        }
    }

    /// <summary>The built-in Admin API resource is maintained by the server; changing it could lock administrators out.</summary>
    private static void EnsureEditable(ApiResource resource)
    {
        if (resource.BuiltIn)
        {
            throw AdministrationException.Conflict($"The built-in API resource '{resource.Name}' is managed by the server and can't be changed.");
        }
    }

    private static string? ValidateDisplayName(string? displayName)
    {
        displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        return displayName?.Length > MaxDisplayNameLength
            ? throw new AdministrationException($"The display name must be at most {MaxDisplayNameLength} characters.")
            : displayName;
    }

    private static string? ValidateScopeDescription(string? description)
    {
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        return description?.Length > MaxScopeDescriptionLength
            ? throw new AdministrationException($"The scope description must be at most {MaxScopeDescriptionLength} characters.")
            : description;
    }

    /// <summary>Returns the cleaned description.</summary>
    private async Task<string?> ValidateNewScopeAsync(NewScope scope, CancellationToken cancellationToken)
    {
        if (!ProtocolSyntax.IsValidScopeToken(scope.Name) || scope.Name.Length > 200)
        {
            throw new AdministrationException($"Scope '{scope.Name}' is invalid: use 1-200 printable ASCII characters without spaces, '\"' or '\\'.");
        }

        if (IdentityScopes.All.Contains(scope.Name))
        {
            throw new AdministrationException($"'{scope.Name}' is a reserved identity scope.");
        }

        if (scope.Name == BuiltIn.AdminScope)
        {
            throw AdministrationException.Conflict($"The scope '{scope.Name}' is reserved.");
        }

        if (await db.ApiScopes.AnyAsync(s => s.Name == scope.Name, cancellationToken))
        {
            throw AdministrationException.Conflict($"Scope '{scope.Name}' already exists.");
        }

        return ValidateScopeDescription(scope.Description);
    }

    private void Audit(string eventType, ApiResource resource, AuditActor actor, Dictionary<string, object?>? details = null)
    {
        details ??= [];
        details["name"] = resource.Name;
        audit.Add(eventType, actor: actor.Name, ipAddress: actor.IpAddress, details: details);
    }
}
