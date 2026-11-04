using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;
using ValkyrieID.Server.Redis;

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
    /// <summary>
    /// How long a redeemed code is remembered, so a replay is detected (and the tokens issued from it revoked, RFC 6749
    /// §4.1.2) for longer than the code's own lifetime.
    /// </summary>
    public static readonly TimeSpan ReplayDetectionWindow = TimeSpan.FromMinutes(10);

    /// <summary>Codes are 43 characters; anything much longer is rejected without lookup.</summary>
    public const int MaxCodeLength = 256;

    /// <summary>Stores <paramref name="grant"/> and returns the new code.</summary>
    /// <exception cref="SharedStateUnavailableException">The shared store can't be reached.</exception>
    Task<string> CreateAsync(AuthorizationCodeGrant grant, CancellationToken cancellationToken);

    /// <summary>Atomically redeems a code. Expired codes are reported as <see cref="CodeRedemptionStatus.Unknown"/>.</summary>
    /// <exception cref="SharedStateUnavailableException">The shared store can't be reached.</exception>
    Task<CodeRedemption> RedeemAsync(string code, CancellationToken cancellationToken);

    public static string NewCode() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}

/// <summary>The single-instance, in-process store. Codes don't survive a restart, which only costs a retry.</summary>
internal sealed class InMemoryAuthorizationCodeStore(TimeProvider timeProvider) : IAuthorizationCodeStore
{
    private readonly ConcurrentDictionary<string, Entry> _codes = new(StringComparer.Ordinal);

    public Task<string> CreateAsync(AuthorizationCodeGrant grant, CancellationToken cancellationToken)
    {
        Purge();
        var code = IAuthorizationCodeStore.NewCode();
        _codes[Hash(code)] = new Entry(grant);
        return Task.FromResult(code);
    }

    public Task<CodeRedemption> RedeemAsync(string code, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (code.Length > IAuthorizationCodeStore.MaxCodeLength || !_codes.TryGetValue(Hash(code), out var entry))
        {
            return Task.FromResult(new CodeRedemption(CodeRedemptionStatus.Unknown, null));
        }

        lock (entry)
        {
            if (entry.RedeemedAt is { } redeemedAt)
            {
                return Task.FromResult(now < redeemedAt + IAuthorizationCodeStore.ReplayDetectionWindow
                    ? new CodeRedemption(CodeRedemptionStatus.AlreadyRedeemed, entry.Grant)
                    : new CodeRedemption(CodeRedemptionStatus.Unknown, null));
            }

            if (now >= entry.Grant.ExpiresAt)
            {
                return Task.FromResult(new CodeRedemption(CodeRedemptionStatus.Unknown, null));
            }

            // The entry is kept after redemption, so a replay can be detected and reported.
            entry.RedeemedAt = now;
            return Task.FromResult(new CodeRedemption(CodeRedemptionStatus.Redeemed, entry.Grant));
        }
    }

    private void Purge()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (key, entry) in _codes)
        {
            lock (entry)
            {
                var expired = entry.RedeemedAt is { } redeemedAt
                    ? now >= redeemedAt + IAuthorizationCodeStore.ReplayDetectionWindow
                    : now >= entry.Grant.ExpiresAt;
                if (expired)
                {
                    _codes.TryRemove(key, out _);
                }
            }
        }
    }

    // Codes are 256-bit random values, so a fast hash is enough; the key is never compared as a secret.
    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private sealed class Entry(AuthorizationCodeGrant grant)
    {
        public AuthorizationCodeGrant Grant { get; } = grant;

        public DateTimeOffset? RedeemedAt { get; set; }
    }
}

/// <summary>
/// The shared store for several instances (PROJECT.md §3). Each code is looked up by an id derived from it, and its
/// grant is encrypted with a key derived from it, so Redis (or a copy of it) holds nothing that identifies a code or
/// reveals the grant without the code itself.
/// </summary>
internal sealed class RedisAuthorizationCodeStore(RedisContext redis, TimeProvider timeProvider) : IAuthorizationCodeStore
{
    // Atomic across instances: the code is deleted and its replay marker written in one step, so a code can be
    // redeemed once even when two instances receive it at the same moment (RFC 6749 §4.1.2).
    // Returns {1, grant} when redeemed, {2, grant} when already redeemed, {0} when unknown.
    private const string RedeemScript = """
        local grant = redis.call('GET', KEYS[1])
        if grant then
          redis.call('DEL', KEYS[1])
          redis.call('SET', KEYS[2], grant, 'PX', ARGV[1])
          return {1, grant}
        end
        local used = redis.call('GET', KEYS[2])
        if used then
          return {2, used}
        end
        return {0}
        """;

