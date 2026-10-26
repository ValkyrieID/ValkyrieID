using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class SqliteClientCompatibilityTests : ClientCompatibilityTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));

    protected override ValkyrieAppFactory CreateFactory() =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, "valkyrieid.db")};Pooling=False");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
