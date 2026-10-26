using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ValkyrieID.Data;
using ValkyrieID.Data.PostgreSql;
using ValkyrieID.Data.Sqlite;

namespace ValkyrieID.Server.Configuration;

internal sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        List<string> failures = [];

        if (!Enum.IsDefined(options.Provider))
        {
            failures.Add($"Database:Provider must be one of: {string.Join(", ", Enum.GetNames<DatabaseProvider>())}.");
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add("Database:ConnectionString is required.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal sealed class DatabaseHealthCheck(ValkyrieDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, "Cannot connect to the database.");
}

internal static partial class DatabaseExtensions
{
    public static IServiceCollection AddValkyrieDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
        services.AddDbContext<ValkyrieDbContext>((sp, builder) =>
        {
            var options = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            _ = options.Provider switch
            {
                DatabaseProvider.Sqlite => builder.UseValkyrieSqlite(options.ConnectionString),
                DatabaseProvider.PostgreSql => builder.UseValkyriePostgreSql(options.ConnectionString),
                _ => throw new InvalidOperationException($"Unsupported database provider '{options.Provider}'."),
            };
        });

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseExtensions).FullName!);

        if (!options.AutoMigrate)
        {
            LogAutoMigrateDisabled(logger);
            return;
        }

        if (options.Provider == DatabaseProvider.Sqlite)
        {
            SqliteDatabase.EnsureDirectoryExists(options.ConnectionString);
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        LogApplyingMigrations(logger, pending.Count, options.Provider);
        await db.Database.MigrateAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} pending database migration(s) ({Provider})")]
    private static partial void LogApplyingMigrations(ILogger logger, int count, DatabaseProvider provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic database migration is disabled")]
    private static partial void LogAutoMigrateDisabled(ILogger logger);
}
