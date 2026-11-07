using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Cli;

/// <summary>
/// <c>valkyrieid healthcheck [--live|--ready]</c>: probes the running server's health endpoint over loopback and
/// exits 0 when it is healthy, 1 otherwise. It is the container health check (the image has no shell or curl), so
/// it runs before the host is built: no master key, database, or migrations are touched.
/// </summary>
/// <remarks>
/// The probe uses plain http on loopback. While the issuer is https, the server refuses plain-http requests except
/// for the health endpoints (see <c>UseRequireHttps</c>), and nothing sensitive is sent.
/// </remarks>
internal static class HealthCheckCommand
{
    public const string Name = "healthcheck";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public const string Usage = """
        Usage: valkyrieid healthcheck [--ready|--live] [--url <http-url>] [--timeout <seconds>]

        Calls /health/ready (default) or /health/live of the server running on this machine and exits with 0 when
        it is healthy, 1 otherwise. The address comes from ASPNETCORE_HTTP_PORTS, ASPNETCORE_URLS, or
        Kestrel:Endpoints, and ReverseProxy:PathBase; --url overrides it (e.g. http://127.0.0.1:8080/auth).
        """;

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == Name;

    /// <summary>Reads the same configuration sources as the server, without building the host.</summary>
    public static IConfiguration LoadConfiguration()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddEnvironmentVariables("DOTNET_")
            .AddEnvironmentVariables("ASPNETCORE_")
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    public static async Task<int> RunAsync(
        IConfiguration configuration,
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        Uri target;
        TimeSpan timeout;
        try
        {
            var parsed = CliArguments.Parse(args);
            var live = parsed.Flag("live");
            if (live && parsed.Flag("ready"))
            {
                throw new CliException("Use either --live or --ready.");
            }

            timeout = parsed.OptionalInt("timeout") is { } seconds
                ? seconds <= 300 ? TimeSpan.FromSeconds(seconds) : throw new CliException("Option --timeout must be at most 300 seconds.")
                : DefaultTimeout;
            var url = parsed.Optional("url");
            parsed.Flag("ready");
            parsed.EnsureConsumed(1);

            var baseUrl = url is not null ? ParseOverride(url) : ResolveBaseUrl(configuration);
            target = new Uri(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + (live ? "/health/live" : "/health/ready"));
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}");
            await error.WriteLineAsync(Usage);
            return 1;
        }

        // No proxy (HTTP_PROXY must not reroute a loopback probe), no redirects (a redirect is not healthy).
        handler ??= new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = timeout };
        try
        {
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var healthy = response.StatusCode == System.Net.HttpStatusCode.OK;
            await (healthy ? output : error).WriteLineAsync($"{target.AbsolutePath}: {(int)response.StatusCode} {(healthy ? "healthy" : "unhealthy")}");
            return healthy ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync($"{target.AbsolutePath}: unreachable ({(ex is TaskCanceledException ? "timed out" : ex.Message)})");
            return 1;
        }
    }

    /// <summary>
    /// The server's first plain-http address with wildcard hosts replaced by loopback, plus the path base. Follows
    /// Kestrel's precedence: <c>Kestrel:Endpoints</c>, then <c>urls</c>, then <c>http_ports</c>, then the default
    /// http://localhost:5000.
    /// </summary>
    internal static Uri ResolveBaseUrl(IConfiguration configuration)
    {
        var endpoints = configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]).OfType<string>().ToList();
        Uri? address;
        if (endpoints.Count > 0)
        {
            address = FirstHttp(endpoints) ?? throw new CliException("Kestrel:Endpoints has no plain-http endpoint to probe; use --url.");
        }
        else if (configuration["urls"] is { Length: > 0 } urls)
        {
            address = FirstHttp(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                ?? throw new CliException("ASPNETCORE_URLS has no plain-http address to probe; use --url.");
        }
        else if (configuration["http_ports"] is { Length: > 0 } ports)
        {
            var port = ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            address = int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number is > 0 and <= 65535
                ? new Uri($"http://127.0.0.1:{number}")
                : throw new CliException($"ASPNETCORE_HTTP_PORTS has an invalid port '{port}'.");
        }
        else
        {
            address = new Uri("http://127.0.0.1:5000");
        }

        var pathBase = configuration[$"{ReverseProxyOptions.SectionName}:{nameof(ReverseProxyOptions.PathBase)}"];
        return string.IsNullOrEmpty(pathBase) ? address : new Uri(address, pathBase);
    }

    private static Uri? FirstHttp(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            // Kestrel accepts "*", "+", and empty hosts, which aren't valid URIs; they mean every interface.
            var normalized = System.Text.RegularExpressions.Regex.Replace(
                url, @"^http://(\*|\+|0\.0\.0\.0|)(?=[:/]|$)", "http://127.0.0.1", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Replace("http://[::]", "http://[::1]", StringComparison.OrdinalIgnoreCase);
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
            {
                return new Uri(uri.GetLeftPart(UriPartial.Authority));
            }
        }

        return null;
    }

    private static Uri ParseOverride(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri
            : throw new CliException("--url must be an absolute http or https URL without query or fragment.");
}
