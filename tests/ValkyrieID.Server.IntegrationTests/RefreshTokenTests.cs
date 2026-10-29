using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Refresh tokens, revocation, introspection, and reverse proxy support, run against every database provider.</summary>
public abstract class RefreshTokenTests
{
    private const string Password = "correct horse battery staple";
    private const string RedirectUri = "https://app.example.test/callback";
    private const string SpaOrigin = "https://spa.example.test";

    protected abstract ValkyrieAppFactory CreateFactory(
        TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null, IPAddress? remoteIpAddress = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Discovery

    [Fact]
    public async Task Discovery_advertises_refresh_tokens_revocation_and_introspection()
    {
        await using var factory = CreateFactory();

        var document = await ReadJsonAsync(await factory.CreateClient().GetAsync("/.well-known/openid-configuration", CancellationToken));

        Assert.Contains("refresh_token", Strings(document.GetProperty("grant_types_supported")));
        Assert.Contains("offline_access", Strings(document.GetProperty("scopes_supported")));
        Assert.Equal($"{ValkyrieAppFactory.DefaultIssuer}/connect/revocation", document.GetProperty("revocation_endpoint").GetString());
        Assert.Equal(["client_secret_basic", "client_secret_post", "none"], Strings(document.GetProperty("revocation_endpoint_auth_methods_supported")));
        Assert.Equal($"{ValkyrieAppFactory.DefaultIssuer}/connect/introspect", document.GetProperty("introspection_endpoint").GetString());
        Assert.Equal(["client_secret_basic", "client_secret_post"], Strings(document.GetProperty("introspection_endpoint_auth_methods_supported")));
    }

    // Issuance and rotation

    [Fact]
    public async Task Offline_access_issues_a_refresh_token_that_rotates_on_use()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var first = await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret), nonce: "n-1");
        Assert.Equal("openid offline_access", first.GetProperty("scope").GetString());
        var refreshToken = first.GetProperty("refresh_token").GetString()!;
        var originalIdToken = new JsonWebToken(first.GetProperty("id_token").GetString()!);

        var response = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret)), CancellationToken);
        var second = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("Bearer", second.GetProperty("token_type").GetString());
        Assert.Equal("openid offline_access", second.GetProperty("scope").GetString());
        var rotated = second.GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(refreshToken, rotated);

        // OIDC Core §12.2: same iss, sub, aud, auth_time; no nonce.
        var idToken = new JsonWebToken(second.GetProperty("id_token").GetString()!);
        Assert.Equal(originalIdToken.Subject, idToken.Subject);
        Assert.Equal(originalIdToken.Issuer, idToken.Issuer);
        Assert.Equal(["web"], idToken.Audiences);
        Assert.Equal(originalIdToken.GetPayloadValue<long>("auth_time"), idToken.GetPayloadValue<long>("auth_time"));
        Assert.Equal(originalIdToken.GetClaim("sid").Value, idToken.GetClaim("sid").Value);
        Assert.False(idToken.TryGetClaim("nonce", out _));
        Assert.Equal(AtHash(second.GetProperty("access_token").GetString()!), idToken.GetClaim("at_hash").Value);

        // The rotated token works in turn.
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(RefreshRequest(rotated, Basic("web", secret)), CancellationToken)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.Equal(2, await db.AuditLog.CountAsync(e => e.EventType == "token.refreshed", CancellationToken));
        var details = await db.AuditLog.Select(e => e.Details).ToListAsync(CancellationToken);
        Assert.DoesNotContain(details, d => d != null && (d.Contains(refreshToken, StringComparison.Ordinal) || d.Contains(rotated, StringComparison.Ordinal)));
        var hashes = await db.RefreshTokens.Select(t => t.Hash).ToListAsync(CancellationToken);
        Assert.Equal(3, hashes.Count);
        Assert.DoesNotContain(hashes, h => Encoding.UTF8.GetString(h).Contains(refreshToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Offline_access_is_ignored_unless_the_client_is_allowed_refresh_tokens()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var noGrant = await CreateClientAsync(factory, "no-grant", grants: ["authorization_code"]);
        var noScope = await CreateClientAsync(factory, "no-scope", scopes: ["openid"]);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var withoutGrant = await RedeemAsync(browser, "no-grant", "openid offline_access", Basic("no-grant", noGrant));
        var withoutScope = await RedeemAsync(browser, "no-scope", "openid offline_access", Basic("no-scope", noScope));

        Assert.Equal("openid", withoutGrant.GetProperty("scope").GetString());
        Assert.False(withoutGrant.TryGetProperty("refresh_token", out _));
        Assert.Equal("openid", withoutScope.GetProperty("scope").GetString());
        Assert.False(withoutScope.TryGetProperty("refresh_token", out _));
    }

    [Fact]
    public async Task No_refresh_token_without_offline_access()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var body = await RedeemAsync(browser, "web", "openid", Basic("web", secret));

        Assert.False(body.TryGetProperty("refresh_token", out _));
    }

    [Fact]
    public async Task Public_clients_get_rotating_refresh_tokens()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var first = await RedeemAsync(browser, "spa", "openid offline_access", null, clientId: "spa");
        var refreshToken = first.GetProperty("refresh_token").GetString()!;
        var response = await browser.SendAsync(RefreshRequest(refreshToken, clientId: "spa"), CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(refreshToken, (await ReadJsonAsync(response)).GetProperty("refresh_token").GetString());
    }

    [Fact]
    public async Task Refresh_scope_can_narrow_but_not_widen()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await RunCliOkAsync(factory, "resource", "create", "orders-api", "--scope", "orders.read", "--scope", "orders.write");
        var secret = await CreateClientAsync(factory, "web", scopes: ["openid", "offline_access", "orders.read", "orders.write"]);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var first = await RedeemAsync(browser, "web", "openid offline_access orders.read", Basic("web", secret));
        var refreshToken = first.GetProperty("refresh_token").GetString()!;

        var wider = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret), scope: "openid orders.write"), CancellationToken);
        await AssertErrorAsync(wider, HttpStatusCode.BadRequest, "invalid_scope");

        var narrower = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret), scope: "orders.read"), CancellationToken);
        var body = await ReadJsonAsync(narrower);
        Assert.Equal(HttpStatusCode.OK, narrower.StatusCode);
        Assert.Equal("orders.read", body.GetProperty("scope").GetString());
        Assert.False(body.TryGetProperty("id_token", out _));
        Assert.Equal(["orders-api"], new JsonWebToken(body.GetProperty("access_token").GetString()!).Audiences);

        // RFC 6749 §6: the new refresh token keeps the original scope.
        var full = await ReadJsonAsync(await browser.SendAsync(RefreshRequest(body.GetProperty("refresh_token").GetString()!, Basic("web", secret)), CancellationToken));
        Assert.Equal("openid offline_access orders.read", full.GetProperty("scope").GetString());
    }

    // Attacks

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_whole_family()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var original = (await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret))).GetProperty("refresh_token").GetString()!;
        var rotated = (await ReadJsonAsync(await browser.SendAsync(RefreshRequest(original, Basic("web", secret)), CancellationToken)))
            .GetProperty("refresh_token").GetString()!;

        var reuse = await browser.SendAsync(RefreshRequest(original, Basic("web", secret)), CancellationToken);
        var afterReuse = await browser.SendAsync(RefreshRequest(rotated, Basic("web", secret)), CancellationToken);

        await AssertErrorAsync(reuse, HttpStatusCode.BadRequest, "invalid_grant");
        await AssertErrorAsync(afterReuse, HttpStatusCode.BadRequest, "invalid_grant");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "token.refresh_reuse_detected", CancellationToken));
        Assert.All(await db.RefreshTokens.ToListAsync(CancellationToken), t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Refresh_tokens_are_bound_to_their_client()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var web = await CreateClientAsync(factory, "web");
        var other = await CreateClientAsync(factory, "other");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var refreshToken = (await RedeemAsync(browser, "web", "openid offline_access", Basic("web", web))).GetProperty("refresh_token").GetString()!;

        var stolen = await browser.SendAsync(RefreshRequest(refreshToken, Basic("other", other)), CancellationToken);
        var wrongSecret = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", other)), CancellationToken);
        var garbage = await browser.SendAsync(RefreshRequest("not-a-token", Basic("web", web)), CancellationToken);
        var legitimate = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", web)), CancellationToken);

        await AssertErrorAsync(stolen, HttpStatusCode.BadRequest, "invalid_grant");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        await AssertErrorAsync(garbage, HttpStatusCode.BadRequest, "invalid_grant");
        Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);
    }

    [Fact]
    public async Task Clients_without_the_refresh_token_grant_cannot_refresh()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var refreshToken = (await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret))).GetProperty("refresh_token").GetString()!;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var client = await db.Clients.SingleAsync(CancellationToken);
            client.AllowedGrantTypes = ["authorization_code"];
            await db.SaveChangesAsync(CancellationToken);
        }

        var response = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret)), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "unauthorized_client");
    }

    [Fact]
    public async Task Replaying_an_authorization_code_revokes_its_refresh_tokens()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = await AuthorizeAsync(browser, "web", "openid offline_access", challenge);
        var refreshToken = (await ReadJsonAsync(await browser.SendAsync(CodeRequest(code, verifier, Basic("web", secret)), CancellationToken)))
            .GetProperty("refresh_token").GetString()!;

        var replay = await browser.SendAsync(CodeRequest(code, verifier, Basic("web", secret)), CancellationToken);
        var refresh = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret)), CancellationToken);

        await AssertErrorAsync(replay, HttpStatusCode.BadRequest, "invalid_grant");
        await AssertErrorAsync(refresh, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Theory]
    [InlineData(3599, true)]
    [InlineData(3600, false)]
    public async Task Refresh_tokens_expire_after_the_sliding_lifetime(int seconds, bool valid)
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock, new() { ["Tokens:RefreshTokenSlidingLifetime"] = "01:00:00" });
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var refreshToken = (await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret))).GetProperty("refresh_token").GetString()!;

        clock.Advance(TimeSpan.FromSeconds(seconds));
        var response = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret)), CancellationToken);

        if (valid)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_grant");
        }
    }

    [Fact]
    public async Task Rotation_never_extends_a_family_beyond_its_absolute_lifetime()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock, new()
        {
            ["Tokens:RefreshTokenSlidingLifetime"] = "01:00:00",
            ["Tokens:RefreshTokenAbsoluteLifetime"] = "01:30:00",
        });
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var refreshToken = (await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret))).GetProperty("refresh_token").GetString()!;

        clock.Advance(TimeSpan.FromMinutes(50));
        var rotated = (await ReadJsonAsync(await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret)), CancellationToken)))
            .GetProperty("refresh_token").GetString()!;
        clock.Advance(TimeSpan.FromMinutes(40) - TimeSpan.FromSeconds(1));
        var beforeLimit = await ReadJsonAsync(await browser.SendAsync(RefreshRequest(rotated, Basic("web", secret)), CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(1));
        var atLimit = await browser.SendAsync(RefreshRequest(beforeLimit.GetProperty("refresh_token").GetString()!, Basic("web", secret)), CancellationToken);

        await AssertErrorAsync(atLimit, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Fact]
    public async Task Per_client_refresh_token_lifetimes_override_the_defaults()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web", options: ["--refresh-token-lifetime", "60"]);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var refreshToken = (await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret))).GetProperty("refresh_token").GetString()!;

        clock.Advance(TimeSpan.FromSeconds(60));
        var response = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret)), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_grant");
    }

    // Revocation triggers

    [Fact]
    public async Task Logout_keeps_offline_refresh_tokens()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var refreshToken = (await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret))).GetProperty("refresh_token").GetString()!;

        // OIDC Back-Channel Logout §2.7: offline_access refresh tokens normally SHOULD NOT be revoked at logout.
        await Forms.PostAsync(browser, "/account/logout", []);
        var response = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret)), CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("user-disable")]
    [InlineData("client-disable")]
    public async Task Refresh_tokens_are_revoked_on_password_change_and_disable(string trigger)
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var refreshToken = (await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret))).GetProperty("refresh_token").GetString()!;

        switch (trigger)
        {
            case "password":
                var result = await factory.RunCliWithInputAsync("another correct horse battery\n", "admin", "reset-password", "alice");
                Assert.True(result.ExitCode == 0, result.Error);
                break;
            case "user-disable":
                await RunCliOkAsync(factory, "user", "disable", "alice");
                await RunCliOkAsync(factory, "user", "enable", "alice");
                break;
            default:
                await RunCliOkAsync(factory, "client", "disable", "web");
                await RunCliOkAsync(factory, "client", "enable", "web");
                break;
        }

        var response = await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", secret)), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Fact]
    public async Task Deleting_a_client_deletes_its_refresh_tokens()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret));

        await RunCliOkAsync(factory, "client", "delete", "web");

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().RefreshTokens.ToListAsync(CancellationToken));
    }

    // Revocation endpoint

    [Fact]
    public async Task Revoking_a_refresh_token_revokes_its_family()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var original = (await RedeemAsync(browser, "web", "openid offline_access", Basic("web", secret))).GetProperty("refresh_token").GetString()!;
        var rotated = (await ReadJsonAsync(await browser.SendAsync(RefreshRequest(original, Basic("web", secret)), CancellationToken)))
            .GetProperty("refresh_token").GetString()!;

        // Revoking the already-used token still ends the family.
        var revoke = await browser.SendAsync(RevocationRequest(original, Basic("web", secret), hint: "refresh_token"), CancellationToken);
        var refresh = await browser.SendAsync(RefreshRequest(rotated, Basic("web", secret)), CancellationToken);
        var again = await browser.SendAsync(RevocationRequest(rotated, Basic("web", secret)), CancellationToken);

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal("no-store", revoke.Headers.CacheControl?.ToString());
        await AssertErrorAsync(refresh, HttpStatusCode.BadRequest, "invalid_grant");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog.AnyAsync(e => e.EventType == "token.revoked", CancellationToken));
    }

    [Fact]
    public async Task Revocation_validates_the_client_and_token_ownership()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var web = await CreateClientAsync(factory, "web");
        var other = await CreateClientAsync(factory, "other");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var body = await RedeemAsync(browser, "web", "openid offline_access", Basic("web", web));
        var refreshToken = body.GetProperty("refresh_token").GetString()!;
        var accessToken = body.GetProperty("access_token").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(RevocationRequest(refreshToken, null), CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(RevocationRequest(refreshToken, Basic("web", other)), CancellationToken)).StatusCode);

        // RFC 7009 §2.1: a token issued to another client is refused, and stays valid.
        await AssertErrorAsync(await browser.SendAsync(RevocationRequest(refreshToken, Basic("other", other)), CancellationToken), HttpStatusCode.BadRequest, "unauthorized_client");
        await AssertErrorAsync(await browser.SendAsync(RevocationRequest(refreshToken, clientId: "spa"), CancellationToken), HttpStatusCode.BadRequest, "unauthorized_client");

        // RFC 7009 §2.2: unknown tokens are not an error; §2.2.1: access tokens can't be revoked.
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(RevocationRequest("unknown", Basic("web", web)), CancellationToken)).StatusCode);
        await AssertErrorAsync(await browser.SendAsync(RevocationRequest(accessToken, Basic("web", web), hint: "access_token"), CancellationToken), HttpStatusCode.BadRequest, "unsupported_token_type");
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(RevocationRequest(accessToken, Basic("other", other)), CancellationToken)).StatusCode);

        var missing = new HttpRequestMessage(HttpMethod.Post, "/connect/revocation") { Content = Form() };
        missing.Headers.Authorization = Basic("web", web);
        await AssertErrorAsync(await browser.SendAsync(missing, CancellationToken), HttpStatusCode.BadRequest, "invalid_request");

        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", web)), CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Public_clients_can_revoke_from_a_registered_origin()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa", corsOrigin: SpaOrigin);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var refreshToken = (await RedeemAsync(browser, "spa", "openid offline_access", null, clientId: "spa")).GetProperty("refresh_token").GetString()!;

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/connect/revocation");
        preflight.Headers.Add("Origin", SpaOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var preflightResponse = await browser.SendAsync(preflight, CancellationToken);
        var revoke = RevocationRequest(refreshToken, clientId: "spa");
        revoke.Headers.Add("Origin", SpaOrigin);
        var revokeResponse = await browser.SendAsync(revoke, CancellationToken);

        Assert.Equal(SpaOrigin, preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
        Assert.Equal(SpaOrigin, revokeResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        await AssertErrorAsync(await browser.SendAsync(RefreshRequest(refreshToken, clientId: "spa"), CancellationToken), HttpStatusCode.BadRequest, "invalid_grant");
    }

    // Introspection

    [Fact]
    public async Task Clients_introspect_their_own_refresh_and_access_tokens()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var web = await CreateClientAsync(factory, "web");
        var other = await CreateClientAsync(factory, "other");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var body = await RedeemAsync(browser, "web", "openid offline_access", Basic("web", web));
        var refreshToken = body.GetProperty("refresh_token").GetString()!;
        var accessToken = body.GetProperty("access_token").GetString()!;
        var userId = await UserIdAsync(factory, "alice");

        var response = await browser.SendAsync(IntrospectionRequest(refreshToken, Basic("web", web)), CancellationToken);
        var refresh = await ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(refresh.GetProperty("active").GetBoolean());
        Assert.Equal("web", refresh.GetProperty("client_id").GetString());
        Assert.Equal(userId.ToString(), refresh.GetProperty("sub").GetString());
        Assert.Equal("openid offline_access", refresh.GetProperty("scope").GetString());

        var access = await ReadJsonAsync(await browser.SendAsync(IntrospectionRequest(accessToken, Basic("web", web), hint: "access_token"), CancellationToken));
        Assert.True(access.GetProperty("active").GetBoolean());
        Assert.Equal("Bearer", access.GetProperty("token_type").GetString());
        Assert.Equal(ValkyrieAppFactory.DefaultIssuer, access.GetProperty("iss").GetString());

        // Another client learns nothing about these tokens.
        AssertInactive(await ReadJsonAsync(await browser.SendAsync(IntrospectionRequest(refreshToken, Basic("other", other)), CancellationToken)));
        AssertInactive(await ReadJsonAsync(await browser.SendAsync(IntrospectionRequest(accessToken, Basic("other", other)), CancellationToken)));
        AssertInactive(await ReadJsonAsync(await browser.SendAsync(IntrospectionRequest("garbage", Basic("web", web)), CancellationToken)));

        // A rotated refresh token becomes inactive immediately.
        await browser.SendAsync(RefreshRequest(refreshToken, Basic("web", web)), CancellationToken);
        AssertInactive(await ReadJsonAsync(await browser.SendAsync(IntrospectionRequest(refreshToken, Basic("web", web)), CancellationToken)));
    }

    [Fact]
    public async Task Api_resources_introspect_access_tokens_issued_for_them()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await RunCliOkAsync(factory, "resource", "create", "orders-api", "--scope", "orders.read");
        await RunCliOkAsync(factory, "resource", "create", "billing-api", "--scope", "billing.read");
        var orders = await CreateResourceSecretAsync(factory, "orders-api");
        var billing = await CreateResourceSecretAsync(factory, "billing-api");
        var (_, output, _) = await factory.RunCliAsync("client", "create", "worker", "--scope", "orders.read");
        var workerSecret = SecretFrom(output, "client_secret: ");
        var client = factory.CreateClient();
        var accessToken = (await ReadJsonAsync(await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("worker", workerSecret)), CancellationToken)))
            .GetProperty("access_token").GetString()!;

        var active = await ReadJsonAsync(await client.SendAsync(IntrospectionRequest(accessToken, Basic("orders-api", orders)), CancellationToken));
        Assert.True(active.GetProperty("active").GetBoolean());
        Assert.Equal("orders.read", active.GetProperty("scope").GetString());
        Assert.Equal("worker", active.GetProperty("client_id").GetString());
        Assert.Equal(["orders-api"], Strings(active.GetProperty("aud")));

        AssertInactive(await ReadJsonAsync(await client.SendAsync(IntrospectionRequest(accessToken, Basic("billing-api", billing)), CancellationToken)));

        // Client secret post is refused until enabled for the resource.
        HttpRequestMessage PostRequest() => new(HttpMethod.Post, "/connect/introspect")
        {
            Content = Form(("token", accessToken), ("client_id", "orders-api"), ("client_secret", orders)),
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(PostRequest(), CancellationToken)).StatusCode);
        await RunCliOkAsync(factory, "resource", "enable-secret-post", "orders-api");
        Assert.True((await ReadJsonAsync(await client.SendAsync(PostRequest(), CancellationToken))).GetProperty("active").GetBoolean());

        // Within the clock skew, then expired.
        clock.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(29));
        Assert.True((await ReadJsonAsync(await client.SendAsync(IntrospectionRequest(accessToken, Basic("orders-api", orders)), CancellationToken))).GetProperty("active").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(1));
        AssertInactive(await ReadJsonAsync(await client.SendAsync(IntrospectionRequest(accessToken, Basic("orders-api", orders)), CancellationToken)));
    }

    [Fact]
    public async Task Introspection_requires_authentication()
    {
        await using var factory = CreateFactory();
        await RunCliOkAsync(factory, "resource", "create", "orders-api", "--scope", "orders.read");
        var orders = await CreateResourceSecretAsync(factory, "orders-api");
        await CreatePublicClientAsync(factory, "spa");
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(IntrospectionRequest("x", null), CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(IntrospectionRequest("x", Basic("orders-api", orders + "x")), CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(IntrospectionRequest("x", Basic("unknown", orders)), CancellationToken)).StatusCode);
        var publicClient = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect") { Content = Form(("token", "x"), ("client_id", "spa")) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(publicClient, CancellationToken)).StatusCode);

        await RunCliOkAsync(factory, "resource", "disable", "orders-api");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(IntrospectionRequest("x", Basic("orders-api", orders)), CancellationToken)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .AnyAsync(e => e.EventType == "introspection.authentication_failed", CancellationToken));
    }

    [Fact]
    public async Task Cli_manages_resource_secrets()
    {
        await using var factory = CreateFactory();
        await RunCliOkAsync(factory, "resource", "create", "orders-api", "--scope", "orders.read");

        var (_, output, _) = await factory.RunCliAsync("resource", "add-secret", "orders-api", "--description", "gateway");
        var secretId = SecretFrom(output, "secret_id: ");
        var (_, list, _) = await factory.RunCliAsync("resource", "list");
        await RunCliOkAsync(factory, "resource", "remove-secret", "orders-api", secretId);

        Assert.Contains($"secret {secretId}  active", list, StringComparison.Ordinal);
        Assert.NotEqual(0, (await factory.RunCliAsync("resource", "remove-secret", "orders-api", secretId)).ExitCode);
    }

    [Theory]
    [InlineData("--grant", "refresh_token")]
    [InlineData("--grant", "client_credentials", "--grant", "refresh_token")]
    [InlineData("--grant", "authorization_code", "--redirect-uri", RedirectUri, "--refresh-token-lifetime", "60")]
    [InlineData("--grant", "authorization_code", "--grant", "refresh_token", "--redirect-uri", RedirectUri, "--refresh-token-lifetime", "60", "--refresh-token-absolute-lifetime", "30")]
    public async Task Cli_rejects_invalid_refresh_token_configurations(params string[] options)
    {
        await using var factory = CreateFactory();

        var (exitCode, _, _) = await factory.RunCliAsync(["client", "create", "bad", "--scope", "openid", .. options]);

        Assert.NotEqual(0, exitCode);
    }

    // Reverse proxy

    [Fact]
    public async Task Forwarded_headers_are_applied_only_from_trusted_proxies()
    {
        var settings = new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = "10.0.0.5" };
        await using var trusted = CreateFactory(settings: settings, remoteIpAddress: IPAddress.Parse("10.0.0.5"));
        await using var untrusted = CreateFactory(settings: settings, remoteIpAddress: IPAddress.Parse("10.0.0.6"));

        Assert.Equal("203.0.113.7", await FailedLoginIpAsync(trusted));
        Assert.Equal("10.0.0.6", await FailedLoginIpAsync(untrusted));
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_without_configured_proxies()
    {
        await using var factory = CreateFactory(remoteIpAddress: IPAddress.Loopback);

        Assert.Equal("127.0.0.1", await FailedLoginIpAsync(factory));
    }

    [Fact]
    public async Task Path_base_serves_endpoints_under_the_issuer_path()
    {
        await using var factory = CreateFactory(settings: new()
        {
            ["Issuer:Url"] = "https://id.example.test/auth",
            ["ReverseProxy:PathBase"] = "/auth",
        });
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();

        var document = await ReadJsonAsync(await browser.GetAsync("/auth/.well-known/openid-configuration", CancellationToken));
        Assert.Equal("https://id.example.test/auth", document.GetProperty("issuer").GetString());
        Assert.Equal("https://id.example.test/auth/connect/revocation", document.GetProperty("revocation_endpoint").GetString());

        var (_, challenge) = NewPkce();
        var toLogin = await browser.GetAsync("/auth" + AuthorizeUrl("spa", "openid", challenge), CancellationToken);
        Assert.StartsWith("/auth/account/login?", toLogin.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("/auth/connect/authorize?"), toLogin.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auth", "https://id.example.test/auth")]
    [InlineData("/auth/", "https://id.example.test/auth")]
    [InlineData("/auth", "https://id.example.test/other")]
    public async Task Startup_fails_with_an_invalid_path_base(string pathBase, string issuer)
    {
        await using var factory = CreateFactory(settings: new() { ["Issuer:Url"] = issuer, ["ReverseProxy:PathBase"] = pathBase });

        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains("ReverseProxy:PathBase", exception.ToString(), StringComparison.Ordinal);
    }

    // Helpers

    private static async Task<string?> FailedLoginIpAsync(ValkyrieAppFactory factory)
    {
        var request = TokenRequest(Form(("grant_type", "client_credentials"), ("client_id", "nobody"), ("client_secret", "x")));
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().SendAsync(request, CancellationToken)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .Where(e => e.EventType == "client.authentication_failed").Select(e => e.IpAddress).SingleAsync(CancellationToken);
    }

    private static void AssertInactive(JsonElement response)
    {
        Assert.False(response.GetProperty("active").GetBoolean());
        Assert.Equal(["active"], response.EnumerateObject().Select(p => p.Name));
    }

    private static async Task<JsonElement> RedeemAsync(
        HttpClient browser, string client, string scope, AuthenticationHeaderValue? authorization, string? nonce = null, string? clientId = null)
    {
        var (verifier, challenge) = NewPkce();
        var code = await AuthorizeAsync(browser, client, scope, challenge, nonce);
        var response = await browser.SendAsync(CodeRequest(code, verifier, authorization, clientId), CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await ReadJsonAsync(response);
    }

    private static string AuthorizeUrl(string clientId, string scope, string challenge, string? nonce = null)
    {
        List<KeyValuePair<string, string>> pairs =
        [
            new("response_type", "code"), new("client_id", clientId), new("scope", scope), new("redirect_uri", RedirectUri),
            new("code_challenge", challenge), new("code_challenge_method", "S256"),
        ];
        if (nonce is not null)
        {
            pairs.Add(new("nonce", nonce));
        }

        return "/connect/authorize?" + string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    private static async Task<string> AuthorizeAsync(HttpClient browser, string clientId, string scope, string challenge, string? nonce = null)
    {
        var response = await browser.GetAsync(AuthorizeUrl(clientId, scope, challenge, nonce), CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(CancellationToken));
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.True(query.ContainsKey("code"), response.Headers.Location.ToString());
        return query["code"].ToString();
    }

    private static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string AtHash(string accessToken) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)).AsSpan(0, 16));

    private static HttpRequestMessage CodeRequest(string code, string verifier, AuthenticationHeaderValue? authorization, string? clientId = null)
    {
        List<(string, string)> values = [("grant_type", "authorization_code"), ("code", code), ("redirect_uri", RedirectUri), ("code_verifier", verifier)];
        if (clientId is not null)
        {
            values.Add(("client_id", clientId));
        }

        return TokenRequest(Form([.. values]), authorization);
    }

    private static HttpRequestMessage RefreshRequest(string refreshToken, AuthenticationHeaderValue? authorization = null, string? clientId = null, string? scope = null)
    {
        List<(string, string)> values = [("grant_type", "refresh_token"), ("refresh_token", refreshToken)];
        if (clientId is not null)
        {
            values.Add(("client_id", clientId));
        }

        if (scope is not null)
        {
            values.Add(("scope", scope));
        }

        return TokenRequest(Form([.. values]), authorization);
    }

    private static HttpRequestMessage RevocationRequest(string token, AuthenticationHeaderValue? authorization = null, string? clientId = null, string? hint = null) =>
        EndpointRequest("/connect/revocation", token, authorization, clientId, hint);

    private static HttpRequestMessage IntrospectionRequest(string token, AuthenticationHeaderValue? authorization, string? hint = null) =>
        EndpointRequest("/connect/introspect", token, authorization, null, hint);

    private static HttpRequestMessage EndpointRequest(string path, string token, AuthenticationHeaderValue? authorization, string? clientId, string? hint)
    {
        List<(string, string)> values = [("token", token)];
        if (clientId is not null)
        {
            values.Add(("client_id", clientId));
        }

        if (hint is not null)
        {
            values.Add(("token_type_hint", hint));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Form([.. values]) };
        request.Headers.Authorization = authorization;
        return request;
    }

    private static HttpRequestMessage TokenRequest(FormUrlEncodedContent form, AuthenticationHeaderValue? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = authorization;
        return request;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)));

    private static AuthenticationHeaderValue Basic(string clientId, string secret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret)}")));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;

    private static string[] Strings(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(e => e.GetString()!)] : [value.GetString()!];

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(error, (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    private static async Task CreateUserAsync(ValkyrieAppFactory factory, string userName)
    {
        var result = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", userName);
        Assert.True(result.ExitCode == 0, result.Error);
    }

    private static async Task<Guid> UserIdAsync(ValkyrieAppFactory factory, string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users
            .Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync(CancellationToken);
    }

    private static async Task<string> CreateClientAsync(
        ValkyrieAppFactory factory, string clientId, string[]? scopes = null, string[]? grants = null, string[]? options = null)
    {
        var (exitCode, output, error) = await factory.RunCliAsync(
            ["client", "create", clientId, "--redirect-uri", RedirectUri,
                .. (grants ?? ["authorization_code", "refresh_token"]).SelectMany(g => new[] { "--grant", g }),
                .. (scopes ?? ["openid", "offline_access"]).SelectMany(s => new[] { "--scope", s }),
                .. options ?? []]);
        Assert.True(exitCode == 0, error);
        return SecretFrom(output, "client_secret: ");
    }

    private static Task CreatePublicClientAsync(ValkyrieAppFactory factory, string clientId, string? corsOrigin = null) =>
        RunCliOkAsync(factory,
        [
            "client", "create", clientId, "--public", "--grant", "authorization_code", "--grant", "refresh_token",
            "--redirect-uri", RedirectUri, "--scope", "openid", "--scope", "offline_access",
            .. corsOrigin is null ? Array.Empty<string>() : ["--cors-origin", corsOrigin],
        ]);

    private static async Task<string> CreateResourceSecretAsync(ValkyrieAppFactory factory, string name)
    {
        var (exitCode, output, error) = await factory.RunCliAsync("resource", "add-secret", name);
        Assert.True(exitCode == 0, error);
        return SecretFrom(output, "resource_secret: ");
    }

    private static string SecretFrom(string output, string prefix) =>
        output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    private static async Task RunCliOkAsync(ValkyrieAppFactory factory, params string[] args)
    {
        var (exitCode, _, error) = await factory.RunCliAsync(args);
        Assert.True(exitCode == 0, error);
    }
}
