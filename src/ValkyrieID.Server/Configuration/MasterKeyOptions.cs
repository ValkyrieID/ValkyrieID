using Microsoft.Extensions.Options;
using ValkyrieID.Core.Security;

namespace ValkyrieID.Server.Configuration;

public sealed class MasterKeyOptions
{
    public const string SectionName = "MasterKey";

    /// <summary>Base64-encoded 256-bit key used to encrypt secrets at rest.</summary>
    public string? Current { get; set; }

    /// <summary>Previous keys, still accepted for decryption during master key rotation.</summary>
    public List<string> Previous { get; set; } = [];
}

internal sealed class MasterKeyOptionsValidator : IValidateOptions<MasterKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, MasterKeyOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Current))
        {
            failures.Add(
                "MasterKey:Current is required. Generate one with `openssl rand -base64 32` "
                + "and supply it via configuration or the MasterKey__Current environment variable.");
        }
        else if (!MasterKey.TryParse(options.Current, out _, out var error))
        {
            failures.Add($"MasterKey:Current is invalid: {error}");
        }

        for (var i = 0; i < options.Previous.Count; i++)
        {
            if (!MasterKey.TryParse(options.Previous[i], out _, out var error))
            {
                failures.Add($"MasterKey:Previous:{i} is invalid: {error}");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal static class MasterKeyServiceCollectionExtensions
{
    public static IServiceCollection AddValkyrieMasterKey(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MasterKeyOptions>().Bind(configuration.GetSection(MasterKeyOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<MasterKeyOptions>, MasterKeyOptionsValidator>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MasterKeyOptions>>().Value;
            return new SecretProtector(
                MasterKey.Parse(options.Current ?? string.Empty),
                options.Previous.Select(MasterKey.Parse));
        });

        return services;
    }
}
