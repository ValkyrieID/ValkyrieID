using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class SqliteEmailTests : EmailTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));
    private int _databases;

    internal override ValkyrieAppFactory CreateFactory(TimeProvider timeProvider, Dictionary<string, string?> settings, TestEmailSender sender) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, $"valkyrieid-{++_databases}.db")};Pooling=False")
        {
            TimeProvider = timeProvider,
            Settings = settings,
            EmailSender = sender,
            DisableEmailWorker = true,
        };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
