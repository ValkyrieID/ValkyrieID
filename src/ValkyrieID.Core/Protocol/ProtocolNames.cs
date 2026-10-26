namespace ValkyrieID.Core.Protocol;

public static class GrantTypes
{
    public const string AuthorizationCode = "authorization_code";
    public const string ClientCredentials = "client_credentials";
    public const string RefreshToken = "refresh_token";
}

public static class IdentityScopes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "openid", "profile", "email", "roles", "groups", "offline_access",
    };
}

public static class ProtocolSyntax
{
    /// <summary>
    /// RFC 6749 §3.3: <c>scope-token = 1*( %x21 / %x23-5B / %x5D-7E )</c>.
    /// </summary>
    public static bool IsValidScopeToken(string value) =>
        value.Length > 0 && value.All(c => c is '\x21' or (>= '\x23' and <= '\x5B') or (>= '\x5D' and <= '\x7E'));

    /// <summary>
    /// RFC 6749 Appendix A.1: <c>client-id = *VSCHAR</c> (%x20-7E). Spaces are also rejected to keep ids unambiguous.
    /// </summary>
    public static bool IsValidClientId(string value) =>
        value.Length is > 0 and <= 200 && value.All(c => c is > '\x20' and <= '\x7E');

    /// <summary>Resource names become the <c>aud</c> claim; restricted to the same characters as scopes.</summary>
    public static bool IsValidResourceName(string value) => value.Length <= 200 && IsValidScopeToken(value);
}
