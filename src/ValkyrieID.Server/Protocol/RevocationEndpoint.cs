using Microsoft.Net.Http.Headers;
using ValkyrieID.Core.Clients;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Protocol;

/// <summary>Reads the form body of a POST-only protocol endpoint.</summary>
internal static class ProtocolForm
{
    /// <summary>
    /// RFC 6749 §3.2, RFC 7009 §2.1, RFC 7662 §2.1: parameters are sent as application/x-www-form-urlencoded and
    /// MUST NOT be repeated.
    /// </summary>
    public static async Task<(IFormCollection? Form, OAuthError? Error)> ReadAsync(HttpContext context)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return (null, OAuthError.Request("The request must use application/x-www-form-urlencoded."));
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return form.Any(p => p.Value.Count > 1)
            ? (null, OAuthError.Request("Request parameters must not be repeated."))
            : (form, null);
    }
}

/// <summary>
/// Token revocation (RFC 7009). Refresh tokens are revoked together with their whole family. Access tokens are
/// self-contained JWTs and can't be revoked before they expire, so they are answered with
/// <c>unsupported_token_type</c> (RFC 7009 §2.2.1).
/// </summary>
internal sealed class RevocationEndpoint(
    ValkyrieDbContext db,
    ClientAuthenticator authenticator,
    RefreshTokenStore refreshTokens,
    AccessTokenValidator accessTokens,
    AuditRecorder audit)
{
    public const string Path = "/connect/revocation";

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var (form, error) = await ProtocolForm.ReadAsync(context);
        if (form is null || error is not null)
        {
            return error ?? OAuthError.Request("The request is malformed.");
        }

        // RFC 7009 §2.1: the client authenticates as at the token endpoint; public clients identify themselves.
        var authentication = await authenticator.AuthenticateAsync(context, form, cancellationToken);
        if (authentication is not { Client: { } client })
        {
            return authentication.Error ?? OAuthError.Client();
        }

        var token = form["token"].ToString();
        if (token.Length == 0)
        {
            return OAuthError.Request("The token parameter is required.");
        }

        // RFC 7009 §2.1: token_type_hint is only a hint; every supported type is searched regardless.
        if (await refreshTokens.FindAsync(token, cancellationToken) is { } refreshToken)
        {
            // RFC 7009 §2.1: the server verifies that the token was issued to the client making the request and
            // refuses the request otherwise.
            if (!string.Equals(refreshToken.ClientId, client.ClientId, StringComparison.Ordinal))
            {
                Audit(context, client, "token.revocation_rejected", false, refreshToken, "issued_to_another_client");
                await db.SaveChangesAsync(cancellationToken);
                return new OAuthError(OAuthError.UnauthorizedClient, "The token was not issued to this client.");
            }

            // RFC 7009 §2.1: revoking a refresh token SHOULD also invalidate related tokens; the whole family goes.
            await refreshTokens.RevokeFamilyAsync(refreshToken.FamilyId, cancellationToken);
            Audit(context, client, "token.revoked", true, refreshToken, null);
            await db.SaveChangesAsync(cancellationToken);
            return Ok();
        }

        if (await accessTokens.ValidateAsync(token, cancellationToken) is { } accessToken
            && accessToken.TryGetPayloadValue<string>("client_id", out var tokenClientId)
            && string.Equals(tokenClientId, client.ClientId, StringComparison.Ordinal))
        {
            return new OAuthError(OAuthError.UnsupportedTokenType, "Access tokens can't be revoked; they expire on their own.");
        }

        // RFC 7009 §2.2: invalid or unknown tokens don't cause an error response.
        return Ok();
    }

    private void Audit(HttpContext context, Client client, string eventType, bool success, Core.Tokens.RefreshToken token, string? reason) =>
        audit.Add(
            eventType,
            success: success,
            actor: client.ClientId,
            clientId: client.ClientId,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            subjectId: token.UserId,
            details: new Dictionary<string, object?>
            {
                ["token_type"] = "refresh_token",
                ["refresh_token_family"] = token.FamilyId,
                ["reason"] = reason,
            });

    private static IResult Ok() => new EmptyNoStoreResult();

    /// <summary>RFC 7009 §2.2: HTTP 200; the content of the response body is ignored by the client.</summary>
    private sealed class EmptyNoStoreResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            ProtocolHeaders.NoStore(httpContext.Response);
            return Task.CompletedTask;
        }
    }
}
