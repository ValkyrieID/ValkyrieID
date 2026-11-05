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
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>
/// Security review (phase 10a): transport security, security headers and cookies, CORS scope, rate limits, token
/// validation, and the RFC 9700 hardening items. Run against every database provider. See docs/security/checklist.md.
/// </summary>
public abstract class SecurityTests
{
    private const string Password = "correct horse battery staple";
    private const string RedirectUri = "https://app.example.test/callback";
    private const string PostLogoutUri = "https://app.example.test/signed-out";

    protected abstract ValkyrieAppFactory CreateFactory(Dictionary<string, string?>? settings = null, System.Collections.Concurrent.ConcurrentQueue<string>? logs = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Transport security (RFC 6749 §3.1, §3.2; OIDC Core §16.17)

    [Theory]
    [InlineData("/account/login", "text/html")]
    [InlineData("/connect/authorize?client_id=web", "text/html")]
    [InlineData("/.well-known/openid-configuration", "application/json")]
    [InlineData("/api/admin/users", "application/json")]
    public async Task Plain_http_requests_are_refused_while_the_issuer_is_https(string path, string contentType)
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateHttpClient().GetAsync(path, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(contentType, response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Contains("HTTPS is required", await response.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Token_requests_over_plain_http_are_refused_before_the_client_is_authenticated()
    {
        await using var factory = CreateFactory();
        var auditEntries = await AuditCountAsync(factory);

        var response = await factory.CreateHttpClient().PostAsync("/connect/token", Form(("grant_type", "client_credentials"), ("client_id", "x"), ("client_secret", "y")), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("invalid_request", (await ReadJsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal(auditEntries, await AuditCountAsync(factory));
    }

    private static async Task<int> AuditCountAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog.CountAsync(CancellationToken);
    }

    [Fact]
    public async Task Health_checks_and_loopback_http_issuers_stay_reachable_over_http()
    {
        await using var factory = CreateFactory();
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateHttpClient().GetAsync("/health/live", CancellationToken)).StatusCode);

        await using var development = CreateFactory(new() { ["Issuer:Url"] = "http://localhost:5102" });
        Assert.Equal(HttpStatusCode.OK, (await development.CreateHttpClient().GetAsync("/.well-known/openid-configuration", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Hsts_lasts_a_year_by_default()
    {
        await using var factory = CreateFactory();

        var response = await PublicHostClient(factory).GetAsync("/account/login", CancellationToken);

        Assert.Equal("max-age=31536000", response.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task Hsts_subdomains_and_preload_are_opt_in_and_validated()
    {
        await using var factory = CreateFactory(new() { ["Hsts:IncludeSubDomains"] = "true", ["Hsts:Preload"] = "true" });
        var response = await PublicHostClient(factory).GetAsync("/account/login", CancellationToken);
        Assert.Equal("max-age=31536000; includeSubDomains; preload", response.Headers.GetValues("Strict-Transport-Security").Single());

        await using var invalid = CreateFactory(new() { ["Hsts:Preload"] = "true" });
        var error = Assert.ThrowsAny<Exception>(() => invalid.CreateClient());
        Assert.Contains("Hsts:Preload", error.ToString(), StringComparison.Ordinal);
    }

    // Security headers

    [Fact]
    public async Task Error_pages_and_json_responses_have_a_strict_csp()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var error = await client.GetAsync("/connect/authorize?client_id=unknown&redirect_uri=https%3A%2F%2Fevil.example%2F", CancellationToken);
        var json = await client.GetAsync("/.well-known/openid-configuration", CancellationToken);

        foreach (var response in new[] { error, json })
        {
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.StartsWith("default-src 'none';", csp, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
            Assert.EndsWith("form-action 'self'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        }

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Null(error.Headers.Location);
    }

    [Fact]
    public async Task Only_the_admin_surfaces_are_cross_origin_isolated()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        foreach (var path in new[] { "/admin/", "/api/admin/users" })
        {
            var response = await client.GetAsync(path, CancellationToken);
            Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
            Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        }

        // Relying parties may open sign-in in a popup and read discovery cross-origin.
        foreach (var path in new[] { "/account/login", "/connect/authorize", "/.well-known/openid-configuration" })
        {
            var response = await client.GetAsync(path, CancellationToken);
            Assert.False(response.Headers.Contains("Cross-Origin-Opener-Policy"), path);
            Assert.False(response.Headers.Contains("Cross-Origin-Resource-Policy"), path);
        }
    }

    [Fact]
    public async Task Login_pages_allow_form_posts_only_to_self_and_a_registered_redirect_uri()
    {
        await using var factory = CreateFactory();
        await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();

        var registered = await LoginPageCspAsync(browser, AuthorizeUrl("web", RedirectUri));
        var unregistered = await LoginPageCspAsync(browser, AuthorizeUrl("web", "https://evil.example/callback"));
        var unknownClient = await LoginPageCspAsync(browser, AuthorizeUrl("nobody", RedirectUri));
        var notAuthorize = await LoginPageCspAsync(browser, "/account?redirect_uri=" + Uri.EscapeDataString(RedirectUri));
        var none = await LoginPageCspAsync(browser, null);

        Assert.EndsWith("form-action 'self' https://app.example.test", registered, StringComparison.Ordinal);
        Assert.All([unregistered, unknownClient, notAuthorize, none], csp => Assert.EndsWith("form-action 'self'", csp, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_clients_get_no_form_action_exception()
    {
        await using var factory = CreateFactory();
        await CreateClientAsync(factory, "web");
        Assert.Equal(0, (await factory.RunCliAsync("client", "disable", "web")).ExitCode);

        var csp = await LoginPageCspAsync(factory.CreateBrowser(), AuthorizeUrl("web", RedirectUri));

        Assert.EndsWith("form-action 'self'", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Form_post_responses_post_only_to_the_redirect_uri()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var response = await browser.GetAsync(AuthorizeUrl("web", RedirectUri) + "&response_mode=form_post", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("; form-action https://app.example.test", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_pages_allow_only_a_registered_post_logout_redirect_uri()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var unregistered = await browser.GetAsync(
            "/connect/endsession?client_id=web&post_logout_redirect_uri=" + Uri.EscapeDataString("https://evil.example/out"), CancellationToken);
        var page = await browser.GetAsync(unregistered.Headers.Location!.OriginalString, CancellationToken);

        Assert.EndsWith("form-action 'self'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_and_protocol_responses_are_not_cached()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var account = await browser.GetAsync("/account", CancellationToken);
        var authorize = await browser.GetAsync(AuthorizeUrl("web", RedirectUri), CancellationToken);

        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        Assert.Equal("no-store", account.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.Equal("no-store", authorize.Headers.CacheControl?.ToString());
    }

    // Cookies

    [Fact]
    public async Task Antiforgery_and_pending_login_cookies_are_host_only_secure_and_http_only()
    {
        await using var factory = CreateFactory(new() { ["TwoFactor:Required"] = "true" });
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();

        var page = await browser.GetAsync("/account/login", CancellationToken);
        var antiforgery = Assert.Single(page.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-valkyrie.af=", StringComparison.Ordinal));
        AssertHostCookie(antiforgery, "strict");

        var login = await Forms.LoginAsync(browser, "alice", Password);
        Assert.Equal(HttpStatusCode.SeeOther, login.StatusCode);
        var pending = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-valkyrie.pending=", StringComparison.Ordinal));
        AssertHostCookie(pending, "strict");
    }

    [Fact]
    public async Task The_verification_prompt_cookie_is_host_only_secure_and_http_only()
    {
        await using var factory = CreateFactory(new()
        {
            ["Smtp:Host"] = "smtp.example.test",
            ["Smtp:FromAddress"] = "id@example.test",
            ["Email:VerificationAtSignIn"] = "Prompt",
        });
        await CreateUserAsync(factory, "alice", "alice@example.test");
        await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var authorize = await browser.GetAsync(AuthorizeUrl("web", RedirectUri), CancellationToken);
        Assert.StartsWith("/account/verify-required", authorize.Headers.Location?.OriginalString, StringComparison.Ordinal);
        var page = await browser.GetAsync(authorize.Headers.Location!.OriginalString, CancellationToken);

        var cookie = Assert.Single(page.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-valkyrie.verify-prompted=", StringComparison.Ordinal));
        AssertHostCookie(cookie, "lax");
        // The verification page returns to the authorization request, so its forms may end at the client.
        Assert.EndsWith("form-action 'self' https://app.example.test", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    // CORS (RFC 9700 §2.6: never at the authorization endpoint)

    [Theory]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/introspect")]
    [InlineData("/account/login")]
    public async Task Endpoints_not_called_by_browser_clients_send_no_cors_headers(string path)
    {
        await using var factory = CreateFactory();
        await CreateClientAsync(factory, "web", "--cors-origin", "https://app.example.test");
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", "https://app.example.test");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await factory.CreateClient().SendAsync(request, CancellationToken);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }

    // Anti-forgery

    [Fact]
    public async Task The_logout_confirmation_needs_an_antiforgery_token()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var forged = await browser.PostAsync("/account/logout", Form(), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account", CancellationToken)).StatusCode);
    }

    // Rate limits

    [Fact]
    public async Task Introspection_and_revocation_share_the_token_endpoint_limit()
    {
        await using var factory = CreateFactory(new() { ["RateLimits:Token:PermitLimit"] = "2" });
        var client = factory.CreateClient();

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsync("/connect/introspect", Form(("token", "x")), CancellationToken)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsync("/connect/revocation", Form(("token", "x")), CancellationToken)).StatusCode);
        var limited = await client.PostAsync("/connect/introspect", Form(("token", "x")), CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/connect/revocation", Form(("token", "x")), CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Authorization_requests_are_rate_limited()
    {
        await using var factory = CreateFactory(new() { ["RateLimits:Authorize:PermitLimit"] = "1" });
        var client = factory.CreateClient();

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/connect/authorize", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/connect/authorize", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Two_factor_code_submissions_are_rate_limited()
    {
        await using var factory = CreateFactory(new() { ["RateLimits:TwoFactor:PermitLimit"] = "1" });
        var client = factory.CreateClient();

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsync("/account/login/2fa", Form(("Code", "123456")), CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/account/login/2fa", Form(("Code", "123456")), CancellationToken)).StatusCode);
        // Viewing the page doesn't count.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/account/login/2fa", CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("/account/profile")]
    [InlineData("/account/sessions?handler=RevokeOthers")]
    [InlineData("/account/logout")]
    public async Task Other_account_form_posts_are_rate_limited(string path)
    {
        await using var factory = CreateFactory(new() { ["RateLimits:Account:PermitLimit"] = "1" });
        await CreateUserAsync(factory, "alice");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await browser.PostAsync(path, Form(), CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.PostAsync(path, Form(), CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(path.Split('?')[0], CancellationToken)).StatusCode);
    }

    // RFC 9700 §4.12: 303 after posts that may carry credentials

    [Fact]
    public async Task Credential_posts_are_answered_with_303()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateClientAsync(factory, "web");
        var browser = factory.CreateBrowser();

        var login = await Forms.LoginAsync(browser, "alice", Password, AuthorizeUrl("web", RedirectUri));
        var authorize = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(AuthorizeParameters("web", RedirectUri)), CancellationToken);

        Assert.Equal(HttpStatusCode.SeeOther, login.StatusCode);
        Assert.Equal(HttpStatusCode.SeeOther, authorize.StatusCode);
        Assert.StartsWith(RedirectUri + "?code=", authorize.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    // RFC 9700 §2.1.1: PKCE can only be skipped in OpenID Connect requests

    [Fact]
    public async Task Clients_without_pkce_must_use_it_for_requests_without_openid()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        Assert.Equal(0, (await factory.RunCliAsync("resource", "create", "orders-api", "--scope", "orders.read")).ExitCode);
        await CreateClientAsync(factory, "web", "--no-pkce", "--scope", "orders.read");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var oauth = await browser.GetAsync(AuthorizeUrl("web", RedirectUri, "orders.read", pkce: false), CancellationToken);
        var oidc = await browser.GetAsync(AuthorizeUrl("web", RedirectUri, "openid orders.read", pkce: false) + "&nonce=n-1", CancellationToken);
        var oauthWithPkce = await browser.GetAsync(AuthorizeUrl("web", RedirectUri, "orders.read"), CancellationToken);

        var error = QueryHelpers.ParseQuery(oauth.Headers.Location!.Query);
        Assert.Equal("invalid_request", error["error"].ToString());
        Assert.False(error.ContainsKey("code"));
        Assert.True(QueryHelpers.ParseQuery(oidc.Headers.Location!.Query).ContainsKey("code"));
        Assert.True(QueryHelpers.ParseQuery(oauthWithPkce.Headers.Location!.Query).ContainsKey("code"));
    }

    // RFC 9700 §4.15: clients can't be mistaken for users

    [Theory]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    [InlineData("3F2504E04F8941D39A0C0305E82C3301")]
    [InlineData("{3f2504e0-4f89-41d3-9a0c-0305e82c3301}")]
    public async Task Guid_shaped_client_ids_are_refused(string clientId)
    {
        await using var factory = CreateFactory();

        var (exitCode, _, error) = await factory.RunCliAsync("client", "create", clientId, "--grant", "client_credentials");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("GUID", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tokens_whose_subject_is_their_client_are_not_treated_as_user_tokens()
    {
        await using var factory = CreateFactory(new() { ["AdminApi:RequireTwoFactor"] = "false" });
        var userId = await CreateUserAsync(factory, "alice", admin: true);
        var sub = userId.ToString();

        var userToken = await ForgeAsync(factory, Claims(sub, "web", "web", "openid"));
        var clientToken = await ForgeAsync(factory, Claims(sub, sub, sub, "openid"));
        var adminToken = await ForgeAsync(factory, Claims(sub, "valkyrieid-admin-ui", "valkyrieid-admin", "openid valkyrieid.admin"));
        var clientAdminToken = await ForgeAsync(factory, Claims(sub, sub, "valkyrieid-admin", "openid valkyrieid.admin"));

        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(factory, "/connect/userinfo", userToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(factory, "/connect/userinfo", clientToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(factory, "/api/admin/users", adminToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(factory, "/api/admin/users", clientAdminToken)).StatusCode);
    }

    // Access token validation (RFC 9068 §4)

    [Fact]
    public async Task Tokens_with_another_issuer_type_or_no_signature_are_rejected()
    {
        await using var factory = CreateFactory(new() { ["AdminApi:RequireTwoFactor"] = "false" });
        var sub = (await CreateUserAsync(factory, "alice", admin: true)).ToString();
        var claims = Claims(sub, "valkyrieid-admin-ui", "valkyrieid-admin", "openid valkyrieid.admin");

        var valid = await ForgeAsync(factory, claims);
        var otherIssuer = await ForgeAsync(factory, claims, issuer: "https://evil.example");
        var idTokenType = await ForgeAsync(factory, claims, type: "JWT");
        var unsigned = Unsigned(valid);

        foreach (var path in new[] { "/connect/userinfo", "/api/admin/users" })
        {
            Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(factory, path, valid)).StatusCode);
            foreach (var token in new[] { otherIssuer, idTokenType, unsigned })
            {
                var response = await GetWithTokenAsync(factory, path, token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Userinfo_and_admin_api_errors_are_not_cached()
    {
        await using var factory = CreateFactory(new() { ["AdminApi:RequireTwoFactor"] = "false" });
        var admin = (await CreateUserAsync(factory, "alice", admin: true)).ToString();
        var user = (await CreateUserAsync(factory, "bob")).ToString();

        var userinfo = await GetWithTokenAsync(factory, "/connect/userinfo", await ForgeAsync(factory, Claims(admin, "web", "web", "openid")));
        var forbidden = await GetWithTokenAsync(factory, "/api/admin/users", await ForgeAsync(factory, Claims(user, "valkyrieid-admin-ui", "valkyrieid-admin", "valkyrieid.admin")));
        var notFound = await GetWithTokenAsync(factory, "/api/admin/no-such-thing", await ForgeAsync(factory, Claims(admin, "valkyrieid-admin-ui", "valkyrieid-admin", "valkyrieid.admin")));

        Assert.Equal(HttpStatusCode.OK, userinfo.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.All([userinfo, forbidden, notFound], r => Assert.Equal("no-store", r.Headers.CacheControl?.ToString()));
    }

    // Helpers

    [Fact]
    public async Task Logs_never_contain_passwords_secrets_codes_or_tokens()
    {
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var factory = CreateFactory(logs: logs);
        await CreateUserAsync(factory, "alice");
        var (exitCode, output, error) = await factory.RunCliAsync(
            "client", "create", "web", "--grant", "authorization_code", "--grant", "refresh_token", "--redirect-uri", RedirectUri,
            "--post-logout-redirect-uri", PostLogoutUri, "--scope", "openid", "--scope", "offline_access");
        Assert.True(exitCode == 0, error);
        var secret = output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("client_secret: ", StringComparison.Ordinal))["client_secret: ".Length..];
        var client = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"web:{Uri.EscapeDataString(secret)}")));
        var browser = factory.CreateBrowser();

        // Failed and successful sign-ins.
        await Forms.LoginAsync(browser, "alice", "a wrong password!");
        await Forms.LoginAsync(factory.CreateBrowser(), "nobody", Password);
        await Forms.LoginAsync(browser, "alice", Password);

        // Authorization code, token, refresh, userinfo, introspection, and revocation.
        var parameters = AuthorizeParameters("web", RedirectUri, "openid offline_access");
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        parameters.RemoveAll(p => p.Key == "code_challenge");
        parameters.Add(new("code_challenge", Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))));
        var authorize = await browser.GetAsync("/connect/authorize?" + string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")), CancellationToken);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var tokenResponse = await PostWithAuthAsync(browser, "/connect/token", client,
            ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", RedirectUri), ("code_verifier", verifier));
        Assert.True(tokenResponse.IsSuccessStatusCode, authorize.Headers.Location + " " + await tokenResponse.Content.ReadAsStringAsync(CancellationToken));
        var tokens = await ReadJsonAsync(tokenResponse);
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var idToken = tokens.GetProperty("id_token").GetString()!;
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        // A wrong client secret.
        await PostWithAuthAsync(browser, "/connect/token", new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("web:not-the-secret"))), ("grant_type", "refresh_token"), ("refresh_token", refreshToken));

        var refreshed = await ReadJsonAsync(await PostWithAuthAsync(browser, "/connect/token", client, ("grant_type", "refresh_token"), ("refresh_token", refreshToken)));
        var newRefreshToken = refreshed.GetProperty("refresh_token").GetString()!;
        await GetWithTokenAsync(factory, "/connect/userinfo", accessToken);
        await PostWithAuthAsync(browser, "/connect/introspect", client, ("token", accessToken));
        await PostWithAuthAsync(browser, "/connect/introspect", client, ("token", newRefreshToken));
        // Reuse of the rotated token revokes the family; a code replay revokes the code's refresh tokens.
        await PostWithAuthAsync(browser, "/connect/token", client, ("grant_type", "refresh_token"), ("refresh_token", refreshToken));
        await PostWithAuthAsync(browser, "/connect/token", client, ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", RedirectUri), ("code_verifier", verifier));
        await PostWithAuthAsync(browser, "/connect/revocation", client, ("token", newRefreshToken));

        // RP-initiated logout with the ID token as hint.
        var endSession = await browser.GetAsync($"/connect/endsession?id_token_hint={idToken}&post_logout_redirect_uri={Uri.EscapeDataString(PostLogoutUri)}", CancellationToken);
        await Forms.PostAsync(browser, endSession.Headers.Location!.OriginalString, []);

        var all = string.Join('\n', logs);
        Assert.NotEmpty(logs);
        string[] secrets = [Password, "a wrong password!", secret, "not-the-secret", code, verifier, accessToken, idToken, refreshToken, newRefreshToken];
        Assert.All(secrets, value => Assert.DoesNotContain(value, all, StringComparison.Ordinal));
    }

    private static Task<HttpResponseMessage> PostWithAuthAsync(HttpClient client, string path, AuthenticationHeaderValue authorization, params (string Key, string Value)[] values)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Form(values) };
        request.Headers.Authorization = authorization;
        return client.SendAsync(request, CancellationToken);
    }
    private static HttpClient PublicHostClient(ValkyrieAppFactory factory) =>
        // HSTS isn't sent for localhost (RFC 6797 §8.1 lets hosts be excluded; ASP.NET Core excludes loopback names).
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri(ValkyrieAppFactory.DefaultIssuer) });

    private static async Task<string> LoginPageCspAsync(HttpClient browser, string? returnUrl)
    {
        var path = returnUrl is null ? "/account/login" : "/account/login?returnUrl=" + Uri.EscapeDataString(returnUrl);
        var response = await browser.GetAsync(path, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Content-Security-Policy").Single();
    }

    private static void AssertHostCookie(string cookie, string sameSite)
    {
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=" + sameSite, cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private static List<KeyValuePair<string, string>> AuthorizeParameters(string clientId, string redirectUri, string scope = "openid", bool pkce = true)
    {
        List<KeyValuePair<string, string>> pairs = [new("response_type", "code"), new("client_id", clientId), new("redirect_uri", redirectUri), new("scope", scope)];
        if (pkce)
        {
            var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            pairs.Add(new("code_challenge", Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))));
            pairs.Add(new("code_challenge_method", "S256"));
        }

        return pairs;
    }

    private static string AuthorizeUrl(string clientId, string redirectUri, string scope = "openid", bool pkce = true) =>
        "/connect/authorize?" + string.Join('&', AuthorizeParameters(clientId, redirectUri, scope, pkce).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    private static Dictionary<string, object> Claims(string sub, string clientId, string audience, string scope) => new()
    {
        ["sub"] = sub,
        ["client_id"] = clientId,
        ["aud"] = audience,
        ["scope"] = scope,
        ["jti"] = Guid.NewGuid().ToString("N"),
    };

    /// <summary>A token signed with the server's current key, to test what the validators accept.</summary>
    private static async Task<string> ForgeAsync(ValkyrieAppFactory factory, Dictionary<string, object> claims, string? issuer = null, string type = "at+jwt")
    {
        var keys = await factory.Services.GetRequiredService<SigningKeyStore>().GetAsync(CancellationToken);
        var key = keys.Current(DateTimeOffset.UtcNow) ?? throw new InvalidOperationException("No signing key.");
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? ValkyrieAppFactory.DefaultIssuer,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            TokenType = type,
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>The same claims with <c>alg: none</c> and no signature.</summary>
    private static string Unsigned(string token)
    {
        var header = Base64Url.EncodeToString("""{"alg":"none","typ":"at+jwt"}"""u8);
        return $"{header}.{token.Split('.')[1]}.";
    }

    private static Task<HttpResponseMessage> GetWithTokenAsync(ValkyrieAppFactory factory, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return factory.CreateClient().SendAsync(request, CancellationToken);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;

    private static async Task<Guid> CreateUserAsync(ValkyrieAppFactory factory, string userName, string? email = null, bool admin = false)
    {
        string[] args = email is null ? ["user", "create", userName] : ["user", "create", userName, "--email", email];
        var result = await factory.RunCliWithInputAsync(Password + "\n", args);
        Assert.True(result.ExitCode == 0, result.Error);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var user = await db.Users.Include(u => u.Roles).SingleAsync(u => u.UserName == userName, CancellationToken);
        if (admin)
        {
            user.Roles.Add(await db.Roles.SingleAsync(r => r.Id == Role.AdminRoleId, CancellationToken));
            await db.SaveChangesAsync(CancellationToken);
        }

        return user.Id;
    }

    private static async Task CreateClientAsync(ValkyrieAppFactory factory, string clientId, params string[] options)
    {
        var (exitCode, _, error) = await factory.RunCliAsync(
            ["client", "create", clientId, "--grant", "authorization_code", "--redirect-uri", RedirectUri,
                "--post-logout-redirect-uri", PostLogoutUri, "--scope", "openid", .. options]);
        Assert.True(exitCode == 0, error);
    }
}
