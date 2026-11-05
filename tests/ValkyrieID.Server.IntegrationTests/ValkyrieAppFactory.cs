using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ValkyrieID.Data;
using ValkyrieID.Server.AdminUi;
using ValkyrieID.Server.Cli;
using ValkyrieID.Server.Email;
using ValkyrieID.Server.Logout;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class ValkyrieAppFactory(
    DatabaseProvider provider,
    string connectionString,
    string? masterKey = null,
    string? previousMasterKey = null)
    : WebApplicationFactory<Program>
{
    public const string DefaultIssuer = "https://id.example.test";

    /// <summary>
    /// A client for https://localhost: the server refuses plain-http requests while the issuer is https (RFC 6749
    /// §3.1, §3.2).
    /// </summary>
    public new HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    /// <summary>A client for plain http, to test that such requests are refused.</summary>
    public HttpClient CreateHttpClient() => base.CreateClient();

    /// <summary>
    /// On Linux every file watcher takes an inotify instance, and the default limit (128, also on GitHub's runners) is
    /// reached when many test servers run in parallel. Test servers don't watch appsettings files (runtime settings
    /// come from the database), and the remaining watchers (static files, asp-append-version) poll instead.
    /// </summary>
    static ValkyrieAppFactory()
    {
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
    }

    public string MasterKey { get; } = masterKey ?? Core.Security.MasterKey.Generate().ToBase64();

    /// <summary>Replaces the server clock when set.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Additional configuration; a <see langword="null"/> value removes a default setting.</summary>
    public Dictionary<string, string?> Settings { get; init; } = [];

    /// <summary>The client address the server sees, as if the request came from that peer.</summary>
    public IPAddress? RemoteIpAddress { get; init; }

    /// <summary>Receives the server's back-channel logout requests instead of the network.</summary>
    public HttpMessageHandler? BackChannelHandler { get; init; }

    /// <summary>Stops the background delivery worker, so tests drive deliveries themselves.</summary>
    public bool DisableBackChannelWorker { get; init; }

    /// <summary>Receives the server's emails instead of an SMTP server.</summary>
    internal TestEmailSender? EmailSender { get; init; }

    /// <summary>Stops the background email worker, so tests send queued emails themselves.</summary>
    public bool DisableEmailWorker { get; init; }

    /// <summary>The host environment; <c>Testing</c> unless a test needs Development-only behavior.</summary>
    public string EnvironmentName { get; init; } = "Testing";

    /// <summary>Serves the admin UI from this directory instead of the build output in wwwroot/admin.</summary>
    public string? AdminUiDirectory { get; init; }

    /// <summary>
    /// Receives every formatted log message (with exception details) at the configured levels, plus Trace for
    /// ValkyrieID's own categories.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<string>? CapturedLogs { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
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

        if (BackChannelHandler is { } handler)
        {
            builder.ConfigureTestServices(services => services.AddHttpClient(BackChannelLogoutDispatcher.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler));
        }

        if (DisableBackChannelWorker)
        {
            builder.ConfigureTestServices(services => RemoveHostedService<BackChannelLogoutWorker>(services));
        }

        if (EmailSender is { } sender)
        {
            builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(sender));
        }

        if (DisableEmailWorker)
        {
            builder.ConfigureTestServices(services => RemoveHostedService<EmailWorker>(services));
        }

        if (AdminUiDirectory is { } adminUi)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(new AdminUiFiles(new PhysicalFileProvider(adminUi))));
        }

        if (CapturedLogs is { } logs)
        {
            builder.UseSetting("Logging:LogLevel:ValkyrieID", "Trace");
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(logs)));
        }
    }

    private sealed class CapturingLoggerProvider(System.Collections.Concurrent.ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, logs);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, System.Collections.Concurrent.ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            // The structured values too, in case a template doesn't render one of them.
            logs.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception} {(state is IEnumerable<KeyValuePair<string, object?>> values ? string.Join(' ', values.Select(v => $"{v.Key}={v.Value}")) : null)}");
    }

    private static void RemoveHostedService<T>(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
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

/// <summary>Collects sent emails; can be told to fail.</summary>
internal sealed class TestEmailSender : IEmailSender
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<EmailMessage> _sent = new();

    /// <summary>Thrown for every send while set.</summary>
    public EmailDeliveryException? Failure { get; set; }

    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
