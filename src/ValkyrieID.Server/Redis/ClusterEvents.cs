using System.Collections.Concurrent;
using System.Threading.Channels;
using StackExchange.Redis;
using ValkyrieID.Server.Email;
using ValkyrieID.Server.Logout;
using ValkyrieID.Server.Settings;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Redis;

/// <summary>Changes other instances should pick up right away instead of at their next poll.</summary>
internal enum ClusterEvent
{
    Settings,
    SigningKeys,
    BackChannelLogout,
    Email,
}

/// <summary>
/// Tells other instances (and processes such as the CLI's targets) that something changed in the database. The
/// messages carry no data: receivers re-read the database, and polling stays as the fallback because pub/sub delivery
/// isn't guaranteed. Without Redis, publishing does nothing.
/// </summary>
internal interface IClusterEvents
{
    void Publish(ClusterEvent clusterEvent);

    /// <summary>Waits briefly for pending messages, so a short-lived process (the CLI) sends them before it exits.</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

internal sealed class NoClusterEvents : IClusterEvents
{
    public void Publish(ClusterEvent clusterEvent)
    {
    }

    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed partial class RedisClusterEvents(RedisContext redis, ILogger<RedisClusterEvents> logger) : IClusterEvents
{
    public const string ChannelName = "events";

    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Task, bool> _pending = new();

    /// <summary>Identifies this process, so it ignores its own messages.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public void Publish(ClusterEvent clusterEvent)
    {
        var task = PublishAsync(clusterEvent);
        _pending[task] = true;
        _ = task.ContinueWith(t => _pending.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(_pending.Keys).WaitAsync(FlushTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Other instances still notice the change when they poll.
        }
    }

    public static bool TryParse(RedisValue message, out string instanceId, out ClusterEvent clusterEvent)
    {
        var parts = ((string?)message ?? string.Empty).Split(':');
        instanceId = parts[0];
        clusterEvent = default;
        return parts.Length == 2 && Enum.TryParse(parts[1], ignoreCase: false, out clusterEvent) && Enum.IsDefined(clusterEvent);
    }

    private async Task PublishAsync(ClusterEvent clusterEvent)
    {
        try
        {
            await redis.Connection.GetSubscriber().PublishAsync(redis.Channel(ChannelName), $"{InstanceId}:{clusterEvent}");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogPublishFailed(logger, clusterEvent, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not notify other instances of {Event}; they pick it up at their next poll")]
    private static partial void LogPublishFailed(ILogger logger, ClusterEvent @event, Exception exception);
}

/// <summary>Reacts to <see cref="ClusterEvent"/>s from other instances.</summary>
internal sealed partial class ClusterEventSubscriber(
    RedisContext redis,
    RedisClusterEvents events,
    RuntimeSettingsStore settings,
    SigningKeyStore signingKeys,
    BackChannelLogoutSignal backChannelLogout,
    EmailSignal email,
    ILogger<ClusterEventSubscriber> logger) : IHostedService, IDisposable
{
    private ChannelMessageQueue? _queue;
    private Task? _processing;
    private int _stopped;
    private readonly CancellationTokenSource _stopping = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _queue = await redis.Connection.GetSubscriber().SubscribeAsync(redis.Channel(RedisClusterEvents.ChannelName));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // StackExchange.Redis resubscribes on reconnect only for existing subscriptions; without one, polling
            // still applies changes.
            LogSubscribeFailed(logger, ex);
            return;
        }

        _processing = ProcessAsync(_queue, _stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The test host can stop twice; only the first stop does anything.
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        await _stopping.CancelAsync();
        if (_queue is not null)
        {
            try
            {
                await _queue.UnsubscribeAsync();
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
            {
                // Shutting down anyway; the subscription ends with the connection.
            }
        }

        if (_processing is not null)
        {
            await _processing.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose() => _stopping.Dispose();

    private async Task ProcessAsync(ChannelMessageQueue queue, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in queue.WithCancellation(cancellationToken))
            {
                if (!RedisClusterEvents.TryParse(message.Message, out var instanceId, out var clusterEvent) || instanceId == events.InstanceId)
                {
                    continue;
                }

                try
                {
                    await HandleAsync(clusterEvent, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogHandleFailed(logger, clusterEvent, ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task HandleAsync(ClusterEvent clusterEvent, CancellationToken cancellationToken)
    {
        switch (clusterEvent)
        {
            case ClusterEvent.Settings:
                await settings.LoadAsync(validate: true, cancellationToken);
                break;
            case ClusterEvent.SigningKeys:
                signingKeys.Invalidate();
                break;
            case ClusterEvent.BackChannelLogout:
                backChannelLogout.Notify();
                break;
            case ClusterEvent.Email:
                email.Notify();
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not subscribe to change notifications from other instances; relying on polling")]
    private static partial void LogSubscribeFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling the {Event} notification from another instance failed")]
    private static partial void LogHandleFailed(ILogger logger, ClusterEvent @event, Exception exception);
}
