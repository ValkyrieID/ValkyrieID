using System.Text.Json.Serialization;

namespace ValkyrieID.Server.Protocol;

internal sealed record OAuthErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description);

/// <summary>An RFC 6749 §5.2 error response.</summary>
internal sealed class OAuthError(string error, string? description, int statusCode = StatusCodes.Status400BadRequest) : IResult
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidClient = "invalid_client";
    public const string InvalidGrant = "invalid_grant";
    public const string UnauthorizedClient = "unauthorized_client";
    public const string UnsupportedGrantType = "unsupported_grant_type";
    public const string InvalidScope = "invalid_scope";

    public string Error { get; } = error;

    public static OAuthError Request(string description) => new(InvalidRequest, description);

    /// <summary>
    /// RFC 6749 §5.2: invalid_client MAY be 401; it MUST be 401 with WWW-Authenticate when the client used the
    /// Authorization header. We always use 401, which per RFC 9110 §15.5.2 requires a challenge.
    /// </summary>
    public static OAuthError Client() => new(InvalidClient, "Client authentication failed.", StatusCodes.Status401Unauthorized);

    public Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = statusCode;
        ProtocolHeaders.NoStore(response);
        if (statusCode == StatusCodes.Status401Unauthorized)
        {
            response.Headers.WWWAuthenticate = "Basic realm=\"ValkyrieID\"";
        }

        return response.WriteAsJsonAsync(new OAuthErrorResponse(Error, description), httpContext.RequestAborted);
    }
}

internal static class ProtocolHeaders
{
    /// <summary>RFC 6749 §5.1: token responses must not be cached.</summary>
    public static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}
