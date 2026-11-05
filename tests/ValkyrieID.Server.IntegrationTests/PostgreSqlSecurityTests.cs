using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlSecurityTests(PostgreSqlFixture postgres) : SecurityTests, IClassFixture<PostgreSqlFixture>
{
    protected override ValkyrieAppFactory CreateFactory(Dictionary<string, string?>? settings = null, System.Collections.Concurrent.ConcurrentQueue<string>? logs = null) =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString($"valkyrieid_{Guid.NewGuid():N}"))
        {
            Settings = settings ?? [],
            CapturedLogs = logs,
        };
}
