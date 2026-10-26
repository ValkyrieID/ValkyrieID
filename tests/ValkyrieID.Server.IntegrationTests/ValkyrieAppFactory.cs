using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class ValkyrieAppFactory(
    DatabaseProvider provider,
    string connectionString,
    string? masterKey = null,
    string? previousMasterKey = null)
    : WebApplicationFactory<Program>
{
    public string MasterKey { get; } = masterKey ?? Core.Security.MasterKey.Generate().ToBase64();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", provider.ToString());
        builder.UseSetting("Database:ConnectionString", connectionString);
        builder.UseSetting("MasterKey:Current", MasterKey);
        if (previousMasterKey is not null)
        {
            builder.UseSetting("MasterKey:Previous:0", previousMasterKey);
        }
    }
}
