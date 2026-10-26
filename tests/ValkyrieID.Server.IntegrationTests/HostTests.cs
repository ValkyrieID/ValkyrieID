using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Host tests run against every supported database provider.</summary>
public abstract class HostTests
{
    private const string Payload = "session-cookie-payload";

    protected abstract ValkyrieAppFactory CreateFactory(string? masterKey = null, string? previousMasterKey = null);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Liveness_endpoint_is_healthy()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync("/health/live", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_endpoint_checks_the_database()
    {
        await using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync("/health/ready", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Migrations_are_applied_on_startup()
    {
        await using var factory = CreateFactory();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(CancellationToken));
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync(CancellationToken));
    }

    [Fact]
    public async Task Data_protection_keys_survive_a_restart()
    {
        string protectedPayload;
        string masterKey;

        await using (var first = CreateFactory())
        {
            masterKey = first.MasterKey;
            protectedPayload = Protector(first).Protect(Payload);
        }

        await using var second = CreateFactory(masterKey);

        Assert.Equal(Payload, Protector(second).Unprotect(protectedPayload));
    }

    [Fact]
    public async Task Data_protection_keys_are_encrypted_at_rest()
    {
        await using var factory = CreateFactory();
        Protector(factory).Protect(Payload);

        await using var scope = factory.Services.CreateAsyncScope();
        var keys = await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>()
            .DataProtectionKeys.ToListAsync(CancellationToken);

        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.Contains("<encryptedKey", key.Xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<masterKey", key.Xml, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Data_protection_keys_are_unreadable_with_a_different_master_key()
    {
        string protectedPayload;

        await using (var first = CreateFactory())
        {
            protectedPayload = Protector(first).Protect(Payload);
        }

        await using var second = CreateFactory(MasterKey.Generate().ToBase64());

        Assert.ThrowsAny<CryptographicException>(() => Protector(second).Unprotect(protectedPayload));
    }

    [Fact]
    public async Task Data_protection_keys_remain_readable_after_master_key_rotation()
    {
        string protectedPayload;
        string oldMasterKey;

        await using (var first = CreateFactory())
        {
            oldMasterKey = first.MasterKey;
            protectedPayload = Protector(first).Protect(Payload);
        }

        await using var rotated = CreateFactory(MasterKey.Generate().ToBase64(), previousMasterKey: oldMasterKey);

        Assert.Equal(Payload, Protector(rotated).Unprotect(protectedPayload));
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public async Task Startup_fails_without_a_valid_master_key(string masterKey)
    {
        await using var factory = CreateFactory(masterKey);

        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains("MasterKey:Current", exception.ToString(), StringComparison.Ordinal);
        if (masterKey.Length > 0)
        {
            Assert.DoesNotContain(masterKey, exception.ToString(), StringComparison.Ordinal);
        }
    }

    private static IDataProtector Protector(ValkyrieAppFactory factory) =>
        factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
}
