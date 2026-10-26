using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Data;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Protocol;

/// <summary>
/// The UserInfo endpoint (OIDC Core A5.3). Accepts access tokens issued with the <c>openid</c> scope; the
/// audience isn't checked, because the token's audience is the API resources or the client.
/// </summary>
internal sealed class UserInfoEndpoint(
    ValkyrieDbContext db,
    SigningKeyStore keyStore,
    TimeProvider timeProvider,
    IOptions<IssuerOptions> issuerOptions,
    IOptions<TokenOptions> tokenOptions)
{
    public const string Path = "/connect/userinfo";

    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var (token, error) = await ReadTokenAsync(context.Request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (token is null)
        {
            // RFC 6750 A3.1: without authentication information, no error code is included.
            return new BearerChallenge(StatusCodes.Status401Unauthorized, null, null);
        }

        var keys = await keyStore.GetAsync(cancellationToken);
        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuerOptions.Value.Issuer,
            ValidateAudience = false,
            // RFC 9068 A4: access tokens are recognized by their typ header, so ID tokens are rejected here.
            ValidTypes = ["at+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = [.. keys.Keys.Select(k => k.SecurityKey)],
            // Lifetime is checked below against the server's TimeProvider.
            ValidateLifetime = false,
        });
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt || !IsCurrent(jwt))
        {
            return InvalidToken();
        }

        var scopes = jwt.TryGetPayloadValue<string>("scope", out var scope) ? scope.Split(' ') : [];
        if (!scopes.Contains(IdentityScopes.OpenId))
        {
            return new BearerChallenge(StatusCodes.Status403Forbidden, "insufficient_scope", "The access token was not issued with the openid scope.");
        }

        var user = Guid.TryParse(jwt.Subject, out var subject)
            ? await db.Users.AsNoTracking()
                .Include(u => u.Roles).Include(u => u.Groups).Include(u => u.Claims).AsSplitQuery()
                .SingleOrDefaultAsync(u => u.Id == subject, cancellationToken)
            : null;
        if (user is not { Enabled: true })
        {
            return InvalidToken();
        }

        var claims = UserClaims.For(user, scopes);
        claims["sub"] = user.Id.ToString();
        ProtocolHeaders.NoStore(context.Response);
        return Results.Json(claims);
    }

    private bool IsCurrent(JsonWebToken jwt)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var skew = tokenOptions.Value.ClockSkew;
        return jwt.ValidTo != DateTime.MinValue
            && now < jwt.ValidTo + skew
            && (jwt.ValidFrom == DateTime.MinValue || now >= jwt.ValidFrom - skew);
    }

    /// <summary>
    /// RFC 6750 A2.1 (Authorization header) and A2.2 (form body). The query parameter method (A2.3) isn't
    /// supported. More than one method in a request is an error.
    /// </summary>
    private static async Task<(string? Token, IResult? Error)> ReadTokenAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        string? headerToken = null;
        if (request.Headers.Authorization.Count > 0)
        {
            if (request.Headers.Authorization.Count > 1
                || !AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
                || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(header.Parameter))
            {
                return (null, InvalidRequest("The Authorization header is malformed."));
            }

            headerToken = header.Parameter;
        }

        string? bodyToken = null;
        if (HttpMethods.IsPost(request.Method) && request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            if (form.TryGetValue("access_token", out var values))
            {
                if (values.Count != 1)
                {
                    return (null, InvalidRequest("access_token must not be repeated."));
                }

                bodyToken = values.ToString();
            }
        }

        if (headerToken is not null && bodyToken is not null)
        {
            return (null, InvalidRequest("Only one method of sending the access token may be used."));
        }

        return (headerToken ?? bodyToken, null);
    }

    private static BearerChallenge InvalidToken() =>
        new(StatusCodes.Status401Unauthorized, "invalid_token", "The access token is invalid or expired.");

    private static BearerChallenge InvalidRequest(string description) =>
        new(StatusCodes.Status400BadRequest, "invalid_request", description);

    /// <summary>RFC 6750 A3: an error response with a <c>WWW-Authenticate: Bearer</c> challenge.</summary>
    private sealed class BearerChallenge(int statusCode, string? error, string? description) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = statusCode;
            ProtocolHeaders.NoStore(response);
            response.Headers.WWWAuthenticate = error is null
                ? "Bearer realm=\"ValkyrieID\""
                : $"Bearer realm=\"ValkyrieID\", error=\"{error}\", error_description=\"{description}\"";
            return Task.CompletedTask;
        }
    }
}
