using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Tokens;

internal sealed record IdTokenRequest(
    string ClientId,
    string Subject,
    string SessionId,
    DateTimeOffset AuthTime,
    IReadOnlyList<string> AuthenticationMethods,
    string? Nonce,
    string AccessToken,
    IReadOnlyDictionary<string, object> UserClaims,
    TimeSpan Lifetime);

/// <summary>Issues ID tokens (OIDC Core A2), signed with RS256.</summary>
internal sealed class IdTokenIssuer(
    SigningKeyStore keyStore,
    TimeProvider timeProvider,
    IOptions<IssuerOptions> issuerOptions)
{
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public async Task<string> IssueAsync(IdTokenRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var keys = await keyStore.GetAsync(cancellationToken);
        var key = keys.Current(now)
            ?? throw new InvalidOperationException("No active signing key is available.");

        var claims = new Dictionary<string, object>(request.UserClaims, StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Sub] = request.Subject,
            // OIDC Core A2: aud contains the client_id; with a single audience azp is not needed.
            [JwtRegisteredClaimNames.Aud] = request.ClientId,
            [JwtRegisteredClaimNames.AuthTime] = request.AuthTime.ToUnixTimeSeconds(),
            // OIDC Back-Channel Logout 1.0 A2.1: the session identifier.
            ["sid"] = request.SessionId,
            // OIDC Core A3.1.3.6: at_hash for responses that include an access token from the token endpoint.
            [JwtRegisteredClaimNames.AtHash] = LeftHalfHash(request.AccessToken),
        };
        if (request.Nonce is not null)
        {
            claims[JwtRegisteredClaimNames.Nonce] = request.Nonce;
        }

        if (request.AuthenticationMethods.Count > 0)
        {
            claims[JwtRegisteredClaimNames.Amr] = request.AuthenticationMethods.ToArray();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuerOptions.Value.Issuer,
            IssuedAt = now.UtcDateTime,
            Expires = (now + request.Lifetime).UtcDateTime,
            TokenType = "JWT",
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.RsaSha256),
            Claims = claims,
        };

        return Handler.CreateToken(descriptor);
    }

    /// <summary>
    /// OIDC Core A3.1.3.6: base64url of the left-most half of the hash of the ASCII octets of the value, using the
    /// hash algorithm of the ID token's <c>alg</c> (SHA-256 for RS256).
    /// </summary>
    internal static string LeftHalfHash(string value)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(value));
        return Base64Url.EncodeToString(hash.AsSpan(0, hash.Length / 2));
    }
}
