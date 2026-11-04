using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Data;
using ValkyrieID.Server.Redis;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>
/// Redis configured but unreachable: everything that needs shared state fails closed instead of falling back to
/// per-instance state. Needs no container (nothing listens on the port), so it runs with the SQLite tests.
/// </summary>
public sealed class RedisUnavailableTests : IDisposable
{
    private const string UnreachableRedis = "127.0.0.1:1,connectTimeout=500,syncTimeout=500,asyncTimeout=500,connectRetry=0";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Readiness_reports_redis_and_liveness_stays_up()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/connect/token")]
    [InlineData("GET", "/connect/authorize?client_id=x")]
    public async Task Rate_limited_endpoints_answer_503_with_retry_after(string method, string path)
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path), CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Login_attempts_are_refused_instead_of_counted_per_instance()
    {
        await using var factory = CreateFactory();
        var limiter = factory.Services.GetRequiredService<Authentication.LoginAttemptLimiter>();

        var decision = await limiter.AcquireAsync("alice", CancellationToken);

        Assert.False(decision.Allowed);
        Assert.True(decision.Unavailable);
    }

    [Fact]
    public async Task The_code_store_fails_closed()
    {
        await using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<IAuthorizationCodeStore>();
        var grant = new AuthorizationCodeGrant("spa", "https://app.example.test/callback", Guid.NewGuid(), "sid", DateTimeOffset.UtcNow,
            ["pwd"], ["openid"], ["spa"], null, null, DateTimeOffset.UtcNow.AddSeconds(60));

        Assert.IsType<RedisAuthorizationCodeStore>(store);
        await Assert.ThrowsAsync<SharedStateUnavailableException>(() => store.CreateAsync(grant, CancellationToken));
        await Assert.ThrowsAsync<SharedStateUnavailableException>(() => store.RedeemAsync(IAuthorizationCodeStore.NewCode(), CancellationToken));
    }

    [Fact]
    public async Task The_cli_still_works()
    {
        await using var factory = CreateFactory();

        var (exitCode, _, error) = await factory.RunCliAsync("settings", "set", "Tokens:AccessTokenLifetime", "00:12:00");

        Assert.True(exitCode == 0, error);
    }

    private ValkyrieAppFactory CreateFactory(Dictionary<string, string?>? settings = null) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, $"{Guid.NewGuid():N}.db")};Pooling=False")
        {
            Settings = new(new Dictionary<string, string?> { ["Redis:ConnectionString"] = UnreachableRedis }.Concat(settings ?? [])),
        };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
