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
    [property: JsonPropertyName("scope")] string Scope);

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
    AuditRecorder audit,
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
            GrantTypes.ClientCredentials => await ClientCredentialsAsync(context, client, form, cancellationToken),
            _ => new OAuthError(OAuthError.UnsupportedGrantType, "The grant type is not supported."),
        };
    }

    /// <summary>RFC 6749 §4.4.</summary>
    private async Task<IResult> ClientCredentialsAsync(HttpContext context, Client client, IFormCollection form, CancellationToken cancellationToken)
    {
        if (!client.AllowedGrantTypes.Contains(GrantTypes.ClientCredentials))
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
}
