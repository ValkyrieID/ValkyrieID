using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;
using ValkyrieID.Server.Pages.Account;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>TOTP 2FA, recovery codes, enforcement, and the related CLI commands, run against every database provider.</summary>
public abstract partial class TwoFactorTests
{
    private const string Password = "correct horse battery staple";

    protected abstract ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Enrollment from the account page

    [Fact]
    public async Task Enabled_2fa_requires_a_code_before_the_session_is_created()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (secret, recoveryCodes) = await EnrollAsync(factory, clock, "alice");
        Assert.Equal(RecoveryCodes.Count, recoveryCodes.Count);

        var browser = factory.CreateBrowser();
        var login = await Forms.LoginAsync(browser, "alice", Password);

        Assert.Equal(HttpStatusCode.SeeOther, login.StatusCode);
        Assert.Equal("/account/login/2fa?returnUrl=%2Faccount", login.Headers.Location?.OriginalString);
        Assert.DoesNotContain(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-valkyrie.session=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account", CancellationToken)).StatusCode);

        clock.Advance(Totp.Step);
        var verify = await PostCodeAsync(browser, CurrentCode(secret, clock));

        Assert.Equal(HttpStatusCode.SeeOther, verify.StatusCode);
        Assert.Equal("/account", verify.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(["pwd", "otp", "mfa"], await LatestSessionMethodsAsync(factory));
    }

    [Fact]
    public async Task Enrollment_requires_the_current_password_and_a_valid_code()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (state, secret) = await StartEnrollmentAsync(browser);

        var wrongPassword = await PostEnrollmentAsync(browser, state, CurrentCode(secret, clock), "wrong password!");
        var wrongCode = await PostEnrollmentAsync(browser, state, WrongCode(secret, clock), Password);
        var forgedState = await PostEnrollmentAsync(browser, state[..^4] + "AAAA", CurrentCode(secret, clock), Password);

        Assert.Contains("password is incorrect", await wrongPassword.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("code is invalid", await wrongCode.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("code is invalid", await forgedState.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.False(await TwoFactorEnabledAsync(factory));

        clock.Advance(TimeSpan.FromMinutes(16));
        var expired = await PostEnrollmentAsync(browser, state, CurrentCode(secret, clock), Password);
        Assert.Contains("code is invalid", await expired.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.False(await TwoFactorEnabledAsync(factory));
    }

    [Fact]
    public async Task Totp_secret_is_encrypted_and_recovery_codes_are_hashed_at_rest()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (secret, recoveryCodes) = await EnrollAsync(factory, clock, "alice");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var user = await db.Users.SingleAsync(CancellationToken);
        var stored = await db.RecoveryCodes.Select(c => c.CodeHash).ToListAsync(CancellationToken);

        Assert.NotNull(user.TotpSecret);
        Assert.False(user.TotpSecret.AsSpan().IndexOf(secret) >= 0);
        Assert.Equal(secret, scope.ServiceProvider.GetRequiredService<SecretProtector>().Unprotect(user.TotpSecret, "ValkyrieID.TotpSecret"));
        Assert.Equal(recoveryCodes.Select(c => Convert.ToHexString(RecoveryCodes.Hash(c)!)).Order(), stored.Select(Convert.ToHexString).Order());
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.2fa.enabled" && e.SubjectId == user.Id, CancellationToken));
    }

    // Attacks on the second step

    [Fact]
    public async Task A_code_can_only_be_used_once()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (secret, _) = await EnrollAsync(factory, clock, "alice");
        clock.Advance(Totp.Step);
        var code = CurrentCode(secret, clock);

        var first = factory.CreateBrowser();
        await Forms.LoginAsync(first, "alice", Password);
        Assert.Equal(HttpStatusCode.SeeOther, (await PostCodeAsync(first, code)).StatusCode);

        var second = factory.CreateBrowser();
        await Forms.LoginAsync(second, "alice", Password);
        var replay = await PostCodeAsync(second, code);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Contains("already been used", await replay.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);

        // A code from an earlier step within the drift window is also rejected once a later one was used.
        var earlier = await PostCodeAsync(second, Totp.Compute(secret, Totp.TimeStep(clock.GetUtcNow()) - 1));
        Assert.Equal(HttpStatusCode.OK, earlier.StatusCode);
    }

    [Fact]
    public async Task Codes_are_accepted_one_step_early_or_late_but_not_two()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (secret, _) = await EnrollAsync(factory, clock, "alice");
        clock.Advance(TimeSpan.FromMinutes(5));
        var step = Totp.TimeStep(clock.GetUtcNow());

        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(browser, Totp.Compute(secret, step - 2))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(browser, Totp.Compute(secret, step + 2))).StatusCode);
        Assert.Equal(HttpStatusCode.SeeOther, (await PostCodeAsync(browser, Totp.Compute(secret, step - 1))).StatusCode);

