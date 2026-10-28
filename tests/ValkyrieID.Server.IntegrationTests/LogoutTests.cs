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
using Microsoft.IdentityModel.Tokens;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Logout;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>RP-initiated logout, back-channel logout, and session revocation, run against every database provider.</summary>
public abstract class LogoutTests
{
    private const string Password = "correct horse battery staple";
    private const string RedirectUri = "https://app.example.test/callback";
    private const string PostLogoutUri = "https://app.example.test/signed-out";
    private const string BackChannelUri = "https://app.example.test/backchannel";
    private const string OtherBackChannelUri = "https://other.example.test/backchannel";

    protected abstract ValkyrieAppFactory CreateFactory(
        TimeProvider? timeProvider = null,
        Dictionary<string, string?>? settings = null,
        BackChannelReceiver? receiver = null,
        bool disableWorker = false);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Discovery

    [Fact]
    public async Task Discovery_advertises_rp_initiated_and_back_channel_logout()
    {
        await using var factory = CreateFactory();

        var document = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/.well-known/openid-configuration", CancellationToken)).RootElement;

        Assert.Equal($"{ValkyrieAppFactory.DefaultIssuer}/connect/endsession", document.GetProperty("end_session_endpoint").GetString());
        Assert.True(document.GetProperty("backchannel_logout_supported").GetBoolean());
        Assert.True(document.GetProperty("backchannel_logout_session_supported").GetBoolean());
        Assert.False(document.TryGetProperty("frontchannel_logout_supported", out _));
        Assert.False(document.TryGetProperty("check_session_iframe", out _));
    }

    // RP-initiated logout

    [Fact]
    public async Task A_hint_for_the_current_session_signs_out_notifies_clients_then_redirects()
    {
        var receiver = new BackChannelReceiver();
        await using var factory = CreateFactory(receiver: receiver);
        await CreateUserAsync(factory, "alice");
        var web = await CreateClientAsync(factory, "web", backChannelUri: BackChannelUri);
        var other = await CreateClientAsync(factory, "other", backChannelUri: OtherBackChannelUri);
        var silent = await CreateClientAsync(factory, "silent");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var idToken = await SignInToClientAsync(browser, "web", web);
        await SignInToClientAsync(browser, "other", other);
        await SignInToClientAsync(browser, "silent", silent);

        var response = await browser.GetAsync(EndSessionUrl(("id_token_hint", idToken), ("post_logout_redirect_uri", PostLogoutUri), ("state", "s t&1")), CancellationToken);

        // RP-Initiated Logout §3: the redirect happens after the clients were notified.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"{PostLogoutUri}?state=s%20t%261", response.Headers.Location!.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var requests = receiver.Requests.OrderBy(r => r.Uri.AbsoluteUri, StringComparer.Ordinal).ToList();
        Assert.Equal([BackChannelUri, OtherBackChannelUri], requests.Select(r => r.Uri.AbsoluteUri));
        Assert.All(requests, r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("application/x-www-form-urlencoded", r.ContentType);
        });

