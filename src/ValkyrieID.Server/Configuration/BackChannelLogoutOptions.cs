using Microsoft.Extensions.Options;

namespace ValkyrieID.Server.Configuration;

public sealed class BackChannelLogoutOptions
{
    public const string SectionName = "BackChannelLogout";

    /// <summary>The default delays before each retry; a delivery is abandoned after the last one.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    /// <summary>
    /// How long a logout token is valid. OIDC Back-Channel Logout 1.0 A4: "preferably at most two minutes in the
    /// future". A new token is created for every attempt.
    /// </summary>
    public static readonly TimeSpan LogoutTokenLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Timeout of each request to a client. A sign-out waits at most this long for the clients before redirecting
    /// the browser; failed requests are retried in the background.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the delivery worker checks for pending notifications when it isn't woken. Only needed for
    /// notifications queued by another process (the CLI or another instance).
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Delays before each retry. Unset means <see cref="DefaultRetryDelays"/>.</summary>
    // An array that defaults to null: the configuration binder appends to non-empty default collections.
    public TimeSpan[]? RetryDelays { get; set; }

    internal IReadOnlyList<TimeSpan> EffectiveRetryDelays => RetryDelays ?? DefaultRetryDelays;

    /// <summary>How long an instance holds a delivery while sending it; afterwards another instance may take it over.</summary>
    internal TimeSpan LeaseDuration => RequestTimeout + TimeSpan.FromSeconds(30);
}

internal sealed class BackChannelLogoutOptionsValidator : IValidateOptions<BackChannelLogoutOptions>
{
    public ValidateOptionsResult Validate(string? name, BackChannelLogoutOptions options)
    {
        List<string> failures = [];

        if (options.RequestTimeout < TimeSpan.FromSeconds(1) || options.RequestTimeout > TimeSpan.FromSeconds(60))
        {
            failures.Add("BackChannelLogout:RequestTimeout must be between 1 and 60 seconds.");
        }

        if (options.PollInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("BackChannelLogout:PollInterval must be at least 1 second.");
        }

        if (options.RetryDelays is { } delays && (delays.Length > 20 || delays.Any(d => d <= TimeSpan.Zero || d > TimeSpan.FromDays(1))))
        {
            failures.Add("BackChannelLogout:RetryDelays must have at most 20 entries, each between 0 (exclusive) and 1 day.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
