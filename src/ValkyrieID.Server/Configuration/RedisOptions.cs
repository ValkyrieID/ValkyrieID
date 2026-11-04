using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ValkyrieID.Server.Configuration;

/// <summary>Redis for multi-instance deployments (PROJECT.md §3). Without a connection string, state stays in-process.</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>A StackExchange.Redis configuration string, e.g. <c>redis:6379,password=…,ssl=true</c>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Prefix for every key and channel, so several deployments can share one Redis.</summary>
    public string KeyPrefix { get; set; } = "valkyrieid:";

    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);
}

internal sealed class RedisOptionsValidator : IValidateOptions<RedisOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisOptions options)
    {
        List<string> failures = [];
        if (options.Enabled)
        {
            try
            {
                ConfigurationOptions.Parse(options.ConnectionString!);
            }
            catch (ArgumentException)
            {
                // The message isn't included: it can contain the password.
                failures.Add("Redis:ConnectionString is not a valid StackExchange.Redis configuration string.");
            }
        }

        if (string.IsNullOrEmpty(options.KeyPrefix) || options.KeyPrefix.Length > 100 || options.KeyPrefix.Any(c => char.IsWhiteSpace(c) || c is '{' or '}'))
        {
            // Braces would form a Redis Cluster hash tag and put every key in one slot (or split a code from its
            // replay marker).
            failures.Add("Redis:KeyPrefix must be 1 to 100 characters without whitespace or braces.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
