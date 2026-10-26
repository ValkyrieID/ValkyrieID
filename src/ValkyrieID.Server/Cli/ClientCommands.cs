using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;

namespace ValkyrieID.Server.Cli;

internal sealed class ClientCommands(IServiceProvider services, TextWriter output)
{
    private readonly ValkyrieDbContext _db = services.GetRequiredService<ValkyrieDbContext>();
    private readonly AuditRecorder _audit = services.GetRequiredService<AuditRecorder>();
    private readonly TimeProvider _time = services.GetRequiredService<TimeProvider>();

    public Task RunAsync(CliArguments args, CancellationToken cancellationToken) => args.Argument(1, "subcommand") switch
    {
        "create" => CreateAsync(args, cancellationToken),
        "list" => ListAsync(args, cancellationToken),
        "add-secret" => AddSecretAsync(args, cancellationToken),
        "remove-secret" => RemoveSecretAsync(args, cancellationToken),
        "enable" => SetEnabledAsync(args, true, cancellationToken),
        "disable" => SetEnabledAsync(args, false, cancellationToken),
        "delete" => DeleteAsync(args, cancellationToken),
        var other => throw new CliException($"Unknown client command '{other}'."),
    };

    private async Task CreateAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var scopes = args.Many("scope").Distinct(StringComparer.Ordinal).ToList();
        var name = args.Optional("name");
        var lifetime = args.OptionalInt("access-token-lifetime");
        var expiresDays = args.OptionalInt("secret-expires-days");
        args.EnsureConsumed(3);

        if (!ProtocolSyntax.IsValidClientId(clientId))
        {
            throw new CliException("The client id must be 1-200 printable ASCII characters without spaces.");
        }

        if (await _db.Clients.AnyAsync(c => c.ClientId == clientId, cancellationToken))
        {
            throw new CliException($"Client '{clientId}' already exists.");
        }

        if (scopes.Count == 0)
        {
            throw new CliException("At least one --scope is required.");
        }

        await EnsureApiScopesExistAsync(scopes, cancellationToken);

        var now = _time.GetUtcNow();
        var client = new Client
        {
            ClientId = clientId,
            DisplayName = name,
            Type = ClientType.Confidential,
            AllowedGrantTypes = [GrantTypes.ClientCredentials],
            AllowedScopes = scopes,
            AccessTokenLifetime = lifetime is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            CreatedAt = now,
        };
        var (secret, plaintext) = NewSecret(client, null, expiresDays, now);
        _db.Clients.Add(client);
        _audit.Add("client.created", actor: AuditRecorder.CliActor, clientId: clientId, details: new Dictionary<string, object?>
        {
            ["scopes"] = scopes,
            ["grant_types"] = client.AllowedGrantTypes,
            ["secret_id"] = secret.Id,
        });
        await _db.SaveChangesAsync(cancellationToken);

