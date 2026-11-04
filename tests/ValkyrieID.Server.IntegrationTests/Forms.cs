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

    /// <summary>
    /// Signs in a user who must use 2FA but hasn't set it up (e.g. an admin while the Admin API requires a second
    /// factor): the password, then the enrollment page at sign-in. Returns the new TOTP secret.
    /// </summary>
    public static async Task<byte[]> LoginWithEnrollmentAsync(HttpClient client, string identifier, string password, TimeProvider? clock = null)
    {
        var login = await LoginAsync(client, identifier, password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith("/account/login/enroll", login.Headers.Location?.OriginalString, StringComparison.Ordinal);
        return await CompleteEnrollmentAsync(client, clock);
    }

    /// <summary>Completes the 2FA enrollment at sign-in that a login or the setup wizard redirected to.</summary>
    public static async Task<byte[]> CompleteEnrollmentAsync(HttpClient client, TimeProvider? clock = null)
    {
        const string path = "/account/login/enroll?returnUrl=%2Faccount";
        var page = await client.GetAsync(path, CancellationToken);
        var html = await page.Content.ReadAsStringAsync(CancellationToken);
        var state = StatePattern().Match(html);
        var secretText = SecretPattern().Match(html);
        Assert.True(state.Success && secretText.Success, html);
        var secret = Core.Security.Base32.Decode(secretText.Groups[1].Value);
        var code = Core.Security.Totp.Compute(secret, Core.Security.Totp.TimeStep((clock ?? TimeProvider.System).GetUtcNow()));

        var token = TokenPattern().Match(html).Groups[1].Value;
        var confirm = await client.PostAsync(path, new FormUrlEncodedContent(
        [
            new("State", WebUtility.HtmlDecode(state.Groups[1].Value)),
            new("Code", code),
            new("__RequestVerificationToken", WebUtility.HtmlDecode(token)),
        ]), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Contains("Two-factor authentication is on", await confirm.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        return secret;
    }

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

    [GeneratedRegex("name=\"State\" value=\"([^\"]+)\"")]
    private static partial Regex StatePattern();

    [GeneratedRegex("id=\"totp-secret\">([A-Z2-7 ]+)</code>")]
    private static partial Regex SecretPattern();
}
