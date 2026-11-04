using Microsoft.Extensions.Options;
using ValkyrieID.Server.Redis;

namespace ValkyrieID.Server.Configuration;

internal static class RedisExtensions
{
    public static bool RedisEnabled(this IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[$"{RedisOptions.SectionName}:{nameof(RedisOptions.ConnectionString)}"]);

    /// <summary>
    /// With <c>Redis:ConnectionString</c>, authorization codes and rate-limit counters are shared through Redis and
    /// changes are announced to other instances; without it, everything stays in-process (single instance).
    /// </summary>
    public static IServiceCollection AddValkyrieRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RedisOptions>().Bind(configuration.GetSection(RedisOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RedisOptions>, RedisOptionsValidator>();

        if (!configuration.RedisEnabled())
        {
            services.AddSingleton<IClusterEvents, NoClusterEvents>();
            return services;
        }

        services.AddSingleton<RedisContext>();
        services.AddSingleton<RedisRateLimitCounter>();
        services.AddSingleton<RedisClusterEvents>();
        services.AddSingleton<IClusterEvents>(sp => sp.GetRequiredService<RedisClusterEvents>());
        services.AddHostedService<ClusterEventSubscriber>();
        return services;
    }
}
