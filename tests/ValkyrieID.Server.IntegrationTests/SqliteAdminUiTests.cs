using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class SqliteAdminUiTests : AdminUiTests
{
    protected override ValkyrieAppFactory CreateFactoryCore(string adminUiDirectory, Dictionary<string, string?>? settings = null, string? database = null, string? masterKey = null) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(WorkDirectory, "db", $"{database ?? Guid.NewGuid().ToString("N")}.db")};Pooling=False", masterKey)
        {
            AdminUiDirectory = adminUiDirectory,
            Settings = settings ?? [],
        };
}
