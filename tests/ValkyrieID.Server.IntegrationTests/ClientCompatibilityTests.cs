using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>
/// Signs in through ASP.NET Core's own OpenID Connect handler and calls an API protected with its JWT bearer
/// handler, so the server is checked against a real client library rather than only our own tests.
/// </summary>
public abstract partial class ClientCompatibilityTests
{
    private const string Password = "correct horse battery staple";
    private const string RelyingParty = "https://rp.example.test";

    protected abstract ValkyrieAppFactory CreateFactory();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AspNetCore_OpenIdConnect_and_JwtBearer_handlers_work_end_to_end()
    {
        await using var factory = CreateFactory();
        Assert.Equal(0, (await factory.RunCliWithInputAsync(Password + "\n", "user", "create", "alice", "--email", "alice@example.test")).ExitCode);
        Assert.Equal(0, (await factory.RunCliAsync("resource", "create", "orders-api", "--scope", "orders.read")).ExitCode);
        var (exitCode, output, error) = await factory.RunCliAsync(
            "client", "create", "rp", "--grant", "authorization_code", "--redirect-uri", RelyingParty + "/signin-oidc",
            "--scope", "openid", "--scope", "profile", "--scope", "email", "--scope", "orders.read",
            // The ASP.NET Core handler only supports client_secret_post.
            "--allow-secret-post");
        Assert.True(exitCode == 0, error);
        var secret = output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("client_secret: ", StringComparison.Ordinal))["client_secret: ".Length..];

        await using var rp = await StartRelyingPartyAsync(factory, secret);
        using var rpBrowser = new HttpClient(new CookieContainerHandler { InnerHandler = rp.GetTestServer().CreateHandler() }) { BaseAddress = new Uri(RelyingParty) };
        var idpBrowser = factory.CreateBrowser();

        // 1. The relying party challenges and sends the browser to the authorization endpoint.
        var challenge = await rpBrowser.GetAsync("/", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var authorize = challenge.Headers.Location!;
        Assert.StartsWith(ValkyrieAppFactory.DefaultIssuer + "/connect/authorize?", authorize.ToString(), StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", authorize.Query, StringComparison.Ordinal);

        // 2. The user signs in and is sent back to the authorization request.
        var toLogin = await idpBrowser.GetAsync(authorize.PathAndQuery, CancellationToken);
        var returnUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), toLogin.Headers.Location!.ToString()).Query)["returnUrl"].ToString();
        await Forms.LoginAsync(idpBrowser, "alice", Password, returnUrl);
        var response = await idpBrowser.GetAsync(returnUrl, CancellationToken);

        // 3. The handler uses response_mode=form_post by default; post the form like a browser would.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(CancellationToken);
        var action = WebUtility.HtmlDecode(ActionPattern().Match(html).Groups[1].Value);
        Assert.Equal(RelyingParty + "/signin-oidc", action);
        var fields = HiddenFieldPattern().Matches(html).Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)));
        var callback = await rpBrowser.PostAsync("/signin-oidc", new FormUrlEncodedContent(fields), CancellationToken);
        Assert.True(callback.StatusCode == HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync(CancellationToken));

        // 4. The relying party has a signed-in user with claims from the ID token and userinfo.
        var home = await rpBrowser.GetAsync("/", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var body = JsonDocument.Parse(await home.Content.ReadAsStringAsync(CancellationToken)).RootElement;
        Assert.Equal("alice", body.GetProperty("name").GetString());
        Assert.Equal("alice@example.test", body.GetProperty("email").GetString());
        Assert.True(Guid.TryParse(body.GetProperty("sub").GetString(), out _));

        // 5. The access token is accepted by the JWT bearer handler for the API audience.
        var api = new HttpRequestMessage(HttpMethod.Get, "/api");
        api.Headers.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("access_token").GetString());
        var apiResponse = await rpBrowser.SendAsync(api, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);
        Assert.Equal(body.GetProperty("sub").GetString(), await apiResponse.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<WebApplication> StartRelyingPartyAsync(ValkyrieAppFactory factory, string secret)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.EnvironmentName = Environments.Development;
        var backchannel = factory.Server.CreateHandler();

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect(options =>
            {
                options.Authority = ValkyrieAppFactory.DefaultIssuer;
                options.BackchannelHttpHandler = backchannel;
                options.ClientId = "rp";
                options.ClientSecret = secret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.Scope.Add("orders.read");
                options.GetClaimsFromUserInfoEndpoint = true;
                options.SaveTokens = true;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "preferred_username";
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = ValkyrieAppFactory.DefaultIssuer;
                options.BackchannelHttpHandler = backchannel;
                options.Audience = "orders-api";
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidTypes = ["at+jwt"];
            });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/", async (HttpContext context) => await context.Response.WriteAsJsonAsync(new
        {
            sub = context.User.FindFirst("sub")?.Value,
            name = context.User.Identity?.Name,
            email = context.User.FindFirst("email")?.Value,
            access_token = await context.GetTokenAsync("access_token"),
        }, context.RequestAborted)).RequireAuthorization();
        app.MapGet("/api", (HttpContext context) => context.User.FindFirst("sub")?.Value)
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser());
        await app.StartAsync(CancellationToken);
        return app;
    }

    [GeneratedRegex("<form method=\"post\" action=\"([^\"]+)\">")]
    private static partial Regex ActionPattern();

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")]
    private static partial Regex HiddenFieldPattern();
}
