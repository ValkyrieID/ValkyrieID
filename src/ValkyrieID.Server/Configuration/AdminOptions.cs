namespace ValkyrieID.Server.Configuration;

/// <summary>The Admin REST API under <c>/api/admin</c> (PROJECT.md §9).</summary>
public sealed class AdminApiOptions
{
    public const string SectionName = "AdminApi";

    /// <summary>
    /// When <see langword="false"/>, <c>/api/admin</c> answers 404 and the Admin API resource is disabled, so no admin
    /// tokens can be issued. The admin UI needs the API, so it is turned off too. The CLI keeps working.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The admin's sign-in must have used a second factor (its <c>amr</c> contains one of
    /// <see cref="Authentication.AuthenticationMethods.SecondFactor"/>). Users with the admin role must then set up 2FA.
    /// </summary>
    public bool RequireTwoFactor { get; set; } = true;
}

/// <summary>The Angular admin UI under <c>/admin</c> (PROJECT.md §8.2).</summary>
public sealed class AdminUiOptions
{
    public const string SectionName = "AdminUI";

    /// <summary>When <see langword="false"/>, <c>/admin</c> answers 404 and the built-in admin UI client is disabled.</summary>
    public bool Enabled { get; set; } = true;
}

internal static class AdminOptionsExtensions
{
    public static IServiceCollection AddValkyrieAdminOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdminApiOptions>().Bind(configuration.GetSection(AdminApiOptions.SectionName));
        services.AddOptions<AdminUiOptions>().Bind(configuration.GetSection(AdminUiOptions.SectionName));
        return services;
    }

    public static bool AdminApiEnabled(this IConfiguration configuration) =>
        configuration.GetSection(AdminApiOptions.SectionName).Get<AdminApiOptions>()?.Enabled ?? true;

    /// <summary>The UI is served only when both it and the Admin API it talks to are enabled.</summary>
    public static bool AdminUiEnabled(this IConfiguration configuration) =>
        configuration.AdminApiEnabled() && (configuration.GetSection(AdminUiOptions.SectionName).Get<AdminUiOptions>()?.Enabled ?? true);
}
