using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Tokens;

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
        "add-redirect-uri" => EditListAsync(args, "redirect_uri", c => c.RedirectUris, RedirectUris.IsValid, add: true, cancellationToken),
        "remove-redirect-uri" => EditListAsync(args, "redirect_uri", c => c.RedirectUris, RedirectUris.IsValid, add: false, cancellationToken),
        "add-post-logout-redirect-uri" => EditListAsync(args, "post_logout_redirect_uri", c => c.PostLogoutRedirectUris, RedirectUris.IsValid, add: true, cancellationToken),
        "remove-post-logout-redirect-uri" => EditListAsync(args, "post_logout_redirect_uri", c => c.PostLogoutRedirectUris, RedirectUris.IsValid, add: false, cancellationToken),
        "add-cors-origin" => EditListAsync(args, "cors_origin", c => c.AllowedCorsOrigins, RedirectUris.IsValidOrigin, add: true, cancellationToken),
        "remove-cors-origin" => EditListAsync(args, "cors_origin", c => c.AllowedCorsOrigins, RedirectUris.IsValidOrigin, add: false, cancellationToken),
        "set-backchannel-logout-uri" => SetBackChannelLogoutUriAsync(args, set: true, cancellationToken),
        "clear-backchannel-logout-uri" => SetBackChannelLogoutUriAsync(args, set: false, cancellationToken),
        "enable-secret-post" => SetSecretPostAsync(args, true, cancellationToken),
        "disable-secret-post" => SetSecretPostAsync(args, false, cancellationToken),
        var other => throw new CliException($"Unknown client command '{other}'."),
    };

    private async Task CreateAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var scopes = args.Many("scope").Distinct(StringComparer.Ordinal).ToList();
        var name = args.Optional("name");
        var lifetime = args.OptionalInt("access-token-lifetime");
        var idTokenLifetime = args.OptionalInt("id-token-lifetime");
        var refreshSliding = args.OptionalInt("refresh-token-lifetime");
        var refreshAbsolute = args.OptionalInt("refresh-token-absolute-lifetime");
        var expiresDays = args.OptionalInt("secret-expires-days");
        var grants = args.Many("grant").Distinct(StringComparer.Ordinal).ToList();
        var redirectUris = args.Many("redirect-uri").Distinct(StringComparer.Ordinal).ToList();
        var postLogoutRedirectUris = args.Many("post-logout-redirect-uri").Distinct(StringComparer.Ordinal).ToList();
        var corsOrigins = args.Many("cors-origin").Distinct(StringComparer.Ordinal).ToList();
        var backChannelLogoutUri = args.Optional("backchannel-logout-uri");
        var isPublic = args.Flag("public");
        var noPkce = args.Flag("no-pkce");
        var allowSecretPost = args.Flag("allow-secret-post");
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

        if (grants.Count == 0)
        {
            grants = [GrantTypes.ClientCredentials];
        }

        ValidateGrants(grants, redirectUris, isPublic, noPkce, expiresDays);
        if (isPublic && allowSecretPost)
        {
            throw new CliException("Public clients have no secret, so --allow-secret-post doesn't apply.");
        }
        ValidateRefreshTokenLifetimes(grants, refreshSliding, refreshAbsolute);
        EnsureValid(redirectUris, RedirectUris.IsValid, "redirect URI");
        EnsureValid(postLogoutRedirectUris, RedirectUris.IsValid, "post-logout redirect URI");
        EnsureValid(corsOrigins, RedirectUris.IsValidOrigin, "CORS origin");
        var type = isPublic ? ClientType.Public : ClientType.Confidential;
        if (backChannelLogoutUri is not null)
        {
            EnsureValidBackChannelLogoutUri(backChannelLogoutUri, type, grants);
        }
        if (scopes.Any(s => !ProtocolSyntax.IsValidScopeToken(s)))
        {
            throw new CliException("Scopes must be non-empty and contain no spaces or quotes.");
        }

        await EnsureApiScopesExistAsync(scopes, cancellationToken);

        var now = _time.GetUtcNow();
        var client = new Client
        {
            ClientId = clientId,
            DisplayName = name,
            Type = type,
            AllowedGrantTypes = grants,
            AllowedScopes = scopes,
            RedirectUris = redirectUris,
            PostLogoutRedirectUris = postLogoutRedirectUris,
            BackChannelLogoutUri = backChannelLogoutUri,
            AllowedCorsOrigins = corsOrigins,
            RequirePkce = !noPkce,
            AllowClientSecretPost = allowSecretPost,
            AccessTokenLifetime = lifetime is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            IdTokenLifetime = idTokenLifetime is { } idSeconds ? TimeSpan.FromSeconds(idSeconds) : null,
            RefreshTokenSlidingLifetime = refreshSliding is { } slidingSeconds ? TimeSpan.FromSeconds(slidingSeconds) : null,
            RefreshTokenAbsoluteLifetime = refreshAbsolute is { } absoluteSeconds ? TimeSpan.FromSeconds(absoluteSeconds) : null,
            CreatedAt = now,
        };
        (ClientSecret Secret, string Plaintext)? secret = isPublic ? null : NewSecret(client, null, expiresDays, now);
        _db.Clients.Add(client);
        _audit.Add("client.created", actor: AuditRecorder.CliActor, clientId: clientId, details: new Dictionary<string, object?>
        {
            ["type"] = client.Type.ToString(),
            ["scopes"] = scopes,
            ["grant_types"] = client.AllowedGrantTypes,
            ["redirect_uris"] = redirectUris,
            ["post_logout_redirect_uris"] = postLogoutRedirectUris,
            ["backchannel_logout_uri"] = backChannelLogoutUri,
            ["cors_origins"] = corsOrigins,
            ["require_pkce"] = client.PkceRequired,
            ["allow_client_secret_post"] = client.AllowClientSecretPost,
            ["secret_id"] = secret?.Secret.Id,
        });
        await _db.SaveChangesAsync(cancellationToken);

        await output.WriteLineAsync($"client_id: {clientId}");
        if (secret is { } created)
        {
            await WriteSecretAsync(created.Secret, created.Plaintext);
        }
    }

    private static void ValidateGrants(List<string> grants, List<string> redirectUris, bool isPublic, bool noPkce, int? expiresDays)
    {
        var unsupported = grants.FirstOrDefault(g => g is not (GrantTypes.AuthorizationCode or GrantTypes.ClientCredentials or GrantTypes.RefreshToken));
        if (unsupported is not null)
        {
            throw new CliException($"Unsupported grant type '{unsupported}'. Use authorization_code, client_credentials, or refresh_token.");
        }

        var usesCode = grants.Contains(GrantTypes.AuthorizationCode);

        // Refresh tokens are only issued from an authorization code (with the offline_access scope).
        if (grants.Contains(GrantTypes.RefreshToken) && !usesCode)
        {
            throw new CliException("The refresh_token grant needs the authorization_code grant.");
        }
        if (usesCode && redirectUris.Count == 0)
        {
            throw new CliException("The authorization_code grant needs at least one --redirect-uri.");
        }

        if (!usesCode && (redirectUris.Count > 0 || noPkce))
        {
            throw new CliException("--redirect-uri and --no-pkce only apply to the authorization_code grant.");
        }

        if (isPublic)
        {
            // RFC 6749 A4.4: client_credentials is for confidential clients. Public clients always use PKCE.
            if (grants.Contains(GrantTypes.ClientCredentials))
            {
                throw new CliException("Public clients can't use the client_credentials grant.");
            }

            if (noPkce || expiresDays is not null)
            {
                throw new CliException("Public clients have no secret and always use PKCE.");
            }
        }
    }

    private static void ValidateRefreshTokenLifetimes(List<string> grants, int? sliding, int? absolute)
    {
        if ((sliding is not null || absolute is not null) && !grants.Contains(GrantTypes.RefreshToken))
        {
            throw new CliException("Refresh token lifetimes only apply to clients with the refresh_token grant.");
        }

        if (sliding is <= 0 || absolute is <= 0)
        {
            throw new CliException("Refresh token lifetimes must be positive.");
        }

        if (sliding is { } s && absolute is { } a && a < s)
        {
            throw new CliException("--refresh-token-absolute-lifetime must be at least --refresh-token-lifetime.");
        }
    }

    private static void EnsureValid(List<string> values, Func<string, bool> isValid, string kind)
    {
        var invalid = values.FirstOrDefault(v => !isValid(v));
        if (invalid is not null)
        {
            throw new CliException(kind == "CORS origin"
                ? $"Invalid CORS origin '{invalid}'. Use scheme://host[:port] without a path or trailing slash; https, or http for localhost."
                : $"Invalid {kind} '{invalid}'. Use an absolute https URI without a fragment, http on a loopback host, or a private-use scheme such as com.example.app:/callback.");
        }
    }

    private static void EnsureValidBackChannelLogoutUri(string uri, ClientType type, IReadOnlyList<string> grants)
    {
        // Only clients that sign users in have sessions to end.
        if (!grants.Contains(GrantTypes.AuthorizationCode))
        {
            throw new CliException("A back-channel logout URI needs the authorization_code grant.");
        }

        if (!RedirectUris.IsValidBackChannelLogoutUri(uri, type))
        {
            throw new CliException(
                $"Invalid back-channel logout URI '{uri}'. Use an absolute https URI without a fragment; http is only allowed on a loopback host for confidential clients.");
        }
    }

    private async Task SetBackChannelLogoutUriAsync(CliArguments args, bool set, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var uri = set ? args.Argument(3, "uri") : null;
        args.EnsureConsumed(set ? 4 : 3);

        var client = await FindAsync(clientId, cancellationToken);
        if (uri is not null)
        {
            EnsureValidBackChannelLogoutUri(uri, client.Type, client.AllowedGrantTypes);
        }

        client.BackChannelLogoutUri = uri;
        _audit.Add(set ? "client.backchannel_logout_uri_set" : "client.backchannel_logout_uri_cleared", actor: AuditRecorder.CliActor, clientId: clientId,
            details: new Dictionary<string, object?> { ["backchannel_logout_uri"] = uri });
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync(set ? $"Set back-channel logout URI '{uri}'." : "Cleared the back-channel logout URI.");
    }

    private async Task EditListAsync(
        CliArguments args,
        string kind,
        Func<Client, List<string>> list,
        Func<string, bool> isValid,
        bool add,
        CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        var value = args.Argument(3, "value");
        args.EnsureConsumed(4);

        var client = await FindAsync(clientId, cancellationToken);
        var values = list(client);
        if (add)
        {
            EnsureValid([value], isValid, kind == "cors_origin" ? "CORS origin" : kind.Replace('_', ' '));
            if (kind == "redirect_uri" && !client.AllowedGrantTypes.Contains(GrantTypes.AuthorizationCode))
            {
                throw new CliException($"Client '{clientId}' doesn't use the authorization_code grant.");
            }

            if (values.Contains(value, StringComparer.Ordinal))
            {
                throw new CliException($"Client '{clientId}' already has {kind.Replace('_', ' ')} '{value}'.");
            }

            values.Add(value);
        }
        else
        {
            if (!values.Remove(value))
            {
                throw new CliException($"Client '{clientId}' has no {kind.Replace('_', ' ')} '{value}'.");
            }

            if (kind == "redirect_uri" && values.Count == 0 && client.AllowedGrantTypes.Contains(GrantTypes.AuthorizationCode))
            {
                throw new CliException("A client using the authorization_code grant needs at least one redirect URI.");
            }
        }

        // Assign a new list so EF Core detects the change to the primitive collection.
        values = [.. values];
        switch (kind)
        {
            case "redirect_uri": client.RedirectUris = values; break;
            case "post_logout_redirect_uri": client.PostLogoutRedirectUris = values; break;
            default: client.AllowedCorsOrigins = values; break;
        }

        _audit.Add($"client.{kind}_{(add ? "added" : "removed")}", actor: AuditRecorder.CliActor, clientId: clientId,
            details: new Dictionary<string, object?> { [kind] = value });
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"{(add ? "Added" : "Removed")} {kind.Replace('_', ' ')} '{value}'.");
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
                $"{client.ClientId}  {(client.Enabled ? "enabled" : "disabled")}  {client.Type}  grants: {string.Join(' ', client.AllowedGrantTypes)}  scopes: {string.Join(' ', client.AllowedScopes)}{(client.AllowClientSecretPost ? "  client_secret_post" : "")}");
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
        if (client.Type == ClientType.Public)
        {
            throw new CliException($"Client '{clientId}' is a public client and can't have secrets.");
        }

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

    private async Task SetSecretPostAsync(CliArguments args, bool allow, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        args.EnsureConsumed(3);

        var client = await FindAsync(clientId, cancellationToken);
        if (client.Type == ClientType.Public)
        {
            throw new CliException($"Client '{clientId}' is a public client and has no secret.");
        }

        client.AllowClientSecretPost = allow;
        _audit.Add(allow ? "client.secret_post_enabled" : "client.secret_post_disabled", actor: AuditRecorder.CliActor, clientId: clientId);
        await _db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"client_secret_post {(allow ? "enabled" : "disabled")} for '{clientId}'.");
    }

    private async Task SetEnabledAsync(CliArguments args, bool enabled, CancellationToken cancellationToken)
    {
        var clientId = args.Argument(2, "client-id");
        args.EnsureConsumed(3);

        var client = await FindAsync(clientId, cancellationToken);
        client.Enabled = enabled;
        _audit.Add(enabled ? "client.enabled" : "client.disabled", actor: AuditRecorder.CliActor, clientId: clientId);
        if (!enabled)
        {
            // PROJECT.md §3: disabled clients can't obtain tokens; their refresh tokens are revoked.
            await services.GetRequiredService<RefreshTokenStore>().RevokeForClientAsync(clientId, cancellationToken);
        }

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
