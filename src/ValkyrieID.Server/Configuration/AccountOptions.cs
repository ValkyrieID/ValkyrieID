using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Security;

namespace ValkyrieID.Server.Configuration;

public sealed class PasswordPolicyOptions
{
    public const string SectionName = "PasswordPolicy";

    /// <summary>Minimum length in Unicode code points (NIST SP 800-63B §5.1.1.2: at least 8).</summary>
    public int MinimumLength { get; set; } = 8;

    /// <summary>Maximum length in Unicode code points (NIST SP 800-63B §5.1.1.2: at least 64 should be permitted).</summary>
    public int MaximumLength { get; set; } = 128;

    public bool RequireUppercase { get; set; }

    public bool RequireLowercase { get; set; }

    public bool RequireDigit { get; set; }

    public bool RequireNonAlphanumeric { get; set; }

    /// <summary>Reject passwords found in the Have I Been Pwned database (k-anonymity range API).</summary>
    public bool CheckBreachedPasswords { get; set; }

    public Uri BreachedPasswordApiUrl { get; set; } = new("https://api.pwnedpasswords.com/range/");
}

public sealed class PasswordHashingOptions
{
    public const string SectionName = "PasswordHashing";

    /// <summary>
    /// Configurations from the OWASP Password Storage Cheat Sheet (memory in KiB, iterations), all with parallelism 1.
    /// The configured parameters must be at least as strong as one of them.
    /// </summary>
    internal static readonly (int MemorySizeKiB, int Iterations)[] OwaspMinimums =
        [(47104, 1), (19456, 2), (12288, 3), (9216, 4), (7168, 5)];

    public int MemorySizeKiB { get; set; } = Argon2Parameters.Default.MemorySizeKiB;

    public int Iterations { get; set; } = Argon2Parameters.Default.Iterations;

    public int Parallelism { get; set; } = Argon2Parameters.Default.Parallelism;

    internal Argon2Parameters ToParameters() => new(MemorySizeKiB, Iterations, Parallelism);
}

public sealed class LockoutOptions
{
    public const string SectionName = "Lockout";

    /// <summary>Consecutive failed logins that lock the account.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class SsoSessionOptions
{
    public const string SectionName = "Session";

    /// <summary>Absolute lifetime of the SSO session cookie.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(8);
}

public sealed class RateLimitRule
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }
}

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>Login and setup form submissions per client IP address.</summary>
    public RateLimitRule Login { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    /// <summary>Login attempts per username or email, whether or not the account exists.</summary>
    public RateLimitRule LoginAccount { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(15) };

    /// <summary>Token endpoint requests per client IP address.</summary>
    public RateLimitRule Token { get; set; } = new() { PermitLimit = 100, Window = TimeSpan.FromMinutes(1) };
}

public sealed partial class BrandingOptions
{
    public const string SectionName = "Branding";

    public string ProductName { get; set; } = "ValkyrieID";

    /// <summary>Directory with branding files (logo, background image, custom CSS), served under <c>/branding/</c>.</summary>
    public string? Directory { get; set; }

    /// <summary>File name of the logo in <see cref="Directory"/>.</summary>
    public string? Logo { get; set; }

    /// <summary>File name of the background image in <see cref="Directory"/>.</summary>
    public string? BackgroundImage { get; set; }

    /// <summary>File name of a stylesheet in <see cref="Directory"/>, loaded after the built-in styles.</summary>
    public string? CustomCss { get; set; }

    public string? PrimaryColor { get; set; }

    public string? BackgroundColor { get; set; }

    public string? SurfaceColor { get; set; }

    public string? TextColor { get; set; }

    internal IEnumerable<(string Name, string? Value)> Colors =>
    [
        ("PrimaryColor", PrimaryColor),
        ("BackgroundColor", BackgroundColor),
        ("SurfaceColor", SurfaceColor),
        ("TextColor", TextColor),
    ];

    internal IEnumerable<(string Name, string? Value)> Files =>
    [
        ("Logo", Logo),
        ("BackgroundImage", BackgroundImage),
        ("CustomCss", CustomCss),
    ];

    // Only values that can't break out of a CSS declaration: hex, named, or rgb()/hsl() colors.
    [GeneratedRegex(@"^(#[0-9a-fA-F]{3,8}|[a-zA-Z]{3,30}|(rgb|rgba|hsl|hsla)\([0-9.,%\s/a-z]{1,60}\))$")]
    internal static partial Regex ColorPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-][A-Za-z0-9._-]{0,99}$")]
    internal static partial Regex FileNamePattern();
}

