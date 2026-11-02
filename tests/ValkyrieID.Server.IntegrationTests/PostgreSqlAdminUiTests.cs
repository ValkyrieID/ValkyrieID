using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlAdminUiTests(PostgreSqlFixture postgres) : AdminUiTests, IClassFixture<PostgreSqlFixture>
{
    protected override ValkyrieAppFactory CreateFactoryCore(string adminUiDirectory, Dictionary<string, string?>? settings = null, string? database = null, string? masterKey = null) =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString($"valkyrieid_{database ?? Guid.NewGuid().ToString("N")}"), masterKey)
        {
            AdminUiDirectory = adminUiDirectory,
            Settings = settings ?? [],
        };
}
