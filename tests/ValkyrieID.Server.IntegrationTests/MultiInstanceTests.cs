using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ValkyrieID.Data;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Two server instances sharing one PostgreSQL database and one Redis, as in a multi-instance deployment.</summary>
public abstract class MultiInstanceTests(PostgreSqlFixture postgres, IRedisServer redis)
{
    protected const string Password = "correct horse battery staple";
    private const string RedirectUri = "https://app.example.test/callback";

    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_code_issued_by_one_instance_is_redeemed_by_another_and_a_replay_there_revokes_its_tokens()
    {
        await using var cluster = Start();
        var secret = await SetUpAsync(cluster.A);
        var browserA = cluster.A.CreateBrowser();
        await Forms.LoginAsync(browserA, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = await AuthorizeAsync(browserA, challenge);

        var redeemed = await cluster.B.CreateClient().SendAsync(CodeRequest(code, verifier, secret), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var refreshToken = (await ReadJsonAsync(redeemed)).GetProperty("refresh_token").GetString()!;

        // RFC 6749 §4.1.2: the replay is denied on the other instance too, and the tokens issued from the code revoked.
        var replay = await cluster.A.CreateClient().SendAsync(CodeRequest(code, verifier, secret), CancellationToken);
        var refresh = await cluster.B.CreateClient().SendAsync(TokenRequest(Basic(secret), ("grant_type", "refresh_token"), ("refresh_token", refreshToken)), CancellationToken);

        await AssertErrorAsync(replay, HttpStatusCode.BadRequest, "invalid_grant");
        await AssertErrorAsync(refresh, HttpStatusCode.BadRequest, "invalid_grant");
        await using var scope = cluster.A.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .AnyAsync(e => e.EventType == "token.rejected" && e.Details!.Contains("code_replayed"), CancellationToken));
    }

    [Fact]
    public async Task The_sso_session_from_one_instance_is_accepted_by_another()
    {
        await using var cluster = Start();
        await SetUpAsync(cluster.A);
        var login = await Forms.LoginAsync(cluster.A.CreateBrowser(), "alice", Password);
        var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        // The session cookie from instance A, sent to instance B: Data Protection keys and sessions are shared.
        var request = new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl(NewPkce().Challenge));
        request.Headers.Add("Cookie", cookies);
        var response = await cluster.B.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false })
            .SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(RedirectUri + "?code=", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_redemptions_on_two_instances_succeed_only_once()
    {
        await using var cluster = Start();
        var secret = await SetUpAsync(cluster.A);
        var browser = cluster.A.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        for (var i = 0; i < 5; i++)
        {
            var (verifier, challenge) = NewPkce();
            var code = await AuthorizeAsync(browser, challenge);
            var responses = await Task.WhenAll(
                cluster.A.CreateClient().SendAsync(CodeRequest(code, verifier, secret), CancellationToken),
                cluster.B.CreateClient().SendAsync(CodeRequest(code, verifier, secret), CancellationToken));

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Redis_holds_no_codes_or_readable_grants()
    {
        var prefix = $"test-{Guid.NewGuid():N}:";
        await using var cluster = Start(keyPrefix: prefix);
        await SetUpAsync(cluster.A);
        var browser = cluster.A.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var code = await AuthorizeAsync(browser, NewPkce().Challenge, nonce: "nonce-visible-if-plaintext");
        string subject;
        await using (var scope = cluster.A.Services.CreateAsyncScope())
        {
            subject = (await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users.SingleAsync(CancellationToken)).Id.ToString();
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var keys = connection.GetServers().Where(s => !s.IsReplica).SelectMany(s => s.Keys(pattern: prefix + "*")).ToList();
        Assert.Contains(keys, k => k.ToString().StartsWith(prefix + "code:", StringComparison.Ordinal));

        foreach (var key in keys)
        {
            var name = key.ToString();
            Assert.DoesNotContain(code, name, StringComparison.Ordinal);
            if (name.StartsWith(prefix + "code:", StringComparison.Ordinal))
            {
                var value = Encoding.Latin1.GetString((byte[])(await connection.GetDatabase().StringGetAsync(key))!);
                foreach (var plaintext in new[] { code, subject, "nonce-visible-if-plaintext", RedirectUri, "openid", "web" })
                {
                    Assert.DoesNotContain(plaintext, value, StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                // Rate-limit counters: no IP addresses or usernames in key names.
                Assert.StartsWith(prefix + "rl:", name, StringComparison.Ordinal);
                Assert.DoesNotContain("alice", name, StringComparison.Ordinal);
                Assert.DoesNotContain("127.0.0.1", name, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Ip_rate_limits_are_shared_by_the_instances()
    {
        await using var cluster = Start(new() { ["RateLimits:Token:PermitLimit"] = "3", ["RateLimits:Token:Window"] = "00:05:00" });
        HttpResponseMessage[] responses =
        [
            await PostTokenAsync(cluster.A),
            await PostTokenAsync(cluster.B),
            await PostTokenAsync(cluster.A),
            await PostTokenAsync(cluster.B),
        ];

        Assert.All(responses[..3], r => Assert.NotEqual(HttpStatusCode.TooManyRequests, r.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, responses[3].StatusCode);
        var retryAfter = int.Parse(responses[3].Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 300);
    }

    [Fact]
    public async Task Per_account_login_limits_are_shared_by_the_instances()
    {
        await using var cluster = Start(new() { ["RateLimits:LoginAccount:PermitLimit"] = "2" });
        await SetUpAsync(cluster.A);

        var first = await Forms.LoginAsync(cluster.A.CreateBrowser(), "alice", "wrong password 1");
        var second = await Forms.LoginAsync(cluster.B.CreateBrowser(), "ALICE", "wrong password 2");
        // Even the right password is refused once the account's limit is used up, on either instance.
        var third = await Forms.LoginAsync(cluster.A.CreateBrowser(), "alice", Password);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Settings_changes_reach_other_instances_without_waiting_for_the_poll()
    {
        await using var cluster = Start(new() { ["Settings:ReloadInterval"] = "00:10:00" });
        var monitor = cluster.B.Services.GetRequiredService<IOptionsMonitor<TokenOptions>>();
        Assert.NotEqual(TimeSpan.FromMinutes(12), monitor.CurrentValue.AccessTokenLifetime);

        var (exitCode, _, error) = await cluster.A.RunCliAsync("settings", "set", "Tokens:AccessTokenLifetime", "00:12:00");
        Assert.True(exitCode == 0, error);

        await EventuallyAsync(() => Task.FromResult(monitor.CurrentValue.AccessTokenLifetime == TimeSpan.FromMinutes(12)));
    }

    [Fact]
    public async Task New_signing_keys_reach_other_instances_without_waiting_for_the_poll()
    {
        await using var cluster = Start(new() { ["SigningKeys:CheckInterval"] = "01:00:00" });
        var client = cluster.B.CreateClient();
        var before = await JwksKeyIdsAsync(client);

        string created;
        await using (var scope = cluster.A.Services.CreateAsyncScope())
        {
            created = (await scope.ServiceProvider.GetRequiredService<SigningKeyRotator>().RotateNowAsync("test", null, CancellationToken))!.Id;
        }

        Assert.DoesNotContain(created, before);
        await EventuallyAsync(async () => (await JwksKeyIdsAsync(client)).Contains(created));
    }

    [Fact]
    public async Task Concurrent_rotations_on_two_instances_create_one_key()
    {
        await using var cluster = Start();
        await using var scopeA = cluster.A.Services.CreateAsyncScope();
        await using var scopeB = cluster.B.Services.CreateAsyncScope();

        var results = await Task.WhenAll(
            scopeA.ServiceProvider.GetRequiredService<SigningKeyRotator>().RotateNowAsync("test", null, CancellationToken),
            scopeB.ServiceProvider.GetRequiredService<SigningKeyRotator>().RotateNowAsync("test", null, CancellationToken));

        Assert.Single(results, k => k is not null);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await scopeA.ServiceProvider.GetRequiredService<ValkyrieDbContext>().SigningKeys.CountAsync(k => k.ActivatesAt > now, CancellationToken));
    }

    [Fact]
    public async Task Instances_starting_together_create_one_signing_key()
    {
        var database = postgres.CreateConnectionString($"valkyrieid_{Guid.NewGuid():N}");
        var masterKey = Core.Security.MasterKey.Generate().ToBase64();
        var settings = redis.Settings();
        // Migrate first, so the race is on the signing keys, not on creating the database.
        await using (var migrate = new ValkyrieAppFactory(DatabaseProvider.PostgreSql, database, masterKey) { Settings = settings })
        {
            await using var scope = migrate.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            await db.SigningKeys.ExecuteDeleteAsync(CancellationToken);
        }

        await using var a = new ValkyrieAppFactory(DatabaseProvider.PostgreSql, database, masterKey) { Settings = settings };
        await using var b = new ValkyrieAppFactory(DatabaseProvider.PostgreSql, database, masterKey) { Settings = settings };
        await Task.WhenAll(Task.Run(() => a.Services, CancellationToken), Task.Run(() => b.Services, CancellationToken));

        await using var check = a.Services.CreateAsyncScope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<ValkyrieDbContext>().SigningKeys.CountAsync(CancellationToken));
    }

    // Helpers

    protected sealed class Cluster(ValkyrieAppFactory a, ValkyrieAppFactory b) : IAsyncDisposable
    {
        public ValkyrieAppFactory A { get; } = a;

        public ValkyrieAppFactory B { get; } = b;

        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
        }
    }

    protected Cluster Start(Dictionary<string, string?>? settings = null, string? keyPrefix = null)
    {
        var database = postgres.CreateConnectionString($"valkyrieid_{Guid.NewGuid():N}");
        var masterKey = Core.Security.MasterKey.Generate().ToBase64();
        var all = new Dictionary<string, string?>(redis.Settings(keyPrefix).Concat(settings ?? []));
        var a = new ValkyrieAppFactory(DatabaseProvider.PostgreSql, database, masterKey) { Settings = all };
        // The first instance creates the database; the second starts against it.
        _ = a.Services;
        var b = new ValkyrieAppFactory(DatabaseProvider.PostgreSql, database, masterKey) { Settings = all };
        _ = b.Services;
        return new Cluster(a, b);
    }

    /// <summary>Creates the user alice and the confidential client <c>web</c> with refresh tokens; returns its secret.</summary>
    protected static async Task<string> SetUpAsync(ValkyrieAppFactory factory)
    {
        var user = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", "alice");
        Assert.True(user.ExitCode == 0, user.Error);
        var (exitCode, output, error) = await factory.RunCliAsync(
            "client", "create", "web", "--redirect-uri", RedirectUri, "--grant", "authorization_code", "--grant", "refresh_token",
            "--scope", "openid", "--scope", "offline_access");
        Assert.True(exitCode == 0, error);
        return output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("client_secret: ", StringComparison.Ordinal))["client_secret: ".Length..];
    }

    private static string AuthorizeUrl(string challenge, string? nonce = null) =>
        "/connect/authorize" + QueryString.Create(new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "web",
            ["scope"] = "openid offline_access",
            ["redirect_uri"] = RedirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["nonce"] = nonce,
        }.Where(p => p.Value is not null));

    protected static async Task<string> AuthorizeAsync(HttpClient browser, string challenge, string? nonce = null)
    {
        var response = await browser.GetAsync(AuthorizeUrl(challenge, nonce), CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.True(query.ContainsKey("code"), response.Headers.Location.ToString());
        return query["code"].ToString();
    }

    protected static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    protected static HttpRequestMessage CodeRequest(string code, string verifier, string secret) =>
        TokenRequest(Basic(secret), ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", RedirectUri), ("code_verifier", verifier));

    private static HttpRequestMessage TokenRequest(AuthenticationHeaderValue? authorization, params (string Key, string Value)[] values)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))),
        };
        request.Headers.Authorization = authorization;
        return request;
    }

    private static Task<HttpResponseMessage> PostTokenAsync(ValkyrieAppFactory factory) =>
        factory.CreateClient().SendAsync(TokenRequest(null, ("grant_type", "unknown")), CancellationToken);

    private static AuthenticationHeaderValue Basic(string secret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"web:{Uri.EscapeDataString(secret)}")));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(error, (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    private static async Task<List<string>> JwksKeyIdsAsync(HttpClient client)
    {
        var jwks = await ReadJsonAsync(await client.GetAsync("/.well-known/jwks.json", CancellationToken));
        return [.. jwks.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()!)];
    }

    /// <summary>Waits up to 10 s; far less than the poll intervals the tests configure, so only a notification explains success.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The other instance didn't pick up the change.");
            await Task.Delay(100, CancellationToken);
        }
    }
}
