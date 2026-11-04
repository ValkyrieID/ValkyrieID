using Microsoft.Extensions.Options;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Settings;

namespace ValkyrieID.Server.Configuration;

internal static class SettingsExtensions
{
    /// <summary>
    /// Adds the database settings as the first configuration source, so every other source (appsettings, environment
    /// variables, command line) takes precedence (PROJECT.md §10). The values are loaded by
    /// <see cref="LoadRuntimeSettingsAsync"/> once the database is migrated.
    /// </summary>
    public static WebApplicationBuilder AddValkyrieRuntimeSettings(this WebApplicationBuilder builder)
    {
        var source = new DatabaseConfigurationSource();
        builder.Configuration.Sources.Insert(0, source);

        var services = builder.Services;
        services.AddSingleton(source.Provider);
        services.AddSingleton<RuntimeSettingsStore>();
        services.AddScoped<SettingsAdministration>();
        services.AddSingleton<SettingsReloadService>();
        services.AddHostedService(sp => sp.GetRequiredService<SettingsReloadService>());

        services.AddSingleton<RuntimeOptionsValidator>();
        services.AddOptions<SettingsOptions>().Bind(builder.Configuration.GetSection(SettingsOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SettingsOptions>>(sp => sp.GetRequiredService<RuntimeOptionsValidator>());
        services.AddOptions<AuditLogOptions>().Bind(builder.Configuration.GetSection(AuditLogOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuditLogOptions>>(sp => sp.GetRequiredService<RuntimeOptionsValidator>());
        services.AddSingleton<AuditLogCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditLogCleanupService>());
        return builder;
    }

    /// <summary>
    /// Loads the settings stored in the database. They aren't validated here: the server validates its options at
    /// startup and refuses to start if they are invalid; the CLI can still run to fix them.
    /// </summary>
    public static Task LoadRuntimeSettingsAsync(this IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<RuntimeSettingsStore>().LoadAsync(validate: false, cancellationToken);
}
