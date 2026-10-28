using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Tokens;

/// <summary>Issues logout tokens (OIDC Back-Channel Logout 1.0 A2.4), signed with the ID token keys.</summary>
internal sealed class LogoutTokenIssuer(
    SigningKeyStore keyStore,
    TimeProvider timeProvider,
    IOptions<IssuerOptions> issuerOptions)
{
    public const string TokenType = "logout+jwt";
    public const string BackChannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public async Task<string> IssueAsync(string clientId, string subject, string sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var keys = await keyStore.GetAsync(cancellationToken);
        var key = keys.Current(now)
            ?? throw new InvalidOperationException("No active signing key is available.");

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuerOptions.Value.Issuer,
            IssuedAt = now.UtcDateTime,
            Expires = (now + BackChannelLogoutOptions.LogoutTokenLifetime).UtcDateTime,
            // A2.4: explicit typing is RECOMMENDED; it also keeps the token from being accepted as an ID token.
            TokenType = TokenType,
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.RsaSha256),
            // A2.4: sub and sid are both included, so every client can find its session. A nonce is prohibited.
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Aud] = clientId,
                [JwtRegisteredClaimNames.Sub] = subject,
                ["sid"] = sessionId,
                [JwtRegisteredClaimNames.Jti] = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
                ["events"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [BackChannelLogoutEvent] = new Dictionary<string, object>(),
                },
            },
        };

        return Handler.CreateToken(descriptor);
    }
}
