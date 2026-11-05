using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Clients;
using ValkyrieID.Data;
using ValkyrieID.Server.Protocol;

namespace ValkyrieID.Server.Configuration;

/// <summary>A page that returns to <see cref="ReturnUrl"/>, often an authorization request, after a form post.</summary>
internal interface IReturnUrlPage
{
    string? ReturnUrl { get; }
}

/// <summary>A page whose form post redirects to another site (e.g. a validated post-logout redirect URI).</summary>
internal interface IExternalFormTargetPage
{
    string? FormTarget { get; }
}

/// <summary>
/// Security headers for every Razor page response:
/// <list type="bullet">
/// <item><c>Cache-Control: no-store</c>, since the pages show account data, codes, and anti-forgery tokens.</item>
/// <item>CSP <c>form-action</c>. Browsers apply it to the whole redirect chain after a form post, so a login form
/// that ends at a relying party's redirect URI needs that origin. Only the origin of a redirect URI that is
/// registered for an enabled client (or an already validated post-logout redirect URI) is added to <c>'self'</c>.</item>
/// </list>
/// </summary>
internal sealed class PageSecurityFilter(ValkyrieDbContext db) : IAsyncAlwaysRunResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        ProtocolHeaders.NoStore(httpContext.Response);

        var target = context.Controller switch
        {
            IExternalFormTargetPage page => page.FormTarget,
            IReturnUrlPage page => await AuthorizeRedirectUriAsync(httpContext, page.ReturnUrl),
            _ => null,
        };
        if (!httpContext.Request.Path.StartsWithSegments(Administration.BuiltIn.AdminUiPath, StringComparison.OrdinalIgnoreCase))
        {
            httpContext.Response.Headers.ContentSecurityPolicy = SecurityHeadersExtensions.PagePolicy(target);
        }

        await next();
    }

    /// <summary>
    /// The redirect URI of <paramref name="returnUrl"/> when it is a local authorization request whose client is
    /// enabled and has that redirect URI registered (RFC 9700 §4.1.3 matching), otherwise <see langword="null"/>.
    /// </summary>
    private async Task<string?> AuthorizeRedirectUriAsync(HttpContext context, string? returnUrl)
    {
        if (returnUrl is null || returnUrl.Length > 8192)
        {
            return null;
        }

        var queryStart = returnUrl.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? returnUrl : returnUrl[..queryStart];
        if (!string.Equals(path, context.Request.PathBase + AuthorizeEndpoint.Path, StringComparison.OrdinalIgnoreCase) || queryStart < 0)
        {
            return null;
        }

        var query = QueryHelpers.ParseQuery(returnUrl[queryStart..]);
        if (!query.TryGetValue("client_id", out var clientIds) || clientIds.Count != 1
            || !query.TryGetValue("redirect_uri", out var redirectUris) || redirectUris.Count != 1
            || clientIds[0] is not { Length: > 0 } clientId || redirectUris[0] is not { Length: > 0 } redirectUri)
        {
            return null;
        }

        var registered = await db.Clients.AsNoTracking()
            .Where(c => c.ClientId == clientId && c.Enabled)
            .Select(c => c.RedirectUris)
            .SingleOrDefaultAsync(context.RequestAborted);
        return registered is not null && registered.Any(r => RedirectUris.Matches(r, redirectUri)) ? redirectUri : null;
    }
}
