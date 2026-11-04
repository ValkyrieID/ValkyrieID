using Microsoft.EntityFrameworkCore;
using ValkyrieID.Data;

namespace ValkyrieID.Server.Coordination;

/// <summary>
/// Takes and releases a <see cref="Core.Coordination.Lease"/> with conditional updates, so only one holder at a time,
/// across instances and within one process, does the job it guards.
/// </summary>
internal sealed class DatabaseLease(ValkyrieDbContext db, TimeProvider timeProvider)
{
    /// <summary>Returns a token identifying this holder, or <see langword="null"/> if someone else holds the lease.</summary>
    public async Task<string?> TryAcquireAsync(string name, TimeSpan duration, CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        var now = timeProvider.GetUtcNow();
        var expiresAt = now + duration;
        var updated = await db.Leases
            .Where(l => l.Name == name && (l.ExpiresAt == null || l.ExpiresAt <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Owner, owner).SetProperty(l => l.ExpiresAt, expiresAt), cancellationToken);
        return updated == 1 ? owner : null;
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the lease.</summary>
    public async Task<string?> AcquireAsync(string name, TimeSpan duration, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Real time, not the server clock: tests freeze the clock.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (await TryAcquireAsync(name, duration, cancellationToken) is { } owner)
            {
                return owner;
            }

            if (waited.Elapsed >= timeout)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    public Task ReleaseAsync(string name, string owner) =>
        db.Leases
            .Where(l => l.Name == name && l.Owner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Owner, (string?)null).SetProperty(l => l.ExpiresAt, (DateTimeOffset?)null), CancellationToken.None);
}