        await output.WriteLineAsync($"client_id: {clientId}");
        await WriteSecretAsync(secret, plaintext);
    }

    private async Task ListAsync(CliArguments args, CancellationToken cancellationToken)
    {
        args.EnsureConsumed(2);
        var now = _time.GetUtcNow();
        var clients = await _db.Clients.AsNoTracking().Include(c => c.Secrets).AsSplitQuery()
            .OrderBy(c => c.ClientId).ToListAsync(cancellationToken);
        foreach (var client in clients)
        {
            await output.WriteLineAsync(
                $"{client.ClientId}  {(client.Enabled ? "enabled" : "disabled")}  {client.Type}  scopes: {string.Join(' ', client.AllowedScopes)}");
            foreach (var secret in client.Secrets.OrderBy(s => s.CreatedAt))
            {
                var state = secret.IsActive(now) ? "active" : "expired";
                await output.WriteLineAsync(
                    $"  secret {secret.Id}  {state}  expires: {secret.ExpiresAt?.ToString("u") ?? "never"}  {secret.Description}");
            }
        }
    }

    private async Task AddSecretAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var description = args.Optional("description");
        var expiresDays = args.OptionalInt("expires-days");
        args.EnsureConsumed(3);

        var client = await FindAsync(clientId, cancellationToken);
        var (secret, plaintext) = NewSecret(client, description, expiresDays, _time.GetUtcNow());
        _audit.Add("client.secret_added", actor: AuditRecorder.CliActor, clientId: clientId,
            details: new Dictionary<string, object?> { ["secret_id"] = secret.Id, ["expires_at"] = secret.ExpiresAt });
        await _db.SaveChangesAsync(cancellationToken);
        await WriteSecretAsync(secret, plaintext);
    }

    private async Task RemoveSecretAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var secretIdText = args.Argument(3, "secret-id");
        args.EnsureConsumed(4);

        var client = await FindAsync(clientId, cancellationToken);
        var secret = Guid.TryParse(secretIdText, out var secretId) ? client.Secrets.SingleOrDefault(s => s.Id == secretId) : null;
        if (secret is null)
        {
            throw new CliException($"Client '{clientId}' has no secret '{secretIdText}'.");
        }

        client.Secrets.Remove(secret);
        _audit.Add("client.secret_removed", actor: AuditRecorder.CliActor, clientId: clientId,
            details: new Dictionary<string, object?> { ["secret_id"] = secret.Id });
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"Removed secret {secret.Id}.");
    }

    private async Task SetEnabledAsync(CliArguments args, bool enabled, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        args.EnsureConsumed(3);

        var client = await FindAsync(clientId, cancellationToken);
        client.Enabled = enabled;
        _audit.Add(enabled ? "client.enabled" : "client.disabled", actor: AuditRecorder.CliActor, clientId: clientId);
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"Client '{clientId}' {(enabled ? "enabled" : "disabled")}.");
    }

    private async Task DeleteAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        args.EnsureConsumed(3);

        var client = await FindAsync(clientId, cancellationToken);
        _db.Clients.Remove(client);
        _audit.Add("client.deleted", actor: AuditRecorder.CliActor, clientId: clientId);
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"Client '{clientId}' deleted.");
    }

    private async Task<Client> FindAsync(string clientId, CancellationToken cancellationToken) =>
        await _db.Clients.Include(c => c.Secrets).AsSplitQuery().SingleOrDefaultAsync(c => c.ClientId == clientId, cancellationToken)
        ?? throw new CliException($"Client '{clientId}' does not exist.");

    private async Task EnsureApiScopesExistAsync(List<string> scopes, CancellationToken cancellationToken)
    {
        var apiScopes = scopes.Where(s => !IdentityScopes.All.Contains(s)).ToList();
        var existing = await _db.ApiScopes.Where(s => apiScopes.Contains(s.Name)).Select(s => s.Name).ToListAsync(cancellationToken);
        var missing = apiScopes.Except(existing, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new CliException($"Unknown scope(s): {string.Join(", ", missing)}. Create them with `resource create` first.");
        }
    }

    private static (ClientSecret Secret, string Plaintext) NewSecret(Client client, string? description, int? expiresDays, DateTimeOffset now)
    {
        var plaintext = SecretHasher.Generate();
        var secret = new ClientSecret
        {
            ClientId = client.Id,
            Hash = SecretHasher.Hash(plaintext),
            Description = description,
            CreatedAt = now,
            ExpiresAt = expiresDays is { } days ? now.AddDays(days) : null,
        };
        client.Secrets.Add(secret);
        return (secret, plaintext);
    }

    private async Task WriteSecretAsync(ClientSecret secret, string plaintext)
    {
        await output.WriteLineAsync($"secret_id: {secret.Id}");
        await output.WriteLineAsync($"client_secret: {plaintext}");
        await output.WriteLineAsync("Store the secret now; it can't be shown again.");
    }
}
