using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Users;

/// <summary>Checks passwords against the Have I Been Pwned range API using k-anonymity: only the first 5 hex characters of the SHA-1 hash are sent.</summary>
internal sealed partial class BreachedPasswordChecker(HttpClient http, IOptions<PasswordPolicyOptions> options, ILogger<BreachedPasswordChecker> logger)
{
    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        var prefix = hash[..5];
        var suffix = hash[5..];

        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Value.BreachedPasswordApiUrl, prefix));
            // Padding hides the real size of the response from network observers.
            request.Headers.Add("Add-Padding", "true");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Fail open: an outage of the external service must not block password changes.
            LogUnavailable(logger, ex);
            return false;
        }

        return IsListed(body, suffix);
    }

    /// <summary>Parses <c>SUFFIX:COUNT</c> lines; padding entries have a count of 0.</summary>
    internal static bool IsListed(string body, string suffix)
    {
        foreach (var line in body.AsSpan().EnumerateLines())
        {
            var separator = line.IndexOf(':');
            if (separator == 35
                && line[..separator].Equals(suffix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                && count > 0)
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The breached password check is unavailable; the password was accepted without it.")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}

internal sealed class PasswordValidator(IOptions<PasswordPolicyOptions> options, BreachedPasswordChecker breachedPasswords)
{
    public async Task<IReadOnlyList<string>> ValidateAsync(string password, string? userName, string? email, CancellationToken cancellationToken)
    {
        var policy = options.Value;
        List<string> errors = [];

        // NIST SP 800-63B §5.1.1.2: each Unicode code point counts as one character.
        var length = password.EnumerateRunes().Count();
        if (length < policy.MinimumLength)
        {
            errors.Add($"The password must be at least {policy.MinimumLength} characters long.");
        }

        if (length > policy.MaximumLength)
        {
            errors.Add($"The password must be at most {policy.MaximumLength} characters long.");
        }

        if (policy.RequireUppercase && !password.Any(char.IsUpper))
        {
            errors.Add("The password must contain an uppercase letter.");
        }

        if (policy.RequireLowercase && !password.Any(char.IsLower))
        {
            errors.Add("The password must contain a lowercase letter.");
        }

        if (policy.RequireDigit && !password.Any(char.IsDigit))
        {
            errors.Add("The password must contain a digit.");
        }

        if (policy.RequireNonAlphanumeric && password.All(char.IsLetterOrDigit))
        {
            errors.Add("The password must contain a character that isn't a letter or digit.");
        }

        if ((userName is not null && password.Equals(userName, StringComparison.OrdinalIgnoreCase))
            || (email is not null && password.Equals(email, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("The password must not be your username or email address.");
        }

        if (errors.Count == 0 && policy.CheckBreachedPasswords && await breachedPasswords.IsBreachedAsync(password, cancellationToken))
        {
            errors.Add("This password has appeared in a data breach. Choose a different one.");
        }

        return errors;
    }
}
