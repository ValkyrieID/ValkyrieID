using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Security;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Redis;
using ValkyrieID.Server.Security;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Configuration;

internal static class RateLimitPolicies
{
    public const string Login = "login";
    public const string Token = "token";
    public const string Authorize = "authorize";
    public const string TwoFactor = "two-factor";
    public const string Email = "email";
    public const string Account = "account";
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
        services.AddOptions<TwoFactorOptions>().Bind(configuration.GetSection(TwoFactorOptions.SectionName));

        services.AddSingleton(sp => new PasswordHasher(sp.GetRequiredService<IOptions<PasswordHashingOptions>>().Value.ToParameters()));
        services.AddHttpClient<BreachedPasswordChecker>(client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddScoped<PasswordValidator>();
        services.AddScoped<UserAdministration>();
        services.AddScoped<LoginService>();
        services.AddScoped<TwoFactorService>();
        services.AddScoped<AccountService>();
        services.AddValkyrieEmail(configuration);
        services.AddScoped<PendingLogin>();
        services.AddScoped<SessionManager>();
        services.AddScoped<SessionService>();
        services.AddScoped<SessionCookieEvents>();
        services.AddScoped<EmailVerificationGate>();
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
            })
            // Remembers a verified password until the second factor is entered; grants no access by itself.
            .AddCookie(PendingLogin.Scheme, options =>
            {
                options.Cookie.Name = PendingLogin.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = PendingLogin.Lifetime;
                options.SlidingExpiration = false;
            });
        // The session cookie's expiry is set per sign-in from Session:Lifetime (SessionManager), so a changed
        // lifetime applies to new sessions without a restart.
        services.AddAuthorization();

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-valkyrie.af";
            // The __Host- prefix requires Path=/, also when the server runs under a path base.
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });
        services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
        services.AddRazorPages().AddMvcOptions(options => options.Filters.Add<PageSecurityFilter>());

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(RedisFixedWindowRateLimiter.UnavailableMetadata, out var unavailable) && unavailable)
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                }

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
            options.AddPolicy(RateLimitPolicies.TwoFactor, context => HttpMethods.IsPost(context.Request.Method)
                ? FixedWindowPerIp(context, RateLimitPolicies.TwoFactor, o => o.TwoFactor)
                : RateLimitPartition.GetNoLimiter(string.Empty));
            options.AddPolicy(RateLimitPolicies.Email, context => HttpMethods.IsPost(context.Request.Method)
                ? FixedWindowPerIp(context, RateLimitPolicies.Email, o => o.Email)
                : RateLimitPartition.GetNoLimiter(string.Empty));
            options.AddPolicy(RateLimitPolicies.Account, context => HttpMethods.IsPost(context.Request.Method)
                ? FixedWindowPerIp(context, RateLimitPolicies.Account, o => o.Account)
                : RateLimitPartition.GetNoLimiter(string.Empty));
        });

        return services;
    }

    public static WebApplication UseValkyrieAccounts(this WebApplication app)
    {
        // Branding:Directory is configuration-only, so it can be read once.
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
        endpoints.MapGet(ThemePath, (HttpContext context, ThemeStylesheet theme) =>
        {
            var (css, etag) = theme.Current;
            context.Response.Headers.ETag = etag;
            // Revalidated on every use, so branding changes show up without a restart or a stale cache.
            context.Response.Headers.CacheControl = "no-cache";
            return context.Request.Headers.IfNoneMatch == etag
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Text(css, "text/css", Encoding.UTF8);
        });
        return endpoints;
    }

    private static RateLimitPartition<string> FixedWindowPerIp(HttpContext context, string policy, Func<RateLimitOptions, RateLimitRule> rule)
    {
        var ip = ClientPartition(context.Connection.RemoteIpAddress);
        var partition = $"{policy}:{ip}";
        if (context.RequestServices.GetService<RedisRateLimitCounter>() is { } counter)
        {
            return RateLimitPartition.Get(partition, key => new RedisFixedWindowRateLimiter(
                counter, key, rule(context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value)));
        }

        return RateLimitPartition.GetFixedWindowLimiter(partition, _ =>
        {
            var options = rule(context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value);
            return new FixedWindowRateLimiterOptions { PermitLimit = options.PermitLimit, Window = options.Window, QueueLimit = 0 };
        });
    }

    /// <summary>
    /// The rate-limit partition of a client address. IPv6 clients usually get a whole /64, so they are grouped by
    /// that prefix; otherwise rotating addresses within it would bypass the limits. IPv4-mapped addresses count as IPv4.
    /// </summary>
    internal static string ClientPartition(System.Net.IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        bytes.AsSpan(8).Clear();
        return new System.Net.IPAddress(bytes) + "/64";
    }

    private static void AddOptions<T>(IServiceCollection services, IConfiguration configuration, string section)
        where T : class
    {
        services.AddOptions<T>().Bind(configuration.GetSection(section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<T>>(sp => (IValidateOptions<T>)(object)sp.GetRequiredService<AccountOptionsValidator>());
    }
}

/// <summary>CSS variables generated from <see cref="BrandingOptions"/>; values are validated, so they can't inject CSS.</summary>
internal sealed class ThemeStylesheet(IOptionsMonitor<BrandingOptions> options)
{
    private (BrandingOptions Options, string Css, string ETag)? _cache;

    /// <summary>The stylesheet for the current branding, rebuilt when the settings change.</summary>
    public (string Css, string ETag) Current
    {
        get
        {
            var branding = options.CurrentValue;
            if (_cache is { } cache && ReferenceEquals(cache.Options, branding))
            {
                return (cache.Css, cache.ETag);
            }

            var css = Build(branding);
            var etag = $"\"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..16]}\"";
            _cache = (branding, css, etag);
            return (css, etag);
        }
    }

    public string Css => Current.Css;

    // site.css defines the dark palette with higher specificity than :root, so the light colors below don't leak into
    // the dark theme. The dark colors use the same selectors as site.css and win by coming later.
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

        css.Append("}\n");

        var dark = new StringBuilder();
        Add(dark, "--vid-primary", branding.DarkPrimaryColor);
        Add(dark, "--vid-background", branding.DarkBackgroundColor);
        Add(dark, "--vid-surface", branding.DarkSurfaceColor);
        Add(dark, "--vid-text", branding.DarkTextColor);
        if (dark.Length > 0)
        {
            css.Append(CultureInfo.InvariantCulture, $"@media (prefers-color-scheme: dark) {{\n:root:not([data-theme=\"light\"]) {{\n{dark}}}\n}}\n");
            css.Append(CultureInfo.InvariantCulture, $":root[data-theme=\"dark\"] {{\n{dark}}}\n");
        }

        return css.ToString();
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
    private const string BasePolicy =
        "default-src 'none'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'";

    // Browsers apply form-action to the redirects after a form post too; pages whose forms end at a relying party
    // add that origin (PageSecurityFilter, ProtocolPages.FormPost).
    private const string ContentSecurityPolicy = BasePolicy + "; form-action 'self'";

    /// <summary>The login pages' policy, with <paramref name="formTarget"/>'s origin allowed as a form target.</summary>
    public static string PagePolicy(string? formTarget) =>
        FormActionSource(formTarget) is { } source ? $"{ContentSecurityPolicy} {source}" : ContentSecurityPolicy;

    /// <summary>
    /// A CSP source expression for a validated redirect URI: its origin (<c>https://host:port</c>), or for a
    /// private-use scheme (RFC 8252 §7.1) the scheme (<c>com.example.app:</c>).
    /// </summary>
    public static string? FormActionSource(string? uri)
    {
        if (uri is null || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || uri.StartsWith('/'))
        {
            return null;
        }

        var source = parsed.Scheme is "https" or "http" ? parsed.GetLeftPart(UriPartial.Authority) : parsed.Scheme + ":";
        // Validated URIs can't contain these, but a CSP header must never be extended with another directive.
        return source.Any(c => c is ';' or ',' or ' ' or '\'' or < '\x21' or > '\x7E') ? null : source;
    }

    // The admin UI (PROJECT.md §8.2) additionally runs its own scripts and calls the same-origin Admin API and token
    // endpoint. Still no inline scripts or styles, nothing from other origins, and no framing.
    private const string AdminUiContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; "
        + "manifest-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments(Administration.BuiltIn.AdminUiPath, StringComparison.OrdinalIgnoreCase)
            ? AdminUiContentSecurityPolicy
            : ContentSecurityPolicy;
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";

        // The admin UI and the Admin API are only used same-origin, without popups: isolate their browsing context
        // group and keep other sites from loading their responses. Not on the protocol and login pages, where COOP
        // would break relying parties' popup sign-in.
        if (context.Request.Path.StartsWithSegments(Administration.BuiltIn.AdminUiPath, StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments(AdminApi.AdminApiAuthentication.PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
        }

        return next(context);
    });
}
