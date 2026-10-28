using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Tokens;

internal sealed record IdTokenHint(string Subject, string? SessionId, IReadOnlyList<string> Audiences);

/// <summary>
/// Validates an <c>id_token_hint</c> (OIDC RP-Initiated Logout 1.0 A2): "the OP MUST validate that it was the
/// issuer of the ID Token". The signature, issuer, and <c>typ</c> are checked; the lifetime is not, because the OP
/// "SHOULD accept ID Tokens [...] even when the exp time has passed".
/// </summary>
internal sealed class IdTokenHintValidator(SigningKeyStore keyStore, IOptions<IssuerOptions> issuerOptions)
{
    private const int MaxLength = 8000;

    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Returns the hint's claims, or <see langword="null"/> if it isn't an ID token issued by this server.</summary>
    public async Task<IdTokenHint?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (token.Length > MaxLength)
        {
            return null;
        }

        var keys = await keyStore.GetAsync(cancellationToken);
        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuerOptions.Value.Issuer,
            // The audience is compared with client_id by the caller.
            ValidateAudience = false,
            // ID tokens are issued with typ "JWT"; access tokens (at+jwt) and logout tokens (logout+jwt) are rejected.
            ValidTypes = ["JWT"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = [.. keys.Keys.Select(k => k.SecurityKey)],
            ValidateLifetime = false,
        });

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt
            || string.IsNullOrEmpty(jwt.Subject) || !jwt.Audiences.Any())
        {
            return null;
        }

        var sessionId = jwt.TryGetPayloadValue<string>("sid", out var sid) ? sid : null;
        return new IdTokenHint(jwt.Subject, sessionId, [.. jwt.Audiences]);
    }
}