        var sid = new JsonWebToken(idToken).GetClaim("sid").Value;
        var userId = await UserIdAsync(factory, "alice");
        await AssertLogoutTokenAsync(factory, requests[0].LogoutToken!, "web", userId, sid);
        await AssertLogoutTokenAsync(factory, requests[1].LogoutToken!, "other", userId, sid);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.NotNull((await db.UserSessions.SingleAsync(CancellationToken)).RevokedAt);
        Assert.Empty(await db.BackChannelLogoutDeliveries.ToListAsync(CancellationToken));
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.logout", CancellationToken));
        Assert.Equal(2, await db.AuditLog.CountAsync(e => e.EventType == "backchannel.delivered" && e.Success, CancellationToken));
        var details = await db.AuditLog.Select(e => e.Details).ToListAsync(CancellationToken);
        Assert.DoesNotContain(details, d => d != null && requests.Any(r => d.Contains(r.LogoutToken!, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_a_hint_the_user_confirms_before_being_signed_out()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateClientAsync(factory, "web", name: "Web App");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var response = await browser.GetAsync(EndSessionUrl(("client_id", "web"), ("post_logout_redirect_uri", PostLogoutUri), ("state", "xyz")), CancellationToken);

        var page = AssertLogoutPage(response);
        Assert.True(await SessionActiveAsync(factory));
        var html = await (await browser.GetAsync(page, CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("<strong>Web App</strong> asks you to sign out.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid", html, StringComparison.Ordinal);

        var confirmed = await Forms.PostAsync(browser, page, []);

        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        Assert.Equal($"{PostLogoutUri}?state=xyz", confirmed.Headers.Location!.OriginalString);
        Assert.False(await SessionActiveAsync(factory));
    }

    [Fact]
    public async Task A_hint_for_an_earlier_session_requires_confirmation()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var oldHint = await SignInToClientAsync(browser, "web", secret);
        await Forms.PostAsync(browser, "/account/logout", []);
        await Forms.LoginAsync(browser, "alice", Password);

        var response = await browser.GetAsync(EndSessionUrl(("id_token_hint", oldHint), ("post_logout_redirect_uri", PostLogoutUri)), CancellationToken);

        // RP-Initiated Logout §2: the OP MUST ask if the ID token doesn't belong to the current session.
        var page = AssertLogoutPage(response);
        Assert.True(await SessionActiveAsync(factory));
        var confirmed = await Forms.PostAsync(browser, page, []);
        Assert.Equal(PostLogoutUri, confirmed.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Expired_hints_are_accepted()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var hint = await SignInToClientAsync(browser, "web", secret);

        clock.Advance(TimeSpan.FromHours(1));
        var response = await browser.GetAsync(EndSessionUrl(("id_token_hint", hint), ("post_logout_redirect_uri", PostLogoutUri)), CancellationToken);

        Assert.Equal(PostLogoutUri, response.Headers.Location!.OriginalString);
        Assert.False(await SessionActiveAsync(factory));
    }

    [Fact]
    public async Task A_user_who_is_not_signed_in_is_sent_straight_back()
    {
        await using var factory = CreateFactory();
        await CreateClientAsync(factory, "web");

        var response = await factory.CreateBrowser().GetAsync(
            EndSessionUrl(("client_id", "web"), ("post_logout_redirect_uri", PostLogoutUri), ("state", "abc"), ("ui_locales", "fr"), ("logout_hint", "alice")),
            CancellationToken);

        // RP-Initiated Logout §4: a logout request for a user who isn't signed in is not an error.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"{PostLogoutUri}?state=abc", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_post_logout_redirect_uri_keeps_its_query()
    {
        await using var factory = CreateFactory();
        await CreateClientAsync(factory, "web", postLogoutUri: "https://app.example.test/bye?from=idp");

        var response = await factory.CreateBrowser().GetAsync(
            EndSessionUrl(("client_id", "web"), ("post_logout_redirect_uri", "https://app.example.test/bye?from=idp"), ("state", "1")), CancellationToken);

        Assert.Equal("https://app.example.test/bye?from=idp&state=1", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Post_requests_are_repeated_as_a_get_so_the_session_cookie_is_sent()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateBrowser().PostAsync(
            "/connect/endsession",
            new FormUrlEncodedContent([new("client_id", "web"), new("state", "a b")]),
            CancellationToken);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/connect/endsession?client_id=web&state=a%20b", response.Headers.Location!.OriginalString);
    }

    // Attacks

    [Theory]
    [InlineData("https://evil.example.test/signed-out", "web")]
    [InlineData("https://app.example.test/signed-out/", "web")]
    [InlineData("http://127.0.0.1:6000/signed-out", "web")]
    [InlineData(PostLogoutUri, null)]
    [InlineData(PostLogoutUri, "unknown")]
    public async Task Unregistered_or_unidentified_post_logout_redirect_uris_are_never_used(string uri, string? clientId)
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateClientAsync(factory, "web", extraPostLogoutUri: "http://127.0.0.1:5000/signed-out");
        var browser = factory.CreateBrowser();

        // Not signed in: a valid request would be redirected straight away.
        var anonymous = await browser.GetAsync(EndSessionUrl(Parameters(uri, clientId)), CancellationToken);
        var anonymousPage = AssertLogoutPage(anonymous);
        Assert.Contains("sign-out request was invalid", await (await browser.GetAsync(anonymousPage, CancellationToken)).Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);

        await Forms.LoginAsync(browser, "alice", Password);
        var page = AssertLogoutPage(await browser.GetAsync(EndSessionUrl(Parameters(uri, clientId)), CancellationToken));
        var confirmed = await Forms.PostAsync(browser, page, []);

        // The user is signed out, but stays at ValkyrieID.
        Assert.StartsWith("/account/logout", confirmed.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.False(await SessionActiveAsync(factory));

        static (string, string)[] Parameters(string uri, string? clientId) =>
            clientId is null ? [("post_logout_redirect_uri", uri)] : [("client_id", clientId), ("post_logout_redirect_uri", uri)];
    }

    [Fact]
    public async Task Invalid_hints_are_not_trusted()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var web = await CreateClientAsync(factory, "web");
        await CreateClientAsync(factory, "other");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var idToken = await SignInToClientAsync(browser, "web", web);
        var accessToken = await AccessTokenAsync(browser, "web", web);
        var claims = new JsonWebToken(idToken);
        var key = await CurrentSigningKeyAsync(factory);
        var foreignKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = key.KeyId };

        List<(string Name, (string, string)[] Parameters)> cases =
        [
            ("forged signature", [("id_token_hint", Sign(foreignKey, ValkyrieAppFactory.DefaultIssuer, "JWT", claims))]),
            ("wrong issuer", [("id_token_hint", Sign(key, "https://evil.example.test", "JWT", claims))]),
            ("access token", [("id_token_hint", accessToken)]),
            ("logout token type", [("id_token_hint", Sign(key, ValkyrieAppFactory.DefaultIssuer, "logout+jwt", claims))]),
            ("garbage", [("id_token_hint", "not-a-jwt")]),
            ("client_id mismatch", [("id_token_hint", idToken), ("client_id", "other")]),
        ];

        foreach (var (name, parameters) in cases)
        {
            var response = await browser.GetAsync(EndSessionUrl([.. parameters, ("post_logout_redirect_uri", PostLogoutUri)]), CancellationToken);

            Assert.True(response.Headers.Location?.OriginalString.StartsWith("/account/logout?request=", StringComparison.Ordinal), name);
            Assert.True(await SessionActiveAsync(factory), name);
            var html = await (await browser.GetAsync(response.Headers.Location, CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
            Assert.Contains("sign-out request was invalid", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_tampered_logout_request_is_ignored()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var page = AssertLogoutPage(await browser.GetAsync(EndSessionUrl(("client_id", "web"), ("post_logout_redirect_uri", PostLogoutUri)), CancellationToken));

        var confirmed = await Forms.PostAsync(browser, page[..^4] + "AAAA", []);

        Assert.StartsWith("/account/logout", confirmed.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    // Back-channel delivery

    [Fact]
    public async Task Failed_deliveries_are_retried_with_backoff_and_a_fresh_token()
    {
        // A whole second, so stored timestamps compare equal on every provider.
        var clock = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var receiver = new BackChannelReceiver();
        var responses = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.NoContent]);
        receiver.Respond = _ => new HttpResponseMessage(responses.Dequeue());
        await using var factory = CreateFactory(clock, receiver: receiver, disableWorker: true);
        var (browser, secret) = await SignedInWithBackChannelClientAsync(factory);
        var hint = await SignInToClientAsync(browser, "web", secret);

        var response = await browser.GetAsync(EndSessionUrl(("id_token_hint", hint), ("post_logout_redirect_uri", PostLogoutUri)), CancellationToken);

        // The browser isn't held up by a failing client.
        Assert.Equal(PostLogoutUri, response.Headers.Location!.OriginalString);
        Assert.Single(receiver.Requests);
        var pending = await SingleDeliveryAsync(factory);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(10), pending.NextAttemptAt);
        Assert.Null(pending.LeaseOwner);

        var dispatcher = factory.Services.GetRequiredService<BackChannelLogoutDispatcher>();
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, await dispatcher.ProcessDueAsync(CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await dispatcher.ProcessDueAsync(CancellationToken));
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(30), (await SingleDeliveryAsync(factory)).NextAttemptAt);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await dispatcher.ProcessDueAsync(CancellationToken));

        Assert.Equal(3, receiver.Requests.Count);
        Assert.Equal(3, receiver.Requests.Select(r => new JsonWebToken(r.LogoutToken).Id).Distinct().Count());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.Empty(await db.BackChannelLogoutDeliveries.ToListAsync(CancellationToken));
        var delivered = await db.AuditLog.SingleAsync(e => e.EventType == "backchannel.delivered", CancellationToken);
        Assert.Contains("\"attempts\":3", delivered.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task Other_responses_are_final_and_not_retried(HttpStatusCode status)
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var receiver = new BackChannelReceiver();
        receiver.Respond = _ =>
        {
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.Found)
            {
                response.Headers.Location = new Uri("https://elsewhere.example.test/");
            }

            return response;
        };
        await using var factory = CreateFactory(clock, receiver: receiver, disableWorker: true);
        var (browser, _) = await SignedInWithBackChannelClientAsync(factory);

        await Forms.PostAsync(browser, "/account/logout", []);
        clock.Advance(TimeSpan.FromHours(2));
        await factory.Services.GetRequiredService<BackChannelLogoutDispatcher>().ProcessDueAsync(CancellationToken);

        Assert.Single(receiver.Requests);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.Empty(await db.BackChannelLogoutDeliveries.ToListAsync(CancellationToken));
        var failed = await db.AuditLog.SingleAsync(e => e.EventType == "backchannel.failed", CancellationToken);
        Assert.False(failed.Success);
        Assert.Contains($"\"result\":\"http_{(int)status}\"", failed.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delivery_gives_up_after_the_last_retry()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var receiver = new BackChannelReceiver { Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        await using var factory = CreateFactory(clock, new() { ["BackChannelLogout:RetryDelays:0"] = "00:00:05" }, receiver, disableWorker: true);
        var (browser, _) = await SignedInWithBackChannelClientAsync(factory);
        await Forms.PostAsync(browser, "/account/logout", []);
        var dispatcher = factory.Services.GetRequiredService<BackChannelLogoutDispatcher>();

        clock.Advance(TimeSpan.FromSeconds(5));
        await dispatcher.ProcessDueAsync(CancellationToken);
        clock.Advance(TimeSpan.FromHours(1));
        await dispatcher.ProcessDueAsync(CancellationToken);

        Assert.Equal(2, receiver.Requests.Count);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.Empty(await db.BackChannelLogoutDeliveries.ToListAsync(CancellationToken));
        var failed = await db.AuditLog.SingleAsync(e => e.EventType == "backchannel.failed", CancellationToken);
        Assert.Contains("\"attempts\":2", failed.Details, StringComparison.Ordinal);
        Assert.Contains("\"result\":\"http_502\"", failed.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_out_waits_at_most_the_request_timeout_and_retries_later()
    {
        var receiver = new BackChannelReceiver { Delay = TimeSpan.FromSeconds(30) };
        await using var factory = CreateFactory(settings: new() { ["BackChannelLogout:RequestTimeout"] = "00:00:01" }, receiver: receiver, disableWorker: true);
        var (browser, secret) = await SignedInWithBackChannelClientAsync(factory);
        var hint = await SignInToClientAsync(browser, "web", secret);
        var started = DateTimeOffset.UtcNow;

        var response = await browser.GetAsync(EndSessionUrl(("id_token_hint", hint), ("post_logout_redirect_uri", PostLogoutUri)), CancellationToken);

        Assert.Equal(PostLogoutUri, response.Headers.Location!.OriginalString);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
        var pending = await SingleDeliveryAsync(factory);
        Assert.Equal(1, pending.Attempts);
        Assert.Null(pending.LeaseOwner);
    }

    [Fact]
    public async Task Only_one_instance_claims_a_delivery()
    {
        var receiver = new BackChannelReceiver { Delay = TimeSpan.FromMilliseconds(200) };
        await using var factory = CreateFactory(receiver: receiver, disableWorker: true);
        await QueueDeliveryAsync(factory, leaseOwner: null, leaseExpiresAt: null);
        var first = factory.Services.GetRequiredService<BackChannelLogoutDispatcher>();
        var second = ActivatorUtilities.CreateInstance<BackChannelLogoutDispatcher>(factory.Services);
        Assert.NotEqual(first.InstanceId, second.InstanceId);

        var attempted = await Task.WhenAll(
            first.ProcessDueAsync(CancellationToken), second.ProcessDueAsync(CancellationToken),
            first.ProcessDueAsync(CancellationToken), second.ProcessDueAsync(CancellationToken));

        Assert.Equal(1, attempted.Sum());
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task An_expired_lease_is_taken_over_but_a_current_one_is_left_alone()
    {
        var receiver = new BackChannelReceiver();
        await using var factory = CreateFactory(receiver: receiver, disableWorker: true);
        var now = DateTimeOffset.UtcNow;
        var held = await QueueDeliveryAsync(factory, "other-instance", now + TimeSpan.FromMinutes(1));
        var dispatcher = factory.Services.GetRequiredService<BackChannelLogoutDispatcher>();

        Assert.Equal(0, await dispatcher.ProcessDueAsync(CancellationToken));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().BackChannelLogoutDeliveries
                .Where(d => d.Id == held)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LeaseExpiresAt, now - TimeSpan.FromSeconds(1)), CancellationToken);
        }

        Assert.Equal(1, await dispatcher.ProcessDueAsync(CancellationToken));
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task The_worker_delivers_notifications_queued_by_the_cli()
    {
        var receiver = new BackChannelReceiver();
        await using var factory = CreateFactory(settings: new() { ["BackChannelLogout:PollInterval"] = "00:00:01" }, receiver: receiver);
        await SignedInWithBackChannelClientAsync(factory);

        await RunCliOkAsync(factory, "session", "revoke", "--user", "alice");

        for (var i = 0; i < 100 && receiver.Requests.Count == 0; i++)
        {
            await Task.Delay(100, CancellationToken);
        }

        Assert.Single(receiver.Requests);
    }

    // Session revocation

    [Fact]
    public async Task Revoking_a_session_revokes_its_refresh_tokens_and_notifies_clients()
    {
        var receiver = new BackChannelReceiver();
        await using var factory = CreateFactory(receiver: receiver, disableWorker: true);
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web", backChannelUri: BackChannelUri);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var tokens = await RedeemAsync(browser, "web", secret, "openid offline_access");
        var sid = new JsonWebToken(tokens.GetProperty("id_token").GetString()!).GetClaim("sid").Value;

        var (exitCode, output, error) = await factory.RunCliAsync("session", "revoke", sid);

        Assert.True(exitCode == 0, error);
        Assert.Contains($"Revoked session '{sid}'.", output, StringComparison.Ordinal);
        var refresh = await browser.SendAsync(RefreshRequest(tokens.GetProperty("refresh_token").GetString()!, secret), CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
        var pending = await SingleDeliveryAsync(factory);
        Assert.Equal(sid, pending.SessionId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var revoked = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
                .SingleAsync(e => e.EventType == "session.revoked", CancellationToken);
            Assert.Equal("cli", revoked.Actor);
            Assert.Contains("\"reason\":\"revoked_by_admin\"", revoked.Details, StringComparison.Ordinal);
        }

        await factory.Services.GetRequiredService<BackChannelLogoutDispatcher>().ProcessDueAsync(CancellationToken);
        Assert.Equal(sid, new JsonWebToken(Assert.Single(receiver.Requests).LogoutToken).GetClaim("sid").Value);
    }

    [Fact]
    public async Task Sign_out_notifies_clients_but_keeps_offline_refresh_tokens()
    {
        var receiver = new BackChannelReceiver();
        await using var factory = CreateFactory(receiver: receiver);
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web", backChannelUri: BackChannelUri);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var tokens = await RedeemAsync(browser, "web", secret, "openid offline_access");

        await Forms.PostAsync(browser, "/account/logout", []);

        Assert.Single(receiver.Requests);
        var refresh = await browser.SendAsync(RefreshRequest(tokens.GetProperty("refresh_token").GetString()!, secret), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_notifies_the_clients_of_their_sessions()
    {
        await using var factory = CreateFactory(disableWorker: true);
        await SignedInWithBackChannelClientAsync(factory);

        await RunCliOkAsync(factory, "user", "disable", "alice");

        await SingleDeliveryAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var revoked = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .SingleAsync(e => e.EventType == "session.revoked", CancellationToken);
        Assert.Contains("\"reason\":\"user_disabled\"", revoked.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_commands_list_and_revoke_a_users_sessions()
    {
        await using var factory = CreateFactory(disableWorker: true);
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web");
        var laptop = factory.CreateBrowser();
        var phone = factory.CreateBrowser();
        await Forms.LoginAsync(laptop, "alice", Password);
        await Forms.LoginAsync(phone, "alice", Password);
        await SignInToClientAsync(laptop, "web", secret);
        await SignInToClientAsync(laptop, "web", secret);

        var (_, list, _) = await factory.RunCliAsync("session", "list", "--user", "alice");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            foreach (var session in await db.UserSessions.ToListAsync(CancellationToken))
            {
                Assert.Contains(session.Id, list, StringComparison.Ordinal);
            }

            // A client is recorded once per session.
            Assert.Equal(1, await db.UserSessionClients.CountAsync(CancellationToken));
        }

        Assert.Contains("clients: web", list, StringComparison.Ordinal);
        var (exitCode, revoke, error) = await factory.RunCliAsync("session", "revoke", "--user", "alice");
        Assert.True(exitCode == 0, error);
        Assert.Contains("Revoked 2 session(s)", revoke, StringComparison.Ordinal);
        Assert.Contains("has no active sessions", (await factory.RunCliAsync("session", "list", "--user", "alice")).Output, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await phone.GetAsync("/account", CancellationToken)).StatusCode);

        Assert.Contains("does not exist", (await factory.RunCliAsync("session", "revoke", "nope")).Error, StringComparison.Ordinal);
        Assert.Contains("--user is required", (await factory.RunCliAsync("session", "list")).Error, StringComparison.Ordinal);
    }

    // Client registration

    [Fact]
    public async Task Back_channel_logout_uris_are_validated()
    {
        await using var factory = CreateFactory();

        static string[] Create(string clientId, string uri, params string[] extra) =>
            ["client", "create", clientId, "--grant", "authorization_code", "--redirect-uri", RedirectUri, "--scope", "openid", "--backchannel-logout-uri", uri, .. extra];

        Assert.Equal(1, (await factory.RunCliAsync(Create("a", "http://app.example.test/bc"))).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync(Create("b", "https://app.example.test/bc#fragment"))).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync(Create("c", "com.example.app:/bc"))).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync(Create("d", "http://127.0.0.1:9000/bc", "--public"))).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("client", "create", "e", "--scope", "openid", "--backchannel-logout-uri", BackChannelUri)).ExitCode);
        await RunCliOkAsync(factory, Create("f", "http://127.0.0.1:9000/bc"));
        await RunCliOkAsync(factory, Create("g", "https://app.example.test/bc?tenant=1", "--public"));

        await RunCliOkAsync(factory, "client", "set-backchannel-logout-uri", "f", BackChannelUri);
        Assert.Contains($"backchannel_logout_uri {BackChannelUri}", (await factory.RunCliAsync("client", "list")).Output, StringComparison.Ordinal);
        Assert.Equal(1, (await factory.RunCliAsync("client", "set-backchannel-logout-uri", "f", "https://x.example.test/#a")).ExitCode);
        await RunCliOkAsync(factory, "client", "clear-backchannel-logout-uri", "f");
        Assert.DoesNotContain(BackChannelUri, (await factory.RunCliAsync("client", "list")).Output, StringComparison.Ordinal);
    }

    // Helpers

    private static async Task AssertLogoutTokenAsync(ValkyrieAppFactory factory, string token, string audience, Guid subject, string sid)
    {
        var keys = await factory.Services.GetRequiredService<SigningKeyStore>().GetAsync(CancellationToken);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = ValkyrieAppFactory.DefaultIssuer,
            ValidAudience = audience,
            ValidTypes = ["logout+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = [.. keys.Keys.Select(k => k.SecurityKey)],
        });
        Assert.True(result.IsValid, result.Exception?.ToString());

        // Back-Channel Logout §2.4.
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal(subject.ToString(), jwt.Subject);
        Assert.Equal(sid, jwt.GetClaim("sid").Value);
        Assert.False(string.IsNullOrEmpty(jwt.Id));
        Assert.Equal(TimeSpan.FromMinutes(2), jwt.ValidTo - jwt.IssuedAt);
        Assert.False(jwt.TryGetClaim("nonce", out _));
        var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(jwt.EncodedPayload)).RootElement;
        var events = payload.GetProperty("events");
        Assert.Equal(["http://schemas.openid.net/event/backchannel-logout"], events.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Object, events.GetProperty("http://schemas.openid.net/event/backchannel-logout").ValueKind);
        Assert.Empty(events.GetProperty("http://schemas.openid.net/event/backchannel-logout").EnumerateObject());
    }

    private static string AssertLogoutPage(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/account/logout?request=", location, StringComparison.Ordinal);
        return location;
    }

    /// <summary>A signed-in browser whose session used the client "web", which has a back-channel logout URI.</summary>
    private static async Task<(HttpClient Browser, string Secret)> SignedInWithBackChannelClientAsync(ValkyrieAppFactory factory)
    {
        await CreateUserAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web", backChannelUri: BackChannelUri);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        await SignInToClientAsync(browser, "web", secret);
        return (browser, secret);
    }

    private static async Task<Guid> QueueDeliveryAsync(ValkyrieAppFactory factory, string? leaseOwner, DateTimeOffset? leaseExpiresAt)
    {
        await CreateUserAsync(factory, "alice");
        await CreateClientAsync(factory, "web", backChannelUri: BackChannelUri);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var now = DateTimeOffset.UtcNow;
        var userId = await db.Users.Select(u => u.Id).SingleAsync(CancellationToken);
        var session = new UserSession { UserId = userId, AuthenticatedAt = now, LastActivityAt = now, ExpiresAt = now.AddHours(1), RevokedAt = now };
        var delivery = new BackChannelLogoutDelivery
        {
            ClientId = "web",
            UserId = userId,
            SessionId = session.Id,
            CreatedAt = now,
            NextAttemptAt = now - TimeSpan.FromSeconds(1),
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt,
        };
        db.UserSessions.Add(session);
        db.BackChannelLogoutDeliveries.Add(delivery);
        await db.SaveChangesAsync(CancellationToken);
        return delivery.Id;
    }

    private static async Task<BackChannelLogoutDelivery> SingleDeliveryAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().BackChannelLogoutDeliveries.AsNoTracking().SingleAsync(CancellationToken);
    }

    private static async Task<bool> SessionActiveAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().UserSessions
            .OrderByDescending(s => s.AuthenticatedAt)
            .Select(s => s.RevokedAt == null)
            .FirstAsync(CancellationToken);
    }

    private static async Task<RsaSecurityKey> CurrentSigningKeyAsync(ValkyrieAppFactory factory)
    {
        var keys = await factory.Services.GetRequiredService<SigningKeyStore>().GetAsync(CancellationToken);
        return keys.Current(DateTimeOffset.UtcNow)!.SecurityKey;
    }

    private static string Sign(SecurityKey key, string issuer, string type, JsonWebToken claims) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            TokenType = type,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = claims.Subject,
                ["aud"] = claims.Audiences.Single(),
                ["sid"] = claims.GetClaim("sid").Value,
            },
        });

