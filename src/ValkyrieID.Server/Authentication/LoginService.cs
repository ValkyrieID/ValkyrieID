using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Authentication;

/// <summary>
/// Verifies a username/email and password. Every failure looks the same to the caller, and a password hash is
/// verified even for unknown, disabled, or locked-out users, so neither the response nor its timing reveals
/// whether an account exists.
/// </summary>
internal sealed class LoginService(
    ValkyrieDbContext db,
    UserAdministration users,
    PasswordHasher hasher,
    AuditRecorder audit,
    TimeProvider timeProvider,
    IOptions<LockoutOptions> lockoutOptions)
{
    /// <summary>Inputs longer than this are rejected without lookup; well above any valid identifier or allowed password.</summary>
    private const int MaxInputLength = 1024;

    public async Task<User?> PasswordLoginAsync(string identifier, string password, string? ipAddress, CancellationToken cancellationToken)
    {
        if (password.Length > MaxInputLength)
        {
            password = password[..MaxInputLength];
        }

        var user = identifier.Length <= MaxInputLength ? await users.FindAsync(identifier, cancellationToken) : null;
        if (user is null)
        {
            hasher.VerifyDummy(password);
            audit.Add("user.login.failed", success: false, ipAddress: ipAddress, details: Reason("unknown_user"));
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var result = hasher.Verify(password, user.PasswordHash);
        var now = timeProvider.GetUtcNow();

        if (!user.Enabled || user.IsLockedOut(now))
        {
            audit.Add("user.login.failed", success: false, ipAddress: ipAddress, subjectId: user.Id,
                details: Reason(user.Enabled ? "locked_out" : "disabled"));
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (result == PasswordVerificationResult.Failed)
        {
            await RecordFailureAsync(user, ipAddress, now, cancellationToken);
            return null;
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.Hash(password);
        }

        audit.Add("user.login.succeeded", ipAddress: ipAddress, subjectId: user.Id, actor: user.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task RecordFailureAsync(User user, string? ipAddress, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Increment in the database so concurrent failures aren't lost.
        await db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1), cancellationToken);
        var failures = await db.Users.Where(u => u.Id == user.Id).Select(u => u.AccessFailedCount).SingleAsync(cancellationToken);

        audit.Add("user.login.failed", success: false, ipAddress: ipAddress, subjectId: user.Id, details: Reason("invalid_password"));

        var lockout = lockoutOptions.Value;
        if (failures >= lockout.MaxFailedAttempts)
        {
            var end = now + lockout.Duration;
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.AccessFailedCount, 0).SetProperty(u => u.LockoutEnd, end),
                cancellationToken);
            audit.Add("user.locked_out", success: false, actor: AuditRecorder.SystemActor, ipAddress: ipAddress, subjectId: user.Id,
                details: new Dictionary<string, object?> { ["until"] = end });
        }

        // The tracked entity is stale after ExecuteUpdate; don't let SaveChanges write its old values.
        db.Entry(user).State = EntityState.Detached;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, object?> Reason(string reason) => new() { ["reason"] = reason };
}

/// <summary>Limits login attempts per username or email, including ones that don't exist.</summary>
internal sealed class LoginAttemptLimiter(IOptions<RateLimitOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.Value.LoginAccount.PermitLimit,
            Window = options.Value.LoginAccount.Window,
            QueueLimit = 0,
        }));

    public bool TryAcquire(string identifier)
    {
        var key = UserNames.Normalize(identifier.Length > UserNames.MaxEmailLength ? identifier[..UserNames.MaxEmailLength] : identifier);
        using var lease = _limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
