using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Security;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Security;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Configuration;

internal static class RateLimitPolicies
{
    public const string Login = "login";
    public const string Token = "token";
    public const string Authorize = "authorize";
}

internal static class AccountExtensions
{
    public const string ThemePath = "/theme.css";
    public const string BrandingPath = "/branding";

    public static IServiceCollection AddValkyrieAccounts(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<AccountOptionsValidator>();
        AddOptions<PasswordPolicyOptions>(services, configuration, PasswordPolicyOptions.SectionName);
        AddOptions<PasswordHashingOptions>(services, configuration, PasswordHashingOptions.SectionName);
        AddOptions<LockoutOptions>(services, configuration, LockoutOptions.SectionName);
        AddOptions<SsoSessionOptions>(services, configuration, SsoSessionOptions.SectionName);
        AddOptions<RateLimitOptions>(services, configuration, RateLimitOptions.SectionName);
        AddOptions<BrandingOptions>(services, configuration, BrandingOptions.SectionName);

        services.AddSingleton(sp => new PasswordHasher(sp.GetRequiredService<IOptions<PasswordHashingOptions>>().Value.ToParameters()));
        services.AddHttpClient<BreachedPasswordChecker>(client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddScoped<PasswordValidator>();
        services.AddScoped<UserAdministration>();
        services.AddScoped<LoginService>();
        services.AddScoped<SessionManager>();
        services.AddScoped<SessionCookieEvents>();
        services.AddSingleton<LoginAttemptLimiter>();
        services.AddScoped<MasterKeyReencryptor>();
        services.AddSingleton<ThemeStylesheet>();

        services.AddAuthentication(SessionClaims.Scheme)
            .AddCookie(SessionClaims.Scheme, options =>
            {
                options.Cookie.Name = SessionClaims.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Lax: the cookie is sent when a relying party redirects the browser to the authorize endpoint (a
                // top-level GET navigation).
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.LoginPath = "/account/login";
                options.LogoutPath = "/account/logout";
                options.AccessDeniedPath = "/account";
                options.ReturnUrlParameter = "returnUrl";
                options.SlidingExpiration = false;
                options.EventsType = typeof(SessionCookieEvents);
            });
        services.AddOptions<CookieAuthenticationOptions>(SessionClaims.Scheme)
            .Configure<IOptions<SsoSessionOptions>>((options, session) => options.ExpireTimeSpan = session.Value.Lifetime);
        services.AddAuthorization();

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-valkyrie.af";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });
        services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
        services.AddRazorPages();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            // Only form submissions count against the login limit, not page views.
            options.AddPolicy(RateLimitPolicies.Login, context => HttpMethods.IsPost(context.Request.Method)
                ? FixedWindowPerIp(context, RateLimitPolicies.Login, o => o.Login)
                : RateLimitPartition.GetNoLimiter(string.Empty));
            options.AddPolicy(RateLimitPolicies.Token, context => FixedWindowPerIp(context, RateLimitPolicies.Token, o => o.Token));
            options.AddPolicy(RateLimitPolicies.Authorize, context => FixedWindowPerIp(context, RateLimitPolicies.Authorize, o => o.Authorize));
        });

        return services;
    }

    public static WebApplication UseValkyrieAccounts(this WebApplication app)
    {
        var branding = app.Services.GetRequiredService<IOptions<BrandingOptions>>().Value;
        app.UseStaticFiles();
        if (branding.Directory is not null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(Path.GetFullPath(branding.Directory)),
                RequestPath = BrandingPath,
            });
        }

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        return app;
    }

    public static IEndpointRouteBuilder MapValkyrieAccounts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapRazorPages();
        endpoints.MapGet(ThemePath, (ThemeStylesheet theme) => Results.Text(theme.Css, "text/css", Encoding.UTF8));
        return endpoints;
    }

    private static RateLimitPartition<string> FixedWindowPerIp(HttpContext context, string policy, Func<RateLimitOptions, RateLimitRule> rule)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"{policy}:{ip}", _ =>
        {
            var options = rule(context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value);
            return new FixedWindowRateLimiterOptions { PermitLimit = options.PermitLimit, Window = options.Window, QueueLimit = 0 };
        });
    }

    private static void AddOptions<T>(IServiceCollection services, IConfiguration configuration, string section)
        where T : class
    {
        services.AddOptions<T>().Bind(configuration.GetSection(section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<T>>(sp => (IValidateOptions<T>)(object)sp.GetRequiredService<AccountOptionsValidator>());
    }
}

/// <summary>CSS variables generated from <see cref="BrandingOptions"/>; values are validated, so they can't inject CSS.</summary>
internal sealed class ThemeStylesheet(IOptions<BrandingOptions> options)
{
    private readonly Lazy<string> _css = new(() => Build(options.Value));

    public string Css => _css.Value;

    private static string Build(BrandingOptions branding)
    {
        var css = new StringBuilder(":root {\n");
        Add(css, "--vid-primary", branding.PrimaryColor);
        Add(css, "--vid-background", branding.BackgroundColor);
        Add(css, "--vid-surface", branding.SurfaceColor);
        Add(css, "--vid-text", branding.TextColor);
        if (branding.BackgroundImage is not null)
        {
            Add(css, "--vid-background-image", $"url(\"{AccountExtensions.BrandingPath}/{branding.BackgroundImage}\")");
        }

        return css.Append("}\n").ToString();
    }

    private static void Add(StringBuilder css, string name, string? value)
    {
        if (value is not null)
        {
            css.Append(CultureInfo.InvariantCulture, $"  {name}: {value};\n");
        }
    }
}

internal static class SecurityHeadersExtensions
{
    // form-action is left out on purpose: browsers apply it to redirects after a form post, which would block
    // redirects back to relying parties after login.
    private const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        return next(context);
    });
}