    private static string EndSessionUrl(params (string Key, string Value)[] parameters) =>
        "/connect/endsession?" + string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    /// <summary>Runs the authorization code flow for <paramref name="clientId"/> and returns the ID token.</summary>
    private static async Task<string> SignInToClientAsync(HttpClient browser, string clientId, string secret) =>
        (await RedeemAsync(browser, clientId, secret, "openid")).GetProperty("id_token").GetString()!;

    private static async Task<string> AccessTokenAsync(HttpClient browser, string clientId, string secret) =>
        (await RedeemAsync(browser, clientId, secret, "openid")).GetProperty("access_token").GetString()!;

    private static async Task<JsonElement> RedeemAsync(HttpClient browser, string clientId, string secret, string scope)
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = await browser.GetAsync(
            $"/connect/authorize?response_type=code&client_id={clientId}&scope={Uri.EscapeDataString(scope)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + $"&code_challenge={challenge}&code_challenge_method=S256",
            CancellationToken);
        Assert.True(authorize.StatusCode == HttpStatusCode.Redirect, await authorize.Content.ReadAsStringAsync(CancellationToken));
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", RedirectUri), new("code_verifier", verifier),
            ]),
        };
        request.Headers.Authorization = Basic(clientId, secret);
        var response = await browser.SendAsync(request, CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;
    }

    private static HttpRequestMessage RefreshRequest(string refreshToken, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "refresh_token"), new("refresh_token", refreshToken)]),
        };
        request.Headers.Authorization = Basic("web", secret);
        return request;
    }

    private static AuthenticationHeaderValue Basic(string clientId, string secret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret)}")));

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
        ValkyrieAppFactory factory,
        string clientId,
        string? backChannelUri = null,
        string? name = null,
        string postLogoutUri = PostLogoutUri,
        string? extraPostLogoutUri = null)
    {
        var (exitCode, output, error) = await factory.RunCliAsync(
        [
            "client", "create", clientId, "--redirect-uri", RedirectUri, "--post-logout-redirect-uri", postLogoutUri,
            "--grant", "authorization_code", "--grant", "refresh_token", "--scope", "openid", "--scope", "offline_access",
            .. backChannelUri is null ? Array.Empty<string>() : ["--backchannel-logout-uri", backChannelUri],
            .. name is null ? Array.Empty<string>() : ["--name", name],
            .. extraPostLogoutUri is null ? Array.Empty<string>() : ["--post-logout-redirect-uri", extraPostLogoutUri],
        ]);
        Assert.True(exitCode == 0, error);
        return output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("client_secret: ", StringComparison.Ordinal))["client_secret: ".Length..];
    }

    private static async Task RunCliOkAsync(ValkyrieAppFactory factory, params string[] args)
    {
        var (exitCode, _, error) = await factory.RunCliAsync(args);
        Assert.True(exitCode == 0, error);
    }
}
