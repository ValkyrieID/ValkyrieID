namespace ValkyrieID.Server.Administration;

/// <summary>Names of the entities the server creates for the Admin API and the admin UI (PROJECT.md §9).</summary>
internal static class BuiltIn
{
    /// <summary>The Admin API's API resource; its name is the audience (<c>aud</c>) of admin access tokens.</summary>
    public const string AdminResource = "valkyrieid-admin";

    /// <summary>The scope an access token needs to call the Admin API.</summary>
    public const string AdminScope = "valkyrieid.admin";

    /// <summary>The admin UI: a public client using the authorization code flow with PKCE.</summary>
    public const string AdminUiClientId = "valkyrieid-admin-ui";

    /// <summary>Where the admin UI is served; its redirect URIs are derived from the issuer URL and this path.</summary>
    public const string AdminUiPath = "/admin";

    /// <summary>Admin access tokens are short-lived: they can't be revoked before they expire (PROJECT.md §5).</summary>
    public static readonly TimeSpan AdminUiAccessTokenLifetime = TimeSpan.FromMinutes(10);
}
