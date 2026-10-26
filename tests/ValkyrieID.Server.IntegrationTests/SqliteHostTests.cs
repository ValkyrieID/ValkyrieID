using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class SqliteHostTests : HostTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));

    protected override ValkyrieAppFactory CreateFactory(string? masterKey = null, string? previousMasterKey = null) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, "valkyrieid.db")};Pooling=False", masterKey, previousMasterKey);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
