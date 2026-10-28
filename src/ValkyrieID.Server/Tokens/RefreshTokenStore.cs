using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Tokens;
using ValkyrieID.Data;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Tokens;

/// <summary>What a new refresh token family is bound to.</summary>
internal sealed record RefreshTokenGrant(
    Guid FamilyId,
    Guid UserId,
    string? SessionId,
    DateTimeOffset AuthTime,
    IReadOnlyList<string> AuthenticationMethods,
    IReadOnlyList<string> Scopes);

/// <summary>Creates, finds, and revokes refresh tokens. Only SHA-256 hashes of the tokens are stored.</summary>
internal sealed class RefreshTokenStore(ValkyrieDbContext db, TimeProvider timeProvider, IOptions<TokenOptions> tokenOptions)
{
    /// <summary>Adds the first token of a new family to the context; the caller saves it.</summary>
    public string Create(Client client, RefreshTokenGrant grant)
    {
        var now = timeProvider.GetUtcNow();
        var familyExpiresAt = now + (client.RefreshTokenAbsoluteLifetime ?? tokenOptions.Value.RefreshTokenAbsoluteLifetime);
        return Add(client, Guid.CreateVersion7(), grant, familyExpiresAt, now).Plaintext;
    }

    /// <summary>
    /// Adds the token replacing <paramref name="previous"/> to the context. It keeps the family, scope, and absolute
    /// expiry (RFC 6749 §6: the new refresh token's scope is identical to the old one's).
    /// </summary>
    public (RefreshToken Token, string Plaintext) CreateReplacement(Client client, RefreshToken previous, DateTimeOffset now) =>
        Add(
            client,
            Guid.CreateVersion7(),
            new RefreshTokenGrant(previous.FamilyId, previous.UserId, previous.SessionId, previous.AuthTime, previous.AuthenticationMethods, previous.Scopes),
            previous.FamilyExpiresAt,
            now);

    public async Task<RefreshToken?> FindAsync(string token, CancellationToken cancellationToken)
    {
        if (token.Length is 0 or > 512)
        {
            return null;
        }

        var hash = Hash(token);
        var found = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Hash == hash, cancellationToken);

        // The lookup is by hash; the fixed-time comparison guards against a non-exact database match.
        return found is not null && CryptographicOperations.FixedTimeEquals(found.Hash, hash) ? found : null;
    }

    /// <summary>
    /// Atomically marks <paramref name="token"/> as used. Returns <see langword="false"/> if it was already used or
    /// revoked, also by a concurrent request.
    /// </summary>
    public async Task<bool> TryConsumeAsync(RefreshToken token, Guid replacedById, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.RefreshTokens
            .Where(t => t.Id == token.Id && t.ConsumedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now).SetProperty(t => t.ReplacedById, replacedById), cancellationToken) == 1;

    public Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken) =>
        RevokeAsync(db.RefreshTokens.Where(t => t.FamilyId == familyId), cancellationToken);

    public Task<int> RevokeForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        RevokeAsync(db.RefreshTokens.Where(t => t.UserId == userId), cancellationToken);

    public Task<int> RevokeForClientAsync(string clientId, CancellationToken cancellationToken) =>
        RevokeAsync(db.RefreshTokens.Where(t => t.ClientId == clientId), cancellationToken);

    /// <summary>Revokes the refresh tokens issued from a session (PROJECT.md §6: revoking a session revokes them).</summary>
    public Task<int> RevokeForSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        RevokeAsync(db.RefreshTokens.Where(t => t.SessionId == sessionId), cancellationToken);

    /// <summary>Deletes families whose absolute lifetime has ended; they can no longer be used or reported.</summary>
    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return db.RefreshTokens.Where(t => t.FamilyExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
    }

    // Refresh tokens are 256-bit random values, so a fast hash is enough.
    internal static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private (RefreshToken Token, string Plaintext) Add(Client client, Guid id, RefreshTokenGrant grant, DateTimeOffset familyExpiresAt, DateTimeOffset now)
    {
        var plaintext = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var sliding = now + (client.RefreshTokenSlidingLifetime ?? tokenOptions.Value.RefreshTokenSlidingLifetime);
        var token = new RefreshToken
        {
            Id = id,
            Hash = Hash(plaintext),
            FamilyId = grant.FamilyId,
            ClientId = client.ClientId,
            UserId = grant.UserId,
            SessionId = grant.SessionId,
            AuthTime = grant.AuthTime,
            AuthenticationMethods = [.. grant.AuthenticationMethods],
            Scopes = [.. grant.Scopes],
            CreatedAt = now,
            ExpiresAt = sliding < familyExpiresAt ? sliding : familyExpiresAt,
            FamilyExpiresAt = familyExpiresAt,
        };
        db.RefreshTokens.Add(token);
        return (token, plaintext);
    }

    private Task<int> RevokeAsync(IQueryable<RefreshToken> tokens, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return tokens.Where(t => t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }
}

/// <summary>Periodically deletes refresh token families past their absolute lifetime.</summary>
internal sealed partial class RefreshTokenCleanupService(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<RefreshTokenCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<RefreshTokenStore>().PurgeExpiredAsync(stoppingToken);
                LogPurged(logger, deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPurgeFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleted {Count} expired refresh tokens")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting expired refresh tokens failed")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
