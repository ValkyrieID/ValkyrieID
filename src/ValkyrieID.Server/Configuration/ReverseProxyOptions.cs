using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace ValkyrieID.Server.Configuration;

/// <summary>
/// Settings for running behind a reverse proxy. Forwarded headers are only honored from the listed proxies. The
/// issuer is never derived from them; it always comes from <c>Issuer:Url</c>.
/// </summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>IP addresses of proxies whose forwarded headers are trusted.</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>Networks in CIDR notation (e.g. <c>10.0.0.0/8</c>) whose forwarded headers are trusted.</summary>
    public List<string> KnownNetworks { get; set; } = [];

    /// <summary>Which forwarded headers to apply.</summary>
    public ForwardedHeaders ForwardedHeaders { get; set; } = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    /// <summary>How many proxy hops to process in <c>X-Forwarded-For</c>.</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>A path prefix the server is mounted under when the proxy doesn't strip it, e.g. <c>/auth</c>.</summary>
    public string? PathBase { get; set; }

    public bool Enabled => KnownProxies.Count > 0 || KnownNetworks.Count > 0;
}

internal sealed class ReverseProxyOptionsValidator(IOptions<IssuerOptions> issuerOptions) : IValidateOptions<ReverseProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ReverseProxyOptions options)
    {
        List<string> failures = [];

        foreach (var proxy in options.KnownProxies.Where(p => !IPAddress.TryParse(p, out _)))
        {
            failures.Add($"ReverseProxy:KnownProxies contains an invalid IP address '{proxy}'.");
        }

        foreach (var network in options.KnownNetworks.Where(n => !IPNetwork.TryParse(n, out _)))
        {
            failures.Add($"ReverseProxy:KnownNetworks contains an invalid network '{network}'; use CIDR notation such as 10.0.0.0/8.");
        }

        if (options.ForwardLimit < 1)
        {
            failures.Add("ReverseProxy:ForwardLimit must be at least 1.");
        }

        if (options.PathBase is { } pathBase)
        {
            if (!pathBase.StartsWith('/') || pathBase.EndsWith('/') || pathBase.Contains('?') || pathBase.Contains('#'))
            {
                failures.Add("ReverseProxy:PathBase must start with '/' and have no trailing slash, query, or fragment, e.g. /auth.");
            }
            else if (Uri.TryCreate(issuerOptions.Value.Url, UriKind.Absolute, out var issuer)
                && !string.Equals(issuer.AbsolutePath.TrimEnd('/'), pathBase, StringComparison.Ordinal))
            {
                // Endpoint URLs are the issuer URL plus the endpoint path, so the issuer must include the path base.
                failures.Add($"ReverseProxy:PathBase '{pathBase}' must match the path of Issuer:Url ('{issuer.AbsolutePath.TrimEnd('/')}').");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal static class ReverseProxyExtensions
{
    public static IServiceCollection AddValkyrieReverseProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ReverseProxyOptions>().Bind(configuration.GetSection(ReverseProxyOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReverseProxyOptions>, ReverseProxyOptionsValidator>();
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ReverseProxyOptions>>((forwarded, proxy) =>
        {
            var options = proxy.Value;
            forwarded.ForwardedHeaders = options.ForwardedHeaders;
            forwarded.ForwardLimit = options.ForwardLimit;

            // Only the configured proxies are trusted; the loopback defaults are dropped.
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var address in options.KnownProxies)
            {
                forwarded.KnownProxies.Add(IPAddress.Parse(address));
            }

            foreach (var network in options.KnownNetworks)
            {
                forwarded.KnownIPNetworks.Add(IPNetwork.Parse(network));
            }
        });
        return services;
    }

    /// <summary>Must run first, so every later component sees the client's address and scheme.</summary>
    public static WebApplication UseValkyrieReverseProxy(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ReverseProxyOptions>>().Value;
        if (options.Enabled)
        {
            app.UseForwardedHeaders();
        }

        if (options.PathBase is { Length: > 0 } pathBase)
        {
            app.UsePathBase(pathBase);
        }

        return app;
    }
}
