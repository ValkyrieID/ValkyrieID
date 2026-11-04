using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Redis;

/// <summary>The shared Redis connection and key prefix; registered only when Redis is configured.</summary>
internal sealed class RedisContext(IOptions<RedisOptions> options) : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _connection = new(() =>
    {
        var configuration = ConfigurationOptions.Parse(options.Value.ConnectionString!);
        // Start even while Redis is down; requests that need it fail closed and readiness reports it.
        configuration.AbortOnConnectFail = false;
        configuration.ClientName ??= "valkyrieid";
        return ConnectionMultiplexer.Connect(configuration);
    });

    public IConnectionMultiplexer Connection => _connection.Value;

    public IDatabase Database => Connection.GetDatabase();

    public RedisKey Key(string name) => options.Value.KeyPrefix + name;

    public RedisChannel Channel(string name) => RedisChannel.Literal(options.Value.KeyPrefix + name);

    public void Dispose()
    {
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }
    }
}

/// <summary>Thrown when shared state in Redis can't be read or written; callers fail closed.</summary>
internal sealed class SharedStateUnavailableException(string message, Exception innerException) : Exception(message, innerException);

internal sealed class RedisHealthCheck(RedisContext redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.Database.PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Cannot connect to Redis.");
        }
    }
}
