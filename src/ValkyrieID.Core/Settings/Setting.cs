namespace ValkyrieID.Core.Settings;

/// <summary>
/// A runtime setting stored in the database (PROJECT.md §10). Values from appsettings and environment variables take
/// precedence over it.
/// </summary>
public sealed class Setting
{
    /// <summary>The configuration key, e.g. <c>Tokens:AccessTokenLifetime</c>.</summary>
    public required string Key { get; init; }

    /// <summary>The value in invariant format; <see langword="null"/> for secrets.</summary>
    public string? Value { get; set; }

    /// <summary>A secret value (e.g. the SMTP password), encrypted with the master key.</summary>
    public byte[]? EncryptedValue { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who made the last change, e.g. <c>admin:&lt;sub&gt;</c> or <c>cli</c>.</summary>
    public string? UpdatedBy { get; set; }
}

/// <summary>A counter that is incremented with every settings change, so other instances know to reload.</summary>
public sealed class SettingsRevision
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;

    public long Revision { get; set; }
}
