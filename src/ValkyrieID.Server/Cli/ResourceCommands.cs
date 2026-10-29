using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Core.Resources;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;

namespace ValkyrieID.Server.Cli;

internal sealed class ResourceCommands(IServiceProvider services, TextWriter output)
{
    private readonly ValkyrieDbContext _db = services.GetRequiredService<ValkyrieDbContext>();
    private readonly AuditRecorder _audit = services.GetRequiredService<AuditRecorder>();
    private readonly TimeProvider _time = services.GetRequiredService<TimeProvider>();

    public Task RunAsync(CliArguments args, CancellationToken cancellationToken) => args.Argument(1, "subcommand") switch
    {
        "create" => CreateAsync(args, cancellationToken),
        "list" => ListAsync(args, cancellationToken),
        "add-scope" => AddScopeAsync(args, cancellationToken),
        "add-secret" => AddSecretAsync(args, cancellationToken),
        "remove-secret" => RemoveSecretAsync(args, cancellationToken),
        "enable" => SetEnabledAsync(args, true, cancellationToken),
        "disable" => SetEnabledAsync(args, false, cancellationToken),
        "delete" => DeleteAsync(args, cancellationToken),
        "enable-secret-post" => SetSecretPostAsync(args, true, cancellationToken),
        "disable-secret-post" => SetSecretPostAsync(args, false, cancellationToken),
        var other => throw new CliException($"Unknown resource command '{other}'."),
    };

    private async Task CreateAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        var scopes = args.Many("scope").Distinct(StringComparer.Ordinal).ToList();
        var displayName = args.Optional("name");
        args.EnsureConsumed(3);

        if (!ProtocolSyntax.IsValidResourceName(name))
        {
            throw new CliException("The resource name must be 1-200 printable ASCII characters without spaces, '\"' or '\\'.");
        }

        if (await _db.ApiResources.AnyAsync(r => r.Name == name, cancellationToken))
        {
            throw new CliException($"API resource '{name}' already exists.");
        }

        if (scopes.Count == 0)
        {
            throw new CliException("At least one --scope is required.");
        }

        foreach (var scope in scopes)
        {
            await ValidateNewScopeAsync(scope, cancellationToken);
        }

