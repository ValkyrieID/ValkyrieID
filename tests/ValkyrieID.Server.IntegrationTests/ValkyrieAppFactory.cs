using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Data;
using ValkyrieID.Server.Cli;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class ValkyrieAppFactory(
    DatabaseProvider provider,
    string connectionString,
    string? masterKey = null,
    string? previousMasterKey = null)
    : WebApplicationFactory<Program>
{
    public const string DefaultIssuer = "https://id.example.test";

    public string MasterKey { get; } = masterKey ?? Core.Security.MasterKey.Generate().ToBase64();

    /// <summary>Replaces the server clock when set.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Additional configuration; a <see langword="null"/> value removes a default setting.</summary>
    public Dictionary<string, string?> Settings { get; init; } = [];

    /// <summary>The client address the server sees, as if the request came from that peer.</summary>
    public IPAddress? RemoteIpAddress { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", provider.ToString());
        builder.UseSetting("Database:ConnectionString", connectionString);
        builder.UseSetting("MasterKey:Current", MasterKey);
        builder.UseSetting("Issuer:Url", DefaultIssuer);
        if (previousMasterKey is not null)
        {
            builder.UseSetting("MasterKey:Previous:0", previousMasterKey);
        }

        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        if (TimeProvider is { } timeProvider)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(timeProvider));
        }

        if (RemoteIpAddress is { } remoteIpAddress)
        {
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(remoteIpAddress)));
        }
    }

    /// <summary>Sets the connection's remote address before the app's own middleware runs.</summary>
    private sealed class RemoteIpStartupFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    public async Task<(int ExitCode, string Output, string Error)> RunCliAsync(params string[] args) =>
        await RunCliWithInputAsync(string.Empty, args);

    /// <summary>Runs a CLI command with <paramref name="input"/> as redirected standard input (e.g. a password).</summary>
    public async Task<(int ExitCode, string Output, string Error)> RunCliWithInputAsync(string input, params string[] args)
    {
        using var reader = new StringReader(input);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApp.RunAsync(Services, args, output, error, TestContext.Current.CancellationToken, reader);
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>A client that keeps cookies over https and doesn't follow redirects, like a browser under test.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });
}

/// <summary>A manually advanced clock.</summary>
public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
