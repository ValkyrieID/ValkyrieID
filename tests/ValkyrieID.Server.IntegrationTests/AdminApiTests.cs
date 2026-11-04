using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Data;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>The Admin REST API: its authorization model and the management endpoints, run against every database provider.</summary>
public abstract class AdminApiTests
{
    private const string Password = "correct horse battery staple";
    private const string AdminUiClientId = "valkyrieid-admin-ui";
    private const string AdminResource = "valkyrieid-admin";
    private const string AdminScope = "valkyrieid.admin";
    private const string AdminRedirectUri = ValkyrieAppFactory.DefaultIssuer + "/admin/callback";

    protected abstract ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // Built-in entities

    [Fact]
    public async Task The_admin_ui_client_and_admin_resource_are_seeded_and_reserved()
    {
        await using var factory = CreateFactory();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var client = await db.Clients.Include(c => c.Secrets).SingleAsync(c => c.ClientId == AdminUiClientId, CancellationToken);
            Assert.True(client.BuiltIn);
            Assert.Equal(Core.Clients.ClientType.Public, client.Type);
            Assert.Equal(["authorization_code"], client.AllowedGrantTypes);
            Assert.Equal(["openid", "profile", AdminScope], client.AllowedScopes);
            Assert.Equal([AdminRedirectUri], client.RedirectUris);
            Assert.Equal([ValkyrieAppFactory.DefaultIssuer + "/admin/"], client.PostLogoutRedirectUris);
            Assert.Empty(client.AllowedCorsOrigins);
            Assert.True(client.PkceRequired);
            Assert.Equal(TimeSpan.FromMinutes(10), client.AccessTokenLifetime);
            Assert.Empty(client.Secrets);
            var resource = await db.ApiResources.Include(r => r.Scopes).SingleAsync(r => r.Name == AdminResource, CancellationToken);
            Assert.True(resource.BuiltIn);
            Assert.Equal([AdminScope], resource.Scopes.Select(s => s.Name));
        }