    public async Task<string> CreateAsync(AuthorizationCodeGrant grant, CancellationToken cancellationToken)
    {
        var code = IAuthorizationCodeStore.NewCode();
        var (id, payload) = AuthorizationCodeProtection.Protect(code, grant);
        var lifetime = grant.ExpiresAt - timeProvider.GetUtcNow();
        if (lifetime < TimeSpan.FromSeconds(1))
        {
            lifetime = TimeSpan.FromSeconds(1);
        }

        await Run(() => redis.Database.StringSetAsync(CodeKey(id), payload, lifetime, When.NotExists), cancellationToken);
        return code;
    }

    public async Task<CodeRedemption> RedeemAsync(string code, CancellationToken cancellationToken)
    {
        if (code.Length > IAuthorizationCodeStore.MaxCodeLength)
        {
            return new CodeRedemption(CodeRedemptionStatus.Unknown, null);
        }

        var id = AuthorizationCodeProtection.Id(code);
        var result = await Run(
            () => redis.Database.ScriptEvaluateAsync(
                RedeemScript,
                [CodeKey(id), ReplayMarkerKey(id)],
                [(long)IAuthorizationCodeStore.ReplayDetectionWindow.TotalMilliseconds]),
            cancellationToken);

        var values = (RedisResult[])result!;
        var status = (int)values[0];
        if (status == 0 || AuthorizationCodeProtection.Unprotect(code, (byte[])values[1]!) is not { } grant)
        {
            return new CodeRedemption(CodeRedemptionStatus.Unknown, null);
        }

        if (status == 2)
        {
            return new CodeRedemption(CodeRedemptionStatus.AlreadyRedeemed, grant);
        }

        // Redis expires codes too, but its clock isn't the server's; the grant's own expiry decides.
        return timeProvider.GetUtcNow() >= grant.ExpiresAt
            ? new CodeRedemption(CodeRedemptionStatus.Unknown, null)
            : new CodeRedemption(CodeRedemptionStatus.Redeemed, grant);
    }

    // The id is a hash tag ({…}), so in Redis Cluster a code and its replay marker are in the same slot, as the redeem
    // script requires; different codes still spread across the cluster.
    internal RedisKey CodeKey(string id) => redis.Key("code:{" + id + "}");

    internal RedisKey ReplayMarkerKey(string id) => redis.Key("code-used:{" + id + "}");

    private static async Task<T> Run<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            throw new SharedStateUnavailableException("The authorization code store (Redis) is unavailable.", ex);
        }
    }
}

/// <summary>
/// Derives a lookup id and an AES-256-GCM key from an authorization code (HKDF-SHA256, RFC 5869). Codes carry 256 bits
/// of entropy, so no salt or slow hash is needed. Format: <c>nonce (12) | ciphertext | tag (16)</c>, with the id as
/// associated data.
/// </summary>
internal static class AuthorizationCodeProtection
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] IdInfo = "ValkyrieID.AuthorizationCode.Id"u8.ToArray();
    private static readonly byte[] KeyInfo = "ValkyrieID.AuthorizationCode.Key"u8.ToArray();

    public static string Id(string code) => Convert.ToHexStringLower(Derive(code, IdInfo));

    public static (string Id, byte[] Payload) Protect(string code, AuthorizationCodeGrant grant)
    {
        var id = Id(code);
        var key = Derive(code, KeyInfo);
        try
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(grant);
            var output = new byte[NonceSize + plaintext.Length + TagSize];
            var nonce = output.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length), Encoding.ASCII.GetBytes(id));
            CryptographicOperations.ZeroMemory(plaintext);
            return (id, output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Returns <see langword="null"/> if the payload is malformed or doesn't belong to <paramref name="code"/>.</summary>
    public static AuthorizationCodeGrant? Unprotect(string code, byte[] payload)
    {
        if (payload.Length < NonceSize + TagSize)
        {
            return null;
        }

        var key = Derive(code, KeyInfo);
        var plaintext = new byte[payload.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(payload.AsSpan(0, NonceSize), payload.AsSpan(NonceSize, plaintext.Length), payload.AsSpan(NonceSize + plaintext.Length), plaintext, Encoding.ASCII.GetBytes(Id(code)));
            return JsonSerializer.Deserialize<AuthorizationCodeGrant>(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Derive(string code, byte[] info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(code), 32, info: info);
}
