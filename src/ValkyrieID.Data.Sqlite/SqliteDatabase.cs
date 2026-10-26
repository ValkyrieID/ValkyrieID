using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ValkyrieID.Data.Sqlite;

public static class SqliteDatabase
{
    public static DbContextOptionsBuilder UseValkyrieSqlite(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlite(connectionString, o => o.MigrationsAssembly(typeof(SqliteDatabase).Assembly.GetName().Name));

    /// <summary>Creates the directory containing the database file, so SQLite can create the file on first run.</summary>
    public static void EnsureDirectoryExists(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrEmpty(dataSource) || dataSource == ":memory:")
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ValkyrieDbContext>
{
    public ValkyrieDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ValkyrieDbContext>();
        builder.UseValkyrieSqlite("Data Source=design.db");
        return new ValkyrieDbContext(builder.Options);
    }
}
