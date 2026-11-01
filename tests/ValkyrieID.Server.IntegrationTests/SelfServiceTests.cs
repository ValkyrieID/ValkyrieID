using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>The self-service account pages: profile, password, 2FA management, and sessions.</summary>
public abstract partial class SelfServiceTests
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "another correct horse battery";

    protected abstract ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/account/profile")]
    [InlineData("/account/password")]
    [InlineData("/account/sessions")]
    [InlineData("/account/two-factor")]
    public async Task Account_pages_require_a_session(string path)
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var response = await factory.CreateBrowser().GetAsync(path, CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/account/profile")]
    [InlineData("/account/password")]
    [InlineData("/account/sessions?handler=RevokeOthers")]
    [InlineData("/account/two-factor?handler=Disable")]
    [InlineData("/account/two-factor?handler=Regenerate")]
    public async Task Posts_without_an_antiforgery_token_are_rejected(string path)
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = await SignInAsync(factory, "alice");
        await SignInAsync(factory, "alice");

        var response = await browser.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["DisplayName"] = "Mallory",
            ["CurrentPassword"] = Password,
            ["NewPassword"] = NewPassword,
            ["ConfirmPassword"] = NewPassword,
            ["Password"] = Password,
        }), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var user = await db.Users.SingleAsync(CancellationToken);
        Assert.Null(user.DisplayName);
        Assert.Equal(2, await db.UserSessions.CountAsync(s => s.RevokedAt == null, CancellationToken));
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);
    }

    // Profile

    [Fact]
    public async Task Profile_names_can_be_changed()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = await SignInAsync(factory, "alice");

        var saved = await PostProfileAsync(browser, "  Alice A. ", "Alice", "");
        var tooLong = await PostProfileAsync(browser, "Alice", new string('x', 101), "Anderson");

        Assert.Contains("has been saved", await saved.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("at most 100 characters", await tooLong.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var user = await db.Users.SingleAsync(CancellationToken);
        Assert.Equal("Alice A.", user.DisplayName);
        Assert.Equal("Alice", user.GivenName);
        Assert.Null(user.FamilyName);
        var audit = await db.AuditLog.SingleAsync(e => e.EventType == "user.profile_updated", CancellationToken);
        Assert.Equal(user.Id.ToString(), audit.Actor);
        Assert.Contains("display_name given_name", audit.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_email_address_cannot_be_changed_from_the_profile()
    {
        await using var factory = CreateFactory();
        Assert.Equal(0, (await factory.RunCliWithInputAsync(Password + "\n", "user", "create", "alice", "--email", "alice@example.test")).ExitCode);
        var browser = await SignInAsync(factory, "alice");

        await Forms.PostAsync(browser, "/account/profile", new() { ["Email"] = "mallory@example.test", ["DisplayName"] = "Alice" });

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal("alice@example.test", (await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users.SingleAsync(CancellationToken)).Email);
    }

    // Password

    [Fact]
    public async Task Changing_the_password_keeps_this_session_and_signs_out_the_others()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var current = await SignInAsync(factory, "alice");
        var other = await SignInAsync(factory, "alice");

        var response = await PostPasswordAsync(current, Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("password has been changed", await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", NewPassword)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.password_changed", CancellationToken));
        var revoked = await db.AuditLog.SingleAsync(e => e.EventType == "session.revoked", CancellationToken);
        Assert.Contains("\"reason\":\"password_changed\"", revoked.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(await db.AuditLog.Select(e => e.Details).ToListAsync(CancellationToken), d => d != null && (d.Contains(Password, StringComparison.Ordinal) || d.Contains(NewPassword, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_wrong_current_password_is_rejected_and_counts_towards_the_lockout()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = await SignInAsync(factory, "alice");

        for (var i = 0; i < 5; i++)
        {
            var wrong = await PostPasswordAsync(browser, "wrong password!", NewPassword);
            Assert.Contains("password is incorrect", await wrong.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        }

        // Locked out: even the right password is refused, and the account can't sign in either.
        var locked = await PostPasswordAsync(browser, Password, NewPassword);
        Assert.Contains("password is incorrect", await locked.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.locked_out", CancellationToken));
        Assert.False(await db.AuditLog.AnyAsync(e => e.EventType == "user.password_changed", CancellationToken));
    }

    [Fact]
    public async Task The_new_password_must_match_the_policy_the_confirmation_and_differ_from_the_current_one()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = await SignInAsync(factory, "alice");

        var mismatch = await PostPasswordAsync(browser, Password, NewPassword, NewPassword + "x");
        var weak = await PostPasswordAsync(browser, Password, "short");
        var same = await PostPasswordAsync(browser, Password, Password);

        Assert.Contains("don&#x27;t match", await mismatch.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("alert", await weak.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("must be different", await same.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);
    }

    [Fact]
    public async Task Password_changes_are_rate_limited()
    {
        await using var factory = CreateFactory(settings: new() { ["RateLimits:TwoFactor:PermitLimit"] = "2" });
        await CreateUserAsync(factory, "alice");
        var browser = await SignInAsync(factory, "alice");

        await PostPasswordAsync(browser, "wrong password!", NewPassword);
        await PostPasswordAsync(browser, "wrong password!", NewPassword);
        var limited = await PostPasswordAsync(browser, Password, NewPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    // Two-factor authentication

    [Fact]
    public async Task Disabling_2fa_with_a_code_keeps_this_session_and_signs_out_the_others()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (secret, _, browser) = await EnrollAsync(factory, clock, "alice");
        var other = await SignInWithCodeAsync(factory, clock, secret, "alice");
        clock.Advance(Totp.Step);

        var response = await PostDisableAsync(browser, Password, CurrentCode(secret, clock));

        Assert.Contains("Two-factor authentication is off", await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal("/account", (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).Headers.Location?.OriginalString);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var user = await db.Users.SingleAsync(CancellationToken);
        Assert.False(user.TwoFactorEnabled);
        Assert.Null(user.TotpSecret);
        Assert.False(await db.RecoveryCodes.AnyAsync(CancellationToken));
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.2fa.disabled", CancellationToken));
        var revoked = await db.AuditLog.SingleAsync(e => e.EventType == "session.revoked", CancellationToken);
        Assert.Contains("\"reason\":\"2fa_disabled\"", revoked.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabling_2fa_works_with_a_recovery_code()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (_, codes, browser) = await EnrollAsync(factory, clock, "alice");

        await PostDisableAsync(browser, Password, codes[0].ToLowerInvariant());

        Assert.False(await TwoFactorEnabledAsync(factory));
    }

    [Fact]
    public async Task Disabling_2fa_requires_the_password_and_a_fresh_code()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (secret, _, browser) = await EnrollAsync(factory, clock, "alice");
        clock.Advance(Totp.Step);
        var code = CurrentCode(secret, clock);
        var other = await SignInWithCodeAsync(factory, clock, secret, "alice", code);

        var wrongPassword = await PostDisableAsync(browser, "wrong password!", CurrentCode(secret, clock));
        var replayed = await PostDisableAsync(browser, Password, code);
        var noCode = await PostDisableAsync(browser, Password, "");

        Assert.Contains("password is incorrect", await wrongPassword.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("already been used", await replayed.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("already been used", await noCode.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.True(await TwoFactorEnabledAsync(factory));
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/account", CancellationToken)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users.Select(u => u.AccessFailedCount).SingleAsync(CancellationToken));
    }

    [Fact]
    public async Task Two_factor_cannot_be_disabled_while_it_is_required_for_the_user()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (secret, codes, browser) = await EnrollAsync(factory, clock, "alice");
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            await setup.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorRequired, true), CancellationToken);
        }

        var page = await (await browser.GetAsync("/account/two-factor", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
        clock.Advance(Totp.Step);
        var forged = await PostDisableAsync(browser, Password, CurrentCode(secret, clock));

        Assert.DoesNotContain("handler=Disable", page, StringComparison.Ordinal);
        Assert.Contains("required for your account", await forged.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.True(await TwoFactorEnabledAsync(factory));
        Assert.Equal(codes.Count, await UnusedRecoveryCodesAsync(factory));
    }

    [Fact]
    public async Task Two_factor_cannot_be_disabled_when_required_globally()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock, new() { ["TwoFactor:Required"] = "true" });
        await CreateUserAsync(factory, "alice");
        var (secret, browser) = await EnrollAtSignInAsync(factory, clock, "alice");
        clock.Advance(Totp.Step);

        var forged = await PostDisableAsync(browser, Password, CurrentCode(secret, clock));

        Assert.Contains("required for your account", await forged.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.True(await TwoFactorEnabledAsync(factory));
    }

    [Fact]
    public async Task Regenerating_recovery_codes_replaces_the_old_ones()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (_, oldCodes, browser) = await EnrollAsync(factory, clock, "alice");

        var wrong = await Forms.PostAsync(browser, "/account/two-factor?handler=Regenerate", new() { ["Password"] = "wrong password!" });
        Assert.Contains("password is incorrect", await wrong.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);

        var response = await Forms.PostAsync(browser, "/account/two-factor?handler=Regenerate", new() { ["Password"] = Password });
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        var newCodes = RecoveryCodePattern().Matches(body).Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(RecoveryCodes.Count, newCodes.Count);
        Assert.Empty(newCodes.Intersect(oldCodes));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account", CancellationToken)).StatusCode);

        var old = factory.CreateBrowser();
        await Forms.LoginAsync(old, "alice", Password);
        Assert.Equal(HttpStatusCode.OK, (await Forms.PostAsync(old, "/account/login/recovery", new() { ["Code"] = oldCodes[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.PostAsync(old, "/account/login/recovery", new() { ["Code"] = newCodes[0] })).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .AnyAsync(e => e.EventType == "user.2fa.recovery_codes_regenerated", CancellationToken));
    }

    // Sessions

    [Fact]
    public async Task Sessions_page_lists_only_the_users_own_active_sessions()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateUserAsync(factory, "bob");
        var browser = await SignInAsync(factory, "alice");
        await SignInAsync(factory, "alice");
        await SignInAsync(factory, "bob");

        var page = await (await browser.GetAsync("/account/sessions", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);

        var ids = SessionIdPattern().Matches(page).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal((await SessionIdsAsync(factory, "alice")).Order(), ids.Order());
        Assert.DoesNotContain((await SessionIdsAsync(factory, "bob")).Single(), page, StringComparison.Ordinal);
        Assert.Contains("(this session)", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_can_revoke_one_of_their_sessions_or_all_others()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var current = await SignInAsync(factory, "alice");
        var second = await SignInAsync(factory, "alice");
        var third = await SignInAsync(factory, "alice");
        var secondId = await LatestSessionIdAsync(factory, skip: 1);

        var revoked = await Forms.PostAsync(current, "/account/sessions?handler=Revoke", new() { ["SessionId"] = secondId });

        Assert.Contains("has been signed out", await revoked.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await third.GetAsync("/account", CancellationToken)).StatusCode);

        var others = await Forms.PostAsync(current, "/account/sessions?handler=RevokeOthers", []);

        Assert.Contains("1 other session has been signed out", await others.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await third.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/account", CancellationToken)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var details = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .Where(e => e.EventType == "session.revoked").Select(e => e.Details).ToListAsync(CancellationToken);
        Assert.Contains(details, d => d!.Contains("\"reason\":\"user_revoked\"", StringComparison.Ordinal));
        Assert.Contains(details, d => d!.Contains("\"reason\":\"user_revoked_others\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Revoking_the_current_session_signs_out()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = await SignInAsync(factory, "alice");
        var sid = (await SessionIdsAsync(factory, "alice")).Single();

        var response = await Forms.PostAsync(browser, "/account/sessions?handler=Revoke", new() { ["SessionId"] = sid });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_user_cannot_revoke_another_users_session()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateUserAsync(factory, "bob");
        var alice = await SignInAsync(factory, "alice");
        var bob = await SignInAsync(factory, "bob");
        var bobSession = (await SessionIdsAsync(factory, "bob")).Single();

        var response = await Forms.PostAsync(alice, "/account/sessions?handler=Revoke", new() { ["SessionId"] = bobSession });
        var unknown = await Forms.PostAsync(alice, "/account/sessions?handler=Revoke", new() { ["SessionId"] = "does-not-exist" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            NormalizeMessage(await unknown.Content.ReadAsStringAsync(CancellationToken)),
            NormalizeMessage(await response.Content.ReadAsStringAsync(CancellationToken)));
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/account", CancellationToken)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .AnyAsync(e => e.EventType == "session.revoked", CancellationToken));
    }

    // Helpers

    private static async Task CreateUserAsync(ValkyrieAppFactory factory, string userName)
    {
        var result = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", userName);
        Assert.True(result.ExitCode == 0, result.Error);
    }

    private static async Task<HttpClient> SignInAsync(ValkyrieAppFactory factory, string userName)
    {
        var browser = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(browser, userName, Password)).StatusCode);
        return browser;
    }

    private static async Task<HttpClient> SignInWithCodeAsync(ValkyrieAppFactory factory, TestTimeProvider clock, byte[] secret, string userName, string? code = null)
    {
        if (code is null)
        {
            clock.Advance(Totp.Step);
            code = CurrentCode(secret, clock);
        }

        var browser = await SignInAsync(factory, userName);
        var verified = await Forms.PostAsync(browser, "/account/login/2fa?returnUrl=%2Faccount", new() { ["Code"] = code });
        Assert.Equal(HttpStatusCode.Redirect, verified.StatusCode);
        return browser;
    }

    /// <summary>Signs in and turns on 2FA from the account page; returns the browser, which stays signed in.</summary>
    private static async Task<(byte[] Secret, IReadOnlyList<string> RecoveryCodes, HttpClient Browser)> EnrollAsync(
        ValkyrieAppFactory factory, TestTimeProvider clock, string userName)
    {
        var browser = await SignInAsync(factory, userName);
        var page = await (await browser.GetAsync("/account/two-factor", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
        var (state, secret) = ParseEnrollment(page);
        var response = await Forms.PostAsync(browser, "/account/two-factor", new() { ["State"] = state, ["Code"] = CurrentCode(secret, clock), ["Password"] = Password });
        var codes = RecoveryCodePattern().Matches(await response.Content.ReadAsStringAsync(CancellationToken)).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(codes);
        return (secret, codes, browser);
    }

    /// <summary>With 2FA required, enrolls during sign-in.</summary>
    private static async Task<(byte[] Secret, HttpClient Browser)> EnrollAtSignInAsync(ValkyrieAppFactory factory, TestTimeProvider clock, string userName)
    {
        var browser = factory.CreateBrowser();
        var login = await Forms.LoginAsync(browser, userName, Password);
        var enrollPath = login.Headers.Location?.OriginalString ?? string.Empty;
        var page = await (await browser.GetAsync(enrollPath, CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
        var (state, secret) = ParseEnrollment(page);
        var confirmed = await Forms.PostAsync(browser, enrollPath, new() { ["State"] = state, ["Code"] = CurrentCode(secret, clock) });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
        return (secret, browser);
    }

    private static (string State, byte[] Secret) ParseEnrollment(string html)
    {
        var state = StatePattern().Match(html);
        var secret = SecretPattern().Match(html);
        Assert.True(state.Success && secret.Success, html);
        return (state.Groups[1].Value, Base32.Decode(secret.Groups[1].Value));
    }

    private static Task<HttpResponseMessage> PostProfileAsync(HttpClient browser, string displayName, string givenName, string familyName) =>
        Forms.PostAsync(browser, "/account/profile", new() { ["DisplayName"] = displayName, ["GivenName"] = givenName, ["FamilyName"] = familyName });

    private static Task<HttpResponseMessage> PostPasswordAsync(HttpClient browser, string current, string next, string? confirm = null) =>
        Forms.PostAsync(browser, "/account/password", new() { ["CurrentPassword"] = current, ["NewPassword"] = next, ["ConfirmPassword"] = confirm ?? next });

    private static Task<HttpResponseMessage> PostDisableAsync(HttpClient browser, string password, string code) =>
        Forms.PostAsync(browser, "/account/two-factor?handler=Disable", new() { ["Password"] = password, ["Code"] = code });

    private static string CurrentCode(byte[] secret, TimeProvider clock) => Totp.Compute(secret, Totp.TimeStep(clock.GetUtcNow()));

    private static async Task<bool> TwoFactorEnabledAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users.AnyAsync(u => u.TwoFactorEnabled, CancellationToken);
    }

    private static async Task<int> UnusedRecoveryCodesAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().RecoveryCodes.CountAsync(c => c.UsedAt == null, CancellationToken);
    }

    private static async Task<List<string>> SessionIdsAsync(ValkyrieAppFactory factory, string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var userId = await db.Users.Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync(CancellationToken);
        return await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null).Select(s => s.Id).ToListAsync(CancellationToken);
    }

    /// <summary>The ID of a session by sign-in order, newest first.</summary>
    private static async Task<string> LatestSessionIdAsync(ValkyrieAppFactory factory, int skip)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var sessions = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().UserSessions.AsNoTracking().ToListAsync(CancellationToken);
        return sessions.OrderByDescending(s => s.AuthenticatedAt).ThenByDescending(s => s.Id, StringComparer.Ordinal).Skip(skip).First().Id;
    }

    private static string NormalizeMessage(string html) => TokenPattern().Replace(html, "TOKEN");

    [GeneratedRegex("name=\"State\" value=\"([^\"]+)\"")]
    private static partial Regex StatePattern();

    [GeneratedRegex("id=\"totp-secret\">([A-Z2-7 ]+)</code>")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("<li><code>([A-Z0-9-]{19})</code></li>")]
    private static partial Regex RecoveryCodePattern();

    [GeneratedRegex("name=\"SessionId\" value=\"([^\"]+)\"")]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"[^\"]+\"")]
    private static partial Regex TokenPattern();
}
