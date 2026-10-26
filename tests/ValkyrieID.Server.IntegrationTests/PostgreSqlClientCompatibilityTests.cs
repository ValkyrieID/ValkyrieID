using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlClientCompatibilityTests(PostgreSqlFixture postgres) : ClientCompatibilityTests, IClassFixture<PostgreSqlFixture>
{
    protected override ValkyrieAppFactory CreateFactory() =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString($"valkyrieid_{Guid.NewGuid():N}"));
}
