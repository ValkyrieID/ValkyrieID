using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Data;
using ValkyrieID.Server.AdminUi;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Serving the Angular admin UI under /admin, its headers, the enable switches, and its sign-in flow.</summary>
public abstract class AdminUiTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string AdminUiClientId = "valkyrieid-admin-ui";
    private const string AdminResource = "valkyrieid-admin";
    private const string Issuer = ValkyrieAppFactory.DefaultIssuer;
    private const string HashedScript = "main-4WZ2KXJQ.js";
    private const string HashedChunk = "chunk-DP7ZSER-.js";

    private const string IndexHtml = """
        <!doctype html>
        <html lang="en">
          <head>
            <base href="/admin/">
            <meta name="valkyrieid-issuer" content="">
            <link rel="stylesheet" href="styles-HMXEKXHJ.css">
          </head>
          <body><app-root></app-root><script src="main-4WZ2KXJQ.js" type="module"></script></body>
        </html>
        """;

    private readonly string _files = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));

    /// <summary>A temporary directory deleted after each test, e.g. for SQLite databases.</summary>
    protected string WorkDirectory => _files;
    protected AdminUiTests()
    {
        Directory.CreateDirectory(_files);
        File.WriteAllText(Path.Combine(_files, AdminUiExtensions.IndexFile), IndexHtml);
        File.WriteAllText(Path.Combine(_files, HashedScript), "console.log('admin');");
        File.WriteAllText(Path.Combine(_files, HashedChunk), "export {};");
        File.WriteAllText(Path.Combine(_files, "favicon.ico"), "icon");
        File.WriteAllText(Path.Combine(_files, "manifest.webmanifest"), "{}");
    }

    /// <summary>A factory serving the test UI files; the same <paramref name="database"/> name reuses one database.</summary>
    protected abstract ValkyrieAppFactory CreateFactoryCore(string adminUiDirectory, Dictionary<string, string?>? settings = null, string? database = null, string? masterKey = null);

    private ValkyrieAppFactory CreateFactory(Dictionary<string, string?>? settings = null, string? database = null, string? masterKey = null) =>
        CreateFactoryCore(_files, settings, database, masterKey);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Serving

    [Theory]
    [InlineData("/admin/")]
    [InlineData("/admin/users/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b")]
    [InlineData("/admin/callback?code=abc&state=xyz&iss=https%3A%2F%2Fid.example.test")]
    [InlineData("/ADMIN/clients")]
    public async Task Spa_routes_serve_the_index_page(string path)
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync(path, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        var html = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("<base href=\"/admin/\">", html, StringComparison.Ordinal);
        Assert.Contains($"<meta name=\"valkyrieid-issuer\" content=\"{Issuer}\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_admin_path_without_a_slash_redirects()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateBrowser().GetAsync("/admin?x=1", CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/?x=1", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Hashed_assets_are_cached_forever_and_other_files_revalidated()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        foreach (var file in new[] { HashedScript, HashedChunk })
        {
            var script = await client.GetAsync($"/admin/{file}", CancellationToken);
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
            Assert.Equal("public, max-age=31536000, immutable", script.Headers.CacheControl?.ToString());
        }

        var icon = await client.GetAsync("/admin/favicon.ico", CancellationToken);
        Assert.Equal("no-cache", icon.Headers.CacheControl?.ToString());

        var manifest = await client.GetAsync("/admin/manifest.webmanifest", CancellationToken);
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/admin/missing.js")]
    [InlineData("/admin/users/missing.css")]
    [InlineData("/admin/..%2Fappsettings.json")]
    public async Task Missing_files_are_not_found_instead_of_the_index_page(string path)
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync(path, CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_get_and_head_are_allowed()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().PostAsync("/admin/users", new StringContent("x"), CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Admin_api_paths_are_never_served_the_spa()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync("/api/admin/no-such-thing", CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_build_answers_not_found()
    {
        var empty = Path.Combine(_files, "empty");
        Directory.CreateDirectory(empty);
        await using var factory = CreateFactoryCore(empty);

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/admin/", CancellationToken)).StatusCode);
    }

    // Headers

    [Theory]
    [InlineData("/admin/")]
    [InlineData("/admin/users")]
    [InlineData("/admin/" + HashedScript)]
    [InlineData("/admin/missing.js")]
    public async Task The_admin_ui_has_its_own_strict_csp(string path)
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync(path, CancellationToken);

        var csp = Directives(response);
        Assert.Equal("'none'", csp["default-src"]);
        Assert.Equal("'self'", csp["script-src"]);
        Assert.Equal("'self'", csp["style-src"]);
        Assert.Equal("'self'", csp["connect-src"]);
        Assert.Equal("'none'", csp["frame-ancestors"]);
        Assert.Equal("'self'", csp["base-uri"]);
        Assert.Equal("'self'", csp["form-action"]);
        Assert.DoesNotContain(csp.Values, value => value.Contains("unsafe", StringComparison.Ordinal) || value.Contains('*', StringComparison.Ordinal));
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Theory]
    [InlineData("/account/login")]
    [InlineData("/administrator")]
    public async Task Other_pages_keep_the_login_pages_csp_without_scripts(string path)
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateBrowser().GetAsync(path, CancellationToken);

        var csp = Directives(response);
        Assert.Equal("'none'", csp["default-src"]);
        Assert.False(csp.ContainsKey("script-src"));
        Assert.False(csp.ContainsKey("connect-src"));
        Assert.Equal("'none'", csp["base-uri"]);
    }

    // Path base

    [Fact]
    public async Task Under_a_path_base_urls_come_from_the_issuer()
    {
        await using var factory = CreateFactory(new()
        {
            ["Issuer:Url"] = Issuer + "/auth",
            ["ReverseProxy:PathBase"] = "/auth",
        });

        var response = await factory.CreateClient().GetAsync("/auth/admin/users", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("<base href=\"/auth/admin/\">", html, StringComparison.Ordinal);
        Assert.Contains($"content=\"{Issuer}/auth\"", html, StringComparison.Ordinal);
        Assert.Equal("'self'", Directives(response)["script-src"]);

        var redirect = await factory.CreateBrowser().GetAsync("/auth/admin", CancellationToken);
        Assert.Equal("/auth/admin/", redirect.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task When_the_proxy_strips_the_path_base_the_base_href_still_includes_it()
    {
        await using var factory = CreateFactory(new() { ["Issuer:Url"] = Issuer + "/auth" });

        var html = await factory.CreateClient().GetStringAsync("/admin/", CancellationToken);

        Assert.Contains("<base href=\"/auth/admin/\">", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<base href=\"/admin/\">")]
    [InlineData("<base href=\"/admin/\" />")]
    [InlineData("<base href=\"/\"/>")]
    public void Rendering_replaces_the_base_and_issuer_tags_in_any_form(string baseTag)
    {
        var html = $"<head>{baseTag}<meta name=\"valkyrieid-issuer\" content=\"\" /></head>";

        var rendered = AdminUiExtensions.RenderIndex(html, new IssuerOptions { Url = "https://id.example.test/a&b/" });

        Assert.Equal("<head><base href=\"/a&amp;b/admin/\"><meta name=\"valkyrieid-issuer\" content=\"https://id.example.test/a&amp;b\"></head>", rendered);
    }

    // Switches

    [Fact]
    public async Task Turning_the_admin_ui_off_hides_it_and_disables_its_client()
    {
        await using var factory = CreateFactory(new() { ["AdminUI:Enabled"] = "false" });

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/admin/", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/admin/" + HashedScript, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/users", CancellationToken)).StatusCode);
        var (clientEnabled, resourceEnabled) = await BuiltInStateAsync(factory);
        Assert.False(clientEnabled);
        Assert.True(resourceEnabled);

        // The disabled client can't start a sign-in.
        await CreateAdminAsync(factory);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "admin", Password);
        var authorize = await browser.GetAsync(AuthorizeUrl(Pkce().Challenge), CancellationToken);
        Assert.NotEqual(HttpStatusCode.Redirect, authorize.StatusCode);
    }

    [Fact]
    public async Task Turning_the_admin_api_off_hides_it_and_the_ui_and_disables_admin_tokens()
    {
        await using var factory = CreateFactory(new() { ["AdminApi:Enabled"] = "false" });

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/api/admin/users", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/admin/", CancellationToken)).StatusCode);
        var (clientEnabled, resourceEnabled) = await BuiltInStateAsync(factory);
        Assert.False(clientEnabled);
        Assert.False(resourceEnabled);

        // The CLI keeps working.
        Assert.Equal(0, (await factory.RunCliAsync("client", "list")).ExitCode);
    }

    [Fact]
    public async Task Turning_the_admin_ui_back_on_restores_its_client()
    {
        var database = Guid.NewGuid().ToString("N");
        string masterKey;
        await using (var off = CreateFactory(new() { ["AdminApi:Enabled"] = "false" }, database))
        {
            masterKey = off.MasterKey;
            Assert.Equal((false, false), await BuiltInStateAsync(off));
        }

        await using var on = CreateFactory(database: database, masterKey: masterKey);

        Assert.Equal((true, true), await BuiltInStateAsync(on));
        await CreateAdminAsync(on);
        var browser = on.CreateBrowser();
        await Forms.LoginAsync(browser, "admin", Password);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(on, browser, prompt: "none")).StatusCode);
    }

    // The admin UI's sign-in, as oidc-client-ts runs it

    [Fact]
    public async Task Silent_sign_in_with_an_sso_session_returns_a_code_with_the_issuer()
    {
        await using var factory = CreateFactory();
        await CreateAdminAsync(factory);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "admin", Password);

        var token = await SignInAsync(factory, browser, prompt: "none");

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var json = JsonDocument.Parse(await token.Content.ReadAsStringAsync(CancellationToken)).RootElement;
        Assert.False(json.TryGetProperty("refresh_token", out _));
        Assert.Equal(600, json.GetProperty("expires_in").GetInt32());
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", json.GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/admin/users", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Silent_sign_in_without_an_sso_session_returns_login_required_to_the_callback()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateBrowser().GetAsync(AuthorizeUrl(Pkce().Challenge, prompt: "none", state: "s1"), CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(Issuer + "/admin/callback?", location.ToString(), StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("login_required", query["error"]);
        Assert.Equal("s1", query["state"]);
        Assert.Equal(Issuer, query["iss"]);
    }

    [Fact]
    public async Task The_code_needs_the_matching_pkce_verifier()
    {
        await using var factory = CreateFactory();
        await CreateAdminAsync(factory);
        var browser = factory.CreateBrowser();
        await Forms.LoginAsync(browser, "admin", Password);

        var response = await SignInAsync(factory, browser, prompt: "none", verifier: Pkce().Verifier);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_registered_callback_is_accepted()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateBrowser().GetAsync(
            AuthorizeUrl(Pkce().Challenge, redirectUri: Issuer + "/admin/callback/../../evil"), CancellationToken);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_files))
        {
            Directory.Delete(_files, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, string> Directives(HttpResponseMessage response) =>
        response.Headers.GetValues("Content-Security-Policy").Single()
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split(' ', 2))
            .ToDictionary(d => d[0], d => d.Length > 1 ? d[1] : string.Empty);

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string AuthorizeUrl(string challenge, string? prompt = null, string state = "state", string redirectUri = Issuer + "/admin/callback") =>
        $"/connect/authorize?response_type=code&client_id={AdminUiClientId}&scope={Uri.EscapeDataString("openid profile valkyrieid.admin")}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&code_challenge={challenge}&code_challenge_method=S256"
        + $"&state={state}&nonce=n" + (prompt is null ? string.Empty : $"&prompt={prompt}");

    /// <summary>The authorization code flow with PKCE; returns the token response.</summary>
    private static async Task<HttpResponseMessage> SignInAsync(ValkyrieAppFactory factory, HttpClient browser, string? prompt = null, string? verifier = null)
    {
        var pkce = Pkce();
        var authorize = await browser.GetAsync(AuthorizeUrl(pkce.Challenge, prompt), CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var query = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query);
        Assert.Equal(Issuer, query["iss"]);

        return await factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"), new("code", query["code"].ToString()),
            new("redirect_uri", Issuer + "/admin/callback"), new("code_verifier", verifier ?? pkce.Verifier), new("client_id", AdminUiClientId),
        ]), CancellationToken);
    }

    private static async Task CreateAdminAsync(ValkyrieAppFactory factory)
    {
        var created = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", "admin");
        Assert.True(created.ExitCode == 0, created.Error);
        var granted = await factory.RunCliAsync("admin", "grant-admin", "admin");
        Assert.True(granted.ExitCode == 0, granted.Error);
    }

    private static async Task<(bool Client, bool Resource)> BuiltInStateAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var client = await db.Clients.SingleAsync(c => c.ClientId == AdminUiClientId, CancellationToken);
        var resource = await db.ApiResources.SingleAsync(r => r.Name == AdminResource, CancellationToken);
        return (client.Enabled, resource.Enabled);
    }
}
