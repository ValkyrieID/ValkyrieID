using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Data;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Auditing;

/// <summary>Deletes audit log entries older than <see cref="AuditLogOptions.Retention"/> (PROJECT.md §11).</summary>
internal sealed partial class AuditLogCleanupService(
    IServiceScopeFactory scopes,
    IOptionsMonitor<AuditLogOptions> options,
    TimeProvider timeProvider,
    ILogger<AuditLogCleanupService> logger) : BackgroundService
{
    private const int BatchSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanUpAsync(stoppingToken);
                await Task.Delay(options.CurrentValue.CleanupInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogCleanupFailed(logger, ex);
                await Task.Delay(options.CurrentValue.CleanupInterval, timeProvider, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Deletes expired entries in batches and returns how many were deleted.</summary>
    public async Task<int> CleanUpAsync(CancellationToken cancellationToken)
    {
        var retention = options.CurrentValue.Retention;
        if (retention <= TimeSpan.Zero)
        {
            return 0;
        }

        var cutoff = timeProvider.GetUtcNow() - retention;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var total = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await db.AuditLog.Where(e => e.Timestamp < cutoff).OrderBy(e => e.Timestamp).Select(e => e.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            total += await db.AuditLog.Where(e => batch.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        if (total > 0)
        {
            scope.ServiceProvider.GetRequiredService<AuditRecorder>().Add("audit.purged", actor: AuditRecorder.SystemActor,
                details: new Dictionary<string, object?> { ["deleted"] = total, ["before"] = cutoff });
            await db.SaveChangesAsync(cancellationToken);
            LogPurged(logger, total);
        }

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} audit log entries past the retention period")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit log cleanup failed")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
