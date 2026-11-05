using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Tokens;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Administration;

/// <summary>A client's configuration, as set by an administrator; validated as a whole on every change.</summary>
internal sealed record ClientSettings(
    string? DisplayName,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris,
    IReadOnlyList<string> CorsOrigins,
    string? BackChannelLogoutUri,
    bool RequirePkce,
    bool AllowClientSecretPost,
    TimeSpan? AccessTokenLifetime,
    TimeSpan? IdTokenLifetime,
    TimeSpan? RefreshTokenSlidingLifetime,
    TimeSpan? RefreshTokenAbsoluteLifetime)
{
    public static ClientSettings From(Client client) => new(
        client.DisplayName,
        client.AllowedGrantTypes,
        client.AllowedScopes,
        client.RedirectUris,
        client.PostLogoutRedirectUris,
        client.AllowedCorsOrigins,
        client.BackChannelLogoutUri,
        client.RequirePkce,
        client.AllowClientSecretPost,
        client.AccessTokenLifetime,
        client.IdTokenLifetime,
        client.RefreshTokenSlidingLifetime,
        client.RefreshTokenAbsoluteLifetime);
}

/// <summary>A newly created secret. The plaintext is returned once and never stored.</summary>
internal sealed record NewSecret(Guid Id, string Plaintext, DateTimeOffset? ExpiresAt);

internal enum ClientList
{
    RedirectUris,
    PostLogoutRedirectUris,
    CorsOrigins,
}

