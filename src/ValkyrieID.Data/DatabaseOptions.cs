namespace ValkyrieID.Data;

public enum DatabaseProvider
{
    Sqlite,
    PostgreSql,
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    public string ConnectionString { get; set; } = "Data Source=data/valkyrieid.db";

    /// <summary>Apply pending migrations on startup. Disable to run migrations manually.</summary>
    public bool AutoMigrate { get; set; } = true;
}
