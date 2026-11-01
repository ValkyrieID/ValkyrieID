using System.Threading.Channels;

namespace ValkyrieID.Server.Logout;

/// <summary>Wakes a background worker when work was queued or rescheduled in this process.</summary>
internal abstract class WakeSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>Waits until <see cref="Notify"/> is called or <paramref name="timeout"/> has passed.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (_channel.Reader.TryRead(out _))
        {
            return;
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, timeProvider, wait.Token);
        var signaled = _channel.Reader.WaitToReadAsync(wait.Token).AsTask();
        await Task.WhenAny(delay, signaled);
        await wait.CancelAsync();
        _channel.Reader.TryRead(out _);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>Wakes the back-channel logout delivery worker.</summary>
internal sealed class BackChannelLogoutSignal : WakeSignal;
