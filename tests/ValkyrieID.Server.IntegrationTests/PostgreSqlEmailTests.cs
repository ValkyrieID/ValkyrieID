using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlEmailTests(PostgreSqlFixture postgres) : EmailTests, IClassFixture<PostgreSqlFixture>
{
    internal override ValkyrieAppFactory CreateFactory(TimeProvider timeProvider, Dictionary<string, string?> settings, TestEmailSender sender) =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString($"valkyrieid_{Guid.NewGuid():N}"))
        {
            TimeProvider = timeProvider,
            Settings = settings,
            EmailSender = sender,
            DisableEmailWorker = true,
        };
}
