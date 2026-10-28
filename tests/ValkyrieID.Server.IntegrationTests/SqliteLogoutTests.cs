using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class SqliteLogoutTests : LogoutTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));
    private int _databases;

    // Each factory gets its own database so tests don't share clients or keys.
    protected override ValkyrieAppFactory CreateFactory(
        TimeProvider? timeProvider = null, Dictionary<string, string?>? settings = null, BackChannelReceiver? receiver = null, bool disableWorker = false) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, $"valkyrieid-{++_databases}.db")};Pooling=False")
        {
            TimeProvider = timeProvider,
            Settings = settings ?? [],
            BackChannelHandler = receiver,
            DisableBackChannelWorker = disableWorker,
        };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
