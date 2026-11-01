using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;

namespace ValkyrieID.Server.Cli;

internal sealed class ClientCommands(IServiceProvider services, TextWriter output)
{
    private readonly ValkyrieDbContext _db = services.GetRequiredService<ValkyrieDbContext>();
    private readonly ClientAdministration _clients = services.GetRequiredService<ClientAdministration>();
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
        "add-redirect-uri" => EditListAsync(args, ClientList.RedirectUris, add: true, cancellationToken),
        "remove-redirect-uri" => EditListAsync(args, ClientList.RedirectUris, add: false, cancellationToken),
        "add-post-logout-redirect-uri" => EditListAsync(args, ClientList.PostLogoutRedirectUris, add: true, cancellationToken),
        "remove-post-logout-redirect-uri" => EditListAsync(args, ClientList.PostLogoutRedirectUris, add: false, cancellationToken),
        "add-cors-origin" => EditListAsync(args, ClientList.CorsOrigins, add: true, cancellationToken),
        "remove-cors-origin" => EditListAsync(args, ClientList.CorsOrigins, add: false, cancellationToken),
        "set-backchannel-logout-uri" => SetBackChannelLogoutUriAsync(args, set: true, cancellationToken),
        "clear-backchannel-logout-uri" => SetBackChannelLogoutUriAsync(args, set: false, cancellationToken),
        "enable-secret-post" => SetSecretPostAsync(args, true, cancellationToken),
        "disable-secret-post" => SetSecretPostAsync(args, false, cancellationToken),
        var other => throw new CliException($"Unknown client command '{other}'."),
    };

    private async Task CreateAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var scopes = args.Many("scope").ToList();
        var name = args.Optional("name");
        var lifetime = args.OptionalInt("access-token-lifetime");
        var idTokenLifetime = args.OptionalInt("id-token-lifetime");
        var refreshSliding = args.OptionalInt("refresh-token-lifetime");
        var refreshAbsolute = args.OptionalInt("refresh-token-absolute-lifetime");
        var expiresDays = args.OptionalInt("secret-expires-days");
        var grants = args.Many("grant").ToList();
        var redirectUris = args.Many("redirect-uri").ToList();
        var postLogoutRedirectUris = args.Many("post-logout-redirect-uri").ToList();
        var corsOrigins = args.Many("cors-origin").ToList();
        var backChannelLogoutUri = args.Optional("backchannel-logout-uri");
        var isPublic = args.Flag("public");
        var noPkce = args.Flag("no-pkce");
        var allowSecretPost = args.Flag("allow-secret-post");
        args.EnsureConsumed(3);

        var settings = new ClientSettings(
            name,
            grants.Count == 0 ? [GrantTypes.ClientCredentials] : grants,
            scopes,
            redirectUris,
            postLogoutRedirectUris,
            corsOrigins,
            backChannelLogoutUri,
            RequirePkce: !noPkce,
            AllowClientSecretPost: allowSecretPost,
            Seconds(lifetime),
            Seconds(idTokenLifetime),
            Seconds(refreshSliding),
            Seconds(refreshAbsolute));
        var type = isPublic ? ClientType.Public : ClientType.Confidential;
        var (_, secret) = await Run(() => _clients.CreateAsync(clientId, type, settings, expiresDays, UserCommands.Actor, cancellationToken));

        await output.WriteLineAsync($"client_id: {clientId}");
        if (secret is not null)
        {
            await WriteSecretAsync(secret);
        }
    }

    private async Task SetBackChannelLogoutUriAsync(CliArguments args, bool set, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var uri = set ? args.Argument(3, "uri") : null;
        args.EnsureConsumed(set ? 4 : 3);

        var client = await FindAsync(clientId, cancellationToken);
        await Run(() => _clients.SetBackChannelLogoutUriAsync(client, uri, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync(set ? $"Set back-channel logout URI '{uri}'." : "Cleared the back-channel logout URI.");
    }

    private async Task EditListAsync(CliArguments args, ClientList list, bool add, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var value = args.Argument(3, "value");
        args.EnsureConsumed(4);

        var client = await FindAsync(clientId, cancellationToken);
        await Run(() => add
            ? _clients.AddToListAsync(client, list, value, UserCommands.Actor, cancellationToken)
            : _clients.RemoveFromListAsync(client, list, value, UserCommands.Actor, cancellationToken));
        var kind = list switch
        {
            ClientList.RedirectUris => "redirect uri",
            ClientList.PostLogoutRedirectUris => "post logout redirect uri",
            _ => "cors origin",
        };
        await output.WriteLineAsync($"{(add ? "Added" : "Removed")} {kind} '{value}'.");
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
                $"{client.ClientId}  {(client.Enabled ? "enabled" : "disabled")}  {client.Type}  grants: {string.Join(' ', client.AllowedGrantTypes)}  scopes: {string.Join(' ', client.AllowedScopes)}{(client.AllowClientSecretPost ? "  client_secret_post" : "")}{(client.BuiltIn ? "  built-in" : "")}");
            foreach (var uri in client.RedirectUris)
            {
                await output.WriteLineAsync($"  redirect_uri {uri}");
            }

            foreach (var uri in client.PostLogoutRedirectUris)
            {
                await output.WriteLineAsync($"  post_logout_redirect_uri {uri}");
            }

            if (client.BackChannelLogoutUri is { } backChannelLogoutUri)
            {
                await output.WriteLineAsync($"  backchannel_logout_uri {backChannelLogoutUri}");
            }

            foreach (var origin in client.AllowedCorsOrigins)
            {
                await output.WriteLineAsync($"  cors_origin {origin}");
            }

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
        var secret = await Run(() => _clients.AddSecretAsync(client, description, expiresDays, UserCommands.Actor, cancellationToken));
        await WriteSecretAsync(secret);
    }

    private async Task RemoveSecretAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var secretIdText = args.Argument(3, "secret-id");
        args.EnsureConsumed(4);

        var client = await FindAsync(clientId, cancellationToken);
        if (!Guid.TryParse(secretIdText, out var secretId))
        {
            throw new CliException($"Client '{clientId}' has no secret '{secretIdText}'.");
        }

        await Run(() => _clients.RemoveSecretAsync(client, secretId, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"Removed secret {secretId}.");
    }

    private async Task SetSecretPostAsync(CliArguments args, bool allow, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        args.EnsureConsumed(3);

        var client = await FindAsync(clientId, cancellationToken);
        await Run(() => _clients.SetSecretPostAsync(client, allow, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"client_secret_post {(allow ? "enabled" : "disabled")} for '{clientId}'.");
    }

    private async Task SetEnabledAsync(CliArguments args, bool enabled, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        args.EnsureConsumed(3);

        var client = await FindAsync(clientId, cancellationToken);
        await Run(() => _clients.SetEnabledAsync(client, enabled, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"Client '{clientId}' {(enabled ? "enabled" : "disabled")}.");
    }

    private async Task DeleteAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        args.EnsureConsumed(3);

        var client = await FindAsync(clientId, cancellationToken);
        await Run(() => _clients.DeleteAsync(client, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"Client '{clientId}' deleted.");
    }

    private async Task<Client> FindAsync(string clientId, CancellationToken cancellationToken) =>
        await _clients.FindAsync(clientId, cancellationToken)
        ?? throw new CliException($"Client '{clientId}' does not exist.");

    private static TimeSpan? Seconds(int? seconds) => seconds is { } value ? TimeSpan.FromSeconds(value) : null;

    private static Task<T> Run<T>(Func<Task<T>> action) => UserCommands.Run(action);

    private static Task Run(Func<Task> action) => UserCommands.Run(action);

    private async Task WriteSecretAsync(NewSecret secret)
    {
        await output.WriteLineAsync($"secret_id: {secret.Id}");
        await output.WriteLineAsync($"client_secret: {secret.Plaintext}");
        await output.WriteLineAsync("Store the secret now; it can't be shown again.");
    }
}
