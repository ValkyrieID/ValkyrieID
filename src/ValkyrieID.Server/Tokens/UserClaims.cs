using ValkyrieID.Core.Protocol;
using ValkyrieID.Core.Users;

namespace ValkyrieID.Server.Tokens;

/// <summary>Builds the user claims released for a set of scopes (OIDC Core A5.4).</summary>
internal static class UserClaims
{
    /// <summary>Claims a custom user claim must never override: protocol claims and the standard user claims.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "iss", "sub", "aud", "exp", "nbf", "iat", "jti", "auth_time", "nonce", "acr", "amr", "azp", "sid",
        "at_hash", "c_hash", "s_hash", "scope", "client_id", "cnf", "typ",
        "name", "given_name", "family_name", "middle_name", "nickname", "preferred_username", "profile", "picture",
        "website", "email", "email_verified", "gender", "birthdate", "zoneinfo", "locale", "phone_number",
        "phone_number_verified", "address", "updated_at", "roles", "groups",
    };

    /// <summary>
    /// The claims to release for <paramref name="scopes"/>. The user must be loaded with roles, groups, and claims.
    /// Empty values are omitted (OIDC Core A5.3.2).
    /// </summary>
    public static Dictionary<string, object> For(User user, IEnumerable<string> scopes)
    {
        var granted = scopes.ToHashSet(StringComparer.Ordinal);
        var claims = new Dictionary<string, object>(StringComparer.Ordinal);

        if (granted.Contains(IdentityScopes.Profile))
        {
            Add(claims, "name", user.DisplayName);
            Add(claims, "given_name", user.GivenName);
            Add(claims, "family_name", user.FamilyName);
            Add(claims, "preferred_username", user.UserName);
            claims["updated_at"] = (user.UpdatedAt == default ? user.CreatedAt : user.UpdatedAt).ToUnixTimeSeconds();

            foreach (var group in user.Claims.Where(c => !Reserved.Contains(c.Type)).GroupBy(c => c.Type, StringComparer.Ordinal))
            {
                var values = group.Select(c => c.Value).ToArray();
                claims[group.Key] = values.Length == 1 ? values[0] : values;
            }
        }

        if (granted.Contains(IdentityScopes.Email) && !string.IsNullOrEmpty(user.Email))
        {
            claims["email"] = user.Email;
            claims["email_verified"] = user.EmailVerified;
        }

        if (granted.Contains(IdentityScopes.Roles) && user.Roles.Count > 0)
        {
            claims["roles"] = user.Roles.Select(r => r.Name).Order(StringComparer.Ordinal).ToArray();
        }

        if (granted.Contains(IdentityScopes.Groups) && user.Groups.Count > 0)
        {
            claims["groups"] = user.Groups.Select(g => g.Name).Order(StringComparer.Ordinal).ToArray();
        }

        return claims;
    }

    private static void Add(Dictionary<string, object> claims, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            claims[name] = value;
        }
    }
}
