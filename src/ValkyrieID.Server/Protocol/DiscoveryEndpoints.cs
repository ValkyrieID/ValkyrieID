using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Protocol;

internal sealed record DiscoveryDocument(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("userinfo_endpoint")] string UserInfoEndpoint,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported,
    [property: JsonPropertyName("response_types_supported")] IReadOnlyList<string> ResponseTypesSupported,
    [property: JsonPropertyName("response_modes_supported")] IReadOnlyList<string> ResponseModesSupported,
    [property: JsonPropertyName("grant_types_supported")] IReadOnlyList<string> GrantTypesSupported,
    [property: JsonPropertyName("token_endpoint_auth_methods_supported")] IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
    [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string> CodeChallengeMethodsSupported,
    [property: JsonPropertyName("subject_types_supported")] IReadOnlyList<string> SubjectTypesSupported,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] IReadOnlyList<string> IdTokenSigningAlgValuesSupported,
    [property: JsonPropertyName("claims_supported")] IReadOnlyList<string> ClaimsSupported,
    [property: JsonPropertyName("prompt_values_supported")] IReadOnlyList<string> PromptValuesSupported,
    [property: JsonPropertyName("claims_parameter_supported")] bool ClaimsParameterSupported,
    [property: JsonPropertyName("request_parameter_supported")] bool RequestParameterSupported,
    [property: JsonPropertyName("request_uri_parameter_supported")] bool RequestUriParameterSupported,
    [property: JsonPropertyName("authorization_response_iss_parameter_supported")] bool AuthorizationResponseIssParameterSupported,
    [property: JsonPropertyName("revocation_endpoint")] string RevocationEndpoint,
    [property: JsonPropertyName("revocation_endpoint_auth_methods_supported")] IReadOnlyList<string> RevocationEndpointAuthMethodsSupported,
    [property: JsonPropertyName("introspection_endpoint")] string IntrospectionEndpoint,
    [property: JsonPropertyName("introspection_endpoint_auth_methods_supported")] IReadOnlyList<string> IntrospectionEndpointAuthMethodsSupported);

internal sealed record JsonWebKey(
    [property: JsonPropertyName("kty")] string KeyType,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("alg")] string Algorithm,
    [property: JsonPropertyName("kid")] string KeyId,
    [property: JsonPropertyName("n")] string Modulus,
    [property: JsonPropertyName("e")] string Exponent);

internal sealed record JsonWebKeySet([property: JsonPropertyName("keys")] IReadOnlyList<JsonWebKey> Keys);

internal static class DiscoveryEndpoints
{
    public const string ConfigurationPath = "/.well-known/openid-configuration";
    public const string JwksPath = "/.well-known/jwks.json";

    private static readonly string[] ClaimsSupported =
    [
        "iss", "sub", "aud", "exp", "iat", "auth_time", "nonce", "amr", "sid", "at_hash",
        "name", "given_name", "family_name", "preferred_username", "updated_at", "email", "email_verified", "roles", "groups",
    ];

    /// <summary>
    /// OIDC Discovery 1.0 §3 / RFC 8414 §2. Only implemented features are advertised.
    /// </summary>
    public static IResult Configuration(HttpContext context, IOptions<IssuerOptions> issuerOptions)
    {
        var issuer = issuerOptions.Value;
        context.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Json(new DiscoveryDocument(
            issuer.Issuer,
            issuer.Endpoint(AuthorizeEndpoint.Path),
            issuer.Endpoint(TokenEndpoint.Path),
            issuer.Endpoint(UserInfoEndpoint.Path),
            issuer.Endpoint(JwksPath),
            [IdentityScopes.OpenId, IdentityScopes.Profile, IdentityScopes.Email, IdentityScopes.Roles, IdentityScopes.Groups, IdentityScopes.OfflineAccess],
            ["code"],
            [AuthorizeEndpoint.Query, AuthorizeEndpoint.FormPost],
            [GrantTypes.AuthorizationCode, GrantTypes.ClientCredentials, GrantTypes.RefreshToken],
            [ClientAuthenticator.ClientSecretBasic, ClientAuthenticator.ClientSecretPost, ClientAuthenticator.None],
            [Pkce.S256],
            ["public"],
            ["RS256"],
            ClaimsSupported,
            ["none", "login", "consent", "select_account"],
            // Request objects and the claims parameter are not supported. request_uri_parameter_supported
            // defaults to true (OIDC Discovery A3), so it is stated explicitly.
            false,
            false,
            false,
            true,
            issuer.Endpoint(RevocationEndpoint.Path),
            [ClientAuthenticator.ClientSecretBasic, ClientAuthenticator.ClientSecretPost, ClientAuthenticator.None],
            issuer.Endpoint(IntrospectionEndpoint.Path),
            [ClientAuthenticator.ClientSecretBasic, ClientAuthenticator.ClientSecretPost]));
    }

    /// <summary>RFC 7517 §5: every published key, including keys not yet active and recently retired keys.</summary>
    public static async Task<IResult> Jwks(HttpContext context, SigningKeyStore store, IOptions<SigningKeyOptions> options)
    {
        var keys = await store.GetAsync(context.RequestAborted);
        var maxAge = (int)Math.Min(options.Value.CheckInterval.TotalSeconds, 3600);
        context.Response.Headers.CacheControl = $"public, max-age={maxAge}";
        return Results.Json(new JsonWebKeySet([.. keys.Keys.OrderByDescending(k => k.Key.ActivatesAt).Select(k => ToJwk(k.Key))]));
    }

    private static JsonWebKey ToJwk(Core.Tokens.SigningKey key)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        return new JsonWebKey(
            "RSA",
            "sig",
            key.Algorithm,
            key.Id,
            Base64Url.EncodeToString(parameters.Modulus),
            Base64Url.EncodeToString(parameters.Exponent));
    }
}
