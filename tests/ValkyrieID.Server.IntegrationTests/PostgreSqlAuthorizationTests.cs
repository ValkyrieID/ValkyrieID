using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlAuthorizationTests(PostgreSqlFixture postgres) : AuthorizationTests, IClassFixture<PostgreSqlFixture>
{
    // Each factory gets its own database so tests don't share clients or keys.
    protected override ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null) =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString($"valkyrieid_{Guid.NewGuid():N}"))
        {
            TimeProvider = timeProvider,
            Settings = settings ?? [],
        };
}
