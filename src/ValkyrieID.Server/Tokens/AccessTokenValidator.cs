using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Tokens;

/// <summary>
/// Validates access tokens issued by this server: signature, issuer, <c>typ</c>, and lifetime with the configured
/// clock skew. Access tokens are self-contained, so a valid token can't be reported as revoked before it expires.
/// </summary>
internal sealed class AccessTokenValidator(
    SigningKeyStore keyStore,
    TimeProvider timeProvider,
    IOptions<IssuerOptions> issuerOptions,
    IOptions<TokenOptions> tokenOptions)
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Returns the validated token, or <see langword="null"/> if it is invalid or expired.</summary>
    public async Task<JsonWebToken?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        var keys = await keyStore.GetAsync(cancellationToken);
        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuerOptions.Value.Issuer,
            // Callers check the audience themselves where it matters.
            ValidateAudience = false,
            // RFC 9068 §4: access tokens are recognized by their typ header, so ID tokens are rejected here.
            ValidTypes = ["at+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = [.. keys.Keys.Select(k => k.SecurityKey)],
            // Lifetime is checked below against the server's TimeProvider.
            ValidateLifetime = false,
        });
        return result.IsValid && result.SecurityToken is JsonWebToken jwt && IsCurrent(jwt) ? jwt : null;
    }

    private bool IsCurrent(JsonWebToken jwt)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var skew = tokenOptions.Value.ClockSkew;
        return jwt.ValidTo != DateTime.MinValue
            && now < jwt.ValidTo + skew
            && (jwt.ValidFrom == DateTime.MinValue || now >= jwt.ValidFrom - skew);
    }
}
