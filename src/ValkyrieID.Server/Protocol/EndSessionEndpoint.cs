using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using ValkyrieID.Data;
using ValkyrieID.Server.Logout;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Protocol;

/// <summary>
/// The end-session endpoint (OIDC RP-Initiated Logout 1.0). A signed-in user always confirms on the sign-out page
/// (A2: the OP "SHOULD ask the End-User whether to log out of the OP"). The browser is only sent to a
/// <c>post_logout_redirect_uri</c> registered exactly for the client identified by the hint or by <c>client_id</c>
/// (A3).
/// </summary>
internal sealed class EndSessionEndpoint(
    ValkyrieDbContext db,
    IdTokenHintValidator hints,
    LogoutRequestProtector protector)
{
    public const string Path = "/connect/endsession";
    public const string LogoutPagePath = "/account/logout";

    private const int MaxParameterLength = 2000;
    private const int MaxHintLength = 8000;

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        // A2: GET and POST must both be supported. The session cookie is SameSite=Lax, so browsers don't send it with
        // a cross-site POST; the request is repeated as a top-level GET, which carries it.
        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (!context.Request.HasFormContentType)
            {
                return ProtocolPages.Error("The request must use application/x-www-form-urlencoded.");
            }

            var form = await context.Request.ReadFormAsync(cancellationToken);
            var query = QueryString.Create(form.SelectMany(p => p.Value.Select(v => new KeyValuePair<string, string?>(p.Key, v))));
            return ProtocolPages.Redirect(context.Request.PathBase + Path + query, StatusCodes.Status303SeeOther);
        }

        var parameters = context.Request.Query.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var request = await ValidateAsync(parameters, cancellationToken);

        // A2: the OP "SHOULD ask the End-User whether to log out of the OP", so a signed-in user always confirms,
        // even with a valid id_token_hint for the current session.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return ProtocolPages.Redirect(LogoutPageUrl(context, request));
        }

        // A4: logging out a user who isn't signed in is not an error.
        return ProtocolPages.Redirect(request.RedirectUri is not null
            ? Location(request.RedirectUri, request.State)
            : LogoutPageUrl(context, request));
    }

    /// <summary>
    /// Whether <paramref name="request"/>'s redirect URI is still registered for its enabled client. A3: the OP "MUST
    /// NOT perform post-logout redirection if the post_logout_redirect_uri value supplied does not exactly match one
    /// of the previously registered post_logout_redirect_uris values".
    /// </summary>
    public static async Task<bool> IsRedirectAllowedAsync(ValkyrieDbContext db, LogoutRequest request, CancellationToken cancellationToken)
    {
        if (request.ClientId is null || request.RedirectUri is null)
        {
            return false;
        }

        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.ClientId == request.ClientId, cancellationToken);
        return client is { Enabled: true } && client.PostLogoutRedirectUris.Contains(request.RedirectUri, StringComparer.Ordinal);
    }

    /// <summary>The redirect URI with <c>state</c> added; an existing query component is kept.</summary>
    public static string Location(string redirectUri, string? state)
    {
        if (state is null)
        {
            return redirectUri;
        }

        var query = QueryString.Create("state", state).Value!;
        return redirectUri.Contains('?', StringComparison.Ordinal) ? redirectUri + "&" + query[1..] : redirectUri + query;
    }

    private string LogoutPageUrl(HttpContext context, LogoutRequest request) =>
        context.Request.PathBase + LogoutPagePath + QueryString.Create("request", protector.Protect(request));

    /// <summary>
    /// A4: "any operations requiring the information that failed to correctly validate MUST be aborted and the
    /// information that failed to validate MUST NOT be used." An invalid request isn't redirected and its hint isn't
    /// trusted, but the user can still sign out.
    /// </summary>
    private async Task<LogoutRequest> ValidateAsync(Dictionary<string, StringValues> parameters, CancellationToken cancellationToken)
    {
        var invalid = new LogoutRequest(null, null, null, Invalid: true);
        if (parameters.Values.Any(v => v.Count > 1)
            || parameters.Any(p => p.Value.ToString().Length > (p.Key == "id_token_hint" ? MaxHintLength : MaxParameterLength)))
        {
            return invalid;
        }

        // logout_hint and ui_locales are accepted and ignored (A2: their use is at the OP's discretion).
        var clientId = Get(parameters, "client_id");
        var redirectUri = Get(parameters, "post_logout_redirect_uri");
        var state = Get(parameters, "state");

        if (Get(parameters, "id_token_hint") is { } hintValue)
        {
            var hint = await hints.ValidateAsync(hintValue, cancellationToken);
            if (hint is null)
            {
                return invalid;
            }

            // A2: "When both client_id and id_token_hint are present, the OP MUST verify that the Client Identifier
            // matches the one used when issuing the ID Token."
            if (clientId is not null && !hint.Audiences.Contains(clientId, StringComparer.Ordinal))
            {
                return invalid;
            }

            clientId ??= hint.Audiences.Count == 1 ? hint.Audiences[0] : null;
        }

        var client = clientId is null
            ? null
            : await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.ClientId == clientId, cancellationToken);
        if (clientId is not null && client is not { Enabled: true })
        {
            return invalid;
        }

        if (redirectUri is null)
        {
            return new LogoutRequest(client?.ClientId, null, null, Invalid: false);
        }

        // A3: without an id_token_hint, the redirect is only performed when the client is otherwise identified: its
        // client_id, with the URI registered for it, is the "other means of confirming the legitimacy of the
        // post-logout redirection target".
        if (client is null || !client.PostLogoutRedirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            return invalid with { ClientId = client?.ClientId };
        }

        return new LogoutRequest(client.ClientId, redirectUri, state, Invalid: false);
    }

    /// <summary>Parameters sent without a value are treated as omitted.</summary>
    private static string? Get(Dictionary<string, StringValues> parameters, string name) =>
        parameters.TryGetValue(name, out var values) && values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;
}
