namespace ValkyrieID.Server.Configuration;

/// <summary>
/// The light/dark theme choice shared by the login pages and the admin UI. A display preference only: it is never
/// trusted for anything else, and unknown values mean "follow the system" (<c>prefers-color-scheme</c>).
/// </summary>
internal static class ThemePreference
{
    /// <summary>Not HttpOnly: the admin UI reads and writes it with <c>document.cookie</c>.</summary>
    public const string CookieName = "__Host-valkyrie.theme";

    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    /// <summary>The <c>data-theme</c> value for the request: <c>light</c>, <c>dark</c>, or <see langword="null"/> to follow the system.</summary>
    public static string? Read(HttpRequest request) => Normalize(request.Cookies[CookieName]);

    /// <summary><c>light</c> or <c>dark</c>; anything else (including <c>system</c>) is <see langword="null"/>.</summary>
    public static string? Normalize(string? value) => value switch
    {
        Light => Light,
        Dark => Dark,
        _ => null,
    };

    public static bool IsValid(string? value) => value is Light or Dark or System;

    public static void Write(HttpResponse response, string value, DateTimeOffset now)
    {
        if (Normalize(value) is { } theme)
        {
            response.Cookies.Append(CookieName, theme, Options(now.Add(Lifetime)));
        }
        else
        {
            response.Cookies.Delete(CookieName, Options(null));
        }
    }

    // The __Host- prefix requires Secure and Path=/ without a Domain, also under a path base.
    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        Secure = true,
        HttpOnly = false,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };
}