/// <summary>Client management shared by the CLI and the Admin API. Every change is validated and audited.</summary>
internal sealed class ClientAdministration(
    ValkyrieDbContext db,
    RefreshTokenStore refreshTokens,
    AuditRecorder audit,
    TimeProvider timeProvider)
{
    public const int MaxDisplayNameLength = 200;
    public const int MaxSecretDescriptionLength = 200;

    public Task<Client?> FindAsync(string clientId, CancellationToken cancellationToken) =>
        db.Clients.Include(c => c.Secrets).AsSplitQuery().SingleOrDefaultAsync(c => c.ClientId == clientId, cancellationToken);

    public Task<Client?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Clients.Include(c => c.Secrets).AsSplitQuery().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <summary>Creates a client; a confidential client gets its first secret, returned once.</summary>
    public async Task<(Client Client, NewSecret? Secret)> CreateAsync(
        string clientId, ClientType type, ClientSettings settings, int? secretExpiresDays, AuditActor actor, CancellationToken cancellationToken)
    {
        if (!ProtocolSyntax.IsValidClientId(clientId))
        {
            throw new AdministrationException("The client id must be 1-200 printable ASCII characters without spaces.");
        }

        // RFC 9700 §4.15.1: client_credentials tokens carry the client id as sub, and users' sub values are GUIDs, so a
        // GUID-shaped client id could be mistaken for a user by resource servers.
        if (ProtocolSyntax.LooksLikeSubject(clientId))
        {
            throw new AdministrationException("The client id must not be a GUID, so it can't be confused with a user's subject identifier.");
        }

        if (clientId == BuiltIn.AdminUiClientId)
        {
            throw AdministrationException.Conflict($"The client id '{clientId}' is reserved.");
        }

        if (await db.Clients.AnyAsync(c => c.ClientId == clientId, cancellationToken))
        {
            throw AdministrationException.Conflict($"Client '{clientId}' already exists.");
        }

        if (type == ClientType.Public && secretExpiresDays is not null)
        {
            throw new AdministrationException("Public clients have no secret.");
        }

        ValidateSecretExpiry(secretExpiresDays);
        settings = Normalize(settings);
        await ValidateAsync(type, settings, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var client = new Client { ClientId = clientId, Type = type, CreatedAt = now };
        Apply(client, settings);
        var secret = type == ClientType.Public ? null : AddSecret(client, null, secretExpiresDays, now);
        db.Clients.Add(client);
        Audit("client.created", client, actor, new Dictionary<string, object?>
        {
            ["type"] = client.Type.ToString(),
            ["scopes"] = client.AllowedScopes,
            ["grant_types"] = client.AllowedGrantTypes,
            ["redirect_uris"] = client.RedirectUris,
            ["post_logout_redirect_uris"] = client.PostLogoutRedirectUris,
            ["backchannel_logout_uri"] = client.BackChannelLogoutUri,
            ["cors_origins"] = client.AllowedCorsOrigins,
            ["require_pkce"] = client.PkceRequired,
            ["allow_client_secret_post"] = client.AllowClientSecretPost,
            ["secret_id"] = secret?.Id,
        });
        await db.SaveChangesAsync(cancellationToken);
        return (client, secret);
    }

    /// <summary>
    /// Replaces the client's configuration. If a grant type or scope is taken away, the client's refresh tokens are
    /// revoked, so tokens issued under the old configuration can't keep the access it no longer allows.
    /// </summary>
    public async Task UpdateAsync(Client client, ClientSettings settings, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(client);
        settings = Normalize(settings);
        await ValidateAsync(client.Type, settings, cancellationToken);

        var before = ClientSettings.From(client);
        var changed = Changes(before, settings);
        if (changed.Count == 0)
        {
            return;
        }

        var narrowed = before.GrantTypes.Except(settings.GrantTypes, StringComparer.Ordinal).Any()
            || before.Scopes.Except(settings.Scopes, StringComparer.Ordinal).Any();
        Apply(client, settings);
        var revoked = narrowed ? await refreshTokens.RevokeForClientAsync(client.ClientId, cancellationToken) : 0;
        Audit("client.updated", client, actor, new Dictionary<string, object?>
        {
            ["fields"] = string.Join(' ', changed),
            ["refresh_tokens_revoked"] = revoked,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddToListAsync(Client client, ClientList list, string value, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(client);
        var (values, kind) = List(client, list);
        if (values.Contains(value, StringComparer.Ordinal))
        {
            throw AdministrationException.Conflict($"Client '{client.ClientId}' already has {kind.Replace('_', ' ')} '{value}'.");
        }

        await ChangeListAsync(client, list, [.. values, value], $"client.{kind}_added", kind, value, actor, cancellationToken);
    }

    public async Task RemoveFromListAsync(Client client, ClientList list, string value, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(client);
        var (values, kind) = List(client, list);
        if (!values.Contains(value, StringComparer.Ordinal))
        {
            throw AdministrationException.NotFound($"Client '{client.ClientId}' has no {kind.Replace('_', ' ')} '{value}'.");
        }

        await ChangeListAsync(client, list, [.. values.Where(v => v != value)], $"client.{kind}_removed", kind, value, actor, cancellationToken);
    }

    public async Task SetBackChannelLogoutUriAsync(Client client, string? uri, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(client);
        var settings = ClientSettings.From(client) with { BackChannelLogoutUri = uri };
        await ValidateAsync(client.Type, settings, cancellationToken);
        client.BackChannelLogoutUri = uri;
        Audit(uri is null ? "client.backchannel_logout_uri_cleared" : "client.backchannel_logout_uri_set", client, actor,
            new Dictionary<string, object?> { ["backchannel_logout_uri"] = uri });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetSecretPostAsync(Client client, bool allow, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(client);
        if (client.Type == ClientType.Public)
        {
            throw new AdministrationException($"Client '{client.ClientId}' is a public client and has no secret.");
        }

        client.AllowClientSecretPost = allow;
        Audit(allow ? "client.secret_post_enabled" : "client.secret_post_disabled", client, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Disabled clients can't obtain tokens; their refresh tokens are revoked (PROJECT.md §4.3).</summary>
    public async Task SetEnabledAsync(Client client, bool enabled, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(client);
        client.Enabled = enabled;
        Audit(enabled ? "client.enabled" : "client.disabled", client, actor);
        if (!enabled)
        {
            await refreshTokens.RevokeForClientAsync(client.ClientId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Client client, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(client);
        db.Clients.Remove(client);
        Audit("client.deleted", client, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<NewSecret> AddSecretAsync(Client client, string? description, int? expiresDays, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(client);
        if (client.Type == ClientType.Public)
        {
            throw new AdministrationException($"Client '{client.ClientId}' is a public client and can't have secrets.");
        }

        ValidateSecretExpiry(expiresDays);
        description = Clean(description);
        if (description?.Length > MaxSecretDescriptionLength)
        {
            throw new AdministrationException($"The description must be at most {MaxSecretDescriptionLength} characters.");
        }

        var secret = AddSecret(client, description, expiresDays, timeProvider.GetUtcNow());
        Audit("client.secret_added", client, actor, new Dictionary<string, object?> { ["secret_id"] = secret.Id, ["expires_at"] = secret.ExpiresAt });
        await db.SaveChangesAsync(cancellationToken);
        return secret;
    }

    public async Task RemoveSecretAsync(Client client, Guid secretId, AuditActor actor, CancellationToken cancellationToken)
    {
        EnsureEditable(client);
        var secret = client.Secrets.SingleOrDefault(s => s.Id == secretId)
            ?? throw AdministrationException.NotFound($"Client '{client.ClientId}' has no secret '{secretId}'.");
        client.Secrets.Remove(secret);
        Audit("client.secret_removed", client, actor, new Dictionary<string, object?> { ["secret_id"] = secret.Id });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ChangeListAsync(
        Client client, ClientList list, List<string> values, string eventType, string kind, string value, AuditActor actor, CancellationToken cancellationToken)
    {
        var current = ClientSettings.From(client);
        var settings = list switch
        {
            ClientList.RedirectUris => current with { RedirectUris = values },
            ClientList.PostLogoutRedirectUris => current with { PostLogoutRedirectUris = values },
            _ => current with { CorsOrigins = values },
        };
        await ValidateAsync(client.Type, settings, cancellationToken);
        Apply(client, settings);
        Audit(eventType, client, actor, new Dictionary<string, object?> { [kind] = value });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static (IReadOnlyList<string> Values, string Kind) List(Client client, ClientList list) => list switch
    {
        ClientList.RedirectUris => (client.RedirectUris, "redirect_uri"),
        ClientList.PostLogoutRedirectUris => (client.PostLogoutRedirectUris, "post_logout_redirect_uri"),
        _ => (client.AllowedCorsOrigins, "cors_origin"),
    };

    /// <summary>The built-in admin UI client is maintained by the server; changing it could lock administrators out.</summary>
    private static void EnsureEditable(Client client)
    {
        if (client.BuiltIn)
        {
            throw AdministrationException.Conflict($"The built-in client '{client.ClientId}' is managed by the server and can't be changed.");
        }
    }

    private static ClientSettings Normalize(ClientSettings settings) => settings with
    {
        DisplayName = Clean(settings.DisplayName),
        GrantTypes = Distinct(settings.GrantTypes),
        Scopes = Distinct(settings.Scopes),
        RedirectUris = Distinct(settings.RedirectUris),
        PostLogoutRedirectUris = Distinct(settings.PostLogoutRedirectUris),
        CorsOrigins = Distinct(settings.CorsOrigins),
        BackChannelLogoutUri = string.IsNullOrEmpty(settings.BackChannelLogoutUri) ? null : settings.BackChannelLogoutUri,
    };

    private async Task ValidateAsync(ClientType type, ClientSettings settings, CancellationToken cancellationToken)
    {
        List<string> errors = [];
        var grants = settings.GrantTypes;
        var usesCode = grants.Contains(GrantTypes.AuthorizationCode);

        if (settings.DisplayName?.Length > MaxDisplayNameLength)
        {
            errors.Add($"The display name must be at most {MaxDisplayNameLength} characters.");
        }

        if (grants.Count == 0)
        {
            errors.Add("At least one grant type is required.");
        }

        if (grants.FirstOrDefault(g => g is not (GrantTypes.AuthorizationCode or GrantTypes.ClientCredentials or GrantTypes.RefreshToken)) is { } unsupported)
        {
            errors.Add($"Unsupported grant type '{unsupported}'. Use authorization_code, client_credentials, or refresh_token.");
        }

        // Refresh tokens are only issued from an authorization code (with the offline_access scope).
        if (grants.Contains(GrantTypes.RefreshToken) && !usesCode)
        {
            errors.Add("The refresh_token grant needs the authorization_code grant.");
        }

        if (usesCode && settings.RedirectUris.Count == 0)
        {
            errors.Add("The authorization_code grant needs at least one redirect URI.");
        }

        if (!usesCode && (settings.RedirectUris.Count > 0 || settings.PostLogoutRedirectUris.Count > 0 || !settings.RequirePkce))
        {
            errors.Add("Redirect URIs, post-logout redirect URIs, and turning off PKCE only apply to the authorization_code grant.");
        }

        if (type == ClientType.Public)
        {
            // RFC 6749 §4.4: client_credentials is for confidential clients. RFC 9700 §2.1.1: public clients use PKCE.
            if (grants.Contains(GrantTypes.ClientCredentials))
            {
                errors.Add("Public clients can't use the client_credentials grant.");
            }

            if (!settings.RequirePkce)
            {
                errors.Add("Public clients always use PKCE.");
            }

            if (settings.AllowClientSecretPost)
            {
                errors.Add("Public clients have no secret, so client_secret_post doesn't apply.");
            }
        }

        ValidateLifetimes(settings, errors);
        AddInvalid(errors, settings.RedirectUris, RedirectUris.IsValid,
            "Invalid redirect URI '{0}'. Use an absolute https URI without a fragment, http on a loopback host, or a private-use scheme such as com.example.app:/callback.");
        AddInvalid(errors, settings.PostLogoutRedirectUris, RedirectUris.IsValid,
            "Invalid post-logout redirect URI '{0}'. Use an absolute https URI without a fragment, http on a loopback host, or a private-use scheme such as com.example.app:/callback.");
        AddInvalid(errors, settings.CorsOrigins, RedirectUris.IsValidOrigin,
            "Invalid CORS origin '{0}'. Use scheme://host[:port] without a path or trailing slash; https, or http for localhost.");

        if (settings.BackChannelLogoutUri is { } backChannelLogoutUri)
        {
            // Only clients that sign users in have sessions to end.
            if (!usesCode)
            {
                errors.Add("A back-channel logout URI needs the authorization_code grant.");
            }
            else if (!RedirectUris.IsValidBackChannelLogoutUri(backChannelLogoutUri, type))
            {
                errors.Add($"Invalid back-channel logout URI '{backChannelLogoutUri}'. Use an absolute https URI without a fragment; http is only allowed on a loopback host for confidential clients.");
            }
        }

        if (settings.Scopes.Count == 0)
        {
            errors.Add("At least one scope is required.");
        }
        else if (settings.Scopes.Any(s => !ProtocolSyntax.IsValidScopeToken(s)))
        {
            errors.Add("Scopes must be non-empty and contain no spaces or quotes.");
        }
        else
        {
            var apiScopes = settings.Scopes.Where(s => !IdentityScopes.All.Contains(s)).ToList();
            var existing = await db.ApiScopes.Where(s => apiScopes.Contains(s.Name)).Select(s => s.Name).ToListAsync(cancellationToken);
            var missing = apiScopes.Except(existing, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                errors.Add($"Unknown scope(s): {string.Join(", ", missing)}. Create them on an API resource first.");
            }

            // Admin API tokens must represent an admin user (PROJECT.md §9), so the scope is useless without one.
            if (settings.Scopes.Contains(BuiltIn.AdminScope) && !usesCode)
            {
                errors.Add($"The {BuiltIn.AdminScope} scope needs the authorization_code grant: the Admin API only accepts tokens of admin users.");
            }
        }

        if (errors.Count > 0)
        {
            throw new AdministrationException(errors);
        }
    }

    private static void ValidateLifetimes(ClientSettings settings, List<string> errors)
    {
        if (settings.AccessTokenLifetime <= TimeSpan.Zero || settings.IdTokenLifetime <= TimeSpan.Zero)
        {
            errors.Add("Token lifetimes must be positive.");
        }

        var sliding = settings.RefreshTokenSlidingLifetime;
        var absolute = settings.RefreshTokenAbsoluteLifetime;
        if ((sliding is not null || absolute is not null) && !settings.GrantTypes.Contains(GrantTypes.RefreshToken))
        {
            errors.Add("Refresh token lifetimes only apply to clients with the refresh_token grant.");
        }

        if (sliding <= TimeSpan.Zero || absolute <= TimeSpan.Zero)
        {
            errors.Add("Refresh token lifetimes must be positive.");
        }

        if (sliding is { } s && absolute is { } a && a < s)
        {
            errors.Add("The absolute refresh token lifetime must be at least the sliding lifetime.");
        }
    }

    private static void ValidateSecretExpiry(int? expiresDays)
    {
        if (expiresDays is <= 0)
        {
            throw new AdministrationException("The secret expiry must be a positive number of days.");
        }
    }

    private static void AddInvalid(List<string> errors, IReadOnlyList<string> values, Func<string, bool> isValid, string format)
    {
        if (values.FirstOrDefault(v => !isValid(v)) is { } invalid)
        {
            errors.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, invalid));
        }
    }

    private static void Apply(Client client, ClientSettings settings)
    {
        client.DisplayName = settings.DisplayName;
        // New lists, so EF Core detects changes to the primitive collections.
        client.AllowedGrantTypes = [.. settings.GrantTypes];
        client.AllowedScopes = [.. settings.Scopes];
        client.RedirectUris = [.. settings.RedirectUris];
        client.PostLogoutRedirectUris = [.. settings.PostLogoutRedirectUris];
        client.AllowedCorsOrigins = [.. settings.CorsOrigins];
        client.BackChannelLogoutUri = settings.BackChannelLogoutUri;
        client.RequirePkce = settings.RequirePkce;
        client.AllowClientSecretPost = settings.AllowClientSecretPost;
        client.AccessTokenLifetime = settings.AccessTokenLifetime;
        client.IdTokenLifetime = settings.IdTokenLifetime;
        client.RefreshTokenSlidingLifetime = settings.RefreshTokenSlidingLifetime;
        client.RefreshTokenAbsoluteLifetime = settings.RefreshTokenAbsoluteLifetime;
    }

    private static List<string> Changes(ClientSettings before, ClientSettings after)
    {
        List<string> changed = [];
        Check("display_name", before.DisplayName == after.DisplayName);
        Check("grant_types", before.GrantTypes.SequenceEqual(after.GrantTypes, StringComparer.Ordinal));
        Check("scopes", before.Scopes.SequenceEqual(after.Scopes, StringComparer.Ordinal));
        Check("redirect_uris", before.RedirectUris.SequenceEqual(after.RedirectUris, StringComparer.Ordinal));
        Check("post_logout_redirect_uris", before.PostLogoutRedirectUris.SequenceEqual(after.PostLogoutRedirectUris, StringComparer.Ordinal));
        Check("cors_origins", before.CorsOrigins.SequenceEqual(after.CorsOrigins, StringComparer.Ordinal));
        Check("backchannel_logout_uri", before.BackChannelLogoutUri == after.BackChannelLogoutUri);
        Check("require_pkce", before.RequirePkce == after.RequirePkce);
        Check("allow_client_secret_post", before.AllowClientSecretPost == after.AllowClientSecretPost);
        Check("access_token_lifetime", before.AccessTokenLifetime == after.AccessTokenLifetime);
        Check("id_token_lifetime", before.IdTokenLifetime == after.IdTokenLifetime);
        Check("refresh_token_lifetime", before.RefreshTokenSlidingLifetime == after.RefreshTokenSlidingLifetime);
        Check("refresh_token_absolute_lifetime", before.RefreshTokenAbsoluteLifetime == after.RefreshTokenAbsoluteLifetime);
        return changed;

        void Check(string field, bool same)
        {
            if (!same)
            {
                changed.Add(field);
            }
        }
    }

    private static NewSecret AddSecret(Client client, string? description, int? expiresDays, DateTimeOffset now)
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
        return new NewSecret(secret.Id, plaintext, secret.ExpiresAt);
    }

    private static List<string> Distinct(IReadOnlyList<string> values) => [.. values.Distinct(StringComparer.Ordinal)];

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Audit(string eventType, Client client, AuditActor actor, IReadOnlyDictionary<string, object?>? details = null) =>
        audit.Add(eventType, actor: actor.Name, ipAddress: actor.IpAddress, clientId: client.ClientId, details: details);
}
