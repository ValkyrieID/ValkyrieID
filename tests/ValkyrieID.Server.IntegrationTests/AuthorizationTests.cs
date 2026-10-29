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

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Authorization code flow, ID tokens, userinfo, and CORS, run against every supported database provider.</summary>
public abstract class AuthorizationTests
{
    private const string Password = "correct horse battery staple";
    private const string RedirectUri = "https://app.example.test/callback";
    private const string SpaOrigin = "https://spa.example.test";

    protected abstract ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Discovery

    [Fact]
    public async Task Discovery_advertises_the_authorization_code_flow()
    {
        await using var factory = CreateFactory();

        var document = await ReadJsonAsync(await factory.CreateClient().GetAsync("/.well-known/openid-configuration", CancellationToken));

        Assert.Equal($"{ValkyrieAppFactory.DefaultIssuer}/connect/authorize", document.GetProperty("authorization_endpoint").GetString());
        Assert.Equal($"{ValkyrieAppFactory.DefaultIssuer}/connect/userinfo", document.GetProperty("userinfo_endpoint").GetString());
        Assert.Equal(["code"], Strings(document.GetProperty("response_types_supported")));
        Assert.Equal(["query", "form_post"], Strings(document.GetProperty("response_modes_supported")));
        Assert.Equal(["authorization_code", "client_credentials", "refresh_token"], Strings(document.GetProperty("grant_types_supported")));
        Assert.Equal(["S256"], Strings(document.GetProperty("code_challenge_methods_supported")));
        Assert.Contains("none", Strings(document.GetProperty("token_endpoint_auth_methods_supported")));
        Assert.Contains("openid", Strings(document.GetProperty("scopes_supported")));
        Assert.True(document.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.False(document.GetProperty("request_uri_parameter_supported").GetBoolean());
    }

    // Success paths

    [Fact]
    public async Task Confidential_client_gets_access_and_id_tokens_and_userinfo()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice", "alice@example.test");
        await AddUserDataAsync(factory, "alice");
        var secret = await CreateClientAsync(factory, "web", ["openid", "profile", "email", "roles", "groups"]);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();

        var location = await AuthorizeAsync(browser, Params("web", "openid profile email roles groups", challenge) with { Nonce = "n-0S6_WzA2Mj", State = "af0ifjsldkj" });
        var query = QueryHelpers.ParseQuery(location.Query);

        Assert.StartsWith(RedirectUri + "?", location.ToString(), StringComparison.Ordinal);
        Assert.Equal("af0ifjsldkj", query["state"].ToString());
        Assert.Equal(ValkyrieAppFactory.DefaultIssuer, query["iss"].ToString());

        var response = await browser.SendAsync(CodeRequest(query["code"]!, verifier, Basic("web", secret)), CancellationToken);
        var body = await ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("openid profile email roles groups", body.GetProperty("scope").GetString());
        Assert.False(body.TryGetProperty("refresh_token", out _));

        var accessToken = body.GetProperty("access_token").GetString()!;
        var idToken = await ValidateIdTokenAsync(browser, body.GetProperty("id_token").GetString()!, "web");
        var userId = await UserIdAsync(factory, "alice");
        Assert.Equal(userId.ToString(), idToken.Subject);
        Assert.Equal("JWT", idToken.Typ);
        Assert.Equal("n-0S6_WzA2Mj", idToken.GetClaim("nonce").Value);
        Assert.Equal(AtHash(accessToken), idToken.GetClaim("at_hash").Value);
        Assert.False(string.IsNullOrEmpty(idToken.GetClaim("sid").Value));
        Assert.True(idToken.TryGetPayloadValue<long>("auth_time", out _));
        Assert.Equal(["pwd"], idToken.GetPayloadValue<string[]>("amr"));
        Assert.Equal(TimeSpan.FromMinutes(5), idToken.ValidTo - idToken.IssuedAt);
        Assert.Equal("alice", idToken.GetClaim("preferred_username").Value);
        Assert.Equal("Alice Example", idToken.GetClaim("name").Value);
        Assert.Equal("alice@example.test", idToken.GetClaim("email").Value);
        Assert.Equal(["admin"], idToken.GetPayloadValue<string[]>("roles"));
        Assert.Equal(["engineering"], idToken.GetPayloadValue<string[]>("groups"));
        Assert.Equal("blue", idToken.GetClaim("favorite_color").Value);

        var access = new JsonWebToken(accessToken);
        Assert.Equal("at+jwt", access.Typ);
        Assert.Equal(userId.ToString(), access.Subject);
        Assert.Equal(["web"], access.Audiences);

        var userInfo = await UserInfoAsync(browser, accessToken);
        Assert.Equal(HttpStatusCode.OK, userInfo.StatusCode);
        var claims = await ReadJsonAsync(userInfo);
        Assert.Equal(userId.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal("alice", claims.GetProperty("preferred_username").GetString());
        Assert.Equal("alice@example.test", claims.GetProperty("email").GetString());
        Assert.False(claims.GetProperty("email_verified").GetBoolean());
        Assert.Equal(["admin"], Strings(claims.GetProperty("roles")));
        Assert.Equal("blue", claims.GetProperty("favorite_color").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "authorize.code_issued" && e.ClientId == "web", CancellationToken));
        var entries = await db.AuditLog.Select(e => e.Details).ToListAsync(CancellationToken);
        Assert.DoesNotContain(entries, d => d != null && (d.Contains(query["code"]!, StringComparison.Ordinal) || d.Contains(accessToken, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Claims_are_released_only_for_granted_scopes()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice", "alice@example.test");
        var secret = await CreateClientAsync(factory, "web", ["openid", "profile", "email"]);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var (verifier, challenge) = NewPkce();
        var code = Code(await AuthorizeAsync(browser, Params("web", "openid", challenge)));
        var body = await ReadJsonAsync(await browser.SendAsync(CodeRequest(code, verifier, Basic("web", secret)), CancellationToken));

        var idToken = new JsonWebToken(body.GetProperty("id_token").GetString()!);
        Assert.False(idToken.TryGetClaim("email", out _));
        Assert.False(idToken.TryGetClaim("preferred_username", out _));
        var claims = await ReadJsonAsync(await UserInfoAsync(browser, body.GetProperty("access_token").GetString()!));
        Assert.Equal(["sub"], claims.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Public_client_uses_pkce_without_a_secret()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();

        var code = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge)));
        var response = await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadJsonAsync(response)).TryGetProperty("id_token", out _));
    }

    [Fact]
    public async Task Public_clients_must_use_pkce()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var location = await AuthorizeAsync(browser, Params("spa", "openid", challenge: null));

        AssertRedirectError(location, "invalid_request");
    }

    [Fact]
    public async Task Public_clients_cannot_authenticate_with_a_secret_or_use_client_credentials()
    {
        await using var factory = CreateFactory();
        await CreatePublicClientAsync(factory, "spa");
        var client = factory.CreateClient();

        var withSecret = await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials"), ("client_id", "spa"), ("client_secret", "guess"))), CancellationToken);
        var withoutSecret = await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials"), ("client_id", "spa"))), CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, withSecret.StatusCode);
        await AssertErrorAsync(withoutSecret, HttpStatusCode.BadRequest, "unauthorized_client");
    }

    [Fact]
    public async Task Confidential_clients_must_authenticate_at_the_token_endpoint()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreateClientAsync(factory, "web", ["openid"]);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = Code(await AuthorizeAsync(browser, Params("web", "openid", challenge)));

        var response = await browser.SendAsync(CodeRequest(code, verifier, clientId: "web"), CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_happens_on_the_login_page_and_returns_to_the_authorize_request()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        var (_, challenge) = NewPkce();

        var toLogin = await browser.GetAsync((Params("spa", "openid", challenge) with { LoginHint = "alice" }).Url, CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, toLogin.StatusCode);
        var loginUrl = toLogin.Headers.Location!.ToString();
        Assert.StartsWith("/account/login?", loginUrl, StringComparison.Ordinal);
        var returnUrl = QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), loginUrl).Query)["returnUrl"].ToString();
        Assert.StartsWith("/connect/authorize?", returnUrl, StringComparison.Ordinal);
        Assert.Contains("value=\"alice\"", await browser.GetStringAsync(loginUrl, CancellationToken), StringComparison.Ordinal);

        var login = await Forms.LoginAsync(browser, "alice", Password, returnUrl);
        Assert.Equal(returnUrl, login.Headers.Location!.ToString());

        var callback = await browser.GetAsync(returnUrl, CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.StartsWith(RedirectUri + "?code=", callback.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_openid_requests_get_an_access_token_only()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        Assert.Equal(0, (await factory.RunCliAsync("resource", "create", "orders-api", "--scope", "orders.read")).ExitCode);
        await CreatePublicClientAsync(factory, "spa", scopes: ["orders.read", "openid"]);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();

        var code = Code(await AuthorizeAsync(browser, Params("spa", "orders.read", challenge)));
        var body = await ReadJsonAsync(await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken));

        Assert.False(body.TryGetProperty("id_token", out _));
        var access = new JsonWebToken(body.GetProperty("access_token").GetString()!);
        Assert.Equal(["orders-api"], access.Audiences);
        Assert.Equal("orders.read", access.GetClaim("scope").Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await UserInfoAsync(browser, body.GetProperty("access_token").GetString()!)).StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1/callback", "http://127.0.0.1:51234/callback", true)]
    [InlineData("http://[::1]/callback", "http://[::1]:8080/callback", true)]
    [InlineData("http://127.0.0.1:5000/callback", "http://127.0.0.1/callback", true)]
    [InlineData("http://127.0.0.1/callback", "http://127.0.0.1:51234/other", false)]
    [InlineData("http://127.0.0.1/callback", "http://127.0.0.1.evil.test/callback", false)]
    [InlineData("http://localhost:4200/callback", "http://localhost:4201/callback", false)]
    public async Task Loopback_redirect_uris_match_on_any_port(string registered, string requested, bool allowed)
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "cli", redirectUri: registered);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (_, challenge) = NewPkce();

        var response = await browser.GetAsync((Params("cli", "openid", challenge) with { RedirectUri = requested }).Url, CancellationToken);

        if (allowed)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith(requested + "?code=", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
    }

    [Fact]
    public async Task Redirect_uri_query_components_are_kept()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa", redirectUri: "https://app.example.test/cb?tenant=a");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (_, challenge) = NewPkce();

        var location = await AuthorizeAsync(browser, Params("spa", "openid", challenge) with { RedirectUri = "https://app.example.test/cb?tenant=a" });

        Assert.StartsWith("https://app.example.test/cb?tenant=a&code=", location.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Form_post_response_mode_renders_an_auto_submitting_form()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (_, challenge) = NewPkce();

        var response = await browser.GetAsync((Params("spa", "openid", challenge) with { State = "<xyz>", ResponseMode = "form_post" }).Url, CancellationToken);
        var html = await response.Content.ReadAsStringAsync(CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains($"action=\"{RedirectUri}\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"code\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"&lt;xyz&gt;\"", html, StringComparison.Ordinal);
        Assert.Contains($"name=\"iss\" value=\"{ValkyrieAppFactory.DefaultIssuer}\"", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"submit\">", html, StringComparison.Ordinal);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = csp.Split("'nonce-")[1].Split('\'')[0];
        Assert.Contains($"<script nonce=\"{nonce}\">", html, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorize_accepts_post_requests()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (_, challenge) = NewPkce();

        var response = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(Params("spa", "openid", challenge).Pairs), CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(RedirectUri + "?code=", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // prompt and max_age

    [Fact]
    public async Task Prompt_none_without_a_session_returns_login_required()
    {
        await using var factory = CreateFactory();
        await CreatePublicClientAsync(factory, "spa");
        var (_, challenge) = NewPkce();

        var location = await AuthorizeAsync(factory.CreateBrowser(), Params("spa", "openid", challenge) with { Prompt = "none", State = "s1" });

        AssertRedirectError(location, "login_required");
        Assert.Equal("s1", QueryHelpers.ParseQuery(location.Query)["state"].ToString());
        Assert.Equal(ValkyrieAppFactory.DefaultIssuer, QueryHelpers.ParseQuery(location.Query)["iss"].ToString());
    }

    [Fact]
    public async Task Prompt_none_with_a_session_issues_a_code()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (_, challenge) = NewPkce();

        var location = await AuthorizeAsync(browser, Params("spa", "openid", challenge) with { Prompt = "none" });

        Assert.False(string.IsNullOrEmpty(Code(location)));
    }

    [Theory]
    [InlineData("none login")]
    [InlineData("bogus")]
    public async Task Invalid_prompt_values_are_rejected(string prompt)
    {
        await using var factory = CreateFactory();
        await CreatePublicClientAsync(factory, "spa");
        var (_, challenge) = NewPkce();

        var location = await AuthorizeAsync(factory.CreateBrowser(), Params("spa", "openid", challenge) with { Prompt = prompt });

        AssertRedirectError(location, "invalid_request");
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("login consent")]
    public async Task Prompt_consent_is_refused_because_there_is_no_consent_screen(string prompt)
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (_, challenge) = NewPkce();

        var location = await AuthorizeAsync(browser, Params("spa", "openid", challenge) with { Prompt = prompt });

        // OIDC Core §3.1.2.1: if the OP can't obtain consent it MUST return an error, typically consent_required.
        AssertRedirectError(location, "consent_required");
    }

    [Theory]
    [InlineData("login", null)]
    [InlineData("select_account", null)]
    [InlineData(null, "0")]
    public async Task Prompt_login_and_max_age_force_a_new_login(string? prompt, string? maxAge)
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        clock.Advance(TimeSpan.FromSeconds(5));
        var (verifier, challenge) = NewPkce();

        var toLogin = await browser.GetAsync((Params("spa", "openid", challenge) with { Prompt = prompt, MaxAge = maxAge }).Url, CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, toLogin.StatusCode);
        var loginUrl = toLogin.Headers.Location!.ToString();
        var loginQuery = QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), loginUrl).Query);
        Assert.Equal("login", loginQuery["prompt"].ToString());
        Assert.DoesNotContain("prompt", QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), loginQuery["returnUrl"].ToString()).Query).Keys);

        // The login page shows the form although a session exists.
        var page = await browser.GetAsync(loginUrl, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var login = await Forms.PostAsync(browser, loginUrl, new()
        {
            ["Identifier"] = "alice",
            ["Password"] = Password,
            ["ReturnUrl"] = loginQuery["returnUrl"].ToString(),
        });
        var callback = await browser.GetAsync(login.Headers.Location!.ToString(), CancellationToken);
        var code = Code(callback.Headers.Location!);
        var body = await ReadJsonAsync(await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken));
        var idToken = new JsonWebToken(body.GetProperty("id_token").GetString()!);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeSeconds(), idToken.GetPayloadValue<long>("auth_time"));

        // The replaced session was revoked.
        await using var scope = factory.Services.CreateAsyncScope();
        var sessions = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().UserSessions.ToListAsync(CancellationToken);
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.RevokedAt is null);
    }

    [Fact]
    public async Task Max_age_within_the_limit_reuses_the_session()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        clock.Advance(TimeSpan.FromSeconds(60));
        var (_, challenge) = NewPkce();

        var withinLimit = await AuthorizeAsync(browser, Params("spa", "openid", challenge) with { MaxAge = "60" });
        var exceeded = await browser.GetAsync((Params("spa", "openid", challenge) with { MaxAge = "59" }).Url, CancellationToken);
        var exceededSilently = await AuthorizeAsync(browser, Params("spa", "openid", challenge) with { MaxAge = "59", Prompt = "none" });

        Assert.False(string.IsNullOrEmpty(Code(withinLimit)));
        Assert.StartsWith("/account/login?", exceeded.Headers.Location!.ToString(), StringComparison.Ordinal);
        AssertRedirectError(exceededSilently, "login_required");
    }

    // Authorization request validation

    [Fact]
    public async Task Unknown_clients_and_unregistered_redirect_uris_are_never_redirected_to()
    {
        await using var factory = CreateFactory();
        await CreatePublicClientAsync(factory, "spa");
        await RunCliOkAsync(factory, "client", "disable", "spa");
        await CreatePublicClientAsync(factory, "spa2");
        var browser = factory.CreateBrowser();
        var (_, challenge) = NewPkce();

        AuthorizeParams[] requests =
        [
            Params("unknown", "openid", challenge),
            Params("spa", "openid", challenge),
            Params("spa2", "openid", challenge) with { RedirectUri = "https://evil.example.test/callback" },
            Params("spa2", "openid", challenge) with { RedirectUri = RedirectUri + "/" },
            Params("spa2", "openid", challenge) with { RedirectUri = "HTTPS://app.example.test/callback" },
            Params("spa2", "openid", challenge) with { RedirectUri = null },
        ];
        foreach (var request in requests)
        {
            var response = await browser.GetAsync(request.Url, CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        }

        var repeated = await browser.GetAsync(Params("spa2", "openid", challenge).Url + "&redirect_uri=" + Uri.EscapeDataString("https://evil.example.test/"), CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Null(repeated.Headers.Location);
    }

    [Theory]
    [InlineData("response_type", "token", "unsupported_response_type")]
    [InlineData("response_type", null, "invalid_request")]
    [InlineData("scope", "unknown.scope", "invalid_scope")]
    [InlineData("scope", "openid profile", "invalid_scope")]
    [InlineData("scope", null, "invalid_scope")]
    [InlineData("scope", "profile", "invalid_scope")]
    [InlineData("code_challenge_method", "plain", "invalid_request")]
    [InlineData("code_challenge", "too-short", "invalid_request")]
    [InlineData("request", "eyJhbGciOiJub25lIn0.e30.", "request_not_supported")]
    [InlineData("request_uri", "https://app.example.test/request.jwt", "request_uri_not_supported")]
    [InlineData("registration", "{}", "registration_not_supported")]
    [InlineData("max_age", "-1", "invalid_request")]
    [InlineData("response_mode", "fragment", "invalid_request")]
    public async Task Invalid_authorization_requests_are_redirected_with_an_error(string name, string? value, string error)
    {
        await using var factory = CreateFactory();
        await CreatePublicClientAsync(factory, "spa");
        var (_, challenge) = NewPkce();
        var pairs = Params("spa", "openid", challenge).Pairs.Where(p => p.Key != name).ToList();
        if (value is not null)
        {
            pairs.Add(new(name, value));
        }

        var response = await factory.CreateBrowser().GetAsync("/connect/authorize" + QueryString(pairs), CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        AssertRedirectError(response.Headers.Location!, error);
    }

    [Fact]
    public async Task Unknown_scopes_are_ignored_in_openid_requests_but_disallowed_scopes_are_rejected()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        Assert.Equal(0, (await factory.RunCliAsync("resource", "create", "orders-api", "--scope", "orders.read", "--scope", "orders.write")).ExitCode);
        await CreatePublicClientAsync(factory, "spa", scopes: ["openid", "orders.read"]);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();

        var code = Code(await AuthorizeAsync(browser, Params("spa", "openid address phone unknown.api orders.read", challenge)));
        var body = await ReadJsonAsync(await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken));
        Assert.Equal("openid orders.read", body.GetProperty("scope").GetString());

        AssertRedirectError(await AuthorizeAsync(browser, Params("spa", "openid orders.write", challenge)), "invalid_scope");
        AssertRedirectError(await AuthorizeAsync(browser, Params("spa", "openid email", challenge)), "invalid_scope");
    }

    [Fact]
    public async Task Repeated_authorization_parameters_are_rejected()
    {
        await using var factory = CreateFactory();
        await CreatePublicClientAsync(factory, "spa");
        var (_, challenge) = NewPkce();

        var location = await AuthorizeAsync(factory.CreateBrowser(), Params("spa", "openid", challenge), "&scope=openid");

        AssertRedirectError(location, "invalid_request");
    }

    [Fact]
    public async Task Clients_without_the_code_grant_are_unauthorized()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var client = await db.Clients.SingleAsync(CancellationToken);
            client.AllowedGrantTypes = [];
            await db.SaveChangesAsync(CancellationToken);
        }

        var (_, challenge) = NewPkce();
        var location = await AuthorizeAsync(factory.CreateBrowser(), Params("spa", "openid", challenge));

        AssertRedirectError(location, "unauthorized_client");
    }

    // Token endpoint attacks

    [Fact]
    public async Task Authorization_codes_are_single_use()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge)));

        var first = await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken);
        var replay = await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertErrorAsync(replay, HttpStatusCode.BadRequest, "invalid_grant");
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .AnyAsync(e => e.EventType == "token.rejected" && e.Details!.Contains("code_replayed"), CancellationToken));
    }

    [Fact]
    public async Task A_failed_redemption_consumes_the_code()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge)));

        var wrong = await browser.SendAsync(CodeRequest(code, NewPkce().Verifier, clientId: "spa"), CancellationToken);
        var retry = await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken);

        await AssertErrorAsync(wrong, HttpStatusCode.BadRequest, "invalid_grant");
        await AssertErrorAsync(retry, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public async Task Authorization_codes_expire_after_60_seconds(int seconds, bool valid)
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge)));

        clock.Advance(TimeSpan.FromSeconds(seconds));
        var response = await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken);

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
    public async Task Codes_are_bound_to_the_redirect_uri_and_client()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        await CreatePublicClientAsync(factory, "other");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var (verifier1, challenge1) = NewPkce();
        var code1 = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge1)));
        var wrongRedirect = await browser.SendAsync(CodeRequest(code1, verifier1, clientId: "spa", redirectUri: RedirectUri + "/other"), CancellationToken);

        var (verifier2, challenge2) = NewPkce();
        var code2 = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge2)));
        var missingRedirect = await browser.SendAsync(TokenRequest(Form(("grant_type", "authorization_code"), ("code", code2), ("code_verifier", verifier2), ("client_id", "spa"))), CancellationToken);

        var (verifier3, challenge3) = NewPkce();
        var code3 = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge3)));
        var otherClient = await browser.SendAsync(CodeRequest(code3, verifier3, clientId: "other"), CancellationToken);

        await AssertErrorAsync(wrongRedirect, HttpStatusCode.BadRequest, "invalid_grant");
        await AssertErrorAsync(missingRedirect, HttpStatusCode.BadRequest, "invalid_grant");
        await AssertErrorAsync(otherClient, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Fact]
    public async Task Pkce_verifier_must_match_the_challenge()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        var (_, challenge1) = NewPkce();
        var code1 = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge1)));
        var missing = await browser.SendAsync(TokenRequest(Form(("grant_type", "authorization_code"), ("code", code1), ("redirect_uri", RedirectUri), ("client_id", "spa"))), CancellationToken);

        var (verifier2, challenge2) = NewPkce();
        var code2 = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge2)));
        var malformed = await browser.SendAsync(CodeRequest(code2, verifier2 + "!", clientId: "spa"), CancellationToken);

        await AssertErrorAsync(missing, HttpStatusCode.BadRequest, "invalid_grant");
        await AssertErrorAsync(malformed, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Fact]
    public async Task Confidential_clients_may_skip_pkce_only_when_allowed_and_cannot_downgrade()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        var strict = await CreateClientAsync(factory, "strict", ["openid"]);
        var relaxed = await CreateClientAsync(factory, "relaxed", ["openid"], "--no-pkce");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);

        AssertRedirectError(await AuthorizeAsync(browser, Params("strict", "openid", challenge: null)), "invalid_request");

        var code = Code(await AuthorizeAsync(browser, Params("relaxed", "openid", challenge: null)));
        var ok = await browser.SendAsync(CodeRequest(code, verifier: null, Basic("relaxed", relaxed)), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // RFC 9700 A2.1.1: a code_verifier for a code issued without a challenge is rejected.
        var downgrade = Code(await AuthorizeAsync(browser, Params("relaxed", "openid", challenge: null)));
        var rejected = await browser.SendAsync(CodeRequest(downgrade, NewPkce().Verifier, Basic("relaxed", relaxed)), CancellationToken);
        await AssertErrorAsync(rejected, HttpStatusCode.BadRequest, "invalid_grant");
        Assert.NotEmpty(strict);
    }

    [Fact]
    public async Task Codes_stop_working_when_the_session_ends()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge)));

        await Forms.PostAsync(browser, "/account/logout", []);
        var response = await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Fact]
    public async Task Disabled_clients_cannot_redeem_codes()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge)));

        await RunCliOkAsync(factory, "client", "disable", "spa");
        var response = await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Userinfo

    [Fact]
    public async Task Userinfo_rejects_missing_invalid_and_wrong_tokens()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await CreateUserAsync(factory, "alice");
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge)));
        var body = await ReadJsonAsync(await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken));
        var accessToken = body.GetProperty("access_token").GetString()!;
        var idToken = body.GetProperty("id_token").GetString()!;

        var none = await browser.GetAsync("/connect/userinfo", CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.DoesNotContain("error=", none.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        var query = await browser.GetAsync("/connect/userinfo?access_token=" + accessToken, CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);

        AssertInvalidToken(await UserInfoAsync(browser, idToken));
        AssertInvalidToken(await UserInfoAsync(browser, accessToken[..^4] + "AAAA"));

        var form = new HttpRequestMessage(HttpMethod.Post, "/connect/userinfo") { Content = new FormUrlEncodedContent([new("access_token", accessToken)]) };
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(form, CancellationToken)).StatusCode);

        var both = new HttpRequestMessage(HttpMethod.Post, "/connect/userinfo") { Content = new FormUrlEncodedContent([new("access_token", accessToken)]) };
        both.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(both, CancellationToken)).StatusCode);

        // Within the clock skew, then expired.
        clock.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(29));
        Assert.Equal(HttpStatusCode.OK, (await UserInfoAsync(browser, accessToken)).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(1));
        AssertInvalidToken(await UserInfoAsync(browser, accessToken));
    }

    [Fact]
    public async Task Userinfo_rejects_client_credentials_tokens_and_disabled_users()
    {
        await using var factory = CreateFactory();
        await CreateUserAsync(factory, "alice");
        Assert.Equal(0, (await factory.RunCliAsync("resource", "create", "orders-api", "--scope", "orders.read")).ExitCode);
        var (_, output, _) = await factory.RunCliAsync("client", "create", "billing", "--scope", "orders.read");
        var billingSecret = output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("client_secret: ", StringComparison.Ordinal))["client_secret: ".Length..];
        await CreatePublicClientAsync(factory, "spa");
        var browser = factory.CreateBrowser();

        var cc = await ReadJsonAsync(await browser.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", billingSecret)), CancellationToken));
        var ccResponse = await UserInfoAsync(browser, cc.GetProperty("access_token").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, ccResponse.StatusCode);
        Assert.Contains("insufficient_scope", ccResponse.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        await Forms.LoginAsync(browser, "alice", Password);
        var (verifier, challenge) = NewPkce();
        var code = Code(await AuthorizeAsync(browser, Params("spa", "openid", challenge)));
        var accessToken = (await ReadJsonAsync(await browser.SendAsync(CodeRequest(code, verifier, clientId: "spa"), CancellationToken))).GetProperty("access_token").GetString()!;
        await RunCliOkAsync(factory, "user", "disable", "alice");

        AssertInvalidToken(await UserInfoAsync(browser, accessToken));
    }

    // CORS

    [Fact]
    public async Task Cors_allows_registered_origins_on_token_and_userinfo_and_any_origin_on_metadata()
    {
        await using var factory = CreateFactory();
        await CreatePublicClientAsync(factory, "spa", corsOrigin: SpaOrigin);
        var client = factory.CreateClient();

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/connect/token");
        preflight.Headers.Add("Origin", SpaOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var preflightResponse = await client.SendAsync(preflight, CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal(SpaOrigin, preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", preflightResponse.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.Ordinal);

        var token = TokenRequest(Form(("grant_type", "authorization_code"), ("client_id", "spa"), ("code", "x"), ("redirect_uri", RedirectUri)));
        token.Headers.Add("Origin", SpaOrigin);
        var tokenResponse = await client.SendAsync(token, CancellationToken);
        Assert.Equal(SpaOrigin, tokenResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(tokenResponse.Headers.Contains("Access-Control-Allow-Credentials"));

        var evil = new HttpRequestMessage(HttpMethod.Options, "/connect/userinfo");
        evil.Headers.Add("Origin", "https://evil.example.test");
        evil.Headers.Add("Access-Control-Request-Method", "GET");
        Assert.False((await client.SendAsync(evil, CancellationToken)).Headers.Contains("Access-Control-Allow-Origin"));

        var discovery = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        discovery.Headers.Add("Origin", "https://anywhere.example.test");
        Assert.Equal("*", (await client.SendAsync(discovery, CancellationToken)).Headers.GetValues("Access-Control-Allow-Origin").Single());

        var authorize = new HttpRequestMessage(HttpMethod.Get, "/connect/authorize");
        authorize.Headers.Add("Origin", SpaOrigin);
        Assert.False((await client.SendAsync(authorize, CancellationToken)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    // CLI

    [Theory]
    [InlineData("--public", "--grant", "client_credentials")]
    [InlineData("--grant", "authorization_code")]
    [InlineData("--grant", "authorization_code", "--redirect-uri", "http://app.example.test/callback")]
    [InlineData("--grant", "authorization_code", "--redirect-uri", "https://app.example.test/callback#fragment")]
    [InlineData("--grant", "authorization_code", "--redirect-uri", "/relative")]
    [InlineData("--grant", "authorization_code", "--redirect-uri", RedirectUri, "--cors-origin", "https://spa.example.test/")]
    [InlineData("--grant", "implicit")]
    [InlineData("--public", "--grant", "authorization_code", "--redirect-uri", RedirectUri, "--no-pkce")]
    public async Task Cli_rejects_invalid_client_configurations(params string[] options)
    {
        await using var factory = CreateFactory();

        var (exitCode, _, _) = await factory.RunCliAsync(["client", "create", "bad", "--scope", "openid", .. options]);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Cli_manages_redirect_uris_and_cors_origins()
    {
        await using var factory = CreateFactory();
        await CreatePublicClientAsync(factory, "spa");

        await RunCliOkAsync(factory, "client", "add-redirect-uri", "spa", "https://app.example.test/second");
        await RunCliOkAsync(factory, "client", "remove-redirect-uri", "spa", RedirectUri);
        await RunCliOkAsync(factory, "client", "add-cors-origin", "spa", SpaOrigin);
        Assert.NotEqual(0, (await factory.RunCliAsync("client", "remove-redirect-uri", "spa", "https://app.example.test/second")).ExitCode);
        Assert.NotEqual(0, (await factory.RunCliAsync("client", "add-secret", "spa")).ExitCode);

        var (_, list, _) = await factory.RunCliAsync("client", "list");
        Assert.Contains("redirect_uri https://app.example.test/second", list, StringComparison.Ordinal);
        Assert.DoesNotContain("redirect_uri " + RedirectUri + "\n", list, StringComparison.Ordinal);
        Assert.Contains("cors_origin " + SpaOrigin, list, StringComparison.Ordinal);
    }

    // Helpers

    private sealed record AuthorizeParams(string ClientId, string Scope, string? Challenge)
    {
        public string? RedirectUri { get; init; } = AuthorizationTests.RedirectUri;
        public string? State { get; init; }
        public string? Nonce { get; init; }
        public string? Prompt { get; init; }
        public string? MaxAge { get; init; }
        public string? ResponseMode { get; init; }
        public string? LoginHint { get; init; }

        public List<KeyValuePair<string, string>> Pairs
        {
            get
            {
                List<KeyValuePair<string, string>> pairs = [new("response_type", "code"), new("client_id", ClientId), new("scope", Scope)];
                Add(pairs, "redirect_uri", RedirectUri);
                Add(pairs, "state", State);
                Add(pairs, "nonce", Nonce);
                Add(pairs, "prompt", Prompt);
                Add(pairs, "max_age", MaxAge);
                Add(pairs, "response_mode", ResponseMode);
                Add(pairs, "login_hint", LoginHint);
                if (Challenge is not null)
                {
                    pairs.Add(new("code_challenge", Challenge));
                    pairs.Add(new("code_challenge_method", "S256"));
                }

                return pairs;
            }
        }

        public string Url => "/connect/authorize" + QueryString(Pairs);

        private static void Add(List<KeyValuePair<string, string>> pairs, string name, string? value)
        {
            if (value is not null)
            {
                pairs.Add(new(name, value));
            }
        }
    }

    private static AuthorizeParams Params(string clientId, string scope, string? challenge) => new(clientId, scope, challenge);

    private static string QueryString(IEnumerable<KeyValuePair<string, string>> pairs) =>
        "?" + string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static async Task<Uri> AuthorizeAsync(HttpClient browser, AuthorizeParams parameters, string extra = "")
    {
        var response = await browser.GetAsync(parameters.Url + extra, CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(CancellationToken));
        return response.Headers.Location!;
    }

    private static string Code(Uri location)
    {
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.True(query.ContainsKey("code"), location.ToString());
        return query["code"].ToString();
    }

    private static void AssertRedirectError(Uri location, string error)
    {
        Assert.StartsWith(RedirectUri + "?", location.ToString(), StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(error, query["error"].ToString());
        Assert.Equal(ValkyrieAppFactory.DefaultIssuer, query["iss"].ToString());
        Assert.False(query.ContainsKey("code"));
    }

    private static void AssertInvalidToken(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("error=\"invalid_token\"", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    private static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string AtHash(string accessToken) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)).AsSpan(0, 16));

    private static HttpRequestMessage CodeRequest(string code, string? verifier, AuthenticationHeaderValue? authorization = null, string? clientId = null, string redirectUri = RedirectUri)
    {
        List<(string, string)> values = [("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirectUri)];
        if (verifier is not null)
        {
            values.Add(("code_verifier", verifier));
        }

        if (clientId is not null)
        {
            values.Add(("client_id", clientId));
        }

        return TokenRequest(Form([.. values]), authorization);
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

    private static Task<HttpResponseMessage> UserInfoAsync(HttpClient client, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonWebToken> ValidateIdTokenAsync(HttpClient client, string token, string audience)
    {
        var jwks = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks.json", CancellationToken));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = ValkyrieAppFactory.DefaultIssuer,
            ValidAudience = audience,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateLifetime = false,
        });
        Assert.True(result.IsValid, result.Exception?.ToString());
        return (JsonWebToken)result.SecurityToken;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(error, (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    private static async Task CreateUserAsync(ValkyrieAppFactory factory, string userName, string? email = null)
    {
        string[] args = email is null ? ["user", "create", userName] : ["user", "create", userName, "--email", email];
        var result = await factory.RunCliWithInputAsync(Password + "\n", args);
        Assert.True(result.ExitCode == 0, result.Error);
    }

    private static async Task AddUserDataAsync(ValkyrieAppFactory factory, string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var user = await db.Users.Include(u => u.Roles).Include(u => u.Groups).Include(u => u.Claims)
            .SingleAsync(u => u.UserName == userName, CancellationToken);
        user.DisplayName = "Alice Example";
        user.Roles.Add(await db.Roles.SingleAsync(r => r.Name == Role.Admin, CancellationToken));
        user.Groups.Add(new Group { Name = "engineering" });
        user.Claims.Add(new UserClaim { UserId = user.Id, Type = "favorite_color", Value = "blue" });
        // Reserved names can't override protocol or standard claims.
        user.Claims.Add(new UserClaim { UserId = user.Id, Type = "sub", Value = "spoofed" });
        user.Claims.Add(new UserClaim { UserId = user.Id, Type = "email", Value = "spoofed@example.test" });
        await db.SaveChangesAsync(CancellationToken);
    }

    private static async Task<Guid> UserIdAsync(ValkyrieAppFactory factory, string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users
            .Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync(CancellationToken);
    }

    private static async Task<string> CreateClientAsync(ValkyrieAppFactory factory, string clientId, string[] scopes, params string[] options)
    {
        var (exitCode, output, error) = await factory.RunCliAsync(
            ["client", "create", clientId, "--grant", "authorization_code", "--redirect-uri", RedirectUri,
                .. scopes.SelectMany(s => new[] { "--scope", s }), .. options]);
        Assert.True(exitCode == 0, error);
        return output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("client_secret: ", StringComparison.Ordinal))["client_secret: ".Length..];
    }

    private static async Task CreatePublicClientAsync(
        ValkyrieAppFactory factory, string clientId, string[]? scopes = null, string redirectUri = RedirectUri, string? corsOrigin = null)
    {
        string[] scopeArgs = [.. (scopes ?? ["openid"]).SelectMany(s => new[] { "--scope", s })];
        string[] corsArgs = corsOrigin is null ? [] : ["--cors-origin", corsOrigin];
        await RunCliOkAsync(factory, ["client", "create", clientId, "--public", "--grant", "authorization_code", "--redirect-uri", redirectUri, .. scopeArgs, .. corsArgs]);
    }

    private static async Task RunCliOkAsync(ValkyrieAppFactory factory, params string[] args)
    {
        var (exitCode, _, error) = await factory.RunCliAsync(args);
        Assert.True(exitCode == 0, error);
    }
}
