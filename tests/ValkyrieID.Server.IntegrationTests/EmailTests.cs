using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Email;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>The email features: password reset, email verification, and email change.</summary>
public abstract partial class EmailTests
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "another correct horse battery";

    /// <summary>Creates a server whose emails go to <paramref name="sender"/>; queued emails are sent by <see cref="DeliverAsync"/>.</summary>
    internal abstract ValkyrieAppFactory CreateFactory(TimeProvider timeProvider, Dictionary<string, string?> settings, TestEmailSender sender);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed record Server(ValkyrieAppFactory Factory, TestEmailSender Sender, TestTimeProvider Clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    private Server Create(Dictionary<string, string?>? settings = null, bool smtp = true)
    {
        var sender = new TestEmailSender();
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        Dictionary<string, string?> all = smtp
            ? new() { ["Smtp:Host"] = "smtp.example.test", ["Smtp:FromAddress"] = "id@example.test" }
            : [];
        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        return new(CreateFactory(clock, all, sender), sender, clock);
    }

    // Feature switch

    [Theory]
    [InlineData("/account/forgot-password")]
    [InlineData("/account/reset-password?token=x")]
    [InlineData("/account/verify-email?token=x")]
    [InlineData("/account/confirm-email?token=x")]
    public async Task Email_pages_are_not_found_without_smtp(string path)
    {
        await using var server = Create(smtp: false);
        await CreateUserAsync(server, "alice", "alice@example.test");

        Assert.Equal(HttpStatusCode.NotFound, (await server.Factory.CreateBrowser().GetAsync(path, CancellationToken)).StatusCode);
        var login = await (await server.Factory.CreateBrowser().GetAsync("/account/login", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain("forgot-password", login, StringComparison.Ordinal);
        var browser = await SignInAsync(server, "alice");
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/account/email", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Smtp_settings_are_validated_at_startup()
    {
        await using var server = Create(new() { ["Smtp:Security"] = "None", ["Smtp:FromAddress"] = "not an address" });

        var error = Assert.ThrowsAny<Exception>(() => server.Factory.CreateClient());
        Assert.Contains("Smtp:Security None", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Smtp:FromAddress", error.ToString(), StringComparison.Ordinal);
    }

    // Password reset

    [Fact]
    public async Task Requesting_a_reset_looks_the_same_for_known_and_unknown_accounts()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        await CreateUserAsync(server, "bob", null);

        var known = await RequestResetAsync(server, "alice");
        var byEmail = await RequestResetAsync(server, "ALICE@example.test");
        var unknown = await RequestResetAsync(server, "mallory");
        var noEmail = await RequestResetAsync(server, "bob");

        var body = await Forms.NormalizedBodyAsync(known);
        Assert.Contains("If an account", body, StringComparison.Ordinal);
        foreach (var response in new[] { byEmail, unknown, noEmail })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(body, await Forms.NormalizedBodyAsync(response));
        }

        // Only the first request sent a link; the second was within the resend interval.
        await DeliverAsync(server);
        var email = Assert.Single(server.Sender.Sent);
        Assert.Equal("alice@example.test", email.To);
        Assert.StartsWith($"{ValkyrieAppFactory.DefaultIssuer}/account/reset-password?token=", LinkPattern().Match(email.Body).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_links_use_the_issuer_and_not_the_request_host()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var browser = server.Factory.CreateBrowser();
        var token = await Forms.GetTokenAsync(browser, "/account/forgot-password");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/forgot-password")
        {
            Content = new FormUrlEncodedContent([new("Identifier", "alice"), new("__RequestVerificationToken", token)]),
        };
        request.Headers.Host = "evil.example";
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        await browser.SendAsync(request, CancellationToken);

        await DeliverAsync(server);
        var email = Assert.Single(server.Sender.Sent);
        Assert.DoesNotContain("evil", email.Body, StringComparison.Ordinal);
        Assert.Contains(ValkyrieAppFactory.DefaultIssuer, email.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queued_emails_are_encrypted_and_deleted_once_sent()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        await RequestResetAsync(server, "alice");

        await using (var scope = server.Factory.Services.CreateAsyncScope())
        {
            var queued = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().OutboundEmails.SingleAsync(CancellationToken);
            var raw = System.Text.Encoding.UTF8.GetString(queued.EncryptedMessage);
            Assert.DoesNotContain("token=", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("alice@example.test", raw, StringComparison.Ordinal);
        }

        await DeliverAsync(server);

        await using (var scope = server.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            Assert.False(await db.OutboundEmails.AnyAsync(CancellationToken));
            var sent = await db.AuditLog.SingleAsync(e => e.EventType == "email.sent", CancellationToken);
            Assert.DoesNotContain("alice@example.test", sent.Details, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Resetting_the_password_signs_out_every_session_and_clears_the_lockout()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var first = await SignInAsync(server, "alice");
        var second = await SignInAsync(server, "alice");
        for (var i = 0; i < 5; i++)
        {
            await Forms.LoginAsync(server.Factory.CreateBrowser(), "alice", "wrong password!");
        }

        var link = await ResetLinkAsync(server, "alice");
        var anonymous = server.Factory.CreateBrowser();

        // Opening the link (as a mail scanner would) doesn't use it up.
        Assert.Contains("Choose a new password", await GetBodyAsync(anonymous, link), StringComparison.Ordinal);
        Assert.Contains("Choose a new password", await GetBodyAsync(anonymous, link), StringComparison.Ordinal);

        var response = await PostResetAsync(anonymous, link, NewPassword);

        Assert.Contains("password has been reset", await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        // Pages with forms (every page has the theme form) get "no-cache, no-store" from anti-forgery.
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.Redirect, (await first.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Forms.LoginAsync(server.Factory.CreateBrowser(), "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(server.Factory.CreateBrowser(), "alice", NewPassword)).StatusCode);

        await using var scope = server.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.Equal(2, await db.AuditLog.CountAsync(e => e.EventType == "session.revoked", CancellationToken));
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.password_reset.completed", CancellationToken));
        Assert.False(await db.EmailTokens.AnyAsync(CancellationToken));

        // The account's address is told about the change; the notice has no link.
        await DeliverAsync(server);
        var notice = server.Sender.Sent.Last();
        Assert.Contains("password was changed", notice.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("http", notice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reset_link_works_only_once()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var link = await ResetLinkAsync(server, "alice");

        var browser = server.Factory.CreateBrowser();
        var page = await GetBodyAsync(browser, link);
        Dictionary<string, string> fields = new() { ["NewPassword"] = NewPassword, ["ConfirmPassword"] = NewPassword };
        await PostFormAsync(browser, link, page, fields);
        var replay = await PostFormAsync(browser, link, page, new() { ["NewPassword"] = "a third correct horse", ["ConfirmPassword"] = "a third correct horse" });

        Assert.Contains("invalid or has expired", await replay.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("invalid or has expired", await GetBodyAsync(server.Factory.CreateBrowser(), link), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(server.Factory.CreateBrowser(), "alice", NewPassword)).StatusCode);
    }

    [Fact]
    public async Task A_reset_link_expires_after_its_lifetime()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var link = await ResetLinkAsync(server, "alice");
        var browser = server.Factory.CreateBrowser();
        var page = await GetBodyAsync(browser, link);

        server.Clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));
        Assert.Contains("Choose a new password", await GetBodyAsync(server.Factory.CreateBrowser(), link), StringComparison.Ordinal);
        server.Clock.Advance(TimeSpan.FromSeconds(1));

        var expired = await PostFormAsync(browser, link, page, new() { ["NewPassword"] = NewPassword, ["ConfirmPassword"] = NewPassword });
        Assert.Contains("invalid or has expired", await expired.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(server.Factory.CreateBrowser(), "alice", Password)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Invalid_reset_tokens_are_rejected(string token)
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");

        var body = await GetBodyAsync(server.Factory.CreateBrowser(), $"/account/reset-password?token={token}");

        Assert.Contains("invalid or has expired", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NewPassword", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_for_another_purpose_cannot_reset_the_password()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var browser = await SignInAsync(server, "alice");
        await Forms.PostAsync(browser, "/account/email?handler=Verify", new());
        await DeliverAsync(server);
        var verifyToken = TokenPattern().Match(server.Sender.Sent.Single().Body).Groups[1].Value;

        var body = await GetBodyAsync(server.Factory.CreateBrowser(), $"/account/reset-password?token={verifyToken}");

        Assert.Contains("invalid or has expired", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_password_voids_pending_reset_links()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var link = await ResetLinkAsync(server, "alice");
        var browser = await SignInAsync(server, "alice");

        await Forms.PostAsync(browser, "/account/password", new() { ["CurrentPassword"] = Password, ["NewPassword"] = NewPassword, ["ConfirmPassword"] = NewPassword });

        Assert.Contains("invalid or has expired", await GetBodyAsync(server.Factory.CreateBrowser(), link), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_new_password_must_meet_the_policy_and_the_link_stays_usable_until_it_does()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var link = await ResetLinkAsync(server, "alice");
        var browser = server.Factory.CreateBrowser();

        var weak = await PostResetAsync(browser, link, "short");
        var mismatch = await PostResetAsync(browser, link, NewPassword, NewPassword + "x");
        var ok = await PostResetAsync(browser, link, NewPassword);

        Assert.Contains("alert", await weak.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("don&#x27;t match", await mismatch.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("password has been reset", await ok.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reset_does_not_bypass_two_factor_authentication()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        await using (var scope = server.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorEnabled, true), CancellationToken);
        }

        var link = await ResetLinkAsync(server, "alice");
        await PostResetAsync(server.Factory.CreateBrowser(), link, NewPassword);
        var login = await Forms.LoginAsync(server.Factory.CreateBrowser(), "alice", NewPassword);

        Assert.Equal(HttpStatusCode.SeeOther, login.StatusCode);
        Assert.StartsWith("/account/login/2fa", login.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_account_gets_no_reset_link_and_cannot_use_an_earlier_one()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var link = await ResetLinkAsync(server, "alice");
        Assert.Equal(0, (await server.Factory.RunCliAsync("user", "disable", "alice")).ExitCode);

        server.Clock.Advance(TimeSpan.FromMinutes(2));
        await RequestResetAsync(server, "alice");
        await DeliverAsync(server);

        Assert.Single(server.Sender.Sent);
        Assert.Contains("invalid or has expired", await GetBodyAsync(server.Factory.CreateBrowser(), link), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_requests_are_rate_limited()
    {
        await using var server = Create(new() { ["RateLimits:Email:PermitLimit"] = "2" });
        var browser = server.Factory.CreateBrowser();

        await RequestResetAsync(server, "a", browser);
        await RequestResetAsync(server, "b", browser);
        var limited = await RequestResetAsync(server, "c", browser);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    // Email verification

    [Fact]
    public async Task An_email_address_can_be_verified_with_a_link()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var browser = await SignInAsync(server, "alice");
        Assert.Contains("(not verified)", await GetBodyAsync(browser, "/account/email"), StringComparison.Ordinal);

        await Forms.PostAsync(browser, "/account/email?handler=Verify", new());
        await DeliverAsync(server);
        var link = LinkPattern().Match(server.Sender.Sent.Single().Body).Value;
        Assert.StartsWith($"{ValkyrieAppFactory.DefaultIssuer}/account/verify-email?token=", link, StringComparison.Ordinal);

        // Works without a session, e.g. on another device.
        var other = server.Factory.CreateBrowser();
        var page = await GetBodyAsync(other, link);
        Assert.False(await EmailVerifiedAsync(server));
        var verified = await PostFormAsync(other, link, page, new());
        var replay = await PostFormAsync(other, link, page, new());

        Assert.Contains("has been verified", await verified.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("invalid or has expired", await replay.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.True(await EmailVerifiedAsync(server));
        Assert.Contains("(verified)", await GetBodyAsync(browser, "/account/email"), StringComparison.Ordinal);
    }

    // Email change

    [Fact]
    public async Task Changing_the_email_requires_confirmation_from_the_new_address()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var browser = await SignInAsync(server, "alice");

        var requested = await PostEmailChangeAsync(browser, "alice@new.example", Password);

        Assert.Contains("sent an email to the new address", await requested.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal("alice@example.test", await EmailAsync(server, "alice"));
        await DeliverAsync(server);
        var confirmation = server.Sender.Sent.Single();
        Assert.Equal("alice@new.example", confirmation.To);
        var link = LinkPattern().Match(confirmation.Body).Value;
        Assert.StartsWith($"{ValkyrieAppFactory.DefaultIssuer}/account/confirm-email?token=", link, StringComparison.Ordinal);

        var other = server.Factory.CreateBrowser();
        var confirmed = await PostFormAsync(other, link, await GetBodyAsync(other, link), new());

        Assert.Contains("has been changed", await confirmed.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal("alice@new.example", await EmailAsync(server, "alice"));
        Assert.True(await EmailVerifiedAsync(server));
        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(server.Factory.CreateBrowser(), "alice@new.example", Password)).StatusCode);

        // The old address is told, without a link and with the new address masked.
        await DeliverAsync(server);
        var notice = server.Sender.Sent.Last();
        Assert.Equal("alice@example.test", notice.To);
        Assert.Contains("a***@new.example", notice.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@new.example", notice.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("http", notice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_email_voids_reset_links_sent_to_the_old_address()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var resetLink = await ResetLinkAsync(server, "alice");
        var browser = await SignInAsync(server, "alice");
        await PostEmailChangeAsync(browser, "alice@new.example", Password);
        await DeliverAsync(server);
        var link = LinkPattern().Match(server.Sender.Sent.Last().Body).Value;
        var other = server.Factory.CreateBrowser();
        await PostFormAsync(other, link, await GetBodyAsync(other, link), new());

        Assert.Contains("invalid or has expired", await GetBodyAsync(server.Factory.CreateBrowser(), resetLink), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_in_use_gets_a_notice_instead_of_a_link_and_the_response_is_the_same()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        await CreateUserAsync(server, "bob", "bob@example.test");
        var browser = await SignInAsync(server, "alice");

        var inUse = await PostEmailChangeAsync(browser, "BOB@example.test", Password);
        server.Clock.Advance(TimeSpan.FromMinutes(2));
        var free = await PostEmailChangeAsync(browser, "carol@example.test", Password);

        Assert.Equal(await Forms.NormalizedBodyAsync(free), await Forms.NormalizedBodyAsync(inUse));
        await DeliverAsync(server);
        var notice = server.Sender.Sent.Single(m => m.To == "BOB@example.test");
        Assert.DoesNotContain("token=", notice.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", notice.Body, StringComparison.Ordinal);
        Assert.Equal("alice@example.test", await EmailAsync(server, "alice"));
    }

    [Fact]
    public async Task Confirming_fails_if_another_account_took_the_address_in_the_meantime()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var browser = await SignInAsync(server, "alice");
        await PostEmailChangeAsync(browser, "shared@example.test", Password);
        await DeliverAsync(server);
        var link = LinkPattern().Match(server.Sender.Sent.Single().Body).Value;
        await CreateUserAsync(server, "bob", "shared@example.test");

        var other = server.Factory.CreateBrowser();
        var response = await PostFormAsync(other, link, await GetBodyAsync(other, link), new());

        Assert.Contains("already used by another account", await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal("alice@example.test", await EmailAsync(server, "alice"));
    }

    [Fact]
    public async Task An_email_change_needs_the_password_and_wrong_ones_count_towards_the_lockout()
    {
        await using var server = Create(new() { ["RateLimits:Email:PermitLimit"] = "100" });
        await CreateUserAsync(server, "alice", "alice@example.test");
        var browser = await SignInAsync(server, "alice");

        for (var i = 0; i < 5; i++)
        {
            server.Clock.Advance(TimeSpan.FromMinutes(2));
            var wrong = await PostEmailChangeAsync(browser, "mallory@example.test", "wrong password!");
            Assert.Contains("password is incorrect", await wrong.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        }

        await DeliverAsync(server);
        Assert.Empty(server.Sender.Sent);
        await using var scope = server.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog.AnyAsync(e => e.EventType == "user.locked_out", CancellationToken));
    }

    [Fact]
    public async Task Email_change_posts_need_an_antiforgery_token()
    {
        await using var server = Create();
        await CreateUserAsync(server, "alice", "alice@example.test");
        var browser = await SignInAsync(server, "alice");

        var response = await browser.PostAsync("/account/email", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["NewEmail"] = "mallory@example.test",
            ["Password"] = Password,
        }), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Delivery

    [Fact]
    public async Task Failed_emails_are_retried_and_permanent_failures_are_dropped()
    {
        await using var server = Create(new() { ["Smtp:RetryDelays:0"] = "00:00:10" });
        await CreateUserAsync(server, "alice", "alice@example.test");
        await RequestResetAsync(server, "alice");

        server.Sender.Failure = new EmailDeliveryException("network_error", permanent: false);
        await DeliverAsync(server);
        server.Clock.Advance(TimeSpan.FromSeconds(10));
        server.Sender.Failure = new EmailDeliveryException("smtp_550", permanent: true);
        await DeliverAsync(server);

        await using var scope = server.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.False(await db.OutboundEmails.AnyAsync(CancellationToken));
        var failed = await db.AuditLog.SingleAsync(e => e.EventType == "email.failed", CancellationToken);
        Assert.Contains("\"attempts\":2", failed.Details, StringComparison.Ordinal);
        Assert.Contains("smtp_550", failed.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cli_reports_smtp_problems_from_a_test_email()
    {
        await using var server = Create();

        Assert.Equal(0, (await server.Factory.RunCliAsync("admin", "send-test-email", "ops@example.test")).ExitCode);
        Assert.Equal("ops@example.test", server.Sender.Sent.Single().To);

        server.Sender.Failure = new EmailDeliveryException("authentication_failed", permanent: false);
        var (exitCode, _, error) = await server.Factory.RunCliAsync("admin", "send-test-email", "ops@example.test");
        Assert.Equal(1, exitCode);
        Assert.Contains("authentication_failed", error, StringComparison.Ordinal);
    }

    // Helpers

    private static async Task CreateUserAsync(Server server, string userName, string? email)
    {
        string[] args = email is null ? ["user", "create", userName] : ["user", "create", userName, "--email", email];
        Assert.Equal(0, (await server.Factory.RunCliWithInputAsync(Password + "\n", args)).ExitCode);
    }

    private static async Task<HttpClient> SignInAsync(Server server, string userName)
    {
        var browser = server.Factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(browser, userName, Password)).StatusCode);
        return browser;
    }

    private static Task<HttpResponseMessage> RequestResetAsync(Server server, string identifier, HttpClient? browser = null) =>
        Forms.PostAsync(browser ?? server.Factory.CreateBrowser(), "/account/forgot-password", new() { ["Identifier"] = identifier });

    private static async Task<string> ResetLinkAsync(Server server, string identifier)
    {
        var before = server.Sender.Sent.Count;
        await RequestResetAsync(server, identifier);
        await DeliverAsync(server);
        Assert.Equal(before + 1, server.Sender.Sent.Count);
        return LinkPattern().Match(server.Sender.Sent.Last().Body).Value;
    }

    /// <summary>Opens a reset link and submits the new password.</summary>
    private static async Task<HttpResponseMessage> PostResetAsync(HttpClient browser, string link, string password, string? confirm = null)
    {
        var page = await GetBodyAsync(browser, link);
        Assert.Contains("NewPassword", page, StringComparison.Ordinal);

        return await PostFormAsync(browser, link, page, new() { ["NewPassword"] = password, ["ConfirmPassword"] = confirm ?? password });
    }

    private static Task<HttpResponseMessage> PostEmailChangeAsync(HttpClient browser, string email, string password) =>
        Forms.PostAsync(browser, "/account/email", new() { ["NewEmail"] = email, ["Password"] = password });

    /// <summary>Submits the form on an email link page, with the token from the link.</summary>
    private static Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string link, string page, Dictionary<string, string> fields)
    {
        var path = new Uri(link, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(link).PathAndQuery : link;
        var antiforgery = AntiforgeryPattern().Match(page);
        var token = TokenPattern().Match(link).Groups[1].Value;
        return browser.PostAsync(path, new FormUrlEncodedContent(
            fields.Append(new("Token", token)).Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(antiforgery.Groups[1].Value)))),
            CancellationToken);
    }

    private static async Task<string> GetBodyAsync(HttpClient browser, string link)
    {
        // Only http(s) links are absolute here: on Linux, Uri.TryCreate also accepts "/path" as a file:// URI.
        var path = Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.PathAndQuery
            : link;
        var response = await browser.GetAsync(path, CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {response.Headers.Location}");
        return await response.Content.ReadAsStringAsync(CancellationToken);
    }

    private static async Task DeliverAsync(Server server) =>
        await server.Factory.Services.GetRequiredService<EmailDispatcher>().ProcessDueAsync(CancellationToken);

    private static async Task<string?> EmailAsync(Server server, string userName)
    {
        await using var scope = server.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users
            .Where(u => u.UserName == userName).Select(u => u.Email).SingleAsync(CancellationToken);
    }

    private static async Task<bool> EmailVerifiedAsync(Server server)
    {
        await using var scope = server.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users
            .Where(u => u.UserName == "alice").Select(u => u.EmailVerified).SingleAsync(CancellationToken);
    }

    [GeneratedRegex(@"https://\S+\?token=[A-Za-z0-9_-]+")]
    private static partial Regex LinkPattern();

    [GeneratedRegex("token=([A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryPattern();
}
