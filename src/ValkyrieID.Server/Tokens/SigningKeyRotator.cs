using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Coordination;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Tokens;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Coordination;
using ValkyrieID.Server.Redis;

namespace ValkyrieID.Server.Tokens;

/// <summary>
/// Creates, rotates, and removes signing keys. A key is published in JWKS when it is created, signs
/// tokens once it activates (at least <see cref="SigningKeyOptions.PublishLead"/> later, except for the
/// very first key), and is removed once every token it could have signed has expired.
/// </summary>
internal sealed partial class SigningKeyRotator(
    ValkyrieDbContext db,
    AuditRecorder audit,
    SecretProtector protector,
    SigningKeyStore store,
    DatabaseLease lease,
    IClusterEvents clusterEvents,
    TimeProvider timeProvider,
    IOptions<SigningKeyOptions> keyOptions,
    IOptionsMonitor<TokenOptions> tokenOptions,
    ILogger<SigningKeyRotator> logger)
{
    /// <summary>
    /// The rotation lease outlives any rotation; it only matters if its holder crashes, and then delays the next
    /// rotation by at most this long.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Creates and removes keys as needed. Runs under a database lease, so instances rotating at the same time can't
    /// both create a key. Returns <see langword="false"/> if another process is rotating right now.
    /// </summary>
    public async Task<bool> RotateAsync(CancellationToken cancellationToken)
    {
        if (await lease.TryAcquireAsync(Lease.SigningKeys, LeaseDuration, cancellationToken) is not { } owner)
        {
            return false;
        }

        try
        {
            await RotateUnderLeaseAsync(cancellationToken);
            return true;
        }
        finally
        {
            await lease.ReleaseAsync(Lease.SigningKeys, owner);
        }
    }

    private async Task RotateUnderLeaseAsync(CancellationToken cancellationToken)
    {
        var options = keyOptions.Value;
        var now = timeProvider.GetUtcNow();
        var keys = await db.SigningKeys.OrderBy(k => k.ActivatesAt).ToListAsync(cancellationToken);
        var changed = false;

        if (!keys.Any(k => k.ActivatesAt <= now))
        {
            // No usable key (first start): create one that is used immediately.
            keys.Add(AddKey(now, now));
            changed = true;
        }

        var latest = keys[^1];
        if (latest.ActivatesAt + options.RotationInterval - options.PublishLead <= now)
        {
            var activatesAt = Max(latest.ActivatesAt + options.RotationInterval, now + options.PublishLead);
            keys.Add(AddKey(now, activatesAt));
            changed = true;
        }

        var retention = await GetRetentionAsync(cancellationToken);
        for (var i = 0; i < keys.Count - 1; i++)
        {
            // A key stops signing when its successor activates. Keep it published until tokens it signed have expired.
            var successorActivatesAt = keys[i + 1].ActivatesAt;
            if (successorActivatesAt <= now && successorActivatesAt + retention <= now)
            {
                db.SigningKeys.Remove(keys[i]);
                audit.Add("signing_key.deleted", actor: AuditRecorder.SystemActor, details: new Dictionary<string, object?> { ["kid"] = keys[i].Id });
                LogKeyDeleted(logger, keys[i].Id);
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
            store.Invalidate();
            clusterEvents.Publish(ClusterEvent.SigningKeys);
        }
    }

    /// <summary>
    /// Creates a new key on request, e.g. on a schedule of the operator's own. Like a scheduled rotation, it is
    /// published in JWKS at once and only signs tokens after <see cref="SigningKeyOptions.PublishLead"/>, so relying
    /// parties that cache JWKS know it before they see it (PROJECT.md §5). Refused while a newer key is pending.
    /// </summary>
    public async Task<SigningKey?> RotateNowAsync(string actor, string? ipAddress, CancellationToken cancellationToken)
    {
        var owner = await lease.AcquireAsync(Lease.SigningKeys, LeaseDuration, TimeSpan.FromSeconds(10), cancellationToken)
            ?? throw Administration.AdministrationException.Conflict("Another process is rotating the signing keys; try again.");
        try
        {
            return await RotateNowUnderLeaseAsync(actor, ipAddress, cancellationToken);
        }
        finally
        {
            await lease.ReleaseAsync(Lease.SigningKeys, owner);
        }
    }

    private async Task<SigningKey?> RotateNowUnderLeaseAsync(string actor, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (await db.SigningKeys.AnyAsync(k => k.ActivatesAt > now, cancellationToken))
        {
            return null;
        }

        var key = SigningKeyStore.Create(protector, keyOptions.Value.RsaKeySize, now, now + keyOptions.Value.PublishLead);
        db.SigningKeys.Add(key);
        audit.Add("signing_key.created", actor: actor, ipAddress: ipAddress, details: new Dictionary<string, object?>
        {
            ["kid"] = key.Id,
            ["alg"] = key.Algorithm,
            ["activates_at"] = key.ActivatesAt,
            ["reason"] = "manual",
        });
        await db.SaveChangesAsync(cancellationToken);
        store.Invalidate();
        clusterEvents.Publish(ClusterEvent.SigningKeys);
        LogKeyCreated(logger, key.Id, key.ActivatesAt);
        return key;
    }

    private SigningKey AddKey(DateTimeOffset now, DateTimeOffset activatesAt)
    {
        var key = SigningKeyStore.Create(protector, keyOptions.Value.RsaKeySize, now, activatesAt);
        db.SigningKeys.Add(key);
        audit.Add("signing_key.created", actor: AuditRecorder.SystemActor, details: new Dictionary<string, object?>
        {
            ["kid"] = key.Id,
            ["alg"] = key.Algorithm,
            ["activates_at"] = activatesAt,
        });
        LogKeyCreated(logger, key.Id, activatesAt);
        return key;
    }

    /// <summary>The longest time a token signed by a retired key can still be valid.</summary>
    private async Task<TimeSpan> GetRetentionAsync(CancellationToken cancellationToken)
    {
        var tokens = tokenOptions.CurrentValue;
        var clientLifetimes = await db.Clients
            .Where(c => c.AccessTokenLifetime != null)
            .Select(c => c.AccessTokenLifetime)
            .ToListAsync(cancellationToken);
        var maxLifetime = clientLifetimes.OfType<TimeSpan>().Append(tokens.AccessTokenLifetime).Max();
        return maxLifetime + tokens.ClockSkew;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    [LoggerMessage(Level = LogLevel.Information, Message = "Created signing key {KeyId}, active from {ActivatesAt}")]
    private static partial void LogKeyCreated(ILogger logger, string keyId, DateTimeOffset activatesAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted expired signing key {KeyId}")]
    private static partial void LogKeyDeleted(ILogger logger, string keyId);
}

internal sealed partial class SigningKeyRotationService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<SigningKeyOptions> options,
    ILogger<SigningKeyRotationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CheckInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SigningKeyRotator>().RotateAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRotationFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Signing key rotation failed")]
    private static partial void LogRotationFailed(ILogger logger, Exception exception);
}
