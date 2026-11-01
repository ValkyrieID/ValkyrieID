using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Resources;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;

namespace ValkyrieID.Server.Cli;

internal sealed class ResourceCommands(IServiceProvider services, TextWriter output)
{
    private readonly ValkyrieDbContext _db = services.GetRequiredService<ValkyrieDbContext>();
    private readonly ResourceAdministration _resources = services.GetRequiredService<ResourceAdministration>();
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

        await UserCommands.Run(() => _resources.CreateAsync(name, displayName, [.. scopes.Select(s => new NewScope(s))], UserCommands.Actor, cancellationToken));
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
                $"{resource.Name}  {(resource.Enabled ? "enabled" : "disabled")}  scopes: {string.Join(' ', resource.Scopes.Select(s => s.Name).Order(StringComparer.Ordinal))}{(resource.AllowClientSecretPost ? "  client_secret_post" : "")}{(resource.BuiltIn ? "  built-in" : "")}");
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

        var resource = await FindAsync(name, cancellationToken);
        var secret = await UserCommands.Run(() => _resources.AddSecretAsync(resource, description, expiresDays, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"secret_id: {secret.Id}");
        await output.WriteLineAsync($"resource_secret: {secret.Plaintext}");
        await output.WriteLineAsync("Store the secret now; it can't be shown again.");
    }

    private async Task RemoveSecretAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        var secretIdText = args.Argument(3, "secret-id");
        args.EnsureConsumed(4);

        var resource = await FindAsync(name, cancellationToken);
        if (!Guid.TryParse(secretIdText, out var secretId))
        {
            throw new CliException($"API resource '{name}' has no secret '{secretIdText}'.");
        }

        await UserCommands.Run(() => _resources.RemoveSecretAsync(resource, secretId, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"Removed secret {secretId}.");
    }

    private async Task AddScopeAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        var scope = args.Argument(3, "scope");
        var description = args.Optional("description");
        args.EnsureConsumed(4);

        var resource = await FindAsync(name, cancellationToken);
        await UserCommands.Run(() => _resources.AddScopeAsync(resource, new NewScope(scope, description), UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"Scope '{scope}' added to '{name}'.");
    }

    private async Task SetSecretPostAsync(CliArguments args, bool allow, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        args.EnsureConsumed(3);

        var resource = await FindAsync(name, cancellationToken);
        await UserCommands.Run(() => _resources.SetSecretPostAsync(resource, allow, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"client_secret_post {(allow ? "enabled" : "disabled")} for '{name}'.");
    }

    private async Task SetEnabledAsync(CliArguments args, bool enabled, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        args.EnsureConsumed(3);

        var resource = await FindAsync(name, cancellationToken);
        await UserCommands.Run(() => _resources.SetEnabledAsync(resource, enabled, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"API resource '{name}' {(enabled ? "enabled" : "disabled")}.");
    }

    private async Task DeleteAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "name");
        args.EnsureConsumed(3);

        var resource = await FindAsync(name, cancellationToken);
        await UserCommands.Run(() => _resources.DeleteAsync(resource, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"API resource '{name}' deleted.");
    }

    private async Task<ApiResource> FindAsync(string name, CancellationToken cancellationToken) =>
        await _resources.FindAsync(name, cancellationToken)
        ?? throw new CliException($"API resource '{name}' does not exist.");
}
