using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using ValkyrieID.Data;

namespace ValkyrieID.Server.Protocol;

/// <summary>
/// CORS for endpoints that browser-based clients call directly. Discovery and JWKS are public metadata and allow
/// any origin. The token and userinfo endpoints allow only origins registered on an enabled client. No
/// credentials (cookies) are allowed cross-origin.
/// </summary>
internal static class ProtocolCors
{
    private static readonly string[] PublicPaths = [DiscoveryEndpoints.ConfigurationPath, DiscoveryEndpoints.JwksPath];
    private static readonly string[] ClientPaths = [TokenEndpoint.Path, UserInfoEndpoint.Path];

    public static IApplicationBuilder UseProtocolCors(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        var isPublic = PublicPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));
        var isClient = !isPublic && ClientPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));
        if (!isPublic && !isClient)
        {
            await next(context);
            return;
        }

        var headers = context.Response.Headers;
        headers.Append(HeaderNames.Vary, HeaderNames.Origin);
        var origin = context.Request.Headers.Origin.ToString();
        var allowed = origin.Length > 0
            && (isPublic || await IsRegisteredOriginAsync(context, origin));
        if (allowed)
        {
            headers.AccessControlAllowOrigin = isPublic ? "*" : origin;
        }

        // Fetch Standard A3.2.2: a CORS preflight is an OPTIONS request with Access-Control-Request-Method.
        if (HttpMethods.IsOptions(context.Request.Method)
            && context.Request.Headers.ContainsKey(HeaderNames.AccessControlRequestMethod))
        {
            if (allowed)
            {
                headers.AccessControlAllowMethods = isPublic ? "GET" : "GET, POST";
                headers.AccessControlAllowHeaders = "Authorization, Content-Type";
                headers.AccessControlMaxAge = "600";
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await next(context);
    });

    private static async Task<bool> IsRegisteredOriginAsync(HttpContext context, string origin)
    {
        var db = context.RequestServices.GetRequiredService<ValkyrieDbContext>();
        var origins = await db.Clients.AsNoTracking().Where(c => c.Enabled)
            .Select(c => c.AllowedCorsOrigins).ToListAsync(context.RequestAborted);
        return origins.Any(list => list.Contains(origin, StringComparer.Ordinal));
    }
}
