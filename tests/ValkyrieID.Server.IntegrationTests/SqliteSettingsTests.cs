using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class SqliteSettingsTests : SettingsTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));

    internal override ValkyrieAppFactory CreateFactory(
        TimeProvider? timeProvider = null,
        Dictionary<string, string?>? settings = null,
        TestEmailSender? sender = null,
        string? database = null,
        string? masterKey = null,
        string? previousMasterKey = null) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, $"{database ?? Guid.NewGuid().ToString("N")}.db")};Pooling=False", masterKey, previousMasterKey)
        {
            TimeProvider = timeProvider,
            Settings = settings ?? [],
            EmailSender = sender,
            DisableEmailWorker = sender is not null,
        };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
