using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Pages.Account;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Setup wizard, login, sessions, lockout, rate limiting, and user CLI tests run against every database provider.</summary>
public abstract class AccountTests
{
    private const string Password = "correct horse battery staple";

    protected abstract ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Setup wizard

    [Fact]
    public async Task Setup_creates_the_first_admin_signs_in_and_then_disappears()
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        var response = await Forms.PostAsync(browser, "/setup", new()
        {
            ["UserName"] = "root",
            ["Email"] = "root@example.test",
            ["Password"] = Password,
            ["ConfirmPassword"] = Password,
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/setup", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync("/setup", CancellationToken)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var user = await db.Users.Include(u => u.Roles).SingleAsync(CancellationToken);
        Assert.Equal("root", user.UserName);
        Assert.Contains(user.Roles, r => r.Name == Role.Admin);
        Assert.StartsWith("$argon2id$v=19$", user.PasswordHash, StringComparison.Ordinal);
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "setup.completed" && e.SubjectId == user.Id, CancellationToken));
    }

    [Fact]
    public async Task Setup_rejects_weak_and_mismatched_passwords()
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        var weak = await Forms.PostAsync(browser, "/setup", new() { ["UserName"] = "root", ["Password"] = "short", ["ConfirmPassword"] = "short" });
        var mismatched = await Forms.PostAsync(browser, "/setup", new() { ["UserName"] = "root", ["Password"] = Password, ["ConfirmPassword"] = Password + "x" });

        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
        Assert.Contains("at least 8 characters", await weak.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Contains("don&#x27;t match", await mismatched.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.False(await AnyUsersAsync(factory));
    }

    [Fact]
    public async Task Setup_is_not_available_once_a_user_exists()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");

        var browser = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/setup", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_setup_requests_create_only_one_admin()
    {
        await using var factory = CreateFactory();
        _ = factory.Server;

        async Task<User?> SetupAsync(string userName)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<UserAdministration>()
                .CompleteSetupAsync(new NewUser(userName, null, null, Password), new AuditActor("setup"), CancellationToken);
        }

        var results = await Task.WhenAll(SetupAsync("first"), SetupAsync("second"));

        Assert.Single(results, r => r is not null);
        await using var check = factory.Services.CreateAsyncScope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users.CountAsync(CancellationToken));
    }

    // Login

    [Theory]
    [InlineData("alice")]
    [InlineData("ALICE")]
    [InlineData("alice@example.test")]
    [InlineData("Alice@Example.Test")]
    public async Task Users_log_in_with_username_or_email(string identifier)
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice", "alice@example.test");
        var browser = factory.CreateBrowser();

        var response = await Forms.LoginAsync(browser, identifier, Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location?.OriginalString);
        var account = await browser.GetAsync("/account", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        Assert.Contains("alice", await account.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_cookie_is_secure_http_only_and_same_site_lax()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");

        var response = await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-valkyrie.session=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_response()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");

        var wrongPassword = await Forms.LoginAsync(factory.CreateBrowser(), "alice", "wrong password!");
        var unknownUser = await Forms.LoginAsync(factory.CreateBrowser(), "mallory", "wrong password!");
        var unknownEmail = await Forms.LoginAsync(factory.CreateBrowser(), "mallory@example.test", "wrong password!");

        Assert.Equal(HttpStatusCode.OK, wrongPassword.StatusCode);
        Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);
        Assert.Equal(wrongPassword.StatusCode, unknownEmail.StatusCode);
        var expected = await Forms.NormalizedBodyAsync(wrongPassword);
        Assert.Contains(LoginModel.InvalidCredentials, expected, StringComparison.Ordinal);
        Assert.Equal(expected, await Forms.NormalizedBodyAsync(unknownUser));
        Assert.Equal(expected, await Forms.NormalizedBodyAsync(unknownEmail));
        Assert.DoesNotContain("wrong password!", expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_users_cannot_log_in()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        Assert.Equal(0, (await factory.RunCliAsync("user", "disable", "alice")).ExitCode);

        var response = await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(LoginModel.InvalidCredentials, await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_and_unlocks_after_the_duration()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock, new() { ["Lockout:MaxFailedAttempts"] = "3", ["Lockout:Duration"] = "00:15:00" });
        await CreateUserAsync(factory, "alice");

        for (var i = 0; i < 3; i++)
        {
            await Forms.LoginAsync(factory.CreateBrowser(), "alice", "wrong password!");
        }

        // Locked: even the right password fails, with the same generic message.
        var locked = await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Contains(LoginModel.InvalidCredentials, await locked.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.locked_out", CancellationToken));
        Assert.Equal(0, (await db.Users.SingleAsync(CancellationToken)).AccessFailedCount);
    }

    [Fact]
    public async Task Cli_unlock_clears_a_lockout()
    {
        await using var factory = CreateFactory(settings: new() { ["Lockout:MaxFailedAttempts"] = "1" });
        await CreateUserAsync(factory, "alice");
        await Forms.LoginAsync(factory.CreateBrowser(), "alice", "wrong password!");
        Assert.Equal(HttpStatusCode.OK, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);

        Assert.Equal(0, (await factory.RunCliAsync("admin", "unlock", "alice")).ExitCode);

        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example/", "/account")]
    [InlineData("//evil.example/", "/account")]
    [InlineData("/\\evil.example/", "/account")]
    [InlineData("javascript:alert(1)", "/account")]
    [InlineData("/account?tab=1", "/account?tab=1")]
    public async Task Return_url_must_be_local(string returnUrl, string expected)
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");

        var response = await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_without_an_anti_forgery_token_is_rejected()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();
        await browser.GetAsync("/account/login", CancellationToken);

        var response = await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Identifier"] = "alice",
            ["Password"] = Password,
        }), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Account_page_requires_a_session()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateBrowser().GetAsync("/account", CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/account/login?returnUrl=", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_pages_send_security_headers()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateBrowser().GetAsync("/account/login", CancellationToken);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    // Branding

    [Fact]
    public async Task Branding_sets_the_product_name_and_theme_colors()
    {
        await using var factory = CreateFactory(settings: new() { ["Branding:ProductName"] = "Acme ID", ["Branding:PrimaryColor"] = "#ff0000" });
        var browser = factory.CreateBrowser();

        var page = await (await browser.GetAsync("/account/login", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
        var theme = await browser.GetAsync("/theme.css", CancellationToken);
        var site = await browser.GetAsync("/css/site.css", CancellationToken);

        Assert.Contains("<title>Sign in – Acme ID</title>", page, StringComparison.Ordinal);
        Assert.Equal("text/css", theme.Content.Headers.ContentType?.MediaType);
        Assert.Contains("--vid-primary: #ff0000;", await theme.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, site.StatusCode);
    }

    [Theory]
    [InlineData("Branding:PrimaryColor", "red; } body { display: none")]
    [InlineData("Branding:TextColor", "url(https://evil.example/x)")]
    [InlineData("Branding:Logo", "../appsettings.json")]
    public async Task Unsafe_branding_values_fail_startup(string key, string value)
    {
        await using var factory = CreateFactory(settings: new() { [key] = value, ["Branding:Directory"] = Path.GetTempPath() });

        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
    }

    // Rate limiting

    [Fact]
    public async Task Login_submissions_are_rate_limited_per_ip()
    {
        await using var factory = CreateFactory(settings: new() { ["RateLimits:Login:PermitLimit"] = "2" });

        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "a", "x");
        await Forms.LoginAsync(browser, "b", "x");
        var limited = await Forms.LoginAsync(browser, "c", "x");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        // Page views don't count.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account/login", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Login_attempts_are_rate_limited_per_account_even_for_unknown_users()
    {
        await using var factory = CreateFactory(settings: new() { ["RateLimits:LoginAccount:PermitLimit"] = "2" });
        await CreateUserAsync(factory, "alice");

        foreach (var identifier in new[] { "alice", "nobody" })
        {
            await Forms.LoginAsync(factory.CreateBrowser(), identifier, "wrong password!");
            await Forms.LoginAsync(factory.CreateBrowser(), identifier.ToUpperInvariant(), "wrong password!");
            var limited = await Forms.LoginAsync(factory.CreateBrowser(), identifier, Password);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Contains(LoginModel.TooManyAttempts, await limited.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Token_endpoint_is_rate_limited_per_ip()
    {
        await using var factory = CreateFactory(settings: new() { ["RateLimits:Token:PermitLimit"] = "1" });
        var client = factory.CreateClient();

        var first = await client.PostAsync("/connect/token", new FormUrlEncodedContent([]), CancellationToken);
        var second = await client.PostAsync("/connect/token", new FormUrlEncodedContent([]), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    // Sessions

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var response = await Forms.PostAsync(browser, "/account/logout", []);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.NotNull((await db.UserSessions.SingleAsync(CancellationToken)).RevokedAt);
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.logout", CancellationToken));
    }

    [Fact]
    public async Task A_copied_cookie_stops_working_once_its_session_is_revoked()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();
        var login = await Forms.LoginAsync(browser, "alice", Password);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-valkyrie.session=", StringComparison.Ordinal)).Split(';')[0];

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            await db.UserSessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), CancellationToken);
        }

        var replay = new HttpRequestMessage(HttpMethod.Get, "/account");
        replay.Headers.Add("Cookie", cookie);
        var response = await factory.CreateBrowser().SendAsync(replay, CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Sessions_expire_after_the_configured_lifetime()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock, new() { ["Session:Lifetime"] = "01:00:00" });
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account", CancellationToken)).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Password_reset_and_disabling_sign_out_existing_sessions()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateUserAsync(factory, "bob");
        var alice = factory.CreateBrowser();
        var bob = factory.CreateBrowser();
        await Forms.LoginAsync(alice, "alice", Password);
        await Forms.LoginAsync(bob, "bob", Password);

        Assert.Equal(0, (await factory.RunCliWithInputAsync("a brand new password\n", "admin", "reset-password", "alice")).ExitCode);
        Assert.Equal(0, (await factory.RunCliAsync("user", "disable", "bob")).ExitCode);

        Assert.Equal(HttpStatusCode.Redirect, (await alice.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await bob.GetAsync("/account", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", "a brand new password")).StatusCode);
    }

    // CLI

    [Fact]
    public async Task Cli_creates_and_lists_users_without_taking_passwords_as_arguments()
    {
        await using var factory = CreateFactory();

        var created = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", "alice", "--email", "alice@example.test", "--name", "Alice");
        var list = await factory.RunCliAsync("user", "list");
        var withPasswordOption = await factory.RunCliAsync("user", "create", "bob", "--password", Password);

        Assert.Equal(0, created.ExitCode);
        Assert.Contains("alice  enabled  alice@example.test", list.Output, StringComparison.Ordinal);
        Assert.Equal(1, withPasswordOption.ExitCode);
        Assert.Contains("Unknown option --password", withPasswordOption.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_rejects_invalid_and_duplicate_users()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice", "alice@example.test");

        var duplicate = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", "ALICE");
        var duplicateEmail = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", "alice2", "--email", "ALICE@example.test");
        var atSign = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", "bob@example.test");
        var weak = await factory.RunCliWithInputAsync("short\n", "user", "create", "bob");
        var sameAsName = await factory.RunCliWithInputAsync("carol-the-admin\n", "user", "create", "carol-the-admin");

        Assert.Contains("already taken", duplicate.Error, StringComparison.Ordinal);
        Assert.Contains("already in use", duplicateEmail.Error, StringComparison.Ordinal);
        Assert.Contains("username must be", atSign.Error, StringComparison.Ordinal);
        Assert.Contains("at least 8 characters", weak.Error, StringComparison.Ordinal);
        Assert.Contains("must not be your username", sameAsName.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_enabled_admin_cannot_be_disabled_or_demoted()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "root");
        await CreateUserAsync(factory, "second");
        Assert.Equal(0, (await factory.RunCliAsync("admin", "grant-admin", "root")).ExitCode);

        var disable = await factory.RunCliAsync("user", "disable", "root");
        var demote = await factory.RunCliAsync("admin", "revoke-admin", "root");
        Assert.Equal(1, disable.ExitCode);
        Assert.Contains("last enabled admin", disable.Error, StringComparison.Ordinal);
        Assert.Equal(1, demote.ExitCode);

        // With another enabled admin, it's allowed.
        Assert.Equal(0, (await factory.RunCliAsync("admin", "grant-admin", "second")).ExitCode);
        Assert.Equal(0, (await factory.RunCliAsync("admin", "revoke-admin", "root")).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("user", "disable", "second")).ExitCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.Equal(3, await db.AuditLog.CountAsync(e => e.EventType.StartsWith("user.role_") && e.Actor == "cli", CancellationToken));
    }

    // Password hashing

    [Fact]
    public async Task Configured_hashing_parameters_are_used_and_old_hashes_upgraded_at_login()
    {
        await using var factory = CreateFactory(settings: new() { ["PasswordHashing:MemorySizeKiB"] = "19456", ["PasswordHashing:Iterations"] = "2" });
        await CreateUserAsync(factory, "alice");
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", await PasswordHashAsync(factory), StringComparison.Ordinal);

        // A hash from an earlier configuration still verifies and is replaced with the current parameters.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var oldHash = new Core.Security.PasswordHasher(new Core.Security.Argon2Parameters(12288, 3, 1)).Hash(Password);
            await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, oldHash), CancellationToken);
        }

        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", await PasswordHashAsync(factory), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8192", "3", "1")]
    [InlineData("47103", "1", "1")]
    [InlineData("12288", "2", "1")]
    [InlineData("12288", "3", "0")]
    [InlineData("2097152", "1", "1")]
    public async Task Hashing_parameters_below_owasp_minimums_fail_startup(string memory, string iterations, string parallelism)
    {
        await using var factory = CreateFactory(settings: new()
        {
            ["PasswordHashing:MemorySizeKiB"] = memory,
            ["PasswordHashing:Iterations"] = iterations,
            ["PasswordHashing:Parallelism"] = parallelism,
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains("PasswordHashing:", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Breached_password_check_is_off_by_default()
    {
        await using var factory = CreateFactory();

        var result = await factory.RunCliWithInputAsync("password\n", "user", "create", "alice");

        Assert.Equal(0, result.ExitCode);
    }

    private static async Task CreateUserAsync(ValkyrieAppFactory factory, string userName, string? email = null)
    {
        string[] args = email is null ? ["user", "create", userName] : ["user", "create", userName, "--email", email];
        var result = await factory.RunCliWithInputAsync(Password + "\n", args);
        Assert.True(result.ExitCode == 0, result.Error);
    }

    private static async Task<string?> PasswordHashAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users.Select(u => u.PasswordHash).SingleAsync(CancellationToken);
    }

    private static async Task<bool> AnyUsersAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users.AnyAsync(CancellationToken);
    }
}