        var resource = new ApiResource
        {
            Name = name,
            DisplayName = displayName,
            CreatedAt = _time.GetUtcNow(),
            Scopes = [.. scopes.Select(s => new ApiScope { Name = s })],
        };
        _db.ApiResources.Add(resource);
        _audit.Add("api_resource.created", actor: AuditRecorder.CliActor,
            details: new Dictionary<string, object?> { ["name"] = name, ["scopes"] = scopes });
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"API resource '{name}' created with scopes: {string.Join(' ', scopes)}");
    }

    private async Task ListAsync(CliArguments args, CancellationToken cancellationToken)
    {
        args.EnsureConsumed(2);
        var resources = await _db.ApiResources.AsNoTracking().Include(r => r.Scopes).Include(r => r.Secrets).AsSplitQuery()
            .OrderBy(r => r.Name).ToListAsync(cancellationToken);
        var now = _time.GetUtcNow();
        foreach (var resource in resources)
        {
            await output.WriteLineAsync(
                $"{resource.Name}  {(resource.Enabled ? "enabled" : "disabled")}  scopes: {string.Join(' ', resource.Scopes.Select(s => s.Name).Order(StringComparer.Ordinal))}{(resource.AllowClientSecretPost ? "  client_secret_post" : "")}");
            foreach (var secret in resource.Secrets.OrderBy(s => s.CreatedAt))
            {
                await output.WriteLineAsync(
                    $"  secret {secret.Id}  {(secret.IsActive(now) ? "active" : "expired")}  expires: {secret.ExpiresAt?.ToString("u") ?? "never"}  {secret.Description}");
            }
        }
    }

    /// <summary>A resource secret lets the API call the introspection endpoint (RFC 7662).</summary>
    private async Task AddSecretAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        var description = args.Optional("description");
        var expiresDays = args.OptionalInt("expires-days");
        args.EnsureConsumed(3);

        if (expiresDays is <= 0)
        {
            throw new CliException("--expires-days must be positive.");
        }

        var resource = await FindAsync(name, cancellationToken);
        var now = _time.GetUtcNow();
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
        _audit.Add("api_resource.secret_added", actor: AuditRecorder.CliActor,
            details: new Dictionary<string, object?> { ["name"] = name, ["secret_id"] = secret.Id, ["expires_at"] = secret.ExpiresAt });
        await _db.SaveChangesAsync(cancellationToken);

        await output.WriteLineAsync($"secret_id: {secret.Id}");
        await output.WriteLineAsync($"resource_secret: {plaintext}");
        await output.WriteLineAsync("Store the secret now; it can't be shown again.");
    }

    private async Task RemoveSecretAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        var secretIdText = args.Argument(3, "secret-id");
        args.EnsureConsumed(4);

        var resource = await FindAsync(name, cancellationToken);
        var secret = Guid.TryParse(secretIdText, out var secretId) ? resource.Secrets.SingleOrDefault(s => s.Id == secretId) : null;
        if (secret is null)
        {
            throw new CliException($"API resource '{name}' has no secret '{secretIdText}'.");
        }

        resource.Secrets.Remove(secret);
        _audit.Add("api_resource.secret_removed", actor: AuditRecorder.CliActor,
            details: new Dictionary<string, object?> { ["name"] = name, ["secret_id"] = secret.Id });
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"Removed secret {secret.Id}.");
    }

    private async Task AddScopeAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        var scope = args.Argument(3, "scope");
        var description = args.Optional("description");
        args.EnsureConsumed(4);

        var resource = await FindAsync(name, cancellationToken);
        await ValidateNewScopeAsync(scope, cancellationToken);
        resource.Scopes.Add(new ApiScope { ApiResourceId = resource.Id, Name = scope, Description = description });
        _audit.Add("api_resource.scope_added", actor: AuditRecorder.CliActor,
            details: new Dictionary<string, object?> { ["name"] = name, ["scope"] = scope });
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"Scope '{scope}' added to '{name}'.");
    }

    private async Task SetSecretPostAsync(CliArguments args, bool allow, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        args.EnsureConsumed(3);

        var resource = await FindAsync(name, cancellationToken);
        resource.AllowClientSecretPost = allow;
        _audit.Add(allow ? "api_resource.secret_post_enabled" : "api_resource.secret_post_disabled", actor: AuditRecorder.CliActor,
            details: new Dictionary<string, object?> { ["name"] = name });
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"client_secret_post {(allow ? "enabled" : "disabled")} for '{name}'.");
    }

    private async Task SetEnabledAsync(CliArguments args, bool enabled, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        args.EnsureConsumed(3);

        var resource = await FindAsync(name, cancellationToken);
        resource.Enabled = enabled;
        _audit.Add(enabled ? "api_resource.enabled" : "api_resource.disabled", actor: AuditRecorder.CliActor,
            details: new Dictionary<string, object?> { ["name"] = name });
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"API resource '{name}' {(enabled ? "enabled" : "disabled")}.");
    }

    private async Task DeleteAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        args.EnsureConsumed(3);

        var resource = await FindAsync(name, cancellationToken);
        _db.ApiResources.Remove(resource);
        _audit.Add("api_resource.deleted", actor: AuditRecorder.CliActor,
            details: new Dictionary<string, object?> { ["name"] = name });
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"API resource '{name}' deleted.");
    }

    private async Task ValidateNewScopeAsync(string scope, CancellationToken cancellationToken)
    {
        if (!ProtocolSyntax.IsValidScopeToken(scope) || scope.Length > 200)
        {
            throw new CliException($"Scope '{scope}' is invalid: use 1-200 printable ASCII characters without spaces, '\"' or '\\'.");
        }

        if (IdentityScopes.All.Contains(scope))
        {
            throw new CliException($"'{scope}' is a reserved identity scope.");
        }

        if (await _db.ApiScopes.AnyAsync(s => s.Name == scope, cancellationToken))
        {
            throw new CliException($"Scope '{scope}' already exists.");
        }
    }

    private async Task<ApiResource> FindAsync(string name, CancellationToken cancellationToken) =>
        await _db.ApiResources.Include(r => r.Scopes).Include(r => r.Secrets).AsSplitQuery().SingleOrDefaultAsync(r => r.Name == name, cancellationToken)
        ?? throw new CliException($"API resource '{name}' does not exist.");
}
