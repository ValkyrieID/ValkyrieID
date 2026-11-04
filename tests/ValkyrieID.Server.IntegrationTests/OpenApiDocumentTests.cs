using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ValkyrieID.Data;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>
/// The Admin API's OpenAPI document is served in Development only and published in the docs. This test keeps the
/// published copy current: run it with <c>VALKYRIEID_UPDATE_OPENAPI=1</c> to rewrite the file after API changes.
/// </summary>
public sealed class OpenApiDocumentTests : IDisposable
{
    private const string PublishedPath = "docs/public/openapi/admin-api.json";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "valkyrieid-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_published_document_matches_the_api()
    {
        await using var factory = CreateFactory("Development");
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;
        Assert.All(document["paths"]!.AsObject(), path => Assert.StartsWith("/api/admin/", path.Key, StringComparison.Ordinal));

        var generated = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .ReplaceLineEndings("\n") + "\n";
        var published = Path.Combine(RepositoryRoot(), PublishedPath);
        if (Environment.GetEnvironmentVariable("VALKYRIEID_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(published)!);
            await File.WriteAllTextAsync(published, generated, CancellationToken);
        }

        Assert.True(File.Exists(published), $"{PublishedPath} is missing; run the tests with VALKYRIEID_UPDATE_OPENAPI=1.");
        var current = (await File.ReadAllTextAsync(published, CancellationToken)).ReplaceLineEndings("\n");
        Assert.True(current == generated, $"{PublishedPath} is out of date; run the tests with VALKYRIEID_UPDATE_OPENAPI=1.");
    }

    [Fact]
    public async Task The_document_is_not_served_outside_development()
    {
        await using var factory = CreateFactory("Production");

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/openapi/v1.json", CancellationToken)).StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ValkyrieAppFactory CreateFactory(string environment) =>
        new(DatabaseProvider.Sqlite, $"Data Source={Path.Combine(_directory, $"{environment}.db")};Pooling=False") { EnvironmentName = environment };

    // From the test binaries, or this source file when they are built outside the repository (--artifacts-path).
    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) ?? string.Empty })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ValkyrieID.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("The repository root (ValkyrieID.slnx) wasn't found.");
    }
}
