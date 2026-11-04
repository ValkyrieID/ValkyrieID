using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Settings;
using ValkyrieID.Data;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Settings;

/// <summary>
/// Runtime settings from the database as a configuration provider. It is the first source, so appsettings,
/// environment variables, and the command line override it (PROJECT.md §10). Its data is set by
/// <see cref="RuntimeSettingsStore"/>.
/// </summary>
internal sealed class DatabaseConfigurationProvider : ConfigurationProvider
{
    public IReadOnlyDictionary<string, string?> Snapshot => new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces the values and notifies <see cref="IOptionsMonitor{TOptions}"/> listeners.</summary>
    public void Replace(IReadOnlyDictionary<string, string?> values)
    {
        Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }
}

internal sealed class DatabaseConfigurationSource : IConfigurationSource
{
    public DatabaseConfigurationProvider Provider { get; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

/// <summary>Loads runtime settings from the database into <see cref="DatabaseConfigurationProvider"/> and validates them.</summary>
internal sealed partial class RuntimeSettingsStore(
    DatabaseConfigurationProvider provider,
    IConfiguration configuration,
    IServiceProvider services,
    IServiceScopeFactory scopes,
    SecretProtector protector,
    ILogger<RuntimeSettingsStore> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>The revision of the loaded values; -1 before the first load.</summary>
    public long Revision { get; private set; } = -1;

    /// <summary>The values from the database currently in effect (secrets decrypted).</summary>
    public IReadOnlyDictionary<string, string?> DatabaseValues => provider.Snapshot;

    /// <summary>
    /// A setting is locked when appsettings, an environment variable, or the command line sets it. A key present with
    /// a <see langword="null"/> value counts as not set.
    /// </summary>
    public bool IsLocked(RuntimeSetting setting) =>
        configuration is IConfigurationRoot root
        && root.Providers.Any(p => !ReferenceEquals(p, provider) && p.TryGet(setting.Key, out var value) && value is not null);

    /// <summary>Where a setting's value comes from: <c>configuration</c>, <c>database</c>, or <c>default</c>.</summary>
    public string SourceOf(RuntimeSetting setting) =>
        IsLocked(setting) ? "configuration" : provider.TryGet(setting.Key, out _) ? "database" : "default";

    /// <summary>
    /// Reads the settings from the database and applies them. With <paramref name="validate"/>, values that would make
    /// the configuration invalid (e.g. conflicting with a changed environment variable) are not applied: the previous
    /// values stay in effect and an error is logged. Returns whether the values were applied.
    /// </summary>
    public async Task<bool> LoadAsync(bool validate, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var (revision, values) = await ReadAsync(db, cancellationToken);
            if (validate && Validate(values) is { Count: > 0 } errors)
            {
                // Remember the revision so the same invalid state isn't reported on every poll.
                Revision = revision;
                LogInvalidSettings(logger, revision, string.Join(" ", errors));
                return false;
            }

            provider.Replace(values);
            Revision = revision;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static Task<long> ReadRevisionAsync(ValkyrieDbContext db, CancellationToken cancellationToken) =>
        db.SettingsRevisions.AsNoTracking()
            .Where(r => r.Id == SettingsRevision.SingletonId)
            .Select(r => r.Revision)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Reads the stored settings, decrypting secrets. Rows that aren't runtime settings are ignored.</summary>
    public async Task<(long Revision, Dictionary<string, string?> Values)> ReadAsync(ValkyrieDbContext db, CancellationToken cancellationToken)
    {
        var revision = await ReadRevisionAsync(db, cancellationToken);
        var rows = await db.Settings.AsNoTracking().ToListAsync(cancellationToken);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (RuntimeSettings.Find(row.Key) is not { } setting)
            {
                LogUnknownSetting(logger, row.Key);
                continue;
            }

            var value = setting.Kind == SettingKind.Secret
                ? row.EncryptedValue is { } encrypted ? protector.UnprotectString(encrypted, RuntimeSettings.SecretPurpose(setting)) : null
                : row.Value;
            if (value is not null)
            {
                values[setting.Key] = value;
            }
        }

        return (revision, values);
    }

    /// <summary>
    /// The configuration as it would be with <paramref name="databaseValues"/> stored: locked settings keep their
    /// configured values, everything else comes from <paramref name="databaseValues"/> or the defaults.
    /// </summary>
    public IConfiguration Merge(IReadOnlyDictionary<string, string?> databaseValues)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in configuration.AsEnumerable())
        {
            merged[key] = value;
        }

        foreach (var setting in RuntimeSettings.All.Where(s => !IsLocked(s)))
        {
            if (databaseValues.TryGetValue(setting.Key, out var value) && value is not null)
            {
                merged[setting.Key] = value;
            }
            else
            {
                merged.Remove(setting.Key);
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(merged).Build();
    }

    /// <summary>Validates every section as it would be with <paramref name="databaseValues"/> stored.</summary>
    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string?> databaseValues)
    {
        var merged = Merge(databaseValues);
        return [.. RuntimeSettings.Sections.SelectMany(s => s.Validate(merged, services)).Distinct()];
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Runtime settings revision {Revision} are invalid and were not applied; the previous settings stay in effect: {Errors}")]
    private static partial void LogInvalidSettings(ILogger logger, long revision, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring unknown runtime setting {Key} in the database")]
    private static partial void LogUnknownSetting(ILogger logger, string key);
}

/// <summary>
/// Picks up settings changed by another process (another instance or the CLI): every
/// <see cref="SettingsOptions.ReloadInterval"/>, the stored revision is compared with the loaded one.
/// </summary>
internal sealed partial class SettingsReloadService(
    RuntimeSettingsStore store,
    IServiceScopeFactory scopes,
    IOptionsMonitor<SettingsOptions> options,
    TimeProvider timeProvider,
    ILogger<SettingsReloadService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CurrentValue.ReloadInterval, timeProvider, stoppingToken);
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogReloadFailed(logger, ex);
            }
        }
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        long revision;
        await using (var scope = scopes.CreateAsyncScope())
        {
            revision = await RuntimeSettingsStore.ReadRevisionAsync(scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>(), cancellationToken);
        }

        if (revision != store.Revision)
        {
            await store.LoadAsync(validate: true, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Reloading the runtime settings failed")]
    private static partial void LogReloadFailed(ILogger logger, Exception exception);
}
