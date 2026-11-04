using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;
using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>A Redis deployment for tests: a single server or a cluster.</summary>
public interface IRedisServer
{
    string ConnectionString { get; }

    /// <summary>Redis settings with a key prefix of their own, so tests don't share codes or rate-limit counters.</summary>
    Dictionary<string, string?> Settings(string? keyPrefix = null);

    protected static Dictionary<string, string?> SettingsFor(string connectionString, string? keyPrefix) => new()
    {
        ["Redis:ConnectionString"] = connectionString,
        ["Redis:KeyPrefix"] = keyPrefix ?? $"test-{Guid.NewGuid():N}:",
    };
}

public sealed class RedisFixture : IRedisServer, IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Dictionary<string, string?> Settings(string? keyPrefix = null) => IRedisServer.SettingsFor(ConnectionString, keyPrefix);

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// A Redis Cluster with three primaries, all in one container. Each node announces 127.0.0.1 and a port that is the
/// same inside and outside the container, so the addresses nodes hand out in redirects work from the test process.
/// </summary>
public sealed class RedisClusterFixture : IRedisServer, IAsyncLifetime
{
    private const string Ready = "CLUSTER READY";

    private readonly int[] _ports = [FreePort(), FreePort(), FreePort()];
    private readonly IContainer _container;

    public RedisClusterFixture()
    {
        var nodes = string.Join(' ', _ports.Select(p => $"127.0.0.1:{p}"));
        // The servers log to the container output, so a startup failure is visible; the waits give up after ~20 s.
        var script = $"""
            for p in {string.Join(' ', _ports)}; do
              redis-server --port $p --cluster-enabled yes --cluster-config-file /tmp/nodes-$p.conf \
                --cluster-announce-ip 127.0.0.1 --protected-mode no --appendonly no --dir /tmp &
            done
            for p in {string.Join(' ', _ports)}; do
              i=0; until redis-cli -p $p ping >/dev/null 2>&1; do i=$((i+1)); [ $i -gt 100 ] && echo "node $p did not start" && exit 1; sleep 0.2; done
            done
            redis-cli --cluster create {nodes} --cluster-yes
            i=0; until redis-cli -p {_ports[0]} cluster info | grep -q cluster_state:ok; do i=$((i+1)); [ $i -gt 100 ] && echo "cluster not ok" && exit 1; sleep 0.2; done
            echo {Ready}
            wait
            """;
        var builder = new ContainerBuilder("redis:8-alpine")
            .WithEntrypoint("sh", "-c", script.Replace("\r", string.Empty, StringComparison.Ordinal))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(Ready));
        foreach (var port in _ports)
        {
            builder = builder.WithPortBinding(port, port);
        }

        _container = builder.Build();
    }

    public string ConnectionString => string.Join(',', _ports.Select(p => $"127.0.0.1:{p}"));

    public Dictionary<string, string?> Settings(string? keyPrefix = null) => IRedisServer.SettingsFor(ConnectionString, keyPrefix);

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    // Cluster nodes also listen on port + 10000 (the cluster bus), so ports must be at most 55535. The range also stays
    // clear of the host ports Docker assigns to other containers (from about 32768 up), so a port a node used is never
    // reused by, say, a PostgreSQL container while a Redis client still reconnects to it.
    private static int FreePort()
    {
        while (true)
        {
            var port = Random.Shared.Next(10000, 20000);
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
            }
        }
    }
}

/// <summary>The authorization code flow with codes and rate limits in Redis.</summary>
[Trait("Category", "Redis")]
public sealed class RedisAuthorizationTests(PostgreSqlFixture postgres, RedisFixture redis)
    : AuthorizationTests, IClassFixture<PostgreSqlFixture>, IClassFixture<RedisFixture>
{
    protected override ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null) =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString($"valkyrieid_{Guid.NewGuid():N}"))
        {
            TimeProvider = timeProvider,
            Settings = new(redis.Settings().Concat(settings ?? [])),
        };
}

/// <summary>Refresh tokens, including code replay revoking them, with codes in Redis.</summary>
[Trait("Category", "Redis")]
public sealed class RedisRefreshTokenTests(PostgreSqlFixture postgres, RedisFixture redis)
    : RefreshTokenTests, IClassFixture<PostgreSqlFixture>, IClassFixture<RedisFixture>
{
    protected override ValkyrieAppFactory CreateFactory(
        TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null, IPAddress? remoteIpAddress = null) =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString($"valkyrieid_{Guid.NewGuid():N}"))
        {
            TimeProvider = timeProvider,
            Settings = new(redis.Settings().Concat(settings ?? [])),
            RemoteIpAddress = remoteIpAddress,
        };
}
