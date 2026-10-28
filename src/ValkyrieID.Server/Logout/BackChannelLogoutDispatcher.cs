using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Logout;

/// <summary>
/// Sends back-channel logout requests (OIDC Back-Channel Logout 1.0 A2.5). Every delivery is claimed with a lease
/// first, so with several instances only one sends it. Each delivery uses its own database context, so they can run
/// in parallel.
/// </summary>
internal sealed partial class BackChannelLogoutDispatcher(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    BackChannelLogoutSignal signal,
    TimeProvider timeProvider,
    IOptions<BackChannelLogoutOptions> options,
    ILogger<BackChannelLogoutDispatcher> logger)
{
    public const string HttpClientName = "ValkyrieID.BackChannelLogout";

    private const int BatchSize = 20;

    /// <summary>Identifies this process as the holder of a delivery's lease.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Delivers notifications this instance claimed when queuing them, in parallel. Failures are rescheduled for the
    /// worker; this only returns once every request has finished or timed out.
    /// </summary>
    public Task DeliverClaimedAsync(IEnumerable<Guid> deliveryIds) =>
        Task.WhenAll(deliveryIds.Select(async id =>
        {
            try
            {
                // Not tied to the browser request: the notification must finish even if the user goes away.
                await DeliverAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogDeliveryError(logger, id, ex);
            }
        }));

    /// <summary>Claims and delivers every notification that is due. Returns the number of deliveries attempted.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            List<Guid> due;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var now = timeProvider.GetUtcNow();
                due = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().BackChannelLogoutDeliveries
                    .Where(d => d.NextAttemptAt <= now && (d.LeaseExpiresAt == null || d.LeaseExpiresAt <= now))
                    .OrderBy(d => d.NextAttemptAt)
                    .Select(d => d.Id)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);
            }

            var attempted = await Task.WhenAll(due.Select(async id =>
            {
                try
                {
                    return await TryClaimAsync(id, cancellationToken) && await DeliverAsync(id, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogDeliveryError(logger, id, ex);
                    return false;
                }
            }));
            total += attempted.Count(a => a);

            if (due.Count < BatchSize)
            {
                return total;
            }
        }
    }

    /// <summary>When the next unclaimed delivery is due, if any.</summary>
    public async Task<DateTimeOffset?> NextAttemptAtAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().BackChannelLogoutDeliveries
            .Where(d => d.LeaseExpiresAt == null)
            .OrderBy(d => d.NextAttemptAt)
            .Select(d => (DateTimeOffset?)d.NextAttemptAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Takes the lease on a due delivery. The conditional update succeeds for exactly one caller, also across
    /// instances; a lease left by a crashed instance can be taken once it has expired.
    /// </summary>
    private async Task<bool> TryClaimAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var now = timeProvider.GetUtcNow();
        var leaseExpiresAt = now + options.Value.LeaseDuration;
        return await db.BackChannelLogoutDeliveries
            .Where(d => d.Id == id && d.NextAttemptAt <= now && (d.LeaseExpiresAt == null || d.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.LeaseOwner, InstanceId).SetProperty(d => d.LeaseExpiresAt, leaseExpiresAt),
                cancellationToken) == 1;
    }

    /// <summary>Sends one delivery this instance holds, then deletes or reschedules it. Returns whether a request was made.</summary>
    private async Task<bool> DeliverAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditRecorder>();
        var delivery = await db.BackChannelLogoutDeliveries
            .SingleOrDefaultAsync(d => d.Id == id && d.LeaseOwner == InstanceId, cancellationToken);
        if (delivery is null)
        {
            return false;
        }

        var uri = await db.Clients.AsNoTracking()
            .Where(c => c.ClientId == delivery.ClientId)
            .Select(c => c.BackChannelLogoutUri)
            .SingleOrDefaultAsync(cancellationToken);
        if (uri is null)
        {
            // The URI was removed after the notification was queued.
            db.BackChannelLogoutDeliveries.Remove(delivery);
            AddAudit(audit, delivery, "backchannel.failed", false, "no_backchannel_logout_uri");
            await SaveAsync(db, delivery, cancellationToken);
            return false;
        }

        var token = await scope.ServiceProvider.GetRequiredService<LogoutTokenIssuer>()
            .IssueAsync(delivery.ClientId, delivery.UserId.ToString(), delivery.SessionId, cancellationToken);
        delivery.Attempts++;
        var (outcome, result) = await SendAsync(uri, token, cancellationToken);

        var delays = options.Value.EffectiveRetryDelays;
        var reschedule = outcome == Outcome.Retry && delivery.Attempts <= delays.Count;
        if (reschedule)
        {
            delivery.NextAttemptAt = timeProvider.GetUtcNow() + delays[delivery.Attempts - 1];
            delivery.LeaseOwner = null;
            delivery.LeaseExpiresAt = null;
            LogRetry(logger, delivery.ClientId, delivery.Attempts, result);
        }
        else
        {
            db.BackChannelLogoutDeliveries.Remove(delivery);
            var delivered = outcome == Outcome.Delivered;
            AddAudit(audit, delivery, delivered ? "backchannel.delivered" : "backchannel.failed", delivered, result);
            if (!delivered)
            {
                LogFailed(logger, delivery.ClientId, delivery.Attempts, result);
            }
        }

        if (await SaveAsync(db, delivery, cancellationToken) && reschedule)
        {
            signal.Notify();
        }

        return true;
    }

    /// <summary>
    /// A2.8: the client responds 200, or 204 from frameworks that substitute it for an empty 200. A2.5: the OP should
    /// only retransmit after "potentially recoverable errors (such as network outage or temporary service
    /// interruption [...])": network errors, timeouts, 408, 429, and 5xx. Anything else, including redirects
    /// (which are not followed), is final.
    /// </summary>
    private async Task<(Outcome Outcome, string Result)> SendAsync(string uri, string token, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.Value.RequestTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent([new("logout_token", token)]),
        };

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var status = (int)response.StatusCode;
            var result = "http_" + status.ToString(CultureInfo.InvariantCulture);
            return status switch
            {
                200 or 204 => (Outcome.Delivered, result),
                408 or 429 or >= 500 => (Outcome.Retry, result),
                _ => (Outcome.Failed, result),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (Outcome.Retry, "timeout");
        }
        catch (HttpRequestException)
        {
            return (Outcome.Retry, "network_error");
        }
    }

    private async Task<bool> SaveAsync(ValkyrieDbContext db, BackChannelLogoutDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The lease expired and another instance took the delivery over; it owns the outcome now.
            LogLeaseLost(logger, delivery.Id);
            return false;
        }
    }

    private static void AddAudit(AuditRecorder audit, BackChannelLogoutDelivery delivery, string eventType, bool success, string result) =>
        audit.Add(
            eventType,
            success: success,
            actor: AuditRecorder.SystemActor,
            clientId: delivery.ClientId,
            subjectId: delivery.UserId,
            details: new Dictionary<string, object?>
            {
                ["sid"] = delivery.SessionId,
                ["attempts"] = delivery.Attempts,
                ["result"] = result,
            });

    private enum Outcome
    {
        Delivered,
        Retry,
        Failed,
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Back-channel logout to client {ClientId} failed on attempt {Attempt} ({Result}); retrying")]
    private static partial void LogRetry(ILogger logger, string clientId, int attempt, string result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Back-channel logout to client {ClientId} failed after {Attempts} attempts ({Result}); giving up")]
    private static partial void LogFailed(ILogger logger, string clientId, int attempts, string result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Back-channel logout delivery {DeliveryId} was taken over by another instance")]
    private static partial void LogLeaseLost(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Back-channel logout delivery {DeliveryId} failed unexpectedly")]
    private static partial void LogDeliveryError(ILogger logger, Guid deliveryId, Exception exception);
}

/// <summary>
/// Delivers queued back-channel logout notifications: right away when woken, when the next retry is due, and every
/// <see cref="BackChannelLogoutOptions.PollInterval"/> for notifications queued by other processes.
/// </summary>
internal sealed partial class BackChannelLogoutWorker(
    BackChannelLogoutDispatcher dispatcher,
    BackChannelLogoutSignal signal,
    TimeProvider timeProvider,
    IOptions<BackChannelLogoutOptions> options,
    ILogger<BackChannelLogoutWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = options.Value.PollInterval;
            try
            {
                await dispatcher.ProcessDueAsync(stoppingToken);
                if (await dispatcher.NextAttemptAtAsync(stoppingToken) is { } next)
                {
                    var untilNext = next - timeProvider.GetUtcNow();
                    wait = untilNext <= TimeSpan.Zero ? TimeSpan.Zero : untilNext < wait ? untilNext : wait;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogProcessingFailed(logger, ex);
            }

            try
            {
                await signal.WaitAsync(wait, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processing back-channel logout deliveries failed")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception);
}