        Assert.Equal(1, (await factory.RunCliAsync("client", "create", AdminUiClientId, "--scope", "openid", "--grant", "authorization_code", "--redirect-uri", "https://evil.example.test/cb")).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("resource", "create", AdminResource, "--scope", "x.read")).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("resource", "create", "other-api", "--scope", AdminScope)).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("client", "add-redirect-uri", AdminUiClientId, "https://evil.example.test/cb")).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("client", "disable", AdminUiClientId)).ExitCode);
        Assert.Equal(1, (await factory.RunCliAsync("resource", "disable", AdminResource)).ExitCode);
    }

    [Fact]
    public async Task Seeding_is_idempotent_and_restores_the_built_in_configuration()
    {
        await using var factory = CreateFactory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var client = await db.Clients.SingleAsync(c => c.ClientId == AdminUiClientId, CancellationToken);
            client.RedirectUris = ["https://evil.example.test/cb"];
            client.Enabled = false;
            await db.SaveChangesAsync(CancellationToken);
        }

        for (var i = 0; i < 2; i++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Administration.BuiltInSeeder>().SeedAsync(CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var client = await db.Clients.SingleAsync(c => c.ClientId == AdminUiClientId, CancellationToken);
            Assert.Equal([AdminRedirectUri], client.RedirectUris);
            Assert.True(client.Enabled);
            Assert.Equal(1, await db.Clients.CountAsync(c => c.ClientId == AdminUiClientId, CancellationToken));
            Assert.Equal(1, await db.AuditLog.CountAsync(e => e.EventType == "client.updated" && e.Actor == "system", CancellationToken));
        }
    }

    [Fact]
    public async Task Built_in_entities_cannot_be_changed_through_the_api()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);
        var clients = await ReadJsonAsync(await api.GetAsync("/api/admin/clients?q=valkyrieid-admin-ui", CancellationToken));
        var clientId = clients.GetProperty("items")[0].GetProperty("id").GetString();
        var resources = await ReadJsonAsync(await api.GetAsync("/api/admin/resources?q=valkyrieid-admin", CancellationToken));
        var resourceId = resources.GetProperty("items")[0].GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync($"/api/admin/clients/{clientId}/disable", null, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.DeleteAsync($"/api/admin/clients/{clientId}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PutAsJsonAsync($"/api/admin/clients/{clientId}", new
        {
            grantTypes = new[] { "authorization_code" },
            scopes = new[] { "openid", AdminScope },
            redirectUris = new[] { "https://evil.example.test/cb" },
        }, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync($"/api/admin/resources/{resourceId}/disable", null, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.DeleteAsync($"/api/admin/resources/{resourceId}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync("/api/admin/clients", new
        {
            clientId = AdminUiClientId,
            type = "public",
            grantTypes = new[] { "authorization_code" },
            scopes = new[] { "openid" },
            redirectUris = new[] { "https://evil.example.test/cb" },
        }, CancellationToken)).StatusCode);
    }

    // Authorization

    [Fact]
    public async Task An_admin_signed_in_through_the_admin_ui_client_can_call_the_api()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);

        var response = await api.GetAsync("/api/admin/users", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var page = await ReadJsonAsync(response);
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        var admin = page.GetProperty("items")[0];
        Assert.Equal("admin", admin.GetProperty("userName").GetString());
        Assert.Equal(["admin"], admin.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Requests_without_a_token_are_challenged_even_for_unknown_paths()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        foreach (var path in new[] { "/api/admin/users", "/api/admin/does-not-exist" })
        {
            var response = await client.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer realm=\"ValkyrieID\"", response.Headers.WwwAuthenticate.ToString());
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
    }

    [Fact]
    public async Task Tokens_are_only_accepted_in_the_authorization_header()
    {
        await using var factory = CreateFactory();
        await CreateAdminAsync(factory, "admin");
        var browser = factory.CreateBrowser();
        await Forms.LoginWithEnrollmentAsync(browser, "admin", Password);
        var token = await AdminTokenAsync(factory, browser);

        // A query parameter (RFC 6750 §2.3) is refused, and the SSO cookie alone grants nothing.
        var inQuery = await factory.CreateClient().GetAsync($"/api/admin/users?access_token={token}", CancellationToken);
        var cookieOnly = await browser.GetAsync("/api/admin/users", CancellationToken);
        var basic = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        var wrongScheme = await factory.CreateClient().SendAsync(basic, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, inQuery.StatusCode);
        Assert.Contains("error=\"invalid_request\"", inQuery.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongScheme.StatusCode);
    }

    [Fact]
    public async Task Id_tokens_tokens_for_other_audiences_and_tokens_without_the_scope_are_rejected()
    {
        await using var factory = CreateFactory();
        var adminId = await CreateAdminAsync(factory, "admin");
        var browser = factory.CreateBrowser();
        await Forms.LoginWithEnrollmentAsync(browser, "admin", Password);
        var tokens = await AdminTokensAsync(factory, browser);

        var idToken = await GetUsersAsync(factory, tokens.GetProperty("id_token").GetString()!);
        var otherAudience = await GetUsersAsync(factory, await MintAsync(factory, adminId.ToString(), ["orders-api"], [AdminScope]));
        var noScope = await GetUsersAsync(factory, await MintAsync(factory, adminId.ToString(), [AdminResource], ["openid"]));
        var garbage = await GetUsersAsync(factory, "not-a-token");

        Assert.Equal(HttpStatusCode.Unauthorized, idToken.StatusCode);
        Assert.Contains("error=\"invalid_token\"", idToken.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, otherAudience.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noScope.StatusCode);
        Assert.Contains("error=\"insufficient_scope\"", noScope.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Contains($"scope=\"{AdminScope}\"", noScope.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
    }

    [Fact]
    public async Task Tokens_without_a_user_are_rejected()
    {
        await using var factory = CreateFactory();
        await CreateAdminAsync(factory, "admin");

        // client_credentials tokens carry the client id as sub.
        var response = await GetUsersAsync(factory, await MintAsync(factory, "automation", [AdminResource], [AdminScope]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Users_without_the_admin_role_are_forbidden_and_the_attempt_is_audited()
    {
        await using var factory = CreateFactory();
        await CreateAdminAsync(factory, "admin");
        var bobId = await CreateUserAsync(factory, "bob");

        var response = await GetUsersAsync(factory, await MintAsync(factory, bobId.ToString(), [AdminResource], [AdminScope]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog
            .SingleAsync(e => e.EventType == "admin_api.access_denied", CancellationToken);
        Assert.Equal(bobId, audit.SubjectId);
        Assert.False(audit.Success);
    }

    [Fact]
    public async Task Losing_the_admin_role_or_being_disabled_takes_effect_before_the_token_expires()
    {
        await using var factory = CreateFactory();
        var aliceId = await CreateAdminAsync(factory, "alice");
        var bobId = await CreateAdminAsync(factory, "bob");
        await CreateAdminAsync(factory, "carol");
        var aliceToken = await MintAsync(factory, aliceId.ToString(), [AdminResource], [AdminScope]);
        var bobToken = await MintAsync(factory, bobId.ToString(), [AdminResource], [AdminScope]);
        Assert.Equal(HttpStatusCode.OK, (await GetUsersAsync(factory, aliceToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetUsersAsync(factory, bobToken)).StatusCode);

        var revoke = await factory.RunCliAsync("admin", "revoke-admin", "alice");
        Assert.True(revoke.ExitCode == 0, revoke.Error);
        var disable = await factory.RunCliAsync("user", "disable", "bob");
        Assert.True(disable.ExitCode == 0, disable.Error);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetUsersAsync(factory, aliceToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetUsersAsync(factory, bobToken)).StatusCode);
    }

    [Fact]
    public async Task Locked_out_admins_are_forbidden()
    {
        await using var factory = CreateFactory();
        var adminId = await CreateAdminAsync(factory, "admin");
        var token = await MintAsync(factory, adminId.ToString(), [AdminResource], [AdminScope]);
        for (var i = 0; i < 5; i++)
        {
            await Forms.LoginAsync(factory.CreateBrowser(), "admin", "wrong password!");
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await GetUsersAsync(factory, token)).StatusCode);
    }

    [Fact]
    public async Task Expired_tokens_are_rejected_after_the_clock_skew()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        var adminId = await CreateAdminAsync(factory, "admin");
        var token = await MintAsync(factory, adminId.ToString(), [AdminResource], [AdminScope], TimeSpan.FromMinutes(10));

        // Default clock skew: 30 s.
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(29));
        Assert.Equal(HttpStatusCode.OK, (await GetUsersAsync(factory, token)).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetUsersAsync(factory, token)).StatusCode);
    }

    [Fact]
    public async Task The_api_sends_no_cors_headers()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/admin/users");
        preflight.Headers.Add("Origin", "https://evil.example.test");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization");
        var preflightResponse = await factory.CreateClient().SendAsync(preflight, CancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        request.Headers.Add("Origin", ValkyrieAppFactory.DefaultIssuer);
        var response = await api.SendAsync(request, CancellationToken);

        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // Users

    [Fact]
    public async Task Users_can_be_created_updated_and_deleted()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);

        var weak = await api.PostAsJsonAsync("/api/admin/users", new { userName = "carol", password = "short" }, CancellationToken);
        var created = await api.PostAsJsonAsync("/api/admin/users", new
        {
            userName = "carol",
            password = Password,
            email = "carol@example.test",
            displayName = "Carol",
            givenName = "Carol",
        }, CancellationToken);
        var duplicate = await api.PostAsJsonAsync("/api/admin/users", new { userName = "Carol", password = Password }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("at least", (await ReadJsonAsync(weak)).GetProperty("errors").GetProperty("")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var carol = await ReadJsonAsync(created);
        var id = carol.GetProperty("id").GetString();
        Assert.Equal($"/api/admin/users/{id}", created.Headers.Location?.OriginalString);
        Assert.Equal("Carol", carol.GetProperty("givenName").GetString());
        Assert.False(carol.TryGetProperty("passwordHash", out _));
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(factory.CreateBrowser(), "carol", Password)).StatusCode);

        var updated = await api.PutAsJsonAsync($"/api/admin/users/{id}", new
        {
            userName = "carol2",
            email = "carol2@example.test",
            emailVerified = false,
            displayName = "Carol C.",
            givenName = (string?)null,
            familyName = "Clark",
        }, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var detail = await ReadJsonAsync(updated);
        Assert.Equal("carol2", detail.GetProperty("userName").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("givenName").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/admin/users/{id}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/admin/users/{id}", CancellationToken)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var adminId = await db.Users.Where(u => u.UserName == "admin").Select(u => u.Id).SingleAsync(CancellationToken);
        var audit = await db.AuditLog.Where(e => e.EventType == "user.created" || e.EventType == "user.updated" || e.EventType == "user.deleted")
            .ToListAsync(CancellationToken);
        Assert.All(audit.Where(e => e.Actor != "cli"), e => Assert.Equal($"admin:{adminId}", e.Actor));
        Assert.Contains(audit, e => e.EventType == "user.updated" && e.Details!.Contains("user_name email", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.EventType == "user.deleted");
        Assert.DoesNotContain(await db.AuditLog.Select(e => e.Details).ToListAsync(CancellationToken), d => d != null && d.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Malformed_request_bodies_are_rejected()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);

        var unknownProperty = await api.PostAsJsonAsync("/api/admin/users", new { userName = "carol", password = Password, admin = true }, CancellationToken);
        var missingProperty = await api.PostAsJsonAsync("/api/admin/users", new { userName = "carol" }, CancellationToken);
        var nullProperty = await api.PostAsJsonAsync("/api/admin/users", new { userName = (string?)null, password = Password }, CancellationToken);
        var badLimit = await api.GetAsync("/api/admin/users?limit=0", CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, unknownProperty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingProperty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nullProperty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users.CountAsync(CancellationToken));
    }

    [Fact]
    public async Task A_changed_email_address_is_unverified_and_voids_pending_links()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);
        var created = await ReadJsonAsync(await api.PostAsJsonAsync("/api/admin/users", new { userName = "carol", password = Password, email = "carol@example.test" }, CancellationToken));
        var id = created.GetProperty("id").GetString();

        var verified = await api.PutAsJsonAsync($"/api/admin/users/{id}", new { userName = "carol", email = "carol@example.test", emailVerified = true, displayName = (string?)null, givenName = (string?)null, familyName = (string?)null }, CancellationToken);
        var changedAndVerified = await api.PutAsJsonAsync($"/api/admin/users/{id}", new { userName = "carol", email = "new@example.test", emailVerified = true, displayName = (string?)null, givenName = (string?)null, familyName = (string?)null }, CancellationToken);
        var changed = await api.PutAsJsonAsync($"/api/admin/users/{id}", new { userName = "carol", email = "new@example.test", emailVerified = false, displayName = (string?)null, givenName = (string?)null, familyName = (string?)null }, CancellationToken);

        Assert.True((await ReadJsonAsync(verified)).GetProperty("emailVerified").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, changedAndVerified.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.False((await ReadJsonAsync(changed)).GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task The_last_admin_cannot_be_disabled_deleted_or_lose_the_role()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);
        var admin = (await ReadJsonAsync(await api.GetAsync("/api/admin/users?q=admin", CancellationToken))).GetProperty("items")[0];
        var id = admin.GetProperty("id").GetString();
        var adminRoleId = "0199e0a0-0000-7000-8000-000000000001";

        var disable = await api.PostAsync($"/api/admin/users/{id}/disable", null, CancellationToken);
        var delete = await api.DeleteAsync($"/api/admin/users/{id}", CancellationToken);
        var removeRole = await api.DeleteAsync($"/api/admin/users/{id}/roles/{adminRoleId}", CancellationToken);
        var deleteRole = await api.DeleteAsync($"/api/admin/roles/{adminRoleId}", CancellationToken);
        var renameRole = await api.PutAsJsonAsync($"/api/admin/roles/{adminRoleId}", new { name = "root" }, CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
        Assert.Contains("last enabled admin", (await ReadJsonAsync(disable)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, removeRole.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteRole.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, renameRole.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/admin/users", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Admin_actions_on_users_revoke_sessions_and_reset_credentials()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);
        var bobId = await CreateUserAsync(factory, "bob");
        var bob = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(bob, "bob", Password)).StatusCode);

        var sessions = await ReadJsonAsync(await api.GetAsync($"/api/admin/users/{bobId}/sessions", CancellationToken));
        Assert.Equal(1, sessions.GetArrayLength());
        var sessionId = sessions[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/admin/users/{bobId}/sessions/{sessionId}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.DeleteAsync($"/api/admin/users/{bobId}/sessions/{sessionId}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await bob.GetAsync("/account", CancellationToken)).StatusCode);

        const string newPassword = "another correct horse battery";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync($"/api/admin/users/{bobId}/password", new { password = "short" }, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.PostAsJsonAsync($"/api/admin/users/{bobId}/password", new { password = newPassword }, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Forms.LoginAsync(factory.CreateBrowser(), "bob", newPassword)).StatusCode);

        var disabled = await ReadJsonAsync(await api.PostAsync($"/api/admin/users/{bobId}/disable", null, CancellationToken));
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Forms.LoginAsync(factory.CreateBrowser(), "bob", newPassword)).StatusCode);
        var required = await ReadJsonAsync(await api.PostAsync($"/api/admin/users/{bobId}/require-2fa", null, CancellationToken));
        Assert.True(required.GetProperty("twoFactorRequired").GetBoolean());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var revoked = await db.AuditLog.FirstAsync(e => e.EventType == "session.revoked", CancellationToken);
        Assert.Contains("revoked_by_admin", revoked.Details, StringComparison.Ordinal);
        Assert.StartsWith("admin:", revoked.Actor, StringComparison.Ordinal);
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.password_reset", CancellationToken));
        Assert.True(await db.AuditLog.AnyAsync(e => e.EventType == "user.2fa.required", CancellationToken));
    }

    // Roles, groups, and claims

    [Fact]
    public async Task Roles_groups_and_claims_can_be_managed_and_are_emitted()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);
        var bobId = await CreateUserAsync(factory, "bob");

        var role = await ReadJsonAsync(await api.PostAsJsonAsync("/api/admin/roles", new { name = "editor", description = "Edits things" }, CancellationToken));
        var group = await ReadJsonAsync(await api.PostAsJsonAsync("/api/admin/groups", new { name = "Finance Team" }, CancellationToken));
        var duplicateRole = await api.PostAsJsonAsync("/api/admin/roles", new { name = "EDITOR" }, CancellationToken);
        var roleId = role.GetProperty("id").GetString();
        var groupId = group.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/admin/users/{bobId}/roles/{roleId}", null, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/admin/users/{bobId}/roles/{roleId}", null, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/admin/users/{bobId}/groups/{groupId}", null, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.PutAsync($"/api/admin/users/{bobId}/roles/{Guid.NewGuid()}", null, CancellationToken)).StatusCode);
        var claim = await api.PostAsJsonAsync($"/api/admin/users/{bobId}/claims", new { type = "department", value = "finance" }, CancellationToken);
        var reserved = await api.PostAsJsonAsync($"/api/admin/users/{bobId}/claims", new { type = "sub", value = "someone-else" }, CancellationToken);
        var reservedRoles = await api.PostAsJsonAsync($"/api/admin/users/{bobId}/claims", new { type = "roles", value = "admin" }, CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, duplicateRole.StatusCode);
        Assert.Equal(HttpStatusCode.Created, claim.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reservedRoles.StatusCode);
        var bob = await ReadJsonAsync(await api.GetAsync($"/api/admin/users/{bobId}", CancellationToken));
        Assert.Equal(["editor"], bob.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.Equal(["Finance Team"], bob.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()));
        Assert.Equal("finance", bob.GetProperty("claims")[0].GetProperty("value").GetString());
        Assert.Equal(1, (await ReadJsonAsync(await api.GetAsync($"/api/admin/roles/{roleId}", CancellationToken))).GetProperty("members").GetInt32());

        // Deleting the claim and the role removes them from the user.
        var claimId = (await ReadJsonAsync(claim)).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync($"/api/admin/users/{bobId}/claims/{claimId}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/admin/roles/{roleId}", CancellationToken)).StatusCode);
        bob = await ReadJsonAsync(await api.GetAsync($"/api/admin/users/{bobId}", CancellationToken));
        Assert.Equal(0, bob.GetProperty("roles").GetArrayLength());
        Assert.Equal(0, bob.GetProperty("claims").GetArrayLength());

        await using var scope = factory.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog.Select(e => e.EventType).ToListAsync(CancellationToken);
        Assert.Contains("role.created", events);
        Assert.Contains("group.created", events);
        Assert.Contains("user.role_added", events);
        Assert.Contains("user.group_added", events);
        Assert.Contains("user.claim_added", events);
        Assert.Contains("user.claim_removed", events);
        Assert.Contains("role.deleted", events);
    }

    // Clients

    [Fact]
    public async Task Client_secrets_are_returned_once_and_work_at_the_token_endpoint()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);
        var resource = await api.PostAsJsonAsync("/api/admin/resources", new { name = "orders-api", scopes = new[] { new { name = "orders.read", description = "Read orders" } } }, CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resource.StatusCode);

        var created = await api.PostAsJsonAsync("/api/admin/clients", new
        {
            clientId = "billing",
            type = "confidential",
            grantTypes = new[] { "client_credentials" },
            scopes = new[] { "orders.read" },
            accessTokenLifetime = 300,
        }, CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await ReadJsonAsync(created);
        var secret = body.GetProperty("secret").GetProperty("secret").GetString()!;
        var id = body.GetProperty("client").GetProperty("id").GetString();
        Assert.Equal(300, body.GetProperty("client").GetProperty("accessTokenLifetime").GetInt32());
        Assert.DoesNotContain(secret, await (await api.GetAsync($"/api/admin/clients/{id}", CancellationToken)).Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, await (await api.GetAsync("/api/admin/clients", CancellationToken)).Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await ClientCredentialsAsync(factory, "billing", secret)).StatusCode);

        var second = await ReadJsonAsync(await api.PostAsJsonAsync($"/api/admin/clients/{id}/secrets", new { description = "rotation", expiresDays = 30 }, CancellationToken));
        var firstId = body.GetProperty("secret").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/admin/clients/{id}/secrets/{firstId}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientCredentialsAsync(factory, "billing", secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientCredentialsAsync(factory, "billing", second.GetProperty("secret").GetString()!)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/admin/clients/{id}/disable", null, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientCredentialsAsync(factory, "billing", second.GetProperty("secret").GetString()!)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var details = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().AuditLog.Select(e => e.Details).ToListAsync(CancellationToken);
        Assert.DoesNotContain(details, d => d != null && d.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Client_configuration_is_validated_as_a_whole()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);

        async Task<HttpStatusCode> CreateAsync(object request) =>
            (await api.PostAsJsonAsync("/api/admin/clients", request, CancellationToken)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { clientId = "a", type = "public", grantTypes = new[] { "client_credentials" }, scopes = new[] { "openid" } }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { clientId = "b", type = "public", grantTypes = new[] { "authorization_code" }, scopes = new[] { "openid" }, redirectUris = new[] { "https://app.example.test/cb" }, requirePkce = false }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { clientId = "c", type = "confidential", grantTypes = new[] { "authorization_code" }, scopes = new[] { "openid" }, redirectUris = new[] { "http://app.example.test/cb" } }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { clientId = "d", type = "confidential", grantTypes = new[] { "authorization_code" }, scopes = new[] { "openid" }, redirectUris = new[] { "https://app.example.test/cb#x" } }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { clientId = "e", type = "confidential", grantTypes = new[] { "implicit" }, scopes = new[] { "openid" } }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { clientId = "f", type = "confidential", grantTypes = new[] { "client_credentials" }, scopes = new[] { AdminScope } }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { clientId = "g", type = "other", grantTypes = new[] { "client_credentials" }, scopes = new[] { "openid" } }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { clientId = "h", type = "confidential", grantTypes = new[] { "client_credentials" }, scopes = new[] { "openid" }, accessTokenLifetime = -1 }));
        Assert.Equal(HttpStatusCode.Created, await CreateAsync(new { clientId = "spa", type = "public", grantTypes = new[] { "authorization_code" }, scopes = new[] { "openid" }, redirectUris = new[] { "https://app.example.test/cb" }, corsOrigins = new[] { "https://app.example.test" } }));

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(["spa", AdminUiClientId], (await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Clients.Select(c => c.ClientId).ToListAsync(CancellationToken)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Narrowing_a_clients_grants_or_scopes_revokes_its_refresh_tokens()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);
        var created = await ReadJsonAsync(await api.PostAsJsonAsync("/api/admin/clients", new
        {
            clientId = "web",
            type = "confidential",
            grantTypes = new[] { "authorization_code", "refresh_token" },
            scopes = new[] { "openid", "offline_access", "profile" },
            redirectUris = new[] { "https://app.example.test/cb" },
        }, CancellationToken));
        var id = created.GetProperty("client").GetProperty("id").GetString();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var userId = await db.Users.Select(u => u.Id).FirstAsync(CancellationToken);
            var client = await db.Clients.SingleAsync(c => c.ClientId == "web", CancellationToken);
            scope.ServiceProvider.GetRequiredService<RefreshTokenStore>().Create(client, new RefreshTokenGrant(Guid.NewGuid(), userId, null, DateTimeOffset.UtcNow, ["pwd"], ["openid", "offline_access", "profile"]));
            await db.SaveChangesAsync(CancellationToken);
        }

        var widened = await api.PutAsJsonAsync($"/api/admin/clients/{id}", new
        {
            grantTypes = new[] { "authorization_code", "refresh_token" },
            scopes = new[] { "openid", "offline_access", "profile", "email" },
            redirectUris = new[] { "https://app.example.test/cb" },
            displayName = "Web app",
        }, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, widened.StatusCode);
        Assert.Equal(0, await RevokedRefreshTokensAsync(factory));

        var narrowed = await api.PutAsJsonAsync($"/api/admin/clients/{id}", new
        {
            grantTypes = new[] { "authorization_code", "refresh_token" },
            scopes = new[] { "openid", "offline_access" },
            redirectUris = new[] { "https://app.example.test/cb" },
        }, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, narrowed.StatusCode);
        Assert.Equal(1, await RevokedRefreshTokensAsync(factory));
    }

    // API resources

    [Fact]
    public async Task Resources_and_scopes_can_be_managed_but_scopes_in_use_cannot_be_removed()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);
        var created = await api.PostAsJsonAsync("/api/admin/resources", new { name = "orders-api", displayName = "Orders", scopes = new[] { new { name = "orders.read" } } }, CancellationToken);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetString();
        var withScope = await ReadJsonAsync(await api.PostAsJsonAsync($"/api/admin/resources/{id}/scopes", new { name = "orders.write", description = "Write" }, CancellationToken));
        var writeScopeId = withScope.GetProperty("scopes").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "orders.write").GetProperty("id").GetString();
        await api.PostAsJsonAsync("/api/admin/clients", new { clientId = "worker", type = "confidential", grantTypes = new[] { "client_credentials" }, scopes = new[] { "orders.write" } }, CancellationToken);

        var removeUsed = await api.DeleteAsync($"/api/admin/resources/{id}/scopes/{writeScopeId}", CancellationToken);
        var deleteUsed = await api.DeleteAsync($"/api/admin/resources/{id}", CancellationToken);
        var duplicateScope = await api.PostAsJsonAsync($"/api/admin/resources/{id}/scopes", new { name = "orders.read" }, CancellationToken);
        var identityScope = await api.PostAsJsonAsync("/api/admin/resources", new { name = "id-api", scopes = new[] { new { name = "openid" } } }, CancellationToken);
        var updated = await api.PutAsJsonAsync($"/api/admin/resources/{id}", new { displayName = "Orders API", allowClientSecretPost = true }, CancellationToken);
        var secret = await api.PostAsJsonAsync($"/api/admin/resources/{id}/secrets", new { }, CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, removeUsed.StatusCode);
        Assert.Contains("worker", (await ReadJsonAsync(removeUsed)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, deleteUsed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicateScope.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, identityScope.StatusCode);
        Assert.True((await ReadJsonAsync(updated)).GetProperty("allowClientSecretPost").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, secret.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ReadJsonAsync(secret)).GetProperty("secret").GetString()));
    }

    // Signing keys and audit log

    [Fact]
    public async Task Signing_keys_can_be_listed_and_rotated_with_a_publish_lead()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);

        var before = await ReadJsonAsync(await api.GetAsync("/api/admin/signing-keys", CancellationToken));
        var rotated = await api.PostAsync("/api/admin/signing-keys/rotate", null, CancellationToken);
        var again = await api.PostAsync("/api/admin/signing-keys/rotate", null, CancellationToken);

        Assert.Equal(["active"], before.EnumerateArray().Select(k => k.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var after = await ReadJsonAsync(rotated);
        Assert.Equal(["pending", "active"], after.EnumerateArray().Select(k => k.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var pendingKid = after[0].GetProperty("id").GetString();
        var jwks = await ReadJsonAsync(await factory.CreateClient().GetAsync("/.well-known/jwks.json", CancellationToken));
        Assert.Contains(pendingKid, jwks.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()));
        Assert.DoesNotContain("private", await rotated.Content.ReadAsStringAsync(CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_audit_log_is_paged_with_a_cursor_and_filtered()
    {
        await using var factory = CreateFactory();
        var api = await AdminClientAsync(factory);
        for (var i = 0; i < 5; i++)
        {
            await api.PostAsJsonAsync("/api/admin/groups", new { name = $"group-{i}" }, CancellationToken);
        }

        List<string> seen = [];
        string? cursor = null;
        do
        {
            var page = await ReadJsonAsync(await api.GetAsync($"/api/admin/audit?type=group.&limit=2{(cursor is null ? "" : "&cursor=" + cursor)}", CancellationToken));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("details").GetProperty("name").GetString()!));
            cursor = page.GetProperty("next").GetString();
        }
        while (cursor is not null);

        Assert.Equal(["group-4", "group-3", "group-2", "group-1", "group-0"], seen);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/admin/audit?cursor=bogus", CancellationToken)).StatusCode);
        var exact = await ReadJsonAsync(await api.GetAsync("/api/admin/audit?type=group.created&limit=200", CancellationToken));
        Assert.Equal(5, exact.GetProperty("items").GetArrayLength());
    }

    // Helpers

    private static async Task<int> RevokedRefreshTokensAsync(ValkyrieAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().RefreshTokens.CountAsync(t => t.RevokedAt != null, CancellationToken);
    }

    private static Task<HttpResponseMessage> ClientCredentialsAsync(ValkyrieAppFactory factory, string clientId, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        return factory.CreateClient().SendAsync(request, CancellationToken);
    }

    private static Task<HttpResponseMessage> GetUsersAsync(ValkyrieAppFactory factory, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return factory.CreateClient().SendAsync(request, CancellationToken);
    }

    /// <summary>An HTTP client for the Admin API, authorized as a new admin user signed in through the admin UI client.</summary>
    private static async Task<HttpClient> AdminClientAsync(ValkyrieAppFactory factory)
    {
        await CreateAdminAsync(factory, "admin");
        var browser = factory.CreateBrowser();
        await Forms.LoginWithEnrollmentAsync(browser, "admin", Password);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync(factory, browser));
        return client;
    }

    private static async Task<string> AdminTokenAsync(ValkyrieAppFactory factory, HttpClient browser) =>
        (await AdminTokensAsync(factory, browser)).GetProperty("access_token").GetString()!;

    /// <summary>The authorization code flow with PKCE, as the admin UI runs it.</summary>
    private static async Task<JsonElement> AdminTokensAsync(ValkyrieAppFactory factory, HttpClient browser)
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = await browser.GetAsync(
            $"/connect/authorize?response_type=code&client_id={AdminUiClientId}&scope={Uri.EscapeDataString("openid profile " + AdminScope)}"
            + $"&redirect_uri={Uri.EscapeDataString(AdminRedirectUri)}&code_challenge={challenge}&code_challenge_method=S256&nonce=n",
            CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();

        var response = await factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", AdminRedirectUri),
            new("code_verifier", verifier), new("client_id", AdminUiClientId),
        ]), CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await ReadJsonAsync(response);
    }

    /// <summary>Issues an access token directly, to test tokens the authorization server wouldn't normally issue.</summary>
    private static async Task<string> MintAsync(ValkyrieAppFactory factory, string subject, string[] audiences, string[] scopes, TimeSpan? lifetime = null, string[]? amr = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var issuer = scope.ServiceProvider.GetRequiredService<AccessTokenIssuer>();
        return (await issuer.IssueAsync(new AccessTokenRequest("test", subject, audiences, scopes, lifetime ?? TimeSpan.FromMinutes(5), amr ?? ["pwd", "otp", "mfa"]), CancellationToken)).Token;
    }

    private static async Task<Guid> CreateAdminAsync(ValkyrieAppFactory factory, string userName)
    {
        var id = await CreateUserAsync(factory, userName);
        var result = await factory.RunCliAsync("admin", "grant-admin", userName);
        Assert.True(result.ExitCode == 0, result.Error);
        return id;
    }

    private static async Task<Guid> CreateUserAsync(ValkyrieAppFactory factory, string userName)
    {
        var result = await factory.RunCliWithInputAsync(Password + "\n", "user", "create", userName);
        Assert.True(result.ExitCode == 0, result.Error);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().Users
            .Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync(CancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;
}
