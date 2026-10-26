using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Tokens;

internal sealed record AccessTokenRequest(
    string ClientId,
    string Subject,
    IReadOnlyList<string> Audiences,
    IReadOnlyList<string> Scopes,
    TimeSpan Lifetime);

internal sealed record IssuedToken(string Token, TimeSpan Lifetime);

/// <summary>Issues JWT access tokens per RFC 9068.</summary>
internal sealed class AccessTokenIssuer(
    SigningKeyStore keyStore,
    TimeProvider timeProvider,
    IOptions<IssuerOptions> issuerOptions)
{
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public async Task<IssuedToken> IssueAsync(AccessTokenRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var keys = await keyStore.GetAsync(cancellationToken);
        var key = keys.Current(now)
            ?? throw new InvalidOperationException("No active signing key is available.");

        // RFC 9068 §2.2: iss, exp, aud, sub, client_id, iat, jti are required; scope is included when scopes were granted.
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = request.Subject,
            ["client_id"] = request.ClientId,
            [JwtRegisteredClaimNames.Jti] = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            [JwtRegisteredClaimNames.Aud] = request.Audiences.Count == 1 ? request.Audiences[0] : request.Audiences.ToArray(),
        };
        if (request.Scopes.Count > 0)
        {
            claims["scope"] = string.Join(' ', request.Scopes);
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuerOptions.Value.Issuer,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + request.Lifetime).UtcDateTime,
            // RFC 9068 §2.1: typ header "at+jwt".
            TokenType = "at+jwt",
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.RsaSha256),
            Claims = claims,
        };

        return new IssuedToken(Handler.CreateToken(descriptor), request.Lifetime);
    }
}
