using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class SqliteSecurityTests : SecurityTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));

    protected override ValkyrieAppFactory CreateFactory(Dictionary<string, string?>? settings = null, System.Collections.Concurrent.ConcurrentQueue<string>? logs = null) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db")};Pooling=False")
        {
            Settings = settings ?? [],
            CapturedLogs = logs,
        };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
