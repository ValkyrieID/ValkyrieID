using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Protocol;

internal sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("id_token"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IdToken = null);

internal sealed class TokenResult(TokenResponse response) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ProtocolHeaders.NoStore(httpContext.Response);
        return httpContext.Response.WriteAsJsonAsync(response, httpContext.RequestAborted);
    }
}

/// <summary>The token endpoint (RFC 6749 §3.2). Phase 2 supports the <c>client_credentials</c> grant.</summary>
internal sealed class TokenEndpoint(
    ValkyrieDbContext db,
    ClientAuthenticator authenticator,
    AccessTokenIssuer issuer,
    IdTokenIssuer idTokenIssuer,
    IAuthorizationCodeStore codes,
    AuditRecorder audit,
    TimeProvider timeProvider,
    IOptions<TokenOptions> tokenOptions)
{
    public const string Path = "/connect/token";

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        // RFC 6749 §3.2: POST with application/x-www-form-urlencoded parameters.
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return OAuthError.Request("The request must use application/x-www-form-urlencoded.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);

        // RFC 6749 §3.1/§3.2: parameters MUST NOT be included more than once.
        if (form.Any(p => p.Value.Count > 1))
        {
            return OAuthError.Request("Request parameters must not be repeated.");
        }

        var grantType = form["grant_type"].ToString();
        if (string.IsNullOrEmpty(grantType))
        {
            return OAuthError.Request("The grant_type parameter is required.");
        }

        var authentication = await authenticator.AuthenticateAsync(context, form, cancellationToken);
        if (authentication is not { Client: { } client })
        {
            return authentication.Error ?? OAuthError.Client();
        }

        return grantType switch
        {
            GrantTypes.AuthorizationCode => await AuthorizationCodeAsync(context, client, form, cancellationToken),
            GrantTypes.ClientCredentials => await ClientCredentialsAsync(context, client, form, cancellationToken),
            _ => new OAuthError(OAuthError.UnsupportedGrantType, "The grant type is not supported."),
        };
    }

    /// <summary>RFC 6749 §4.4.</summary>
    private async Task<IResult> ClientCredentialsAsync(HttpContext context, Client client, IFormCollection form, CancellationToken cancellationToken)
    {
        // RFC 6749 A4.4: client credentials are for confidential clients only.
        if (!client.AllowedGrantTypes.Contains(GrantTypes.ClientCredentials) || client.Type != ClientType.Confidential)
        {
            return new OAuthError(OAuthError.UnauthorizedClient, "The client is not allowed to use this grant type.");
        }

        List<string> requested;
        if (form.TryGetValue("scope", out var scopeValue))
        {
            // RFC 6749 §3.3: scope = scope-token *( SP scope-token ).
            requested = [.. scopeValue.ToString().Split(' ').Distinct(StringComparer.Ordinal)];
            if (requested.Any(s => !ProtocolSyntax.IsValidScopeToken(s)))
            {
                return new OAuthError(OAuthError.InvalidScope, "The scope parameter is malformed.");
            }
        }
        else
        {
            // RFC 6749 §3.3: without a scope parameter the server applies a default: every API scope the client may use.
            requested = [.. client.AllowedScopes.Where(s => !IdentityScopes.All.Contains(s))];
        }

        if (requested.Count == 0
            || requested.Any(s => IdentityScopes.All.Contains(s) || !client.AllowedScopes.Contains(s)))
        {
            return new OAuthError(OAuthError.InvalidScope, "The requested scope is invalid or not allowed.");
        }

        var resolved = await (
                from scope in db.ApiScopes
                join resource in db.ApiResources on scope.ApiResourceId equals resource.Id
                where resource.Enabled && requested.Contains(scope.Name)
                select new { Scope = scope.Name, Resource = resource.Name })
            .ToListAsync(cancellationToken);

        if (resolved.Count != requested.Count)
        {
            return new OAuthError(OAuthError.InvalidScope, "The requested scope is invalid or not allowed.");
        }

        var audiences = resolved.Select(r => r.Resource).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var lifetime = client.AccessTokenLifetime ?? tokenOptions.Value.AccessTokenLifetime;

        // RFC 9068 §2.2: for client credentials, sub is the client identifier.
        var token = await issuer.IssueAsync(
            new AccessTokenRequest(client.ClientId, client.ClientId, audiences, requested, lifetime),
            cancellationToken);

        audit.Add(
            "token.issued",
            actor: client.ClientId,
            clientId: client.ClientId,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            details: new Dictionary<string, object?>
            {
                ["grant_type"] = GrantTypes.ClientCredentials,
                ["scope"] = string.Join(' ', requested),
                ["aud"] = audiences,
            });
        await db.SaveChangesAsync(cancellationToken);

        return new TokenResult(new TokenResponse(token.Token, "Bearer", (long)token.Lifetime.TotalSeconds, string.Join(' ', requested)));
    }

    /// <summary>RFC 6749 A4.1.3 and OIDC Core A3.1.3.</summary>
    private async Task<IResult> AuthorizationCodeAsync(HttpContext context, Client client, IFormCollection form, CancellationToken cancellationToken)
    {
        if (!client.AllowedGrantTypes.Contains(GrantTypes.AuthorizationCode))
        {
            return new OAuthError(OAuthError.UnauthorizedClient, "The client is not allowed to use this grant type.");
        }

        var code = form["code"].ToString();
        if (code.Length == 0)
        {
            return OAuthError.Request("The code parameter is required.");
        }

        // The code is consumed before any other check, so a failed attempt can't be retried with the same code.
        var redemption = codes.Redeem(code);
        if (redemption.Status == CodeRedemptionStatus.AlreadyRedeemed)
        {
            // RFC 6749 A4.1.2: a code used more than once MUST be denied. Refresh tokens issued from it are revoked
            // once refresh tokens exist.
            return await InvalidGrantAsync(context, client, "code_replayed", redemption.Grant?.Subject, cancellationToken);
        }

        if (redemption.Grant is not { } grant)
        {
            return await InvalidGrantAsync(context, client, "unknown_or_expired_code", null, cancellationToken);
        }

        if (!string.Equals(grant.ClientId, client.ClientId, StringComparison.Ordinal))
        {
            return await InvalidGrantAsync(context, client, "code_issued_to_another_client", grant.Subject, cancellationToken);
        }

        // RFC 6749 A4.1.3: redirect_uri must be identical to the one in the authorization request.
        if (!form.TryGetValue("redirect_uri", out var redirectUri)
            || !string.Equals(redirectUri.ToString(), grant.RedirectUri, StringComparison.Ordinal))
        {
            return await InvalidGrantAsync(context, client, "redirect_uri_mismatch", grant.Subject, cancellationToken);
        }

        // RFC 7636 A4.6. RFC 9700 A2.1.1: a code_verifier for a code issued without a challenge is rejected, to
        // prevent PKCE downgrade attacks.
        var verifier = form.TryGetValue("code_verifier", out var verifierValue) ? verifierValue.ToString() : null;
        var pkceValid = grant.CodeChallenge is null
            ? verifier is null
            : verifier is not null && Pkce.IsValidVerifier(verifier) && Pkce.Verify(verifier, grant.CodeChallenge);
        if (!pkceValid)
        {
            return await InvalidGrantAsync(context, client, "pkce_verification_failed", grant.Subject, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var session = await db.UserSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == grant.SessionId, cancellationToken);
        var user = await db.Users.AsNoTracking()
            .Include(u => u.Roles).Include(u => u.Groups).Include(u => u.Claims).AsSplitQuery()
            .SingleOrDefaultAsync(u => u.Id == grant.Subject, cancellationToken);
        if (session is null || !session.IsActive(now) || session.UserId != grant.Subject || user is not { Enabled: true })
        {
            return await InvalidGrantAsync(context, client, "session_ended", grant.Subject, cancellationToken);
        }

        // RFC 9068 A3: aud is required; without API scopes the token is only usable at userinfo, so the client is the audience.
        var audiences = grant.Audiences.Count > 0 ? grant.Audiences : [client.ClientId];
        var subject = user.Id.ToString();
        var token = await issuer.IssueAsync(
            new AccessTokenRequest(client.ClientId, subject, audiences, grant.Scopes, client.AccessTokenLifetime ?? tokenOptions.Value.AccessTokenLifetime),
            cancellationToken);

        string? idToken = null;
        if (grant.Scopes.Contains(IdentityScopes.OpenId))
        {
            idToken = await idTokenIssuer.IssueAsync(
                new IdTokenRequest(
                    client.ClientId,
                    subject,
                    grant.SessionId,
                    grant.AuthTime,
                    grant.AuthenticationMethods,
                    grant.Nonce,
                    token.Token,
                    UserClaims.For(user, grant.Scopes),
                    client.IdTokenLifetime ?? tokenOptions.Value.IdTokenLifetime),
                cancellationToken);
        }

        var scope = string.Join(' ', grant.Scopes);
        audit.Add(
            "token.issued",
            actor: subject,
            clientId: client.ClientId,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            subjectId: user.Id,
            details: new Dictionary<string, object?>
            {
                ["grant_type"] = GrantTypes.AuthorizationCode,
                ["scope"] = scope,
                ["aud"] = audiences,
                ["sid"] = grant.SessionId,
            });
        await db.SaveChangesAsync(cancellationToken);

        return new TokenResult(new TokenResponse(token.Token, "Bearer", (long)token.Lifetime.TotalSeconds, scope, idToken));
    }

    private async Task<IResult> InvalidGrantAsync(HttpContext context, Client client, string reason, Guid? subject, CancellationToken cancellationToken)
    {
        audit.Add(
            "token.rejected",
            success: false,
            actor: client.ClientId,
            clientId: client.ClientId,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            subjectId: subject,
            details: new Dictionary<string, object?> { ["grant_type"] = GrantTypes.AuthorizationCode, ["reason"] = reason });
        await db.SaveChangesAsync(cancellationToken);
        return new OAuthError(OAuthError.InvalidGrant, "The authorization code is invalid, expired, or was issued to another client.");
    }
}
