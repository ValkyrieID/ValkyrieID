using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Tokens;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;

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
    TimeProvider timeProvider,
    IOptions<SigningKeyOptions> keyOptions,
    IOptions<TokenOptions> tokenOptions,
    ILogger<SigningKeyRotator> logger)
{
    public async Task RotateAsync(CancellationToken cancellationToken)
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
        }
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
        var tokens = tokenOptions.Value;
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
