using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Auditing;
using ValkyrieID.Core.Settings;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Email;
using ValkyrieID.Server.Settings;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Runtime settings stored in the database (PROJECT.md §10), run against every database provider.</summary>
public abstract class SettingsTests
{
    private const string Password = "correct horse battery staple";
    private const string AdminResource = "valkyrieid-admin";
    private const string AdminScope = "valkyrieid.admin";
    private const string ClientRedirectUri = "https://app.example.test/cb";

    private static readonly Dictionary<string, string?> Smtp = new()
    {
        ["Smtp:Host"] = "smtp.example.test",
        ["Smtp:FromAddress"] = "id@example.test",
    };

    /// <param name="database">A database name, to start another server on the same database.</param>
    internal abstract ValkyrieAppFactory CreateFactory(
        TimeProvider? timeProvider = null,
        Dictionary<string, string?>? settings = null,
        TestEmailSender? sender = null,
        string? database = null,
        string? masterKey = null,
        string? previousMasterKey = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Reading

    [Fact]
    public async Task Settings_are_listed_with_their_defaults_sources_and_locks()
    {
        await using var factory = CreateFactory(settings: new() { ["Lockout:MaxFailedAttempts"] = "3" });
        var api = await AdminApiAsync(factory);

        var settings = await ReadJsonAsync(await api.GetAsync("/api/admin/settings", CancellationToken));

        var access = Setting(settings, "tokens", "accessTokenLifetime");
        Assert.Equal("duration", access.GetProperty("kind").GetString());
        Assert.Equal(1800, access.GetProperty("value").GetInt32());
        Assert.Equal(1800, access.GetProperty("defaultValue").GetInt32());
        Assert.Equal("default", access.GetProperty("source").GetString());
        Assert.False(access.GetProperty("locked").GetBoolean());

        var attempts = Setting(settings, "lockout", "maxFailedAttempts");
        Assert.Equal(3, attempts.GetProperty("value").GetInt32());
        Assert.Equal("configuration", attempts.GetProperty("source").GetString());
        Assert.True(attempts.GetProperty("locked").GetBoolean());

        var security = Setting(settings, "smtp", "security");
        Assert.Equal(["StartTls", "Tls", "None"], security.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        var smtpPassword = Setting(settings, "smtp", "password");
        Assert.Equal("secret", smtpPassword.GetProperty("kind").GetString());
        Assert.False(smtpPassword.GetProperty("isSet").GetBoolean());

        Assert.True(Setting(settings, "adminApi", "requireTwoFactor").GetProperty("value").GetBoolean());
        Assert.Equal(365 * 86400, Setting(settings, "auditLog", "retention").GetProperty("value").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/admin/settings/nope", CancellationToken)).StatusCode);
    }

    // Changing

    [Fact]
    public async Task A_change_takes_effect_without_a_restart_and_is_audited()
    {
        await using var factory = CreateFactory();
        var api = await AdminApiAsync(factory);
        await CreateUserAsync(factory, "bob");

        var response = await api.PatchAsJsonAsync("/api/admin/settings/lockout", new { maxFailedAttempts = 2 }, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempts = Setting(await ReadJsonAsync(response), "maxFailedAttempts");
        Assert.Equal(2, attempts.GetProperty("value").GetInt32());
        Assert.Equal("database", attempts.GetProperty("source").GetString());

        for (var i = 0; i < 2; i++)
        {
            await Forms.LoginAsync(factory.CreateBrowser(), "bob", "wrong password!");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.NotNull((await db.Users.SingleAsync(u => u.UserName == "bob", CancellationToken)).LockoutEnd);
        var entry = await db.AuditLog.SingleAsync(e => e.EventType == "settings.updated", CancellationToken);
        Assert.StartsWith("admin:", entry.Actor, StringComparison.Ordinal);
        var change = JsonDocument.Parse(entry.Details!).RootElement.GetProperty("changes")[0];
        Assert.Equal("Lockout:MaxFailedAttempts", change.GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Null, change.GetProperty("old").ValueKind);
        Assert.Equal("2", change.GetProperty("new").GetString());
        Assert.Equal(1, await RevisionAsync(factory));
    }

    [Fact]
    public async Task Settings_set_by_configuration_cannot_be_changed()
    {
        await using var factory = CreateFactory(settings: new() { ["Tokens:AccessTokenLifetime"] = "00:10:00" });
        var api = await AdminApiAsync(factory);

        var response = await api.PatchAsJsonAsync("/api/admin/settings/tokens", new { accessTokenLifetime = 60 }, CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Tokens:AccessTokenLifetime", await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(0, await RevisionAsync(factory));
    }

    [Theory]
    [InlineData("passwordPolicy", """{ "minimumLength": 4 }""", "PasswordPolicy:MinimumLength")]
    [InlineData("tokens", """{ "refreshTokenSlidingLifetime": 864000, "refreshTokenAbsoluteLifetime": 86400 }""", "RefreshTokenAbsoluteLifetime")]
    [InlineData("tokens", """{ "accessTokenLifetime": 0 }""", "Tokens:AccessTokenLifetime")]
    [InlineData("tokens", """{ "accessTokenLifetime": "1800" }""", "whole number of seconds")]
    [InlineData("tokens", """{ "accessTokenLifetime": -1 }""", "whole number of seconds")]
    [InlineData("lockout", """{ "maxFailedAttempts": 1.5 }""", "an integer")]
    [InlineData("smtp", """{ "security": "1" }""", "one of StartTls, Tls, None")]
    [InlineData("smtp", """{ "host": "smtp.example.test" }""", "Smtp:FromAddress")]
    [InlineData("smtp", """{ "host": "smtp.example.test", "fromAddress": "id@example.test", "security": "None" }""", "Smtp:Security None")]
    [InlineData("branding", """{ "primaryColor": "red;} body{display:none" }""", "Branding:PrimaryColor")]
    [InlineData("branding", """{ "darkSurfaceColor": "red;} body{display:none" }""", "Branding:DarkSurfaceColor")]
    [InlineData("branding", """{ "logo": "logo.png" }""", "Branding:Logo")]
    [InlineData("email", """{ "verificationAtSignIn": "Require" }""", "while SMTP is configured")]
    [InlineData("auditLog", """{ "retention": 3600 }""", "AuditLog:Retention")]
    [InlineData("tokens", """{ "clockSkew": 60 }""", "Unknown setting 'clockSkew'")]
    public async Task Invalid_values_are_rejected_and_nothing_is_saved(string section, string body, string error)
    {
        await using var factory = CreateFactory();
        var api = await AdminApiAsync(factory);

        var response = await api.PatchAsync($"/api/admin/settings/{section}", new StringContent(body, Encoding.UTF8, "application/json"), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Settings.AnyAsync(CancellationToken));
    }

    [Fact]
    public async Task Null_resets_a_setting_to_its_default()
    {
        await using var factory = CreateFactory();
        var api = await AdminApiAsync(factory);
        await api.PatchAsJsonAsync("/api/admin/settings/tokens", new { accessTokenLifetime = 600 }, CancellationToken);
        Assert.Equal(TimeSpan.FromMinutes(10), Options<TokenOptions>(factory).AccessTokenLifetime);

        var response = await api.PatchAsync("/api/admin/settings/tokens", new StringContent("""{ "accessTokenLifetime": null }""", Encoding.UTF8, "application/json"), CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("default", Setting(await ReadJsonAsync(response), "accessTokenLifetime").GetProperty("source").GetString());
        Assert.Equal(TimeSpan.FromMinutes(30), Options<TokenOptions>(factory).AccessTokenLifetime);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Settings.AnyAsync(CancellationToken));
    }

    [Fact]
    public async Task New_token_lifetimes_apply_to_tokens_issued_afterwards()
    {
        await using var factory = CreateFactory();
        var api = await AdminApiAsync(factory);
        await api.PatchAsJsonAsync("/api/admin/settings/tokens", new { accessTokenLifetime = 300, idTokenLifetime = 120 }, CancellationToken);
        await CreateClientAsync(factory);
        await CreateUserAsync(factory, "bob");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "bob", Password);

        var tokens = await CodeFlowAsync(factory, browser);

        Assert.Equal(300, tokens.GetProperty("expires_in").GetInt32());
        var idToken = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(tokens.GetProperty("id_token").GetString());
        Assert.Equal(TimeSpan.FromMinutes(2), idToken.ValidTo - idToken.IssuedAt);
    }

    // Secrets

    [Fact]
    public async Task The_smtp_password_is_encrypted_and_never_returned_or_audited()
    {
        const string secret = "smtp-pa$$word-4711";
        await using var factory = CreateFactory();
        var api = await AdminApiAsync(factory);

        var response = await api.PatchAsJsonAsync("/api/admin/settings/smtp", new
        {
            host = "smtp.example.test",
            fromAddress = "id@example.test",
            userName = "mailer",
            password = secret,
        }, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        var password = Setting(JsonDocument.Parse(body).RootElement, "password");
        Assert.True(password.GetProperty("isSet").GetBoolean());
        Assert.Equal(JsonValueKind.Null, password.GetProperty("value").ValueKind);
        Assert.Equal(secret, Options<SmtpOptions>(factory).Password);
        Assert.True(Options<SmtpOptions>(factory).Enabled);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var row = await db.Settings.SingleAsync(s => s.Key == "Smtp:Password", CancellationToken);
            Assert.Null(row.Value);
            Assert.NotNull(row.EncryptedValue);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(row.EncryptedValue), StringComparison.Ordinal);
            var details = await db.AuditLog.Select(e => e.Details).ToListAsync(CancellationToken);
            Assert.DoesNotContain(details, d => d != null && d.Contains(secret, StringComparison.Ordinal));
            Assert.Contains(details, d => d != null && d.Contains("(secret)", StringComparison.Ordinal));
        }

        // An empty string removes it again.
        response = await api.PatchAsJsonAsync("/api/admin/settings/smtp", new { userName = (string?)null, password = "" }, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Options<SmtpOptions>(factory).Password);
    }

    [Fact]
    public async Task The_smtp_password_is_reencrypted_with_a_new_master_key()
    {
        var database = Guid.NewGuid().ToString("N");
        string oldKey;
        await using (var first = CreateFactory(database: database))
        {
            oldKey = first.MasterKey;
            var api = await AdminApiAsync(first);
            await api.PatchAsJsonAsync("/api/admin/settings/smtp", new { host = "smtp.example.test", fromAddress = "id@example.test", userName = "u", password = "p" }, CancellationToken);
        }

        await using var second = CreateFactory(database: database, previousMasterKey: oldKey);
        Assert.Equal("p", Options<SmtpOptions>(second).Password);
        var check = await second.RunCliAsync("admin", "reencrypt", "--check");
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("settings: 1", check.Output, StringComparison.Ordinal);
        Assert.Equal(0, (await second.RunCliAsync("admin", "reencrypt")).ExitCode);
        Assert.Equal(0, (await second.RunCliAsync("admin", "reencrypt", "--check")).ExitCode);
    }

    // Branding

    [Fact]
    public async Task Branding_changes_update_the_theme_stylesheet()
    {
        await using var factory = CreateFactory();
        var api = await AdminApiAsync(factory);
        var client = factory.CreateClient();
        var before = await client.GetAsync("/theme.css", CancellationToken);
        var etag = before.Headers.ETag!;

        var notModified = new HttpRequestMessage(HttpMethod.Get, "/theme.css");
        notModified.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(notModified, CancellationToken)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await api.PatchAsJsonAsync("/api/admin/settings/branding", new { primaryColor = "#123456", darkPrimaryColor = "#abcdef", productName = "Acme ID" }, CancellationToken)).StatusCode);

        var after = await client.GetAsync("/theme.css", CancellationToken);
        var css = await after.Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("--vid-primary: #123456;", css, StringComparison.Ordinal);
        Assert.Contains(":root[data-theme=\"dark\"] {\n  --vid-primary: #abcdef;\n}", css, StringComparison.Ordinal);
        Assert.NotEqual(etag, after.Headers.ETag);
        Assert.Contains("Acme ID", await (await factory.CreateBrowser().GetAsync("/account/login", CancellationToken)).Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Branding_files_are_chosen_from_the_branding_directory()
    {
        var directory = Directory.CreateTempSubdirectory("valkyrieid-branding-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "logo.svg"), "<svg/>", CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "site.css"), "body{}", CancellationToken);
            await using var factory = CreateFactory(settings: new() { ["Branding:Directory"] = directory });
            var api = await AdminApiAsync(factory);

            var section = await ReadJsonAsync(await api.GetAsync("/api/admin/settings/branding", CancellationToken));
            Assert.Equal(["logo.svg"], Setting(section, "logo").GetProperty("options").EnumerateArray().Select(o => o.GetString()));
            Assert.Equal(["site.css"], Setting(section, "customCss").GetProperty("options").EnumerateArray().Select(o => o.GetString()));

            Assert.Equal(HttpStatusCode.OK, (await api.PatchAsJsonAsync("/api/admin/settings/branding", new { logo = "logo.svg", customCss = "site.css" }, CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsJsonAsync("/api/admin/settings/branding", new { logo = "site.css" }, CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsJsonAsync("/api/admin/settings/branding", new { logo = "../secret.png" }, CancellationToken)).StatusCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Dark_background_image_is_a_runtime_setting_for_the_dark_theme()
    {
        var directory = Directory.CreateTempSubdirectory("valkyrieid-branding-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "night.png"), [0x89, 0x50, 0x4e, 0x47], CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "site.css"), "body{}", CancellationToken);
            await using var factory = CreateFactory(settings: new() { ["Branding:Directory"] = directory });
            var api = await AdminApiAsync(factory);

            var section = await ReadJsonAsync(await api.GetAsync("/api/admin/settings/branding", CancellationToken));
            Assert.Equal(["night.png"], Setting(section, "darkBackgroundImage").GetProperty("options").EnumerateArray().Select(o => o.GetString()));

            // Only images from the branding directory can be chosen.
            Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsJsonAsync("/api/admin/settings/branding", new { darkBackgroundImage = "site.css" }, CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsJsonAsync("/api/admin/settings/branding", new { darkBackgroundImage = "../night.png" }, CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsJsonAsync("/api/admin/settings/branding", new { darkBackgroundImage = "x\"); } body { display: none" }, CancellationToken)).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await api.PatchAsJsonAsync("/api/admin/settings/branding", new { darkBackgroundImage = "night.png" }, CancellationToken)).StatusCode);

            var css = await (await factory.CreateClient().GetAsync("/theme.css", CancellationToken)).Content.ReadAsStringAsync(CancellationToken);
            Assert.StartsWith(":root {\n}\n", css, StringComparison.Ordinal);
            Assert.Contains(":root[data-theme=\"dark\"] {\n  --vid-background-image: url(\"/branding/night.png\");\n  --vid-glass: color-mix(in srgb, var(--vid-surface) 88%, transparent);\n}", css, StringComparison.Ordinal);

            await using var scope = factory.Services.CreateAsyncScope();
            var entry = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog.SingleAsync(e => e.EventType == "settings.updated", CancellationToken);
            Assert.Equal("Branding:DarkBackgroundImage", JsonDocument.Parse(entry.Details!).RootElement.GetProperty("changes")[0].GetProperty("key").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Reloading

    [Fact]
    public async Task Changes_made_by_another_instance_are_picked_up()
    {
        await using var factory = CreateFactory();
        Assert.Equal(TimeSpan.FromMinutes(30), Options<TokenOptions>(factory).AccessTokenLifetime);

        await WriteDirectlyAsync(factory, "Tokens:AccessTokenLifetime", "00:05:00");
        await factory.Services.GetRequiredService<SettingsReloadService>().CheckAsync(CancellationToken);

        Assert.Equal(TimeSpan.FromMinutes(5), Options<TokenOptions>(factory).AccessTokenLifetime);
    }

    [Fact]
    public async Task Invalid_stored_settings_are_not_applied_on_reload()
    {
        await using var factory = CreateFactory();
        await WriteDirectlyAsync(factory, "Lockout:MaxFailedAttempts", "3");
        await factory.Services.GetRequiredService<SettingsReloadService>().CheckAsync(CancellationToken);
        Assert.Equal(3, Options<LockoutOptions>(factory).MaxFailedAttempts);

        await WriteDirectlyAsync(factory, "Lockout:MaxFailedAttempts", "0");
        await factory.Services.GetRequiredService<SettingsReloadService>().CheckAsync(CancellationToken);

        Assert.Equal(3, Options<LockoutOptions>(factory).MaxFailedAttempts);
    }

    [Fact]
    public async Task Unknown_rows_are_ignored()
    {
        await using var factory = CreateFactory();

        // Configuration-only settings can't be injected through the database.
        await WriteDirectlyAsync(factory, "Issuer:Url", "https://evil.example.test");
        await WriteDirectlyAsync(factory, "Tokens:ClockSkew", "00:05:00");
        await factory.Services.GetRequiredService<SettingsReloadService>().CheckAsync(CancellationToken);

        Assert.Equal(ValkyrieAppFactory.DefaultIssuer, factory.Services.GetRequiredService<IOptions<IssuerOptions>>().Value.Issuer);
        Assert.Equal(TimeSpan.FromSeconds(30), Options<TokenOptions>(factory).ClockSkew);
    }

    // Admin API 2FA requirement

    [Fact]
    public async Task The_admin_api_requires_a_second_factor_by_default()
    {
        await using var factory = CreateFactory();
        var adminId = await CreateAdminAsync(factory, "admin");

        var passwordOnly = await GetAsync(factory, await MintAsync(factory, adminId, ["pwd"]), "/api/admin/users");
        var withSecondFactor = await GetAsync(factory, await MintAsync(factory, adminId, ["pwd", "mfa"]), "/api/admin/users");

        Assert.Equal(HttpStatusCode.Unauthorized, passwordOnly.StatusCode);
        Assert.Contains("error=\"insufficient_user_authentication\"", passwordOnly.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, withSecondFactor.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var denied = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .SingleAsync(e => e.EventType == "admin_api.access_denied", CancellationToken);
        Assert.Contains("second_factor_required", denied.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admins_must_set_up_2fa_at_sign_in_and_their_admin_tokens_carry_amr()
    {
        await using var factory = CreateFactory();
        await CreateAdminAsync(factory, "admin");
        var browser = factory.CreateBrowser();

        await Forms.LoginWithEnrollmentAsync(browser, "admin", Password);

        var token = await AdminTokenAsync(factory, browser);
        var jwt = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token);
        Assert.Equal(["pwd", "otp", "mfa"], jwt.GetPayloadValue<string[]>("amr"));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, token, "/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task The_requirement_can_be_turned_off_but_only_turned_on_with_a_second_factor()
    {
        await using var factory = CreateFactory();
        var adminId = await CreateAdminAsync(factory, "admin");
        var strong = Api(factory, await MintAsync(factory, adminId, ["pwd", "mfa"]));

        Assert.Equal(HttpStatusCode.OK, (await strong.PatchAsJsonAsync("/api/admin/settings/adminApi", new { requireTwoFactor = false }, CancellationToken)).StatusCode);
        var weak = Api(factory, await MintAsync(factory, adminId, ["pwd"]));
        Assert.Equal(HttpStatusCode.OK, (await weak.GetAsync("/api/admin/users", CancellationToken)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await weak.PatchAsJsonAsync("/api/admin/settings/adminApi", new { requireTwoFactor = true }, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await weak.PatchAsync("/api/admin/settings/adminApi", new StringContent("""{ "requireTwoFactor": null }""", Encoding.UTF8, "application/json"), CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await strong.PatchAsJsonAsync("/api/admin/settings/adminApi", new { requireTwoFactor = true }, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await weak.GetAsync("/api/admin/users", CancellationToken)).StatusCode);
    }

    // Email verification at sign-in

    [Fact]
    public async Task Required_email_verification_stops_sign_in_until_the_address_is_verified()
    {
        await using var factory = CreateFactory(settings: new(Smtp) { ["Email:VerificationAtSignIn"] = "Require" }, sender: new TestEmailSender());
        await CreateClientAsync(factory);
        await CreateUserAsync(factory, "bob", "bob@example.test");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "bob", Password);
        var (_, challenge) = Pkce();

        var authorize = await browser.GetAsync(AuthorizeUrl(challenge), CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.StartsWith("/account/verify-required?returnUrl=", authorize.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var page = await browser.GetAsync(authorize.Headers.Location, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Verify it to continue", await page.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);

        // Visiting the page doesn't let the user through, and prompt=none can't show it.
        Assert.StartsWith("/account/verify-required", (await browser.GetAsync(AuthorizeUrl(challenge), CancellationToken)).Headers.Location!.OriginalString, StringComparison.Ordinal);
        var silent = await browser.GetAsync(AuthorizeUrl(challenge) + "&prompt=none", CancellationToken);
        Assert.Equal("interaction_required", QueryHelpers.ParseQuery(silent.Headers.Location!.Query)["error"]);

        await SetEmailVerifiedAsync(factory, "bob");
        var verified = await browser.GetAsync(AuthorizeUrl(challenge), CancellationToken);
        Assert.StartsWith(ClientRedirectUri + "?code=", verified.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompted_email_verification_is_shown_once_per_session()
    {
        await using var factory = CreateFactory(settings: new(Smtp) { ["Email:VerificationAtSignIn"] = "Prompt" }, sender: new TestEmailSender());
        await CreateClientAsync(factory);
        await CreateUserAsync(factory, "bob", "bob@example.test");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "bob", Password);
        var (_, challenge) = Pkce();

        var silent = await browser.GetAsync(AuthorizeUrl(challenge) + "&prompt=none", CancellationToken);
        Assert.StartsWith(ClientRedirectUri + "?code=", silent.Headers.Location!.ToString(), StringComparison.Ordinal);

        var first = await browser.GetAsync(AuthorizeUrl(challenge), CancellationToken);
        Assert.StartsWith("/account/verify-required", first.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(first.Headers.Location, CancellationToken)).StatusCode);

        var second = await browser.GetAsync(AuthorizeUrl(challenge), CancellationToken);
        Assert.StartsWith(ClientRedirectUri + "?code=", second.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Email_verification_at_sign_in_is_off_without_smtp()
    {
        await using var factory = CreateFactory(settings: new() { ["Email:VerificationAtSignIn"] = "Require" });
        await CreateClientAsync(factory);
        await CreateUserAsync(factory, "bob", "bob@example.test");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "bob", Password);

        var authorize = await browser.GetAsync(AuthorizeUrl(Pkce().Challenge), CancellationToken);

        Assert.StartsWith(ClientRedirectUri + "?code=", authorize.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // SMTP test email

    [Fact]
    public async Task A_test_email_is_sent_to_the_admin_and_failures_report_only_a_code()
    {
        var sender = new TestEmailSender();
        await using var factory = CreateFactory(settings: new(Smtp), sender: sender);
        var api = await AdminApiAsync(factory, email: "admin@example.test");

        var sent = await ReadJsonAsync(await api.PostAsync("/api/admin/settings/smtp/test", null, CancellationToken));
        sender.Failure = new EmailDeliveryException("authentication_failed", permanent: false, new InvalidOperationException("535 internal detail"));
        var failed = await api.PostAsync("/api/admin/settings/smtp/test", null, CancellationToken);
        var failedBody = await failed.Content.ReadAsStringAsync(CancellationToken);

        Assert.True(sent.GetProperty("success").GetBoolean());
        Assert.Equal("admin@example.test", Assert.Single(sender.Sent).To);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Contains("authentication_failed", failedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("internal detail", failedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_test_email_needs_smtp()
    {
        await using var factory = CreateFactory();
        var api = await AdminApiAsync(factory, email: "admin@example.test");

        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync("/api/admin/settings/smtp/test", null, CancellationToken)).StatusCode);
    }

    // CLI

    [Fact]
    public async Task The_cli_shows_sets_and_resets_settings()
    {
        await using var factory = CreateFactory(settings: new() { ["Lockout:Duration"] = "00:30:00" });

        var set = await factory.RunCliAsync("settings", "set", "Tokens:AccessTokenLifetime", "00:15:00");
        var secret = await factory.RunCliWithInputAsync("hunter2-hunter2\n", "settings", "set", "Smtp:Password");
        var show = await factory.RunCliAsync("settings", "show", "tokens");
        var showSmtp = await factory.RunCliAsync("settings", "show", "smtp");
        var locked = await factory.RunCliAsync("settings", "set", "Lockout:Duration", "00:01:00");
        var invalid = await factory.RunCliAsync("settings", "set", "Lockout:MaxFailedAttempts", "0");
        var unknown = await factory.RunCliAsync("settings", "set", "Issuer:Url", "https://evil.example.test");

        Assert.True(set.ExitCode == 0, set.Error);
        Assert.True(secret.ExitCode == 0, secret.Error);
        Assert.DoesNotContain("hunter2", secret.Output, StringComparison.Ordinal);
        Assert.Contains("Tokens:AccessTokenLifetime = 900  [database]", show.Output, StringComparison.Ordinal);
        Assert.Contains("Smtp:Password = (set)  [database]", showSmtp.Output, StringComparison.Ordinal);
        Assert.Equal(1, locked.ExitCode);
        Assert.Equal(1, invalid.ExitCode);
        Assert.Equal(1, unknown.ExitCode);
        Assert.Equal(TimeSpan.FromMinutes(15), Options<TokenOptions>(factory).AccessTokenLifetime);

        var reset = await factory.RunCliAsync("settings", "reset", "tokens");
        Assert.True(reset.ExitCode == 0, reset.Error);
        Assert.Equal(TimeSpan.FromMinutes(30), Options<TokenOptions>(factory).AccessTokenLifetime);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog.CountAsync(e => e.EventType == "settings.updated" && e.Actor == "cli", CancellationToken));
    }

    // Audit retention

    [Fact]
    public async Task Audit_entries_past_the_retention_are_deleted()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock, new() { ["AuditLog:Retention"] = "30.00:00:00" });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            db.AuditLog.Add(new AuditLogEntry { Timestamp = clock.GetUtcNow() - TimeSpan.FromDays(31), EventType = "test.old" });
            db.AuditLog.Add(new AuditLogEntry { Timestamp = clock.GetUtcNow() - TimeSpan.FromDays(29), EventType = "test.recent" });
            await db.SaveChangesAsync(CancellationToken);
        }

        var deleted = await factory.Services.GetRequiredService<AuditLogCleanupService>().CleanUpAsync(CancellationToken);

        Assert.Equal(1, deleted);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            Assert.False(await db.AuditLog.AnyAsync(e => e.EventType == "test.old", CancellationToken));
            Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "test.recent", CancellationToken));
            Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "audit.purged", CancellationToken));
        }
    }

    [Fact]
    public async Task A_retention_of_zero_keeps_audit_entries_forever()
    {
        await using var factory = CreateFactory(settings: new() { ["AuditLog:Retention"] = "00:00:00" });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            db.AuditLog.Add(new AuditLogEntry { Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromDays(5000), EventType = "test.old" });
            await db.SaveChangesAsync(CancellationToken);
        }

        Assert.Equal(0, await factory.Services.GetRequiredService<AuditLogCleanupService>().CleanUpAsync(CancellationToken));
    }

    // Helpers

    private static T Options<T>(ValkyrieAppFactory factory)
        where T : class =>
        factory.Services.GetRequiredService<IOptionsMonitor<T>>().CurrentValue;

    private static JsonElement Setting(JsonElement all, string section, string name) =>
        Setting(all.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("name").GetString() == section), name);

    private static JsonElement Setting(JsonElement section, string name) =>
        section.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    private static async Task<long> RevisionAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().SettingsRevisions.Select(r => r.Revision).SingleAsync(CancellationToken);
    }

    /// <summary>Writes a setting as another process would: the row and a new revision, without notifying this instance.</summary>
    private static async Task WriteDirectlyAsync(ValkyrieAppFactory factory, string key, string value)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, CancellationToken);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            row.Value = value;
        }

        await db.SaveChangesAsync(CancellationToken);
        await db.SettingsRevisions.ExecuteUpdateAsync(r => r.SetProperty(x => x.Revision, x => x.Revision + 1), CancellationToken);
    }

    private static async Task SetEmailVerifiedAsync(ValkyrieAppFactory factory, string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users
            .Where(u => u.UserName == userName)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.EmailVerified, true), CancellationToken);
    }

    /// <summary>An Admin API client for a new admin whose token shows a second factor.</summary>
    private static async Task<HttpClient> AdminApiAsync(ValkyrieAppFactory factory, string? email = null) =>
        Api(factory, await MintAsync(factory, await CreateAdminAsync(factory, "admin", email), ["pwd", "otp", "mfa"]));

    private static HttpClient Api(ValkyrieAppFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> GetAsync(ValkyrieAppFactory factory, string token, string path) =>
        Api(factory, token).GetAsync(path, CancellationToken);

    private static async Task<string> MintAsync(ValkyrieAppFactory factory, Guid subject, string[] amr)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var issuer = scope.ServiceProvider.GetRequiredService<AccessTokenIssuer>();
        return (await issuer.IssueAsync(new AccessTokenRequest("test", subject.ToString(), [AdminResource], [AdminScope], TimeSpan.FromMinutes(5), amr), CancellationToken)).Token;
    }

    private static async Task<Guid> CreateAdminAsync(ValkyrieAppFactory factory, string userName, string? email = null)
    {
        var id = await CreateUserAsync(factory, userName, email);
        var result = await factory.RunCliAsync("admin", "grant-admin", userName);
        Assert.True(result.ExitCode == 0, result.Error);
        return id;
    }

    private static async Task<Guid> CreateUserAsync(ValkyrieAppFactory factory, string userName, string? email = null)
    {
        string[] args = email is null ? ["user", "create", userName] : ["user", "create", userName, "--email", email];
        var result = await factory.RunCliWithInputAsync(Password + "\n", args);
        Assert.True(result.ExitCode == 0, result.Error);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users
            .Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync(CancellationToken);
    }

    private static async Task CreateClientAsync(ValkyrieAppFactory factory)
    {
        var result = await factory.RunCliAsync("client", "create", "web", "--public", "--scope", "openid", "--grant", "authorization_code", "--redirect-uri", ClientRedirectUri);
        Assert.True(result.ExitCode == 0, result.Error);
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return (verifier, System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string AuthorizeUrl(string challenge) =>
        $"/connect/authorize?response_type=code&client_id=web&scope=openid&redirect_uri={Uri.EscapeDataString(ClientRedirectUri)}"
        + $"&code_challenge={challenge}&code_challenge_method=S256&nonce=n";

    private static async Task<JsonElement> CodeFlowAsync(ValkyrieAppFactory factory, HttpClient browser)
    {
        var (verifier, challenge) = Pkce();
        var authorize = await browser.GetAsync(AuthorizeUrl(challenge), CancellationToken);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var response = await factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", ClientRedirectUri),
            new("code_verifier", verifier), new("client_id", "web"),
        ]), CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await ReadJsonAsync(response);
    }

    private static async Task<string> AdminTokenAsync(ValkyrieAppFactory factory, HttpClient browser)
    {
        var (verifier, challenge) = Pkce();
        const string redirectUri = ValkyrieAppFactory.DefaultIssuer + "/admin/callback";
        var authorize = await browser.GetAsync(
            $"/connect/authorize?response_type=code&client_id=valkyrieid-admin-ui&scope={Uri.EscapeDataString("openid " + AdminScope)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&code_challenge={challenge}&code_challenge_method=S256&nonce=n",
            CancellationToken);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var response = await factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirectUri),
            new("code_verifier", verifier), new("client_id", "valkyrieid-admin-ui"),
        ]), CancellationToken);
        return (await ReadJsonAsync(response)).GetProperty("access_token").GetString()!;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;
}
