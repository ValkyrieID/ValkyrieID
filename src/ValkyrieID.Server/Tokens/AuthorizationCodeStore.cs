using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ValkyrieID.Server.Tokens;

/// <summary>Everything bound to an authorization code at the authorize endpoint.</summary>
internal sealed record AuthorizationCodeGrant(
    string ClientId,
    string RedirectUri,
    Guid Subject,
    string SessionId,
    DateTimeOffset AuthTime,
    IReadOnlyList<string> AuthenticationMethods,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Audiences,
    string? Nonce,
    string? CodeChallenge,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Identifies the grant; refresh tokens issued from the code form a family with this id.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();
}

internal enum CodeRedemptionStatus
{
    Redeemed,
    Unknown,
    AlreadyRedeemed,
}

internal sealed record CodeRedemption(CodeRedemptionStatus Status, AuthorizationCodeGrant? Grant);

/// <summary>Stores authorization codes. Only a hash of each code is kept; a code can be redeemed once.</summary>
internal interface IAuthorizationCodeStore
{
    /// <summary>Stores <paramref name="grant"/> and returns the new code.</summary>
    string Create(AuthorizationCodeGrant grant);

    /// <summary>Atomically redeems a code. Expired codes are reported as <see cref="CodeRedemptionStatus.Unknown"/>.</summary>
    CodeRedemption Redeem(string code);
}

/// <summary>The single-instance, in-process store. Codes don't survive a restart, which only costs a retry.</summary>
internal sealed class InMemoryAuthorizationCodeStore(TimeProvider timeProvider) : IAuthorizationCodeStore
{
    private readonly ConcurrentDictionary<string, Entry> _codes = new(StringComparer.Ordinal);

    public string Create(AuthorizationCodeGrant grant)
    {
        Purge();
        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _codes[Hash(code)] = new Entry(grant);
        return code;
    }

    public CodeRedemption Redeem(string code)
    {
        var now = timeProvider.GetUtcNow();
        if (!_codes.TryGetValue(Hash(code), out var entry) || now >= entry.Grant.ExpiresAt)
        {
            return new CodeRedemption(CodeRedemptionStatus.Unknown, null);
        }

        // The entry is kept until it expires, so a replay can be detected and reported.
        return Interlocked.Exchange(ref entry.Redeemed, 1) == 0
            ? new CodeRedemption(CodeRedemptionStatus.Redeemed, entry.Grant)
            : new CodeRedemption(CodeRedemptionStatus.AlreadyRedeemed, entry.Grant);
    }

    private void Purge()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (key, entry) in _codes)
        {
            if (now >= entry.Grant.ExpiresAt)
            {
                _codes.TryRemove(key, out _);
            }
        }
    }

    // Codes are 256-bit random values, so a fast hash is enough; the key is never compared as a secret.
    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private sealed class Entry(AuthorizationCodeGrant grant)
    {
        public AuthorizationCodeGrant Grant { get; } = grant;

        public int Redeemed;
    }
}