        var late = factory.CreateBrowser();
        await Forms.LoginAsync(late, "alice", Password);
        Assert.Equal(HttpStatusCode.SeeOther, (await PostCodeAsync(late, Totp.Compute(secret, step + 1))).StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_lock_the_account_even_across_new_password_logins()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (secret, _) = await EnrollAsync(factory, clock, "alice");

        // Re-entering the (known) password must not reset the failure count between code guesses.
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(browser, WrongCode(secret, clock))).StatusCode);
        }

        await Forms.LoginAsync(browser, "alice", Password);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(browser, WrongCode(secret, clock))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(browser, WrongCode(secret, clock))).StatusCode);

        // Locked out: the pending login is no longer valid, and the password alone is rejected too.
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account/login/2fa", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.locked_out", CancellationToken));
    }

    [Fact]
    public async Task The_second_step_cannot_be_reached_without_the_password_or_after_it_expires()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        await EnrollAsync(factory, clock, "alice");

        Assert.Equal(HttpStatusCode.Redirect, (await factory.CreateBrowser().GetAsync("/account/login/2fa", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await factory.CreateBrowser().GetAsync("/account/login/recovery", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await factory.CreateBrowser().GetAsync("/account/login/enroll", CancellationToken)).StatusCode);

        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account/login/2fa", CancellationToken)).StatusCode);

        // A pending 2FA login is not an enrollment, and vice versa.
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account/login/enroll", CancellationToken)).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account/login/2fa", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Pending_logins_are_invalidated_when_the_account_changes()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        await EnrollAsync(factory, clock, "alice");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        // A security stamp change (here: an admin resetting 2FA) invalidates the pending login.
        Assert.Equal(0, (await factory.RunCliAsync("admin", "reset-2fa", "alice")).ExitCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account/login/2fa", CancellationToken)).StatusCode);

        await EnrollAsync(factory, clock, "alice");
        var other = factory.CreateBrowser();
        await Forms.LoginAsync(other, "alice", Password);
        Assert.Equal(0, (await factory.RunCliAsync("user", "disable", "alice")).ExitCode);
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/account/login/2fa", CancellationToken)).StatusCode);
    }

    // Recovery codes

    [Fact]
    public async Task Recovery_codes_work_once()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        var (_, recoveryCodes) = await EnrollAsync(factory, clock, "alice");
        clock.Advance(TimeSpan.FromSeconds(1));

        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var used = await Forms.PostAsync(browser, "/account/login/recovery?returnUrl=%2Faccount", new() { ["Code"] = recoveryCodes[0].ToLowerInvariant() });

        Assert.Equal(HttpStatusCode.SeeOther, used.StatusCode);
        Assert.Equal(["pwd", "mfa"], await LatestSessionMethodsAsync(factory));

        var again = factory.CreateBrowser();
        await Forms.LoginAsync(again, "alice", Password);
        var reused = await Forms.PostAsync(again, "/account/login/recovery", new() { ["Code"] = recoveryCodes[0] });
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        Assert.Contains(RecoveryCodeModel.InvalidCode.Replace("'", "&#x27;", StringComparison.Ordinal), await reused.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.PostAsync(again, "/account/login/recovery", new() { ["Code"] = recoveryCodes[1] })).StatusCode);
    }

    // Enforcement

    [Fact]
    public async Task Required_2fa_makes_users_enroll_before_they_get_a_session()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock, new() { ["TwoFactor:Required"] = "true" });
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();

        var login = await Forms.LoginAsync(browser, "alice", Password, "/connect/authorize?client_id=x");

        Assert.Equal(HttpStatusCode.SeeOther, login.StatusCode);
        Assert.StartsWith("/account/login/enroll?returnUrl=", login.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account", CancellationToken)).StatusCode);

        var enrollPath = login.Headers.Location?.OriginalString ?? string.Empty;
        var page = await (await browser.GetAsync(enrollPath, CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
        var (state, secret) = ParseEnrollment(page);
        var token = await Forms.GetTokenAsync(browser, enrollPath);
        var confirmed = await browser.PostAsync(enrollPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["State"] = state,
            ["Code"] = CurrentCode(secret, clock),
            ["__RequestVerificationToken"] = token,
        }), CancellationToken);
        var body = await confirmed.Content.ReadAsStringAsync(CancellationToken);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(RecoveryCodes.Count, RecoveryCodePattern().Matches(body).Count);
        Assert.Contains("href=\"/connect/authorize?client_id=x\"", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.True(await TwoFactorEnabledAsync(factory));
    }

    [Fact]
    public async Task Users_without_2fa_sign_in_with_a_password_when_it_is_not_required()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");

        var login = await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password);

        Assert.Equal("/account", login.Headers.Location?.OriginalString);
        Assert.Equal(["pwd"], await LatestSessionMethodsAsync(factory));
    }

    [Fact]
    public async Task Cli_requires_2fa_per_user_and_resets_it()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        await CreateUserAsync(factory, "bob");

        Assert.Equal(0, (await factory.RunCliAsync("user", "require-2fa", "alice")).ExitCode);
        Assert.StartsWith("/account/login/enroll", (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal("/account", (await Forms.LoginAsync(factory.CreateBrowser(), "bob", Password)).Headers.Location?.OriginalString);
        Assert.Contains("2fa: required", (await factory.RunCliAsync("user", "list")).Output, StringComparison.Ordinal);

        Assert.Equal(0, (await factory.RunCliAsync("user", "unrequire-2fa", "alice")).ExitCode);
        await EnrollAsync(factory, clock, "bob");

        var reset = await factory.RunCliAsync("admin", "reset-2fa", "bob");

        Assert.Equal(0, reset.ExitCode);
        Assert.Equal("/account", (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).Headers.Location?.OriginalString);
        Assert.Equal("/account", (await Forms.LoginAsync(factory.CreateBrowser(), "bob", Password)).Headers.Location?.OriginalString);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var bob = await db.Users.SingleAsync(u => u.UserName == "bob", CancellationToken);
        Assert.False(bob.TwoFactorEnabled);
        Assert.Null(bob.TotpSecret);
        Assert.False(await db.RecoveryCodes.AnyAsync(CancellationToken));
        // Sessions from before the reset are signed out; only the one created after it is active.
        Assert.Equal(1, await db.UserSessions.CountAsync(s => s.UserId == bob.Id && s.RevokedAt == null, CancellationToken));
        Assert.Equal(2, await db.UserSessions.CountAsync(s => s.UserId == bob.Id && s.RevokedAt != null, CancellationToken));
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.2fa.reset" && e.Actor == "cli", CancellationToken));
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.2fa.required", CancellationToken));
    }

    // Helpers

    private static async Task CreateUserAsync(ValkyrieAppFactory factory, string userName)
    {
        var result = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", userName);
        Assert.True(result.ExitCode == 0, result.Error);
    }

    /// <summary>Signs in with the password and turns on 2FA from the account page.</summary>
    private static async Task<(byte[] Secret, IReadOnlyList<string> RecoveryCodes)> EnrollAsync(ValkyrieAppFactory factory, TestTimeProvider clock, string userName)
    {
        var browser = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(browser, userName, Password)).StatusCode);
        var (state, secret) = await StartEnrollmentAsync(browser);

        var response = await PostEnrollmentAsync(browser, state, CurrentCode(secret, clock), Password);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        var codes = RecoveryCodePattern().Matches(body).Select(m => m.Groups[1].Value).ToList();
        Assert.True(codes.Count > 0, body);
        return (secret, codes);
    }

    private static async Task<(string State, byte[] Secret)> StartEnrollmentAsync(HttpClient browser)
    {
        var page = await browser.GetAsync("/account/two-factor", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        return ParseEnrollment(await page.Content.ReadAsStringAsync(CancellationToken));
    }

    private static (string State, byte[] Secret) ParseEnrollment(string html)
    {
        var state = StatePattern().Match(html);
        var secret = SecretPattern().Match(html);
        Assert.True(state.Success && secret.Success, html);
        return (state.Groups[1].Value, Base32.Decode(secret.Groups[1].Value));
    }

    private static Task<HttpResponseMessage> PostEnrollmentAsync(HttpClient browser, string state, string code, string password) =>
        Forms.PostAsync(browser, "/account/two-factor", new() { ["State"] = state, ["Code"] = code, ["Password"] = password });

    private static Task<HttpResponseMessage> PostCodeAsync(HttpClient browser, string code) =>
        Forms.PostAsync(browser, "/account/login/2fa?returnUrl=%2Faccount", new() { ["Code"] = code });

    private static string CurrentCode(byte[] secret, TimeProvider clock) => Totp.Compute(secret, Totp.TimeStep(clock.GetUtcNow()));

    /// <summary>A code that's valid for no step in the drift window.</summary>
    private static string WrongCode(byte[] secret, TimeProvider clock)
    {
        var step = Totp.TimeStep(clock.GetUtcNow());
        var valid = new[] { step - 1, step, step + 1 }.Select(s => Totp.Compute(secret, s)).ToHashSet();
        return Enumerable.Range(0, 10).Select(i => $"{i}{i}{i}{i}{i}{i}").First(c => !valid.Contains(c));
    }

    private static async Task<bool> TwoFactorEnabledAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users.AnyAsync(u => u.TwoFactorEnabled, CancellationToken);
    }

    private static async Task<List<string>> LatestSessionMethodsAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var sessions = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().UserSessions.AsNoTracking().ToListAsync(CancellationToken);
        return [.. sessions.OrderBy(s => s.AuthenticatedAt).ThenBy(s => s.Id, StringComparer.Ordinal).Last().AuthenticationMethods];
    }

    [GeneratedRegex("name=\"State\" value=\"([^\"]+)\"")]
    private static partial Regex StatePattern();

    [GeneratedRegex("id=\"totp-secret\">([A-Z2-7 ]+)</code>")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("<li><code>([A-Z0-9-]{19})</code></li>")]
    private static partial Regex RecoveryCodePattern();
}
