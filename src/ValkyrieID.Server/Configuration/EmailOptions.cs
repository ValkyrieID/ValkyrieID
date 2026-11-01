using System.Net;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Users;

namespace ValkyrieID.Server.Configuration;

public enum SmtpSecurity
{
    /// <summary>Upgrade the connection with STARTTLS; fail if the server doesn't offer it.</summary>
    StartTls,

    /// <summary>TLS from the start (implicit TLS, usually port 465).</summary>
    Tls,

    /// <summary>Unencrypted; only allowed for a server on the loopback interface.</summary>
    None,
}

/// <summary>The SMTP server. Email features (verification, password reset, email change) are on when it's configured.</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>The default delays before each retry; an email is abandoned after the last one.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string? FromAddress { get; set; }

    /// <summary>The sender's display name. Unset means <see cref="BrandingOptions.ProductName"/>.</summary>
    public string? FromName { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the email worker checks for queued emails when it isn't woken (e.g. queued by the CLI or another instance).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    // An array that defaults to null: the configuration binder appends to non-empty default collections.
    public TimeSpan[]? RetryDelays { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Host);

    internal IReadOnlyList<TimeSpan> EffectiveRetryDelays => RetryDelays ?? DefaultRetryDelays;

    internal TimeSpan LeaseDuration => Timeout + TimeSpan.FromSeconds(30);
}

/// <summary>Lifetimes of the one-time links sent by email.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public TimeSpan PasswordResetLifetime { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan VerificationLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The minimum time between two emails of the same kind to one account. Requests in between are ignored
    /// silently, so the page can't be used to flood a mailbox.
    /// </summary>
    public TimeSpan ResendInterval { get; set; } = TimeSpan.FromMinutes(1);
}

internal sealed class EmailOptionsValidator : IValidateOptions<SmtpOptions>, IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, SmtpOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        if (options.Port is < 1 or > 65535)
        {
            failures.Add("Smtp:Port must be between 1 and 65535.");
        }

        if (!UserNames.IsValidEmail(options.FromAddress))
        {
            failures.Add("Smtp:FromAddress must be a valid email address when Smtp:Host is set.");
        }

        if (options.FromName is { Length: > 100 })
        {
            failures.Add("Smtp:FromName must be at most 100 characters.");
        }

        if (options.Security == SmtpSecurity.None && !IsLoopback(options.Host!))
        {
            failures.Add("Smtp:Security None is only allowed for a loopback host; use StartTls or Tls.");
        }

        if (string.IsNullOrEmpty(options.UserName) != string.IsNullOrEmpty(options.Password))
        {
            failures.Add("Smtp:UserName and Smtp:Password must be set together.");
        }

        if (options.Timeout < TimeSpan.FromSeconds(1) || options.Timeout > TimeSpan.FromMinutes(5))
        {
            failures.Add("Smtp:Timeout must be between 1 second and 5 minutes.");
        }

        if (options.PollInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("Smtp:PollInterval must be at least 1 second.");
        }

        if (options.RetryDelays is { } delays && (delays.Length > 20 || delays.Any(d => d <= TimeSpan.Zero || d > TimeSpan.FromDays(1))))
        {
            failures.Add("Smtp:RetryDelays must have at most 20 entries, each between 0 (exclusive) and 1 day.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        List<string> failures = [];
        if (options.PasswordResetLifetime < TimeSpan.FromMinutes(5) || options.PasswordResetLifetime > TimeSpan.FromHours(24))
        {
            failures.Add("Email:PasswordResetLifetime must be between 5 minutes and 24 hours.");
        }

        if (options.VerificationLifetime < TimeSpan.FromMinutes(5) || options.VerificationLifetime > TimeSpan.FromDays(7))
        {
            failures.Add("Email:VerificationLifetime must be between 5 minutes and 7 days.");
        }

        if (options.ResendInterval < TimeSpan.Zero || options.ResendInterval > TimeSpan.FromHours(1))
        {
            failures.Add("Email:ResendInterval must be between 0 and 1 hour.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
