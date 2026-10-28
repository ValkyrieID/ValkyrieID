using System.Collections.Concurrent;
using System.Net;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Stands in for relying parties' back-channel logout endpoints and records what the server sends.</summary>
public sealed class BackChannelReceiver : HttpMessageHandler
{
    private readonly ConcurrentQueue<BackChannelRequest> _requests = new();

    /// <summary>The response to send; 200 OK by default.</summary>
    public Func<BackChannelRequest, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    /// <summary>How long the relying party takes to respond.</summary>
    public TimeSpan Delay { get; set; }

    public IReadOnlyList<BackChannelRequest> Requests => [.. _requests];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(body);
        var received = new BackChannelRequest(
            request.Method,
            request.RequestUri!,
            request.Content?.Headers.ContentType?.MediaType,
            form.TryGetValue("logout_token", out var token) ? token.ToString() : null,
            DateTimeOffset.UtcNow);
        _requests.Enqueue(received);
        return Respond(received);
    }

    // The HTTP client factory disposes handlers when they expire; the receiver outlives them.
    protected override void Dispose(bool disposing)
    {
    }
}

public sealed record BackChannelRequest(HttpMethod Method, Uri Uri, string? ContentType, string? LogoutToken, DateTimeOffset ReceivedAt);
