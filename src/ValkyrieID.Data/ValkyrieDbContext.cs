using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ValkyrieID.Core.Auditing;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Resources;
using ValkyrieID.Core.Tokens;

namespace ValkyrieID.Data;

public sealed class ValkyrieDbContext(DbContextOptions<ValkyrieDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<ApiResource> ApiResources => Set<ApiResource>();

    public DbSet<ApiScope> ApiScopes => Set<ApiScope>();

    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no native DateTimeOffset type and can't order or compare it in queries;
        // store it as a sortable integer instead.
        if (Database.ProviderName == SqliteProviderName)
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ValkyrieDbContext).Assembly);
}
