using Microsoft.Extensions.Options;
using ValkyrieID.Server.Email;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Configuration;

internal static class EmailExtensions
{
    public static IServiceCollection AddValkyrieEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<EmailOptionsValidator>();
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SmtpOptions>>(sp => sp.GetRequiredService<EmailOptionsValidator>());
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>>(sp => sp.GetRequiredService<EmailOptionsValidator>());

        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<EmailSignal>();
        services.AddSingleton<EmailDispatcher>();
        services.AddScoped<EmailQueue>();
        services.AddScoped<AccountEmails>();
        services.AddScoped<EmailTokenService>();
        services.AddScoped<EmailAccountService>();
        services.AddHostedService<EmailWorker>();
        return services;
    }
}
