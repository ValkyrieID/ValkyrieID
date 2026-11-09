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

    private static Dictionary<string, string?> SmtpSettings => new() { ["Smtp:Host"] = "smtp.example.test", ["Smtp:FromAddress"] = "id@example.test" };

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

        // The first user is an admin, and admins must use 2FA by default: the session starts after enrollment.
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/account/login/enroll?returnUrl=%2Faccount", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
        await Forms.CompleteEnrollmentAsync(browser);
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

        // Enough concurrent requests that PostgreSQL reports serialization failures on most runs.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => SetupAsync($"admin{i}")));

        Assert.Single(results, r => r is not null);
        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(CancellationToken));
        Assert.Equal(1, await db.AuditLog.CountAsync(e => e.EventType == "setup.completed", CancellationToken));
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

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
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
    public async Task Sign_in_errors_get_the_focus_and_are_linked_to_the_fields()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");

        var response = await Forms.LoginAsync(factory.CreateBrowser(), "alice", "wrong password!");
        var page = await response.Content.ReadAsStringAsync(CancellationToken);

        // WCAG 3.3.1 and 4.1.3, without JavaScript: the summary is announced and focused on load.
        Assert.Contains("""<div class="alert" role="alert" id="errors" tabindex="-1" autofocus>""", page, StringComparison.Ordinal);
        Assert.Contains("""aria-invalid="true" aria-describedby="errors">""", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_fields_work_with_password_managers()
    {
        await using var factory = CreateFactory();
        var page = await (await factory.CreateBrowser().GetAsync("/account/login", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);

        // WCAG 3.3.8: no cognitive test; password managers can fill the fields, and pasting isn't blocked.
        Assert.Contains("""autocomplete="username""", page, StringComparison.Ordinal);
        Assert.Contains("""autocomplete="current-password""", page, StringComparison.Ordinal);
        Assert.DoesNotContain("onpaste", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("captcha", page, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);

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

        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);
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

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
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
    [InlineData("Branding:DarkPrimaryColor", "red; } body { display: none")]
    [InlineData("Branding:Logo", "../appsettings.json")]
    [InlineData("Branding:DarkBackgroundImage", "x\"); } body { display: none")]
    [InlineData("Branding:DarkBackgroundImage", "../secret.png")]
    public async Task Unsafe_branding_values_fail_startup(string key, string value)
    {
        await using var factory = CreateFactory(settings: new() { [key] = value, ["Branding:Directory"] = Path.GetTempPath() });

        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
    }

    // Themes

    [Fact]
    public async Task Pages_follow_the_system_theme_without_a_stored_choice()
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        var page = await (await browser.GetAsync("/account/login", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);

        Assert.Contains("<html lang=\"en\" data-theme=\"system\"", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/account/theme\"", page, StringComparison.Ordinal);
        Assert.Contains("<input type=\"hidden\" name=\"ReturnUrl\" value=\"/account/login\">", page, StringComparison.Ordinal);
        Assert.Contains("value=\"system\" aria-pressed=\"true\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"dark\" aria-pressed=\"false\"", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Choosing_a_theme_stores_it_and_renders_it_on_the_server(string theme)
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        var response = await Forms.PostAsync(browser, "/account/theme", "/account/login", new() { ["Theme"] = theme, ["ReturnUrl"] = "/account/login?returnUrl=%2Faccount" });

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/account/login?returnUrl=%2Faccount", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-valkyrie.theme=", StringComparison.Ordinal));
        Assert.StartsWith($"__Host-valkyrie.theme={theme};", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        // The admin UI reads it with document.cookie.
        Assert.DoesNotContain("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        var page = await (await browser.GetAsync("/account/login", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains($"<html lang=\"en\" data-theme=\"{theme}\"", page, StringComparison.Ordinal);
        Assert.Contains($"value=\"{theme}\" aria-pressed=\"true\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"system\" aria-pressed=\"false\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_the_system_theme_deletes_the_stored_choice()
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();
        await Forms.PostAsync(browser, "/account/theme", "/account/login", new() { ["Theme"] = "dark", ["ReturnUrl"] = "/account/login" });

        var response = await Forms.PostAsync(browser, "/account/theme", "/account/login", new() { ["Theme"] = "system", ["ReturnUrl"] = "/account/login" });

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-valkyrie.theme=;", StringComparison.Ordinal) && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        var page = await (await browser.GetAsync("/account/login", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("<html lang=\"en\" data-theme=\"system\"", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task Theme_form_only_redirects_to_local_urls(string returnUrl)
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        var response = await Forms.PostAsync(browser, "/account/theme", "/account/login", new() { ["Theme"] = "dark", ["ReturnUrl"] = returnUrl });

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("Dark")]
    [InlineData("")]
    public async Task Theme_form_rejects_unknown_themes(string theme)
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        var response = await Forms.PostAsync(browser, "/account/theme", "/account/login", new() { ["Theme"] = theme, ["ReturnUrl"] = "/account/login" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("__Host-valkyrie.theme", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Theme_form_requires_the_anti_forgery_token()
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        var response = await browser.PostAsync("/account/theme", new FormUrlEncodedContent(new Dictionary<string, string> { ["Theme"] = "dark", ["ReturnUrl"] = "/account" }), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie") && response.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("__Host-valkyrie.theme", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/account/theme", CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("\"><script>alert(1)</script>")]
    [InlineData("system")]
    [InlineData("DARK")]
    public async Task Unknown_theme_cookie_values_follow_the_system(string value)
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account/login");
        request.Headers.Add("Cookie", $"__Host-valkyrie.theme={Uri.EscapeDataString(value)}");

        var page = await (await client.SendAsync(request, CancellationToken)).Content.ReadAsStringAsync(CancellationToken);

        Assert.Contains("<html lang=\"en\" data-theme=\"system\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Protocol_pages_render_the_stored_theme()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/connect/authorize?client_id=unknown");
        request.Headers.Add("Cookie", "__Host-valkyrie.theme=dark");

        var response = await client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("<html lang=\"en\" data-theme=\"dark\">", await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dark_branding_colors_apply_only_to_the_dark_theme()
    {
        await using var factory = CreateFactory(settings: new() { ["Branding:PrimaryColor"] = "#ff0000", ["Branding:DarkPrimaryColor"] = "#00ff00", ["Branding:DarkTextColor"] = "#eeeeee" });
        var browser = factory.CreateBrowser();

        var css = await (await browser.GetAsync("/theme.css", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);

        Assert.StartsWith(":root {\n  --vid-primary: #ff0000;\n}\n", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme: dark) {\n:root:not([data-theme=\"light\"]) {\n  --vid-primary: #00ff00;\n  --vid-text: #eeeeee;\n}\n}\n", css, StringComparison.Ordinal);
        Assert.Contains(":root[data-theme=\"dark\"] {\n  --vid-primary: #00ff00;\n  --vid-text: #eeeeee;\n}\n", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_dark_branding_colors_the_stylesheet_has_no_dark_rules()
    {
        await using var factory = CreateFactory(settings: new() { ["Branding:PrimaryColor"] = "#ff0000" });

        var css = await (await factory.CreateBrowser().GetAsync("/theme.css", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);

        Assert.DoesNotContain("data-theme", css, StringComparison.Ordinal);
    }

    // Layouts and motion (layout.md, Phase 11b)

    [Fact]
    public async Task Entry_animation_plays_only_on_fresh_gets_of_entry_pages()
    {
        await using var factory = CreateFactory(settings: SmtpSettings);
        var browser = factory.CreateBrowser();

        Assert.Contains("<html lang=\"en\" data-theme=\"system\" class=\"entry\">", await BodyAsync(browser, "/setup"), StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\" data-theme=\"system\" class=\"entry\">", await BodyAsync(browser, "/account/login"), StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\" data-theme=\"system\" class=\"entry\">", await BodyAsync(browser, "/account/forgot-password"), StringComparison.Ordinal);

        // Error re-renders after a POST don't replay it.
        var setupError = await Forms.PostAsync(browser, "/setup", new() { ["UserName"] = "root", ["Password"] = Password, ["ConfirmPassword"] = Password + "x" });
        Assert.Equal(HttpStatusCode.OK, setupError.StatusCode);
        Assert.Contains("<html lang=\"en\" data-theme=\"system\">", await setupError.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);

        var setup = await Forms.PostAsync(browser, "/setup", new() { ["UserName"] = "root", ["Password"] = Password, ["ConfirmPassword"] = Password });
        Assert.Equal(HttpStatusCode.SeeOther, setup.StatusCode);
        // Later steps such as the 2FA enrollment never get it.
        Assert.DoesNotContain("class=\"entry\"", await BodyAsync(browser, "/account/login/enroll?returnUrl=%2Faccount"), StringComparison.Ordinal);
        await Forms.CompleteEnrollmentAsync(browser);
        Assert.DoesNotContain("class=\"entry\"", await BodyAsync(browser, "/account"), StringComparison.Ordinal);

        var other = factory.CreateBrowser();
        var loginError = await Forms.LoginAsync(other, "root", "wrong password!");
        Assert.Equal(HttpStatusCode.OK, loginError.StatusCode);
        Assert.DoesNotContain("class=\"entry\"", await loginError.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);

        var login = await Forms.LoginAsync(other, "root", Password, "/account");
        Assert.StartsWith("/account/login/2fa", login.Headers.Location?.OriginalString, StringComparison.Ordinal);
        var twoFactor = await BodyAsync(other, login.Headers.Location!.OriginalString);
        Assert.Contains("<body class=\"auth\">", twoFactor, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"entry\"", twoFactor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_layout_shows_the_brand_lockup_beside_the_card()
    {
        await using var factory = CreateFactory();

        var page = await BodyAsync(factory.CreateBrowser(), "/account/login");

        Assert.Contains("<body class=\"auth\">", page, StringComparison.Ordinal);
        Assert.Contains("<div class=\"auth-lockup\">", page, StringComparison.Ordinal);
        Assert.Contains("<main class=\"auth-card\">", page, StringComparison.Ordinal);
        // The built-in name is the "Valkyrie id" wordmark, hidden from assistive technology, which reads "ValkyrieID".
        Assert.Contains("<span class=\"product-name\"><span class=\"visually-hidden\">ValkyrieID</span><span aria-hidden=\"true\">Valkyrie<span class=\"product-name-suffix\">id</span></span></span>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"art", page, StringComparison.Ordinal);
        // The theme switch is the first control of the page (WCAG 2.4.3).
        Assert.True(page.IndexOf("class=\"theme-switch\"", StringComparison.Ordinal) < page.IndexOf("<main", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_custom_product_name_replaces_the_wordmark()
    {
        await using var factory = CreateFactory(settings: new() { ["Branding:ProductName"] = "Acme <ID>" });

        var page = await BodyAsync(factory.CreateBrowser(), "/account/login");
        var error = await (await factory.CreateBrowser().GetAsync("/connect/authorize?client_id=unknown", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);

        Assert.Contains("<span class=\"product-name\">Acme &lt;ID&gt;</span>", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"product-name\">Acme &lt;ID&gt;</span>", error, StringComparison.Ordinal);
        Assert.DoesNotContain("product-name-suffix", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Built_in_wallpapers_are_served_and_used_without_a_branding_image()
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        var site = await BodyAsync(browser, "/css/site.css");
        var theme = await BodyAsync(browser, "/theme.css");

        Assert.Contains("--vid-background-image: url(\"../img/wall-light.png\");", site, StringComparison.Ordinal);
        Assert.Contains("--vid-background-image: url(\"../img/wall-dark.png\");", site, StringComparison.Ordinal);
        Assert.DoesNotContain("--vid-background-image", theme, StringComparison.Ordinal);
        foreach (var path in new[] { "/img/wall-light.png", "/img/wall-dark.png", "/img/logo-192.png" })
        {
            var response = await browser.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Theory]
    [InlineData("background.png", null, "/branding/background.png", "/branding/background.png")]
    [InlineData("background.png", "dark.png", "/branding/background.png", "/branding/dark.png")]
    [InlineData(null, "dark.png", null, "/branding/dark.png")]
    public async Task Branding_background_images_fill_the_page_in_each_theme(string? light, string? dark, string? expectedLight, string expectedDark)
    {
        var directory = Directory.CreateTempSubdirectory("valkyrieid-branding-").FullName;
        try
        {
            Dictionary<string, string?> settings = new() { ["Branding:Directory"] = directory };
            foreach (var (key, file) in new[] { ("Branding:BackgroundImage", light), ("Branding:DarkBackgroundImage", dark) })
            {
                if (file is not null)
                {
                    await File.WriteAllBytesAsync(Path.Combine(directory, file), [0x89, 0x50, 0x4e, 0x47], CancellationToken);
                    settings[key] = file;
                }
            }

            await using var factory = CreateFactory(settings: settings);
            var theme = await BodyAsync(factory.CreateBrowser(), "/theme.css");
            var lightRule = theme[..theme.IndexOf("}\n", StringComparison.Ordinal)];
            var darkRule = $"  --vid-background-image: url(\"{expectedDark}\");\n  --vid-glass: color-mix(in srgb, var(--vid-surface) 88%, transparent);\n";

            if (expectedLight is null)
            {
                // Without a light image, the light theme keeps the built-in wallpaper and its see-through glass.
                Assert.DoesNotContain("--vid-background-image", lightRule, StringComparison.Ordinal);
                Assert.DoesNotContain("--vid-glass", lightRule, StringComparison.Ordinal);
            }
            else
            {
                // Over a custom image the glass is mostly opaque, so text keeps its contrast whatever the image.
                Assert.Contains($"  --vid-background-image: url(\"{expectedLight}\");\n  --vid-glass: color-mix(in srgb, var(--vid-surface) 88%, transparent);\n", lightRule, StringComparison.Ordinal);
            }

            Assert.Contains($":root:not([data-theme=\"light\"]) {{\n{darkRule}}}", theme, StringComparison.Ordinal);
            Assert.Contains($":root[data-theme=\"dark\"] {{\n{darkRule}}}", theme, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Protocol_error_pages_use_the_sign_in_layout_without_a_theme_switch()
    {
        await using var factory = CreateFactory();

        var page = await (await factory.CreateBrowser().GetAsync("/connect/authorize?client_id=unknown", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);

        Assert.Contains("<body class=\"auth\">", page, StringComparison.Ordinal);
        Assert.Contains("<div class=\"auth-lockup\"><div class=\"brand\"><img src=\"/img/logo-192.png\" alt=\"\" width=\"32\" height=\"32\"><span class=\"product-name\"><span class=\"visually-hidden\">ValkyrieID</span><span aria-hidden=\"true\">Valkyrie<span class=\"product-name-suffix\">id</span></span></span></div></div>", page, StringComparison.Ordinal);
        Assert.Contains("<main class=\"auth-card\">", page, StringComparison.Ordinal);
        Assert.DoesNotContain("theme-switch", page, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"entry\"", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/account", "Overview")]
    [InlineData("/account/profile", "Profile")]
    [InlineData("/account/password", "Password")]
    [InlineData("/account/two-factor", "Two-factor")]
    [InlineData("/account/sessions", "Sessions")]
    public async Task Account_layout_marks_the_current_page_in_the_navigation(string path, string label)
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(browser, "alice", Password)).StatusCode);

        var page = await BodyAsync(browser, path);

        Assert.Contains("<body class=\"app\">", page, StringComparison.Ordinal);
        Assert.Contains("<a href=\"#content\" class=\"skip-link\">", page, StringComparison.Ordinal);
        Assert.Contains("<nav class=\"app-nav\" aria-label=\"Account\">", page, StringComparison.Ordinal);
        Assert.Contains($"aria-current=\"page\">{label}</a>", page, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "aria-current=\"page\""));
        Assert.Contains("<span class=\"visually-hidden\">Signed in as </span>alice", page, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"art", page, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"entry\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sessions_page_highlights_the_current_session()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var other = factory.CreateBrowser();
        await Forms.LoginAsync(other, "alice", Password);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var page = await BodyAsync(browser, "/account/sessions");

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "<li class=\"session").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<li class=\"session session-current\">"));
        Assert.Contains("<span class=\"badge\">This session</span>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sessions_page_shows_a_short_browser_name_with_the_full_user_agent_as_tooltip()
    {
        const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Safari/537.36 Edg/157.0.0.0";
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();
        browser.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        await Forms.LoginAsync(browser, "alice", Password);

        var page = await BodyAsync(browser, "/account/sessions");

        Assert.Matches($"<strong id=\"session-[^\"]+\" title=\"{System.Text.RegularExpressions.Regex.Escape(userAgent)}\">Edge on Windows</strong>", page);
    }

    private static async Task<string> BodyAsync(HttpClient browser, string path)
    {
        var response = await browser.GetAsync(path, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(CancellationToken);
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

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
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
        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", "a brand new password")).StatusCode);
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

        Assert.Equal(HttpStatusCode.SeeOther, (await Forms.LoginAsync(factory.CreateBrowser(), "alice", Password)).StatusCode);
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
