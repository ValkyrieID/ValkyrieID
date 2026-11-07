using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ValkyrieID.Data;
using ValkyrieID.Server.Cli;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>
/// <c>valkyrieid healthcheck</c> against a server listening on a real loopback socket, as in the container, and
/// against raw sockets for the failure modes.
/// </summary>
public sealed class HealthCheckCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("--ready")]
    [InlineData("--live")]
    [InlineData(null)]
    public async Task Healthy_server_exits_with_zero(string? flag)
    {
        await using var factory = CreateFactory();
        var port = StartKestrel(factory);

        var (exitCode, output, _) = await RunAsync(Config(("http_ports", port.ToString(CultureInfo))), flag is null ? [] : [flag]);

        Assert.Equal(0, exitCode);
        Assert.Contains(flag == "--live" ? "/health/live: 200" : "/health/ready: 200", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_http_probe_works_while_the_issuer_is_https()
    {
        // The issuer is https, so other plain-http requests are refused; /health is exempt.
        await using var factory = CreateFactory();
        var port = StartKestrel(factory);
        using var http = new HttpClient();

        var refused = await http.GetAsync($"http://127.0.0.1:{port}/.well-known/openid-configuration", CancellationToken);
        var (exitCode, _, _) = await RunAsync(Config(("urls", $"http://+:{port}")), []);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Probe_uses_the_path_base()
    {
        await using var factory = CreateFactory(new()
        {
            ["Issuer:Url"] = "https://id.example.test/auth",
            ["ReverseProxy:PathBase"] = "/auth",
        });
        var port = StartKestrel(factory);

        var (exitCode, output, _) = await RunAsync(Config(("http_ports", port.ToString(CultureInfo)), ("ReverseProxy:PathBase", "/auth")), []);

        Assert.Equal(0, exitCode);
        Assert.Contains("/auth/health/ready: 200", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Url_option_overrides_the_configured_address()
    {
        await using var factory = CreateFactory();
        var port = StartKestrel(factory);

        var (exitCode, _, _) = await RunAsync(Config(("http_ports", "1")), ["--url", $"http://127.0.0.1:{port}"]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Nothing_listening_exits_with_one()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var (exitCode, _, error) = await RunAsync(Config(("http_ports", port.ToString(CultureInfo))), []);

        Assert.Equal(1, exitCode);
        Assert.Contains("unreachable", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("503 Service Unavailable")]
    [InlineData("302 Found\r\nLocation: http://127.0.0.1:1/health/ready")]
    [InlineData("500 Internal Server Error")]
    public async Task Unhealthy_or_redirecting_server_exits_with_one(string status)
    {
        using var stub = new StubServer(respond: true, status);

        var (exitCode, _, error) = await RunAsync(Config(("http_ports", stub.Port.ToString(CultureInfo))), []);

        Assert.Equal(1, exitCode);
        Assert.Contains("unhealthy", error, StringComparison.Ordinal);
        Assert.Equal("GET /health/ready HTTP/1.1", await stub.RequestLine);
    }

    [Fact]
    public async Task Unhealthy_readiness_check_exits_with_one()
    {
        // An unreachable Redis makes the readiness check fail while the server itself runs.
        await using var factory = CreateFactory(new()
        {
            ["Redis:ConnectionString"] = "127.0.0.1:1,connectTimeout=500,syncTimeout=500,asyncTimeout=500,connectRetry=0",
        });
        var port = StartKestrel(factory);

        var (ready, _, error) = await RunAsync(Config(("http_ports", port.ToString(CultureInfo))), ["--ready", "--timeout", "30"]);
        var (live, _, _) = await RunAsync(Config(("http_ports", port.ToString(CultureInfo))), ["--live"]);

        Assert.Equal(1, ready);
        Assert.Contains("/health/ready: 503 unhealthy", error, StringComparison.Ordinal);
        Assert.Equal(0, live);
    }

    [Fact]
    public async Task Server_that_never_answers_times_out()
    {
        using var stub = new StubServer(respond: false);

        var (exitCode, _, error) = await RunAsync(Config(("http_ports", stub.Port.ToString(CultureInfo))), ["--timeout", "1"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("timed out", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--live", "--ready")]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "301")]
    [InlineData("--url", "/health")]
    [InlineData("--url", "ftp://127.0.0.1/")]
    [InlineData("--unknown", "x")]
    public async Task Invalid_arguments_exit_with_one(params string[] args)
    {
        var (exitCode, _, error) = await RunAsync(Config(), args);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("error:", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, null, null, "http://127.0.0.1:5000/")]
    [InlineData("8080", null, null, null, "http://127.0.0.1:8080/")]
    [InlineData("8080;9090", null, null, null, "http://127.0.0.1:8080/")]
    [InlineData("8080", "http://*:7000", null, null, "http://127.0.0.1:7000/")]
    [InlineData(null, "https://+:443;http://+:80", null, null, "http://127.0.0.1/")]
    [InlineData(null, "http://0.0.0.0:7001", null, null, "http://127.0.0.1:7001/")]
    [InlineData(null, "http://[::]:7002", null, null, "http://[::1]:7002/")]
    [InlineData(null, "http://localhost:7003", null, null, "http://localhost:7003/")]
    [InlineData("8080", "http://+:7000", "http://*:6000", null, "http://127.0.0.1:6000/")]
    [InlineData("8080", null, null, "/auth", "http://127.0.0.1:8080/auth")]
    public void Address_follows_kestrel_precedence(string? httpPorts, string? urls, string? endpoint, string? pathBase, string expected)
    {
        var configuration = Config(
            ("http_ports", httpPorts), ("urls", urls), ("Kestrel:Endpoints:Http:Url", endpoint), ("ReverseProxy:PathBase", pathBase));

        Assert.Equal(new Uri(expected), HealthCheckCommand.ResolveBaseUrl(configuration));
    }

    [Theory]
    [InlineData("https://+:8443", null, null)]
    [InlineData(null, "https://*:8443", null)]
    [InlineData(null, null, "abc")]
    public void Without_a_plain_http_address_the_command_fails(string? urls, string? endpoint, string? httpPorts)
    {
        var configuration = Config(("urls", urls), ("Kestrel:Endpoints:Https:Url", endpoint), ("http_ports", httpPorts));

        Assert.Throws<CliException>(() => HealthCheckCommand.ResolveBaseUrl(configuration));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static readonly IFormatProvider CultureInfo = System.Globalization.CultureInfo.InvariantCulture;

    private ValkyrieAppFactory CreateFactory(Dictionary<string, string?>? settings = null) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, "valkyrieid.db")};Pooling=False")
        {
            Settings = settings ?? [],
        };

    private static int StartKestrel(ValkyrieAppFactory factory)
    {
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        factory.StartServer();
        var address = factory.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        return new Uri(address).Port;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Where(v => v.Value is not null).Select(v => KeyValuePair.Create(v.Key, v.Value))).Build();

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(IConfiguration configuration, string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await HealthCheckCommand.RunAsync(configuration, ["healthcheck", .. args], output, error, cancellationToken: CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Accepts one connection on loopback, records the request line, and answers with a fixed status.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public StubServer(bool respond, string status = "200 OK")
        {
            _listener.Start();
            RequestLine = ServeAsync(respond, status);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<string> RequestLine { get; }

        private async Task<string> ServeAsync(bool respond, string status)
        {
            using var socket = await _listener.AcceptSocketAsync(_stop.Token);
            await using var stream = new NetworkStream(socket);
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(_stop.Token) ?? string.Empty;
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token)))
            {
            }

            if (respond)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
            }
            else
            {
                await Task.Delay(Timeout.Infinite, _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            }

            return requestLine;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
