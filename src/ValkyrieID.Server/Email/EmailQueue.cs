using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Logout;

namespace ValkyrieID.Server.Email;

/// <summary>Wakes the email worker.</summary>
internal sealed class EmailSignal : WakeSignal;

/// <summary>
/// Queues emails in the database; <see cref="EmailWorker"/> sends them. Requests never wait for the SMTP server, so
/// a request that sends an email takes as long as one that doesn't, and the response time doesn't reveal whether an
/// account exists (PROJECT.md §6). Changes are saved by the caller; afterwards call <see cref="Wake"/>.
/// </summary>
internal sealed class EmailQueue(ValkyrieDbContext db, SecretProtector protector, EmailSignal signal, TimeProvider timeProvider)
{
    public const string MessagePurpose = "ValkyrieID.OutboundEmail";

    public void Add(string kind, Guid? userId, EmailMessage message)
    {
        var now = timeProvider.GetUtcNow();
        db.OutboundEmails.Add(new OutboundEmail
        {
            Kind = kind,
            UserId = userId,
            EncryptedMessage = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(message), MessagePurpose),
            CreatedAt = now,
            NextAttemptAt = now,
        });
    }

    public void Wake() => signal.Notify();

    internal static EmailMessage Decrypt(SecretProtector protector, byte[] encrypted) =>
        JsonSerializer.Deserialize<EmailMessage>(protector.Unprotect(encrypted, MessagePurpose))
        ?? throw new JsonException("Empty email message.");
}

/// <summary>
/// Sends queued emails. Every email is claimed with a lease first, so with several instances only one sends it.
/// The row, with the encrypted message, is deleted once sent or abandoned; the audit log keeps the outcome.
/// </summary>
internal sealed partial class EmailDispatcher(
    IServiceScopeFactory scopes,
    IEmailSender sender,
    EmailSignal signal,
    TimeProvider timeProvider,
    IOptionsMonitor<SmtpOptions> options,
    ILogger<EmailDispatcher> logger)
{
    private const int BatchSize = 20;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Claims and sends every email that is due. Returns the number of emails attempted.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            List<Guid> due;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var now = timeProvider.GetUtcNow();
                due = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().OutboundEmails
                    .Where(e => e.NextAttemptAt <= now && (e.LeaseExpiresAt == null || e.LeaseExpiresAt <= now))
                    .OrderBy(e => e.NextAttemptAt)
                    .Select(e => e.Id)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);
            }

            foreach (var id in due)
            {
                try
                {
                    if (await TryClaimAsync(id, cancellationToken) && await SendAsync(id, cancellationToken))
                    {
                        total++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSendError(logger, id, ex);
                }
            }

            if (due.Count < BatchSize)
            {
                return total;
            }
        }
    }

    public async Task<DateTimeOffset?> NextAttemptAtAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().OutboundEmails
            .Where(e => e.LeaseExpiresAt == null)
            .OrderBy(e => e.NextAttemptAt)
            .Select(e => (DateTimeOffset?)e.NextAttemptAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> TryClaimAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var now = timeProvider.GetUtcNow();
        var leaseExpiresAt = now + options.CurrentValue.LeaseDuration;
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().OutboundEmails
            .Where(e => e.Id == id && e.NextAttemptAt <= now && (e.LeaseExpiresAt == null || e.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.LeaseOwner, InstanceId).SetProperty(e => e.LeaseExpiresAt, leaseExpiresAt),
                cancellationToken) == 1;
    }

    private async Task<bool> SendAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditRecorder>();
        var email = await db.OutboundEmails.SingleOrDefaultAsync(e => e.Id == id && e.LeaseOwner == InstanceId, cancellationToken);
        if (email is null)
        {
            return false;
        }

        EmailMessage message;
        try
        {
            message = EmailQueue.Decrypt(scope.ServiceProvider.GetRequiredService<SecretProtector>(), email.EncryptedMessage);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            // Encrypted with a master key that is no longer configured.
            db.OutboundEmails.Remove(email);
            AddAudit(audit, email, false, "undecryptable");
            await SaveAsync(db, email, cancellationToken);
            return false;
        }

        email.Attempts++;
        string result;
        bool retry;
        try
        {
            await sender.SendAsync(message, cancellationToken);
            result = "sent";
            retry = false;
        }
        catch (EmailDeliveryException ex)
        {
            result = ex.Result;
            retry = !ex.Permanent;
        }

        var delays = options.CurrentValue.EffectiveRetryDelays;
        var reschedule = retry && email.Attempts <= delays.Count;
        if (reschedule)
        {
            email.NextAttemptAt = timeProvider.GetUtcNow() + delays[email.Attempts - 1];
            email.LeaseOwner = null;
            email.LeaseExpiresAt = null;
            LogRetry(logger, email.Kind, email.Attempts, result);
        }
        else
        {
            db.OutboundEmails.Remove(email);
            var sent = result == "sent";
            AddAudit(audit, email, sent, result);
            if (!sent)
            {
                LogFailed(logger, email.Kind, email.Attempts, result);
            }
        }

        if (await SaveAsync(db, email, cancellationToken) && reschedule)
        {
            signal.Notify();
        }

        return true;
    }

    private async Task<bool> SaveAsync(ValkyrieDbContext db, OutboundEmail email, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            LogLeaseLost(logger, email.Id);
            return false;
        }
    }

    // The recipient isn't recorded: it's in the encrypted message only.
    private static void AddAudit(AuditRecorder audit, OutboundEmail email, bool success, string result) =>
        audit.Add(
            success ? "email.sent" : "email.failed",
            success: success,
            actor: AuditRecorder.SystemActor,
            subjectId: email.UserId,
            details: new Dictionary<string, object?>
            {
                ["kind"] = email.Kind,
                ["attempts"] = email.Attempts,
                ["result"] = result,
            });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending a {Kind} email failed on attempt {Attempt} ({Result}); retrying")]
    private static partial void LogRetry(ILogger logger, string kind, int attempt, string result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending a {Kind} email failed after {Attempts} attempts ({Result}); giving up")]
    private static partial void LogFailed(ILogger logger, string kind, int attempts, string result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email {EmailId} was taken over by another instance")]
    private static partial void LogLeaseLost(ILogger logger, Guid emailId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Email {EmailId} failed unexpectedly")]
    private static partial void LogSendError(ILogger logger, Guid emailId, Exception exception);
}

/// <summary>Sends queued emails: right away when woken, when a retry is due, and every <see cref="SmtpOptions.PollInterval"/>.</summary>
internal sealed partial class EmailWorker(
    EmailDispatcher dispatcher,
    EmailSignal signal,
    TimeProvider timeProvider,
    IOptionsMonitor<SmtpOptions> options,
    ILogger<EmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // SMTP can be turned on or off in the runtime settings; wake up when that happens.
        using var subscription = options.OnChange(_ => signal.Notify());

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = options.CurrentValue.PollInterval;
            try
            {
                if (options.CurrentValue.Enabled)
                {
                    await dispatcher.ProcessDueAsync(stoppingToken);
                    if (await dispatcher.NextAttemptAtAsync(stoppingToken) is { } next)
                    {
                        var untilNext = next - timeProvider.GetUtcNow();
                        wait = untilNext <= TimeSpan.Zero ? TimeSpan.Zero : untilNext < wait ? untilNext : wait;
                    }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processing queued emails failed")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception);
}
