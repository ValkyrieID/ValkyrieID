using Microsoft.Extensions.Options;

namespace ValkyrieID.Server.Configuration;

public sealed class TokenOptions
{
    public const string SectionName = "Tokens";

    /// <summary>Default access token lifetime; clients may override it.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Clock skew tolerated when validating tokens and timestamps.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class SigningKeyOptions
{
    public const string SectionName = "SigningKeys";

    /// <summary>How long a key is used for signing before the next key takes over.</summary>
    public TimeSpan RotationInterval { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long a new key is published in JWKS before it is used for signing.</summary>
    public TimeSpan PublishLead { get; set; } = TimeSpan.FromDays(2);

    /// <summary>RSA modulus size in bits. RFC 7518 §3.3 requires at least 2048.</summary>
    public int RsaKeySize { get; set; } = 2048;

    /// <summary>How often the rotation job runs and other instances reload keys.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);
}

internal sealed class TokenOptionsValidator : IValidateOptions<TokenOptions>
{
    public ValidateOptionsResult Validate(string? name, TokenOptions options)
    {
        List<string> failures = [];

        if (options.AccessTokenLifetime <= TimeSpan.Zero)
        {
            failures.Add("Tokens:AccessTokenLifetime must be positive.");
        }

        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew > TimeSpan.FromMinutes(5))
        {
            failures.Add("Tokens:ClockSkew must be between 0 and 5 minutes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal sealed class SigningKeyOptionsValidator : IValidateOptions<SigningKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, SigningKeyOptions options)
    {
        List<string> failures = [];

        if (options.RsaKeySize is < 2048 or > 8192 || options.RsaKeySize % 1024 != 0)
        {
            failures.Add("SigningKeys:RsaKeySize must be 2048, 3072, 4096, ... up to 8192.");
        }

        if (options.CheckInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("SigningKeys:CheckInterval must be at least 1 second.");
        }

        if (options.PublishLead < options.CheckInterval)
        {
            failures.Add("SigningKeys:PublishLead must be at least SigningKeys:CheckInterval so every instance sees a new key before it is used.");
        }

        if (options.RotationInterval <= options.PublishLead)
        {
            failures.Add("SigningKeys:RotationInterval must be longer than SigningKeys:PublishLead.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
