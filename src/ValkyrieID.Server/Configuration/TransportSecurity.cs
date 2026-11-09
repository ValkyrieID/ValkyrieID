using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Options;
using ValkyrieID.Server.Protocol;

namespace ValkyrieID.Server.Configuration;

/// <summary>Strict-Transport-Security (RFC 6797) settings. Configuration-only.</summary>
public sealed class HstsSettings
{
    public const string SectionName = "Hsts";

    /// <summary>How long browsers only use https for the issuer's host (RFC 6797 §6.1.1 max-age).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>Also applies to every subdomain of the issuer's host. Only enable when all of them serve https.</summary>
    public bool IncludeSubDomains { get; set; }

    /// <summary>Adds <c>preload</c>, for submission to the browsers' HSTS preload lists.</summary>
    public bool Preload { get; set; }
}

internal sealed class HstsSettingsValidator : IValidateOptions<HstsSettings>
{
    public ValidateOptionsResult Validate(string? name, HstsSettings options)
    {
        List<string> failures = [];
        if (options.MaxAge <= TimeSpan.Zero)
        {
            failures.Add("Hsts:MaxAge must be positive.");
        }

        // The preload lists require includeSubDomains and a max-age of at least one year.
        if (options.Preload && (!options.IncludeSubDomains || options.MaxAge < TimeSpan.FromDays(365)))
        {
            failures.Add("Hsts:Preload requires Hsts:IncludeSubDomains and an Hsts:MaxAge of at least 365 days.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal static class TransportSecurityExtensions
{
    private static readonly string[] JsonPaths =
    [
        "/api", "/.well-known", TokenEndpoint.Path, UserInfoEndpoint.Path, IntrospectionEndpoint.Path, RevocationEndpoint.Path,
    ];

    public static IServiceCollection AddValkyrieTransportSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HstsSettings>().Bind(configuration.GetSection(HstsSettings.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<HstsSettings>, HstsSettingsValidator>();
        services.AddOptions<HstsOptions>().Configure<IOptions<HstsSettings>>((hsts, settings) =>
        {
            hsts.MaxAge = settings.Value.MaxAge;
            hsts.IncludeSubDomains = settings.Value.IncludeSubDomains;
            hsts.Preload = settings.Value.Preload;
        });
        return services;
    }

    /// <summary>
    /// RFC 6749 §3.1 and §3.2: the authorization server MUST require TLS for the authorization and token endpoints
    /// (also OIDC Core §16.17). The issuer is always https (IssuerOptionsValidator), so every request that didn't
    /// arrive over https is refused instead of redirected, since its credentials or codes have already crossed the
    /// network. Behind a TLS-terminating proxy, the proxy must be trusted (ReverseProxy:KnownProxies) so
    /// X-Forwarded-Proto is applied. Health checks stay reachable over http for container probes.
    /// </summary>
    public static IApplicationBuilder UseRequireHttps(this WebApplication app) =>
        app.Use((context, next) =>
        {
            if (context.Request.IsHttps || context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
            {
                return next(context);
            }

            const string message = "HTTPS is required.";
            var path = context.Request.Path;
            var result = JsonPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
                ? new OAuthError(OAuthError.InvalidRequest, message)
                : ProtocolPages.Error(message);
            return result.ExecuteAsync(context);
        });

    /// <summary>
    /// RFC 9700 §4.12: a redirect answering a POST that may carry the user's credentials (login, 2FA, password forms,
    /// the authorize and logout forms) MUST NOT be 307 and SHOULD be 303, the only status that makes every user agent
    /// switch to GET and drop the form body. 302s answering POSTs are therefore sent as 303.
    /// </summary>
    public static IApplicationBuilder UseSeeOtherAfterPost(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        if (HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode is StatusCodes.Status302Found or StatusCodes.Status307TemporaryRedirect)
                {
                    context.Response.StatusCode = StatusCodes.Status303SeeOther;
                }

                return Task.CompletedTask;
            });
        }

        return next(context);
    });
}
