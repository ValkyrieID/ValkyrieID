using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlAccountTests(PostgreSqlFixture postgres) : AccountTests, IClassFixture<PostgreSqlFixture>
{
    protected override ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null) =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString($"valkyrieid_{Guid.NewGuid():N}"))
        {
            TimeProvider = timeProvider,
            Settings = settings ?? [],
        };
}
