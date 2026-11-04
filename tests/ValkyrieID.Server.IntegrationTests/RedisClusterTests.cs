using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Redis;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.IntegrationTests;

[Trait("Category", "Redis")]
public sealed class RedisMultiInstanceTests(PostgreSqlFixture postgres, RedisFixture redis)
    : MultiInstanceTests(postgres, redis), IClassFixture<PostgreSqlFixture>, IClassFixture<RedisFixture>;

/// <summary>The multi-instance tests against a Redis Cluster with three primaries, plus cluster-specific checks.</summary>
[Trait("Category", "Redis")]
public sealed class RedisClusterMultiInstanceTests(PostgreSqlFixture postgres, RedisClusterFixture cluster)
    : MultiInstanceTests(postgres, cluster), IClassFixture<PostgreSqlFixture>, IClassFixture<RedisClusterFixture>
{
    [Fact]
    public async Task The_cluster_rejects_scripts_over_keys_in_different_slots()
    {
        // Guards the tests above: they only prove something if the cluster really enforces slots.
        await using var connection = await ConnectionMultiplexer.ConnectAsync(cluster.ConnectionString);
        var (code, used) = Enumerable.Range(0, 100)
            .Select(i => ((RedisKey)$"x:code:{i}", (RedisKey)$"x:code-used:{i}"))
            .First(k => connection.HashSlot(k.Item1) != connection.HashSlot(k.Item2));

        // StackExchange.Redis rejects it before sending (RedisCommandException, which isn't a RedisException); the
        // server would answer CROSSSLOT.
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            connection.GetDatabase().ScriptEvaluateAsync("return redis.call('GET', KEYS[1])", [code, used]));

        Assert.True(error.Message.Contains("CROSSSLOT", StringComparison.Ordinal) || error.Message.Contains("single slot", StringComparison.Ordinal), error.Message);
    }

    [Fact]
    public async Task A_code_and_its_replay_marker_always_share_a_slot()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(cluster.ConnectionString);
        foreach (var prefix in new[] { "valkyrieid:", "a:b:c-", "x" })
        {
            var redis = new RedisContext(Options.Create(new RedisOptions { ConnectionString = cluster.ConnectionString, KeyPrefix = prefix }));
            var store = new RedisAuthorizationCodeStore(redis, TimeProvider.System);
            for (var i = 0; i < 200; i++)
            {
                var id = AuthorizationCodeProtection.Id(IAuthorizationCodeStore.NewCode());
                Assert.Equal(connection.HashSlot(store.CodeKey(id)), connection.HashSlot(store.ReplayMarkerKey(id)));
            }
        }
    }

    [Fact]
    public async Task Codes_are_spread_over_the_primaries()
    {
        var prefix = $"test-{Guid.NewGuid():N}:";
        await using var instances = Start(keyPrefix: prefix);
        await SetUpAsync(instances.A);
        var browser = instances.A.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        for (var i = 0; i < 30; i++)
        {
            await AuthorizeAsync(browser, NewPkce().Challenge);
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(cluster.ConnectionString);
        var primaries = connection.GetServers().Where(s => !s.IsReplica).ToList();
        Assert.Equal(3, primaries.Count);
        var withCodes = primaries.Count(s => s.Keys(pattern: prefix + "code:*").Any());
        Assert.True(withCodes > 1, $"Codes were stored on {withCodes} of 3 primaries.");
    }

    [Fact]
    public async Task Readiness_is_healthy_on_the_cluster()
    {
        await using var instances = Start();

        var response = await instances.B.CreateClient().GetAsync("/health/ready", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<RedisAuthorizationCodeStore>(instances.B.Services.GetRequiredService<IAuthorizationCodeStore>());
    }
}
