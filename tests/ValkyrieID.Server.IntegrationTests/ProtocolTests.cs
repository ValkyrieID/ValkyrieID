using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ValkyrieID.Data;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Discovery, JWKS, token endpoint, and CLI tests run against every supported database provider.</summary>
public abstract class ProtocolTests
{
    protected abstract ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Discovery and JWKS

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("http://id.example.test")]
    [InlineData("https://id.example.test/?tenant=1")]
    [InlineData("https://id.example.test/#fragment")]
    [InlineData("https://user@id.example.test")]
    public async Task Startup_fails_without_a_valid_issuer(string? issuer)
    {
        await using var factory = CreateFactory(settings: new() { ["Issuer:Url"] = issuer });

        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains("Issuer:Url", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:5102")]
    [InlineData("http://127.0.0.1:8080/")]
    public async Task Http_issuer_is_allowed_for_loopback(string issuer)
    {
        await using var factory = CreateFactory(settings: new() { ["Issuer:Url"] = issuer });

        var document = await ReadJsonAsync(await factory.CreateClient().GetAsync("/.well-known/openid-configuration", CancellationToken));

        Assert.Equal(issuer.TrimEnd('/'), document.GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task Discovery_uses_the_configured_issuer_even_with_spoofed_host_headers()
    {
        await using var factory = CreateFactory();
        var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        request.Headers.Host = "evil.example";
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        request.Headers.Add("X-Forwarded-Proto", "http");

        var response = await factory.CreateClient().SendAsync(request, CancellationToken);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ValkyrieAppFactory.DefaultIssuer, document.GetProperty("issuer").GetString());
        Assert.Equal($"{ValkyrieAppFactory.DefaultIssuer}/.well-known/jwks.json", document.GetProperty("jwks_uri").GetString());
        Assert.Equal($"{ValkyrieAppFactory.DefaultIssuer}/connect/token", document.GetProperty("token_endpoint").GetString());
        Assert.Equal(["authorization_code", "client_credentials", "refresh_token"], Strings(document.GetProperty("grant_types_supported")));
        Assert.Equal(["client_secret_basic", "client_secret_post", "none"], Strings(document.GetProperty("token_endpoint_auth_methods_supported")));
    }

    [Fact]
    public async Task Jwks_publishes_the_public_signing_key_only()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync("/.well-known/jwks.json", CancellationToken);
        var keys = (await ReadJsonAsync(response)).GetProperty("keys").EnumerateArray().ToList();

        var key = Assert.Single(keys);
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.Equal("sig", key.GetProperty("use").GetString());
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.False(string.IsNullOrEmpty(key.GetProperty("kid").GetString()));
        Assert.Equal(256, Base64UrlEncoder.DecodeBytes(key.GetProperty("n").GetString()).Length);
        Assert.False(key.TryGetProperty("d", out _));
        Assert.False(key.TryGetProperty("p", out _));
    }

    [Fact]
    public async Task Signing_keys_are_encrypted_at_rest()
    {
        await using var factory = CreateFactory();
        _ = factory.Server;

        await using var scope = factory.Services.CreateAsyncScope();
        var key = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().SigningKeys.SingleAsync(CancellationToken);

        using var rsa = RSA.Create();
        Assert.ThrowsAny<CryptographicException>(() => rsa.ImportPkcs8PrivateKey(key.EncryptedPrivateKey, out _));
    }

    // client_credentials success paths

    [Fact]
    public async Task Client_credentials_with_basic_auth_issues_a_valid_rfc9068_access_token()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read", "orders.write");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");
        var client = factory.CreateClient();

        var response = await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials"), ("scope", "orders.read")), Basic("billing", secret)), CancellationToken);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(1800, body.GetProperty("expires_in").GetInt64());
        Assert.Equal("orders.read", body.GetProperty("scope").GetString());

        var token = await ValidateAsync(client, body.GetProperty("access_token").GetString()!, "orders-api");
        Assert.Equal("at+jwt", token.Typ);
        Assert.Equal("billing", token.Subject);
        Assert.Equal("billing", token.GetClaim("client_id").Value);
        Assert.Equal("orders.read", token.GetClaim("scope").Value);
        Assert.False(string.IsNullOrEmpty(token.Id));
        Assert.Equal(TimeSpan.FromMinutes(30), token.ValidTo - token.IssuedAt);
    }

    [Fact]
    public async Task Client_credentials_with_client_secret_post_succeeds()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");

        var response = await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials"), ("client_id", "billing"), ("client_secret", secret))), CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Basic_auth_credentials_are_form_url_decoded()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "svc:a+b%", "orders.read");

        // RFC 6749 §2.3.1: the client id is form-urlencoded before Basic encoding.
        var response = await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials")), Basic("svc%3Aa%2Bb%25", secret, encode: false)), CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Omitted_scope_grants_all_allowed_scopes_with_one_audience_per_resource()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        await CreateResourceAsync(factory, "stock-api", "stock.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read", "stock.read");
        var client = factory.CreateClient();

        var response = await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", secret)), CancellationToken);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("orders.read stock.read", body.GetProperty("scope").GetString());
        var token = await ValidateAsync(client, body.GetProperty("access_token").GetString()!, "orders-api");
        Assert.Equal(["orders-api", "stock-api"], token.Audiences.Order());
    }

    [Fact]
    public async Task Client_access_token_lifetime_override_is_used()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", ["orders.read"], "--access-token-lifetime", "300");

        var body = await ReadJsonAsync(await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", secret)), CancellationToken));

        Assert.Equal(300, body.GetProperty("expires_in").GetInt64());
    }

    [Fact]
    public async Task Multiple_secrets_allow_rotation_and_removed_secrets_stop_working()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var oldSecret = await CreateClientAsync(factory, "billing", "orders.read");
        var (exitCode, output, _) = await factory.RunCliAsync("client", "add-secret", "billing", "--description", "rotation");
        Assert.Equal(0, exitCode);
        var newSecret = Field(output, "client_secret");
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", oldSecret)), CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", newSecret)), CancellationToken)).StatusCode);

        string oldSecretId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var secrets = await db.Clients.Where(c => c.ClientId == "billing").SelectMany(c => c.Secrets).ToListAsync(CancellationToken);
            oldSecretId = secrets.Single(s => s.Description is null).Id.ToString();
        }

        Assert.Equal(0, (await factory.RunCliAsync("client", "remove-secret", "billing", oldSecretId)).ExitCode);

        await AssertInvalidClientAsync(await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", oldSecret)), CancellationToken));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", newSecret)), CancellationToken)).StatusCode);
    }

    // Client authentication failures

    [Fact]
    public async Task Wrong_secret_is_rejected_and_audited_without_the_secret()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        await CreateClientAsync(factory, "billing", "orders.read");

        var response = await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", "wrong-secret-value")), CancellationToken);

        await AssertInvalidClientAsync(response);
        await using var scope = factory.Services.CreateAsyncScope();
        var entries = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .Where(e => e.EventType == "client.authentication_failed").ToListAsync(CancellationToken);
        var entry = Assert.Single(entries);
        Assert.False(entry.Success);
        Assert.Equal("billing", entry.ClientId);
        Assert.DoesNotContain("wrong-secret-value", entry.Details ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_client_is_rejected()
    {
        await using var factory = CreateFactory();

        await AssertInvalidClientAsync(await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials")), Basic("nobody", "secret")), CancellationToken));
    }

    [Fact]
    public async Task Missing_client_authentication_is_rejected()
    {
        await using var factory = CreateFactory();

        await AssertInvalidClientAsync(await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials"), ("client_id", "billing"))), CancellationToken));
    }

    [Fact]
    public async Task Malformed_basic_header_is_rejected()
    {
        await using var factory = CreateFactory();
        var request = TokenRequest(Form(("grant_type", "client_credentials")));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "not base64!");

        await AssertInvalidClientAsync(await factory.CreateClient().SendAsync(request, CancellationToken));
    }

    [Fact]
    public async Task Disabled_client_is_rejected()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");
        Assert.Equal(0, (await factory.RunCliAsync("client", "disable", "billing")).ExitCode);

        await AssertInvalidClientAsync(await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", secret)), CancellationToken));
    }

    [Fact]
    public async Task Expired_secret_is_rejected()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var factory = CreateFactory(time);
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", ["orders.read"], "--secret-expires-days", "1");
        var client = factory.CreateClient();

        time.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", secret)), CancellationToken)).StatusCode);

        time.Advance(TimeSpan.FromSeconds(1));
        await AssertInvalidClientAsync(await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", secret)), CancellationToken));
    }

    [Fact]
    public async Task Using_two_authentication_methods_is_rejected()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");

        var response = await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials"), ("client_secret", secret)), Basic("billing", secret)), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Body_client_id_must_match_basic_client_id()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");

        var response = await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials"), ("client_id", "other")), Basic("billing", secret)), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    // Scope abuse

    [Theory]
    [InlineData("orders.write")]
    [InlineData("orders.read orders.write")]
    [InlineData("openid")]
    [InlineData("does.not.exist")]
    [InlineData("orders.read  orders.read")]
    [InlineData("")]
    public async Task Disallowed_or_malformed_scopes_are_rejected(string scope)
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read", "orders.write");
        var secret = await CreateClientAsync(factory, "billing", "orders.read", "openid");

        var response = await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials"), ("scope", scope)), Basic("billing", secret)), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_scope");
    }

    [Fact]
    public async Task Scopes_of_a_disabled_resource_are_rejected()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");
        Assert.Equal(0, (await factory.RunCliAsync("resource", "disable", "orders-api")).ExitCode);

        var response = await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials"), ("scope", "orders.read")), Basic("billing", secret)), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_scope");
    }

    // Request format

    [Fact]
    public async Task Unsupported_grant_type_is_rejected()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");

        var response = await factory.CreateClient().SendAsync(TokenRequest(Form(("grant_type", "password")), Basic("billing", secret)), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "unsupported_grant_type");
    }

    [Fact]
    public async Task Missing_grant_type_is_rejected()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().SendAsync(TokenRequest(Form(("scope", "x"))), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Repeated_parameters_are_rejected()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");

        var response = await factory.CreateClient().SendAsync(
            TokenRequest(Form(("grant_type", "client_credentials"), ("scope", "orders.read"), ("scope", "orders.read")), Basic("billing", secret)), CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Json_body_is_rejected()
    {
        await using var factory = CreateFactory();
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new StringContent("""{"grant_type":"client_credentials"}""", Encoding.UTF8, "application/json"),
        };

        await AssertErrorAsync(await factory.CreateClient().SendAsync(request, CancellationToken), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Get_is_not_allowed()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync("/connect/token?grant_type=client_credentials", CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // Key rotation

    [Fact]
    public async Task Keys_are_published_before_use_and_retired_keys_remain_until_their_tokens_expire()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var factory = CreateFactory(time, new()
        {
            ["SigningKeys:RotationInterval"] = "10.00:00:00",
            ["SigningKeys:PublishLead"] = "1.00:00:00",
        });
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");
        var client = factory.CreateClient();
        var firstKid = Assert.Single(await KidsAsync(client));

        // Day 9: the next key is created and published, but the first key still signs.
        time.Advance(TimeSpan.FromDays(9));
        await RotateAsync(factory);
        var kids = await KidsAsync(client);
        Assert.Equal(2, kids.Count);
        var secondKid = Assert.Single(kids, k => k != firstKid);

        // A minute before the second key activates, the first key still signs.
        time.Advance(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(1));
        var tokenFromFirstKey = await IssueAsync(client, secret);
        Assert.Equal(firstKid, new JsonWebToken(tokenFromFirstKey).Kid);

        // Day 10: the second key activates. Tokens from the first key still validate.
        time.Advance(TimeSpan.FromMinutes(1));
        await RotateAsync(factory);
        Assert.Equal(secondKid, new JsonWebToken(await IssueAsync(client, secret)).Kid);
        Assert.Contains(firstKid, await KidsAsync(client));
        await ValidateAsync(client, tokenFromFirstKey, "orders-api", time.GetUtcNow());

        // Just before its tokens can have expired, the first key is still published.
        time.Advance(TimeSpan.FromMinutes(30));
        await RotateAsync(factory);
        Assert.Contains(firstKid, await KidsAsync(client));

        // Access token lifetime (30 min) + clock skew (30 s) after retirement, it is removed.
        time.Advance(TimeSpan.FromSeconds(30));
        await RotateAsync(factory);
        Assert.DoesNotContain(firstKid, await KidsAsync(client));
    }

    // CLI

    [Fact]
    public async Task Cli_rejects_unknown_scopes_identity_scopes_for_resources_and_duplicates()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");

        Assert.Equal(1, (await factory.RunCliAsync("client", "create", "billing", "--scope", "nope")).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("resource", "create", "id-api", "--scope", "openid")).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("resource", "create", "other-api", "--scope", "orders.read")).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("client", "create", "billing", "--scope", "orders.read", "--bogus", "x")).ExitCode);
        await CreateClientAsync(factory, "billing", "orders.read");
        Assert.Equal(1, (await factory.RunCliAsync("client", "create", "billing", "--scope", "orders.read")).ExitCode);
    }

    [Fact]
    public async Task Cli_stores_only_a_hash_of_the_secret_and_audits_changes()
    {
        await using var factory = CreateFactory();
        await CreateResourceAsync(factory, "orders-api", "orders.read");
        var secret = await CreateClientAsync(factory, "billing", "orders.read");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var stored = await db.Clients.Where(c => c.ClientId == "billing").SelectMany(c => c.Secrets).SingleAsync(CancellationToken);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), stored.Hash);

        var audit = await db.AuditLog.Where(e => e.Actor == "cli").ToListAsync(CancellationToken);
        Assert.Contains(audit, e => e.EventType == "api_resource.created");
        Assert.Contains(audit, e => e.EventType == "client.created" && e.ClientId == "billing");
        Assert.All(audit, e => Assert.DoesNotContain(secret, e.Details ?? string.Empty, StringComparison.Ordinal));
    }

    // Helpers

    private static async Task CreateResourceAsync(ValkyrieAppFactory factory, string name, params string[] scopes)
    {
        var (exitCode, _, error) = await factory.RunCliAsync(["resource", "create", name, .. scopes.SelectMany(s => new[] { "--scope", s })]);
        Assert.True(exitCode == 0, error);
    }

    private static Task<string> CreateClientAsync(ValkyrieAppFactory factory, string clientId, params string[] scopes) =>
        CreateClientAsync(factory, clientId, scopes, []);

    private static async Task<string> CreateClientAsync(ValkyrieAppFactory factory, string clientId, string[] scopes, params string[] options)
    {
        var (exitCode, output, error) = await factory.RunCliAsync(
            ["client", "create", clientId, .. scopes.SelectMany(s => new[] { "--scope", s }), .. options]);
        Assert.True(exitCode == 0, error);
        return Field(output, "client_secret");
    }

    private static string Field(string output, string name) =>
        output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith(name + ": ", StringComparison.Ordinal))[(name.Length + 2)..];

    private static async Task RotateAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SigningKeyRotator>().RotateAsync(CancellationToken);
    }

    private static async Task<string> IssueAsync(HttpClient client, string secret)
    {
        var response = await client.SendAsync(TokenRequest(Form(("grant_type", "client_credentials")), Basic("billing", secret)), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("access_token").GetString()!;
    }

    private static async Task<List<string>> KidsAsync(HttpClient client)
    {
        var jwks = await ReadJsonAsync(await client.GetAsync("/.well-known/jwks.json", CancellationToken));
        return [.. jwks.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()!)];
    }

    /// <summary>Validates a token the way a resource server would: against the published JWKS.</summary>
    private static async Task<JsonWebToken> ValidateAsync(HttpClient client, string token, string audience, DateTimeOffset? now = null)
    {
        var jwks = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks.json", CancellationToken));
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = ValkyrieAppFactory.DefaultIssuer,
            ValidAudience = audience,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidTypes = ["at+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            // The server clock may be a test clock; validate lifetime separately below.
            ValidateLifetime = false,
        };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        Assert.True(result.IsValid, result.Exception?.ToString());
        var jwt = (JsonWebToken)result.SecurityToken;
        if (now is { } instant)
        {
            Assert.InRange(instant.UtcDateTime, jwt.ValidFrom, jwt.ValidTo);
        }

        return jwt;
    }

    private static HttpRequestMessage TokenRequest(FormUrlEncodedContent form, AuthenticationHeaderValue? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = authorization;
        return request;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)));

    private static AuthenticationHeaderValue Basic(string clientId, string secret, bool encode = true)
    {
        var user = encode ? Uri.EscapeDataString(clientId) : clientId;
        var password = Uri.EscapeDataString(secret);
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static async Task AssertInvalidClientAsync(HttpResponseMessage response)
    {
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "invalid_client");
        Assert.Equal("Basic", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(error, (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }
}
