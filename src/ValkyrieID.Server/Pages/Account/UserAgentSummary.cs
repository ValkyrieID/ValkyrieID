namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// A short display name for a session's browser, e.g. "Edge on Windows", from its <c>User-Agent</c>. Display only: the
/// header is client-controlled and is never used for anything else. Unrecognized values are shown as they are.
/// </summary>
internal static class UserAgentSummary
{
    // Order matters: Edge and Opera also send "Chrome/", Chrome also sends "Safari/".
    private static readonly (string Token, string Name)[] Browsers =
    [
        ("Edg/", "Edge"),
        ("EdgA/", "Edge"),
        ("EdgiOS/", "Edge"),
        ("OPR/", "Opera"),
        ("Firefox/", "Firefox"),
        ("FxiOS/", "Firefox"),
        ("CriOS/", "Chrome"),
        ("Chrome/", "Chrome"),
        ("Version/", "Safari"),
    ];

    // Order matters: Android and ChromeOS also send "Linux", iOS also sends "like Mac OS X".
    private static readonly (string Token, string Name)[] Systems =
    [
        ("Windows", "Windows"),
        ("iPhone", "iOS"),
        ("iPad", "iPadOS"),
        ("Android", "Android"),
        ("CrOS", "ChromeOS"),
        ("Mac OS X", "macOS"),
        ("Linux", "Linux"),
    ];

    public static string Summarize(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown device";
        }

        var browser = Find(userAgent, Browsers);
        var system = Find(userAgent, Systems);
        return (browser, system) switch
        {
            ({ } b, { } s) => $"{b} on {s}",
            ({ } b, null) => b,
            (null, { } s) => $"Browser on {s}",
            _ => userAgent,
        };
    }

    private static string? Find(string userAgent, (string Token, string Name)[] candidates)
    {
        foreach (var (token, name) in candidates)
        {
            if (userAgent.Contains(token, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }
}
