using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ValkyrieID.Data.PostgreSql;

public static class PostgreSqlDatabase
{
    public static DbContextOptionsBuilder UseValkyriePostgreSql(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, o => o.MigrationsAssembly(typeof(PostgreSqlDatabase).Assembly.GetName().Name));
}

internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ValkyrieDbContext>
{
    public ValkyrieDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ValkyrieDbContext>();
        builder.UseValkyriePostgreSql("Host=localhost;Database=valkyrieid");
        return new ValkyrieDbContext(builder.Options);
    }
}
