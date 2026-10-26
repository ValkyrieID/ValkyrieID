using System.Net;
using System.Text.RegularExpressions;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Helpers for posting Razor Pages forms with their anti-forgery token.</summary>
internal static partial class Forms
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static async Task<string> GetTokenAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(CancellationToken);
        return TokenPattern().Match(html) is { Success: true } match
            ? WebUtility.HtmlDecode(match.Groups[1].Value)
            : throw new InvalidOperationException($"No anti-forgery token on {path}.");
    }

    /// <summary>Loads <paramref name="path"/> for its anti-forgery token, then posts the form to it.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var token = await GetTokenAsync(client, path);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields.Append(new("__RequestVerificationToken", token))), CancellationToken);
    }

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string identifier, string password, string? returnUrl = null) =>
        PostAsync(client, "/account/login", new()
        {
            ["Identifier"] = identifier,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl ?? string.Empty,
        });

    /// <summary>The page body without per-request values (anti-forgery token, echoed identifier).</summary>
    public static async Task<string> NormalizedBodyAsync(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync(CancellationToken);
        html = TokenPattern().Replace(html, "TOKEN");
        return IdentifierValuePattern().Replace(html, "IDENTIFIER");
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("id=\"identifier\" name=\"Identifier\" type=\"text\" value=\"[^\"]*\"")]
    private static partial Regex IdentifierValuePattern();
}
