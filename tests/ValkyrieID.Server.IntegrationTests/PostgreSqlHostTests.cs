using Testcontainers.PostgreSql;
using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string CreateConnectionString(string database) =>
        $"{_container.GetConnectionString()};Database={database}";

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlHostTests(PostgreSqlFixture postgres) : HostTests, IClassFixture<PostgreSqlFixture>
{
    private readonly string _database = $"valkyrieid_{Guid.NewGuid():N}";

    protected override ValkyrieAppFactory CreateFactory(string? masterKey = null, string? previousMasterKey = null) =>
        new(DatabaseProvider.PostgreSql, postgres.CreateConnectionString(_database), masterKey, previousMasterKey);
}
