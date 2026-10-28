using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Core.Tokens;
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
    [property: JsonPropertyName("id_token"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IdToken = null,
    [property: JsonPropertyName("refresh_token"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RefreshToken = null);

internal sealed class TokenResult(TokenResponse response) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ProtocolHeaders.NoStore(httpContext.Response);
        return httpContext.Response.WriteAsJsonAsync(response, httpContext.RequestAborted);
    }
}

/// <summary>The token endpoint (RFC 6749 §3.2).</summary>
internal sealed class TokenEndpoint(
    ValkyrieDbContext db,
    ClientAuthenticator authenticator,
    AccessTokenIssuer issuer,
    IdTokenIssuer idTokenIssuer,
    IAuthorizationCodeStore codes,
    RefreshTokenStore refreshTokens,
    AuditRecorder audit,
    TimeProvider timeProvider,
    IOptions<TokenOptions> tokenOptions)
{
    public const string Path = "/connect/token";

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        var (form, error) = await ProtocolForm.ReadAsync(context);
        if (form is null || error is not null)
        {
            return error ?? OAuthError.Request("The request is malformed.");
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
            GrantTypes.RefreshToken => await RefreshTokenAsync(context, client, form, cancellationToken),
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
            // RFC 6749 A4.1.2: a code used more than once MUST be denied, and tokens previously issued based on it
            // SHOULD be revoked. Access tokens are self-contained and can't be; the refresh token family can.
            if (redemption.Grant is { } replayed)
            {
                await refreshTokens.RevokeFamilyAsync(replayed.Id, cancellationToken);
            }

            return await InvalidGrantAsync(context, client, GrantTypes.AuthorizationCode, "code_replayed", redemption.Grant?.Subject, cancellationToken);
        }

        if (redemption.Grant is not { } grant)
        {
            return await InvalidGrantAsync(context, client, GrantTypes.AuthorizationCode, "unknown_or_expired_code", null, cancellationToken);
        }

        if (!string.Equals(grant.ClientId, client.ClientId, StringComparison.Ordinal))
        {
            return await InvalidGrantAsync(context, client, GrantTypes.AuthorizationCode, "code_issued_to_another_client", grant.Subject, cancellationToken);
        }

        // RFC 6749 A4.1.3: redirect_uri must be identical to the one in the authorization request.
        if (!form.TryGetValue("redirect_uri", out var redirectUri)
            || !string.Equals(redirectUri.ToString(), grant.RedirectUri, StringComparison.Ordinal))
        {
            return await InvalidGrantAsync(context, client, GrantTypes.AuthorizationCode, "redirect_uri_mismatch", grant.Subject, cancellationToken);
        }

        // RFC 7636 A4.6. RFC 9700 A2.1.1: a code_verifier for a code issued without a challenge is rejected, to
        // prevent PKCE downgrade attacks.
        var verifier = form.TryGetValue("code_verifier", out var verifierValue) ? verifierValue.ToString() : null;
        var pkceValid = grant.CodeChallenge is null
            ? verifier is null
            : verifier is not null && Pkce.IsValidVerifier(verifier) && Pkce.Verify(verifier, grant.CodeChallenge);
        if (!pkceValid)
        {
            return await InvalidGrantAsync(context, client, GrantTypes.AuthorizationCode, "pkce_verification_failed", grant.Subject, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var session = await db.UserSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == grant.SessionId, cancellationToken);
        var user = await db.Users.AsNoTracking()
            .Include(u => u.Roles).Include(u => u.Groups).Include(u => u.Claims).AsSplitQuery()
            .SingleOrDefaultAsync(u => u.Id == grant.Subject, cancellationToken);
        if (session is null || !session.IsActive(now) || session.UserId != grant.Subject || user is not { Enabled: true })
        {
            return await InvalidGrantAsync(context, client, GrantTypes.AuthorizationCode, "session_ended", grant.Subject, cancellationToken);
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
        string? refreshToken = null;
        if (grant.Scopes.Contains(IdentityScopes.OfflineAccess) && client.AllowedGrantTypes.Contains(GrantTypes.RefreshToken))
        {
            refreshToken = refreshTokens.Create(
                client,
                new RefreshTokenGrant(grant.Id, user.Id, grant.SessionId, grant.AuthTime, grant.AuthenticationMethods, grant.Scopes));
        }

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
                ["refresh_token_family"] = refreshToken is null ? null : grant.Id,
            });
        await db.SaveChangesAsync(cancellationToken);

        return new TokenResult(new TokenResponse(token.Token, "Bearer", (long)token.Lifetime.TotalSeconds, scope, idToken, refreshToken));
    }

    /// <summary>RFC 6749 §6, OIDC Core §12, with rotation and reuse detection (RFC 9700 §4.14.2).</summary>
    private async Task<IResult> RefreshTokenAsync(HttpContext context, Client client, IFormCollection form, CancellationToken cancellationToken)
    {
        if (!client.AllowedGrantTypes.Contains(GrantTypes.RefreshToken))
        {
            return new OAuthError(OAuthError.UnauthorizedClient, "The client is not allowed to use this grant type.");
        }

        var value = form["refresh_token"].ToString();
        if (value.Length == 0)
        {
            return OAuthError.Request("The refresh_token parameter is required.");
        }

        var now = timeProvider.GetUtcNow();
        var current = await refreshTokens.FindAsync(value, cancellationToken);
        if (current is null)
        {
            return await InvalidGrantAsync(context, client, GrantTypes.RefreshToken, "unknown_refresh_token", null, cancellationToken);
        }

        // RFC 6749 §6: the refresh token must have been issued to the authenticated client.
        if (!string.Equals(current.ClientId, client.ClientId, StringComparison.Ordinal))
        {
            return await InvalidGrantAsync(context, client, GrantTypes.RefreshToken, "refresh_token_issued_to_another_client", current.UserId, cancellationToken);
        }

        if (current.RevokedAt is not null)
        {
            return await InvalidGrantAsync(context, client, GrantTypes.RefreshToken, "refresh_token_revoked", current.UserId, cancellationToken);
        }

        if (current.ConsumedAt is not null)
        {
            return await ReuseDetectedAsync(context, client, current, cancellationToken);
        }

        // The token is checked against the server's own clock only, so no clock skew applies.
        if (!current.IsActive(now))
        {
            return await InvalidGrantAsync(context, client, GrantTypes.RefreshToken, "refresh_token_expired", current.UserId, cancellationToken);
        }

        // RFC 6749 §6: a requested scope must not include any scope not originally granted.
        IReadOnlyList<string> scopes = current.Scopes;
        if (form.TryGetValue("scope", out var scopeValue))
        {
            var requested = scopeValue.ToString().Split(' ').Distinct(StringComparer.Ordinal).ToList();
            if (requested.Any(s => !ProtocolSyntax.IsValidScopeToken(s)))
            {
                return new OAuthError(OAuthError.InvalidScope, "The scope parameter is malformed.");
            }

            if (requested.Any(s => !current.Scopes.Contains(s)))
            {
                return new OAuthError(OAuthError.InvalidScope, "The requested scope exceeds the scope originally granted.");
            }

            scopes = [.. current.Scopes.Where(requested.Contains)];
        }

        // The client's configuration may have changed since the grant; a scope it may no longer use is not issued.
        if (scopes.Any(s => !client.AllowedScopes.Contains(s)))
        {
            return new OAuthError(OAuthError.InvalidScope, "The requested scope is no longer allowed for the client.");
        }

        var audiences = await ResolveAudiencesAsync(scopes, cancellationToken);
        if (audiences is null)
        {
            return new OAuthError(OAuthError.InvalidScope, "The requested scope is no longer available.");
        }

        var user = await db.Users.AsNoTracking()
            .Include(u => u.Roles).Include(u => u.Groups).Include(u => u.Claims).AsSplitQuery()
            .SingleOrDefaultAsync(u => u.Id == current.UserId, cancellationToken);
        if (user is not { Enabled: true })
        {
            return await InvalidGrantAsync(context, client, GrantTypes.RefreshToken, "user_disabled", current.UserId, cancellationToken);
        }

        // Rotation: the replacement and the consumption of the presented token commit together. A concurrent request
        // with the same token waits for this one, then sees it as consumed and is treated as reuse.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (replacement, plaintext) = refreshTokens.CreateReplacement(client, current, now);
        if (!await refreshTokens.TryConsumeAsync(current, replacement.Id, now, cancellationToken))
        {
            db.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return await ReuseDetectedAsync(context, client, current, cancellationToken);
        }

        if (audiences.Count == 0)
        {
            audiences = [client.ClientId];
        }

        var subject = user.Id.ToString();
        var token = await issuer.IssueAsync(
            new AccessTokenRequest(client.ClientId, subject, audiences, scopes, client.AccessTokenLifetime ?? tokenOptions.Value.AccessTokenLifetime),
            cancellationToken);

        // OIDC Core §12.2: the new ID token keeps iss, sub, aud and auth_time, and SHOULD NOT contain a nonce.
        string? idToken = null;
        if (scopes.Contains(IdentityScopes.OpenId))
        {
            idToken = await idTokenIssuer.IssueAsync(
                new IdTokenRequest(
                    client.ClientId,
                    subject,
                    current.SessionId,
                    current.AuthTime,
                    current.AuthenticationMethods,
                    null,
                    token.Token,
                    UserClaims.For(user, scopes),
                    client.IdTokenLifetime ?? tokenOptions.Value.IdTokenLifetime),
                cancellationToken);
        }

        var scope = string.Join(' ', scopes);
        audit.Add(
            "token.refreshed",
            actor: subject,
            clientId: client.ClientId,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            subjectId: user.Id,
            details: new Dictionary<string, object?>
            {
                ["grant_type"] = GrantTypes.RefreshToken,
                ["scope"] = scope,
                ["aud"] = audiences,
                ["sid"] = current.SessionId,
                ["refresh_token_family"] = current.FamilyId,
            });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new TokenResult(new TokenResponse(token.Token, "Bearer", (long)token.Lifetime.TotalSeconds, scope, idToken, plaintext));
    }

    /// <summary>
    /// RFC 9700 §4.14.2: a refresh token that was already exchanged was presented again, so either the client or an
    /// attacker holds a stolen copy. The whole family is revoked.
    /// </summary>
    private async Task<IResult> ReuseDetectedAsync(HttpContext context, Client client, RefreshToken token, CancellationToken cancellationToken)
    {
        await refreshTokens.RevokeFamilyAsync(token.FamilyId, cancellationToken);
        audit.Add(
            "token.refresh_reuse_detected",
            success: false,
            actor: client.ClientId,
            clientId: client.ClientId,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            subjectId: token.UserId,
            details: new Dictionary<string, object?> { ["refresh_token_family"] = token.FamilyId, ["sid"] = token.SessionId });
        return await InvalidGrantAsync(context, client, GrantTypes.RefreshToken, "refresh_token_reused", token.UserId, cancellationToken);
    }

    /// <summary>The API resources (audiences) of <paramref name="scopes"/>, or <see langword="null"/> if any API scope is gone or disabled.</summary>
    private async Task<List<string>?> ResolveAudiencesAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        var apiScopes = scopes.Where(s => !IdentityScopes.All.Contains(s)).ToList();
        var resolved = await (
                from apiScope in db.ApiScopes
                join resource in db.ApiResources on apiScope.ApiResourceId equals resource.Id
                where resource.Enabled && apiScopes.Contains(apiScope.Name)
                select new { Scope = apiScope.Name, Resource = resource.Name })
            .ToListAsync(cancellationToken);
        return resolved.Count == apiScopes.Count
            ? [.. resolved.Select(r => r.Resource).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            : null;
    }

    private async Task<IResult> InvalidGrantAsync(HttpContext context, Client client, string grantType, string reason, Guid? subject, CancellationToken cancellationToken)
    {
        audit.Add(
            "token.rejected",
            success: false,
            actor: client.ClientId,
            clientId: client.ClientId,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            subjectId: subject,
            details: new Dictionary<string, object?> { ["grant_type"] = grantType, ["reason"] = reason });
        await db.SaveChangesAsync(cancellationToken);
        return new OAuthError(
            OAuthError.InvalidGrant,
            grantType == GrantTypes.RefreshToken
                ? "The refresh token is invalid, expired, revoked, or was issued to another client."
                : "The authorization code is invalid, expired, or was issued to another client.");
    }
}
