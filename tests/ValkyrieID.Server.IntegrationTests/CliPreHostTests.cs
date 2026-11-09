using ValkyrieID.Server.Cli;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>
/// Arguments handled before the host is built: they must never migrate the database or start the server.
/// </summary>
public sealed class CliPreHostTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("client", "--help")]
    [InlineData("user", "create", "-h")]
    public void Help_prints_usage_without_the_host(params string[] args)
    {
        var (exitCode, output, error) = Run(args);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: valkyrieid", output, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    [Fact]
    public void Version_prints_the_informational_version_without_the_host()
    {
        var (exitCode, output, _) = Run("--version");

        Assert.Equal(0, exitCode);
        Assert.Equal(CliApp.Version, output.Trim());
        Assert.NotEqual("unknown", CliApp.Version);
    }

    [Theory]
    [InlineData("clinet", "list")]
    [InlineData("serve")]
    public void Unknown_command_fails_without_the_host(params string[] args)
    {
        var (exitCode, output, error) = Run(args);

        Assert.Equal(1, exitCode);
        Assert.Empty(output);
        Assert.Contains($"unknown command '{args[0]}'", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("--urls", "http://127.0.0.1:5000")]
    [InlineData("urls=http://127.0.0.1:5000")]
    [InlineData("client", "list")]
    [InlineData("help")]
    public void Server_and_commands_build_the_host(params string[] args)
    {
        var (exitCode, output, error) = Run(args);

        Assert.Null(exitCode);
        Assert.Empty(output);
        Assert.Empty(error);
    }

    private static (int? ExitCode, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CliApp.TryRunWithoutHost(args, output, error);
        return (exitCode, output.ToString(), error.ToString());
    }
}
