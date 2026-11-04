using Microsoft.Extensions.Options;

namespace ValkyrieID.Server.Configuration;

/// <summary>Audit log retention (PROJECT.md §11).</summary>
public sealed class AuditLogOptions
{
    public const string SectionName = "AuditLog";

    /// <summary>Entries older than this are deleted; <see cref="TimeSpan.Zero"/> keeps them forever.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>How often old entries are deleted.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Runtime settings stored in the database (PROJECT.md §10).</summary>
public sealed class SettingsOptions
{
    public const string SectionName = "Settings";

    /// <summary>How often each instance checks whether another process changed the settings.</summary>
    public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromSeconds(30);
}

internal sealed class RuntimeOptionsValidator : IValidateOptions<AuditLogOptions>, IValidateOptions<SettingsOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditLogOptions options)
    {
        List<string> failures = [];
        if (options.Retention != TimeSpan.Zero && (options.Retention < TimeSpan.FromDays(1) || options.Retention > TimeSpan.FromDays(3650)))
        {
            failures.Add("AuditLog:Retention must be 0 (keep forever) or between 1 day and 3650 days.");
        }

        if (options.CleanupInterval < TimeSpan.FromMinutes(1) || options.CleanupInterval > TimeSpan.FromDays(1))
        {
            failures.Add("AuditLog:CleanupInterval must be between 1 minute and 1 day.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public ValidateOptionsResult Validate(string? name, SettingsOptions options) =>
        options.ReloadInterval < TimeSpan.FromSeconds(1) || options.ReloadInterval > TimeSpan.FromMinutes(10)
            ? ValidateOptionsResult.Fail("Settings:ReloadInterval must be between 1 second and 10 minutes.")
            : ValidateOptionsResult.Success;
}
