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
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("grant_types_supported")] IReadOnlyList<string> GrantTypesSupported,
    [property: JsonPropertyName("token_endpoint_auth_methods_supported")] IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
    [property: JsonPropertyName("subject_types_supported")] IReadOnlyList<string> SubjectTypesSupported,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] IReadOnlyList<string> IdTokenSigningAlgValuesSupported);

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

    /// <summary>
    /// OIDC Discovery 1.0 §3 / RFC 8414 §2. Only implemented features are advertised; later phases add the
    /// authorization and userinfo endpoints.
    /// </summary>
    public static IResult Configuration(HttpContext context, IOptions<IssuerOptions> issuerOptions)
    {
        var issuer = issuerOptions.Value;
        context.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Json(new DiscoveryDocument(
            issuer.Issuer,
            issuer.Endpoint(JwksPath),
            issuer.Endpoint(TokenEndpoint.Path),
            [GrantTypes.ClientCredentials],
            [ClientAuthenticator.ClientSecretBasic, ClientAuthenticator.ClientSecretPost],
            ["public"],
            ["RS256"]));
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
