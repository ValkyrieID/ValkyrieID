using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class SqliteAccountTests : AccountTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));
    private int _databases;

    protected override ValkyrieAppFactory CreateFactory(TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, $"valkyrieid-{++_databases}.db")};Pooling=False")
        {
            TimeProvider = timeProvider,
            Settings = settings ?? [],
        };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