internal sealed class AccountOptionsValidator :
    IValidateOptions<PasswordPolicyOptions>,
    IValidateOptions<PasswordHashingOptions>,
    IValidateOptions<LockoutOptions>,
    IValidateOptions<SsoSessionOptions>,
    IValidateOptions<RateLimitOptions>,
    IValidateOptions<BrandingOptions>
{
    public ValidateOptionsResult Validate(string? name, PasswordPolicyOptions options)
    {
        List<string> failures = [];
        if (options.MinimumLength < 8)
        {
            failures.Add("PasswordPolicy:MinimumLength must be at least 8.");
        }

        if (options.MaximumLength is < 64 or > 1024 || options.MaximumLength < options.MinimumLength)
        {
            failures.Add("PasswordPolicy:MaximumLength must be between 64 and 1024 and not below MinimumLength.");
        }

        if (options.BreachedPasswordApiUrl is not { IsAbsoluteUri: true, Scheme: "https" })
        {
            failures.Add("PasswordPolicy:BreachedPasswordApiUrl must be an absolute https URL.");
        }

        return Result(failures);
    }

    public ValidateOptionsResult Validate(string? name, PasswordHashingOptions options)
    {
        List<string> failures = [];

        // Upper bounds match what PasswordHasher accepts when verifying stored hashes.
        if (options.MemorySizeKiB > 1024 * 1024 || options.Iterations > 100)
        {
            failures.Add("PasswordHashing:MemorySizeKiB must be at most 1048576 and PasswordHashing:Iterations at most 100.");
        }
        else if (!PasswordHashingOptions.OwaspMinimums.Any(m => options.MemorySizeKiB >= m.MemorySizeKiB && options.Iterations >= m.Iterations))
        {
            failures.Add(
                "PasswordHashing:MemorySizeKiB and PasswordHashing:Iterations must be at least one of the OWASP minimums: "
                + string.Join(", ", PasswordHashingOptions.OwaspMinimums.Select(m => $"{m.MemorySizeKiB} KiB with {m.Iterations} iteration(s)"))
                + ".");
        }

        if (options.Parallelism is < 1 or > 16)
        {
            failures.Add("PasswordHashing:Parallelism must be between 1 and 16.");
        }

        return Result(failures);
    }

    public ValidateOptionsResult Validate(string? name, LockoutOptions options)
    {
        List<string> failures = [];
        if (options.MaxFailedAttempts < 1)
        {
            failures.Add("Lockout:MaxFailedAttempts must be at least 1.");
        }

        if (options.Duration <= TimeSpan.Zero)
        {
            failures.Add("Lockout:Duration must be positive.");
        }

        return Result(failures);
    }

    public ValidateOptionsResult Validate(string? name, SsoSessionOptions options) =>
        options.Lifetime < TimeSpan.FromMinutes(1)
            ? ValidateOptionsResult.Fail("Session:Lifetime must be at least 1 minute.")
            : ValidateOptionsResult.Success;

    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        List<string> failures = [];
        foreach (var (key, rule) in new[] { ("Login", options.Login), ("LoginAccount", options.LoginAccount), ("Token", options.Token) })
        {
            if (rule.PermitLimit < 1 || rule.Window <= TimeSpan.Zero)
            {
                failures.Add($"RateLimits:{key} needs a positive PermitLimit and Window.");
            }
        }

        return Result(failures);
    }

    public ValidateOptionsResult Validate(string? name, BrandingOptions options)
    {
        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.ProductName) || options.ProductName.Length > 100)
        {
            failures.Add("Branding:ProductName must be 1-100 characters.");
        }

        foreach (var (key, value) in options.Colors)
        {
            if (value is not null && !BrandingOptions.ColorPattern().IsMatch(value))
            {
                failures.Add($"Branding:{key} must be a hex, named, rgb() or hsl() color.");
            }
        }

        foreach (var (key, value) in options.Files)
        {
            if (value is null)
            {
                continue;
            }

            if (options.Directory is null)
            {
                failures.Add($"Branding:{key} requires Branding:Directory.");
            }
            else if (!BrandingOptions.FileNamePattern().IsMatch(value))
            {
                failures.Add($"Branding:{key} must be a plain file name in Branding:Directory.");
            }
        }

        if (options.Directory is not null && !System.IO.Directory.Exists(options.Directory))
        {
            failures.Add("Branding:Directory does not exist.");
        }

        return Result(failures);
    }

    private static ValidateOptionsResult Result(List<string> failures) =>
        failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
}
