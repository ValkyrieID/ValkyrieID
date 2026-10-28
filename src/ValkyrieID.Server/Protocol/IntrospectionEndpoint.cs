using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Protocol;

/// <summary>An RFC 7662 §2.2 introspection response. Inactive tokens only carry <c>active: false</c>.</summary>
internal sealed record IntrospectionResponse(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("scope"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null,
    [property: JsonPropertyName("client_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientId = null,
    [property: JsonPropertyName("token_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TokenType = null,
    [property: JsonPropertyName("exp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Expires = null,
    [property: JsonPropertyName("iat"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? IssuedAt = null,
    [property: JsonPropertyName("nbf"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? NotBefore = null,
    [property: JsonPropertyName("sub"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subject = null,
    [property: JsonPropertyName("aud"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Audience = null,
    [property: JsonPropertyName("iss"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Issuer = null,
    [property: JsonPropertyName("jti"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? JwtId = null);

/// <summary>Who is calling the introspection endpoint: a confidential client or an API resource.</summary>
internal sealed record IntrospectionCaller(string? ClientId, string? ResourceName)
{
    public string Name => ClientId ?? ResourceName ?? string.Empty;
}

/// <summary>
/// Token introspection (RFC 7662). Callers authenticate as a confidential client or as an API resource with a secret
/// (RFC 7662 §2.1, §4: the endpoint MUST require authorization, to prevent token scanning). A caller only learns
/// about tokens issued to it or whose audience includes it; anything else is reported as inactive.
/// </summary>
internal sealed class IntrospectionEndpoint(
    ValkyrieDbContext db,
    RefreshTokenStore refreshTokens,
    AccessTokenValidator accessTokens,
    AuditRecorder audit,
    TimeProvider timeProvider,
    IOptions<IssuerOptions> issuerOptions)
{
    public const string Path = "/connect/introspect";

    private static readonly IntrospectionResponse Inactive = new(false);

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var (form, error) = await ProtocolForm.ReadAsync(context);
        if (form is null || error is not null)
        {
            return error ?? OAuthError.Request("The request is malformed.");
        }

        var caller = await AuthenticateAsync(context, form, cancellationToken);
        if (caller is null)
        {
            return OAuthError.Client();
        }

        var token = form["token"].ToString();
        if (token.Length == 0)
        {
            return OAuthError.Request("The token parameter is required.");
        }

        // RFC 7662 §2.1: token_type_hint only optimizes the lookup; both types are searched.
        var response = form["token_type_hint"].ToString() == "access_token"
            ? await AccessTokenAsync(token, caller, cancellationToken) ?? await RefreshTokenAsync(token, caller, cancellationToken)
            : await RefreshTokenAsync(token, caller, cancellationToken) ?? await AccessTokenAsync(token, caller, cancellationToken);

        ProtocolHeaders.NoStore(context.Response);
        return Results.Json(response ?? Inactive);
    }

    /// <summary>Refresh tokens are looked up in the database, so revocation, rotation, and reuse are reflected exactly.</summary>
    private async Task<IntrospectionResponse?> RefreshTokenAsync(string token, IntrospectionCaller caller, CancellationToken cancellationToken)
    {
        var refreshToken = await refreshTokens.FindAsync(token, cancellationToken);
        if (refreshToken is null)
        {
            return null;
        }

        // Only the client a refresh token was issued to may see it; refresh tokens have no audience.
        var now = timeProvider.GetUtcNow();
        if (!string.Equals(refreshToken.ClientId, caller.ClientId, StringComparison.Ordinal) || !refreshToken.IsActive(now))
        {
            return Inactive;
        }

        var usable = await db.Users.AnyAsync(u => u.Id == refreshToken.UserId && u.Enabled, cancellationToken)
            && await db.Clients.AnyAsync(c => c.ClientId == refreshToken.ClientId && c.Enabled, cancellationToken);
        if (!usable)
        {
            return Inactive;
        }

        return new IntrospectionResponse(
            true,
            Scope: string.Join(' ', refreshToken.Scopes),
            ClientId: refreshToken.ClientId,
            Expires: refreshToken.ExpiresAt.ToUnixTimeSeconds(),
            IssuedAt: refreshToken.CreatedAt.ToUnixTimeSeconds(),
            Subject: refreshToken.UserId.ToString(),
            Issuer: issuerOptions.Value.Issuer);
    }

    /// <summary>Access tokens are validated statelessly: signature, issuer, typ, and lifetime.</summary>
    private async Task<IntrospectionResponse?> AccessTokenAsync(string token, IntrospectionCaller caller, CancellationToken cancellationToken)
    {
        var jwt = await accessTokens.ValidateAsync(token, cancellationToken);
        if (jwt is null)
        {
            return null;
        }

        var clientId = jwt.TryGetPayloadValue<string>("client_id", out var value) ? value : null;
        var audiences = jwt.Audiences.ToList();
        var allowed = (caller.ClientId is not null && string.Equals(clientId, caller.ClientId, StringComparison.Ordinal))
            || (caller.ResourceName is not null && audiences.Contains(caller.ResourceName, StringComparer.Ordinal));
        if (!allowed)
        {
            return Inactive;
        }

        return new IntrospectionResponse(
            true,
            Scope: jwt.TryGetPayloadValue<string>("scope", out var scope) ? scope : null,
            ClientId: clientId,
            TokenType: "Bearer",
            Expires: UnixTime(jwt.ValidTo),
            IssuedAt: UnixTime(jwt.IssuedAt),
            NotBefore: UnixTime(jwt.ValidFrom),
            Subject: jwt.Subject,
            Audience: audiences,
            Issuer: jwt.Issuer,
            JwtId: jwt.Id);
    }

    /// <summary>
    /// Authenticates with <c>client_secret_basic</c> or <c>client_secret_post</c> credentials, checked against the
    /// confidential client and the API resource with that identifier. Both are always hashed and compared, so timing
    /// doesn't reveal which identifiers exist.
    /// </summary>
    private async Task<IntrospectionCaller?> AuthenticateAsync(HttpContext context, IFormCollection form, CancellationToken cancellationToken)
    {
        string? id;
        string? secret;
        var bodyId = form.TryGetValue("client_id", out var bodyIdValue) ? bodyIdValue.ToString() : null;
        var bodySecret = form.TryGetValue("client_secret", out var bodySecretValue) ? bodySecretValue.ToString() : null;
        if (context.Request.Headers.Authorization.Count > 0)
        {
            if (bodySecret is not null
                || !ClientAuthenticator.TryParseBasic(context.Request.Headers.Authorization.ToString(), out id, out secret)
                || (bodyId is not null && bodyId != id))
            {
                return await RejectAsync(context, null, "malformed_credentials", cancellationToken);
            }
        }
        else
        {
            id = bodyId;
            secret = bodySecret;
        }

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
        {
            return await RejectAsync(context, id, "missing_credentials", cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var client = await db.Clients.AsNoTracking().Include(c => c.Secrets).AsSplitQuery()
            .SingleOrDefaultAsync(c => c.ClientId == id && c.Enabled && c.Type == ClientType.Confidential, cancellationToken);
        var resource = await db.ApiResources.AsNoTracking().Include(r => r.Secrets).AsSplitQuery()
            .SingleOrDefaultAsync(r => r.Name == id && r.Enabled, cancellationToken);

        var clientMatch = SecretHasher.Verify(secret, client?.Secrets.Where(s => s.IsActive(now)).Select(s => s.Hash) ?? []);
        var resourceMatch = SecretHasher.Verify(
            secret,
            resource?.Secrets.Where(s => s.IsActive(now)).Select(s => s.Hash) ?? []);

        if (clientMatch)
        {
            return new IntrospectionCaller(id, null);
        }

        if (resourceMatch)
        {
            return new IntrospectionCaller(null, id);
        }

        return await RejectAsync(context, id, "invalid_credentials", cancellationToken);
    }

    private async Task<IntrospectionCaller?> RejectAsync(HttpContext context, string? id, string reason, CancellationToken cancellationToken)
    {
        audit.Add(
            "introspection.authentication_failed",
            success: false,
            actor: id,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            details: new Dictionary<string, object?> { ["reason"] = reason });
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    private static long? UnixTime(DateTime value) =>
        value == DateTime.MinValue ? null : new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds();
}
