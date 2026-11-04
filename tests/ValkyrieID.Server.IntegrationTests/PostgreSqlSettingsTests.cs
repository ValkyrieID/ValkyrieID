using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlSettingsTests(PostgreSqlFixture postgres) : SettingsTests, IClassFixture<PostgreSqlFixture>
{
    internal override ValkyrieAppFactory CreateFactory(
        TimeProvider? timeProvider = null,
        Dictionary<string, string?>? settings = null,
        TestEmailSender? sender = null,
        string? database = null,
        string? masterKey = null,
        string? previousMasterKey = null) =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString($"valkyrieid_{database ?? Guid.NewGuid().ToString("N")}"), masterKey, previousMasterKey)
        {
            TimeProvider = timeProvider,
            Settings = settings ?? [],
            EmailSender = sender,
            DisableEmailWorker = sender is not null,
        };
}
