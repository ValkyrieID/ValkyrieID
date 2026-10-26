using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Protocol;

/// <summary>
/// The authorization endpoint (RFC 6749 A3.1, OIDC Core A3.1.2) for <c>response_type=code</c>. There are no
/// consent screens: a signed-in user is sent straight back to the client.
/// </summary>
internal sealed class AuthorizeEndpoint(
    ValkyrieDbContext db,
    IAuthorizationCodeStore codes,
    AuditRecorder audit,
    TimeProvider timeProvider,
    IOptions<IssuerOptions> issuerOptions)
{
    public const string Path = "/connect/authorize";

    public const string Query = "query";
    public const string FormPost = "form_post";

    private const int MaxParameterLength = 2000;

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        // OIDC Core A3.1.2.1: GET and POST (form-serialized) must both be supported.
        IEnumerable<KeyValuePair<string, StringValues>> source;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (!context.Request.HasFormContentType)
            {
                return ProtocolPages.Error("The request must use application/x-www-form-urlencoded.");
            }

            source = await context.Request.ReadFormAsync(cancellationToken);
        }
        else
        {
            source = context.Request.Query;
        }

        var parameters = source.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        // RFC 6749 A3.1.2.4, A4.1.2.1: if the client or redirect URI is missing, invalid, or doesn't match, the user
        // is informed and NOT redirected.
        if (IsRepeated(parameters, "client_id") || IsRepeated(parameters, "redirect_uri"))
        {
            return ProtocolPages.Error("The request is invalid: client_id and redirect_uri must not be repeated.");
        }

        var clientId = Get(parameters, "client_id");
        var client = clientId is null
            ? null
            : await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.ClientId == clientId, cancellationToken);
        if (client is not { Enabled: true })
        {
            return ProtocolPages.Error("The application is unknown or disabled.");
        }

        // OIDC Core A3.1.2.1: redirect_uri is required and must match a registered value exactly.
        var redirectUri = Get(parameters, "redirect_uri");
        if (redirectUri is null || !client.RedirectUris.Any(r => RedirectUris.Matches(r, redirectUri)))
        {
            return ProtocolPages.Error("The redirect URI is not registered for this application.");
        }

        var responseMode = Get(parameters, "response_mode");
        var reply = new AuthorizationReply(
            redirectUri,
            Get(parameters, "state"),
            responseMode == FormPost ? FormPost : Query,
            issuerOptions.Value.Issuer);

        if (responseMode is not (null or Query or FormPost))
        {
            return reply.Error(AuthorizeErrors.InvalidRequest, "The response_mode is not supported.");
        }

        if (parameters.Values.Any(v => v.Count > 1))
        {
            return reply.Error(AuthorizeErrors.InvalidRequest, "Request parameters must not be repeated.");
        }

        if (parameters.Values.Any(v => v.ToString().Length > MaxParameterLength))
        {
            return reply.Error(AuthorizeErrors.InvalidRequest, "A request parameter is too long.");
        }

        var request = Validate(parameters, client);
        if (request.Error is { } validationError)
        {
            return reply.Error(validationError.Code, validationError.Description);
        }

        var scopeResult = await ResolveScopesAsync(Get(parameters, "scope"), client, cancellationToken);
        if (scopeResult.Error is { } scopeError)
        {
            return reply.Error(AuthorizeErrors.InvalidScope, scopeError);
        }

        var isOpenId = scopeResult.Scopes.Contains(IdentityScopes.OpenId);
        var now = timeProvider.GetUtcNow();
        var user = context.User;
        var sessionId = user.FindFirstValue(SessionClaims.SessionId);
        var subject = user.FindFirstValue(SessionClaims.Subject);
        var authTime = long.TryParse(user.FindFirstValue(SessionClaims.AuthTime), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : (DateTimeOffset?)null;
        var signedIn = user.Identity?.IsAuthenticated == true && sessionId is not null && authTime is not null
            && Guid.TryParse(subject, out _);

        // OIDC Core A3.1.2.1: prompt=login and select_account require a fresh login; max_age requires one when the
        // last authentication is older than max_age seconds.
        var forceLogin = request.Prompt.Contains("login") || request.Prompt.Contains("select_account")
            || (request.MaxAge is { } maxAge && authTime is { } at && now.ToUnixTimeSeconds() - at.ToUnixTimeSeconds() > maxAge);

        if (!signedIn || forceLogin)
        {
            // OIDC Core A3.1.2.6: with prompt=none the OP must not show UI.
            if (request.Prompt.Contains("none"))
            {
                return reply.Error(AuthorizeErrors.LoginRequired, "The user must sign in.");
            }

            return Results.Redirect(LoginUrl(context, parameters, forceLogin));
        }

        var grant = new AuthorizationCodeGrant(
            client.ClientId,
            redirectUri,
            Guid.Parse(subject!),
            sessionId!,
            authTime!.Value,
            [.. user.FindAll(SessionClaims.AuthenticationMethod).Select(c => c.Value)],
            scopeResult.Scopes,
            scopeResult.Audiences,
            isOpenId ? Get(parameters, "nonce") : null,
            request.CodeChallenge,
            now + TokenOptions.AuthorizationCodeLifetime);
        var code = codes.Create(grant);

        audit.Add(
            "authorize.code_issued",
            actor: subject,
            clientId: client.ClientId,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            subjectId: grant.Subject,
            details: new Dictionary<string, object?>
            {
                ["scope"] = string.Join(' ', grant.Scopes),
                ["sid"] = grant.SessionId,
                ["pkce"] = grant.CodeChallenge is not null,
            });
        await db.SaveChangesAsync(cancellationToken);

        return reply.Success(code);
    }

    private static AuthorizationRequest Validate(Dictionary<string, StringValues> parameters, Client client)
    {
        // RFC 6749 A3.1.1: response_type is required; only "code" is supported.
        var responseType = Get(parameters, "response_type");
        if (responseType is null)
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.InvalidRequest, "The response_type parameter is required.");
        }

        if (responseType != "code")
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.UnsupportedResponseType, "Only response_type=code is supported.");
        }

        if (!client.AllowedGrantTypes.Contains(GrantTypes.AuthorizationCode))
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.UnauthorizedClient, "The client is not allowed to use the authorization code flow.");
        }

        // OIDC Core A6: request objects and dynamic registration are not supported.
        if (Get(parameters, "request") is not null)
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.RequestNotSupported, "The request parameter is not supported.");
        }

        if (Get(parameters, "request_uri") is not null)
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.RequestUriNotSupported, "The request_uri parameter is not supported.");
        }

        if (Get(parameters, "registration") is not null)
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.RegistrationNotSupported, "The registration parameter is not supported.");
        }

        // RFC 7636 A4.3/A4.4.1, RFC 9700 A2.1.1: only S256; required for public clients and clients that require it.
        var challenge = Get(parameters, "code_challenge");
        var method = Get(parameters, "code_challenge_method");
        if (challenge is null)
        {
            if (method is not null || client.PkceRequired)
            {
                return AuthorizationRequest.Fail(AuthorizeErrors.InvalidRequest, "A code_challenge with code_challenge_method=S256 is required.");
            }
        }
        else if (method != Pkce.S256)
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.InvalidRequest, "Only code_challenge_method=S256 is supported.");
        }
        else if (!Pkce.IsValidS256Challenge(challenge))
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.InvalidRequest, "The code_challenge is malformed.");
        }

        // OIDC Core A3.1.2.1: prompt is a space-delimited list; "none" must not be combined with other values.
        var prompt = (Get(parameters, "prompt") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (prompt.Any(p => p is not ("none" or "login" or "consent" or "select_account")))
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.InvalidRequest, "The prompt parameter has an unsupported value.");
        }

        if (prompt.Contains("none") && prompt.Count > 1)
        {
            return AuthorizationRequest.Fail(AuthorizeErrors.InvalidRequest, "prompt=none can't be combined with other values.");
        }

        long? maxAge = null;
        if (Get(parameters, "max_age") is { } maxAgeValue)
        {
            if (!long.TryParse(maxAgeValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return AuthorizationRequest.Fail(AuthorizeErrors.InvalidRequest, "max_age must be a non-negative integer.");
            }

            maxAge = parsed;
        }

        return new AuthorizationRequest(null, challenge, prompt, maxAge);
    }

    private async Task<ScopeResolution> ResolveScopesAsync(string? scope, Client client, CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            return ScopeResolution.Fail("The scope parameter is required.");
        }

        var requested = scope.Split(' ').Distinct(StringComparer.Ordinal).ToList();
        if (requested.Any(s => !ProtocolSyntax.IsValidScopeToken(s)))
        {
            return ScopeResolution.Fail("The scope parameter is malformed.");
        }

        // OIDC Core A11: offline_access is ignored until refresh tokens are supported.
        requested.Remove(IdentityScopes.OfflineAccess);

        // Identity scopes only mean something in an OpenID Connect request.
        var isOpenId = requested.Contains(IdentityScopes.OpenId);
        if (isOpenId)
        {
            // OIDC Core A3.1.2.1: scope values that are not understood SHOULD be ignored. Scopes that exist but
            // aren't allowed for the client are still rejected below. Plain OAuth requests keep RFC 6749 A4.1.2.1
            // behavior and reject unknown scopes.
            var candidates = requested.Where(s => !IdentityScopes.All.Contains(s)).ToList();
            var known = await db.ApiScopes.Where(s => candidates.Contains(s.Name)).Select(s => s.Name).ToListAsync(cancellationToken);
            requested.RemoveAll(s => !IdentityScopes.All.Contains(s) && !known.Contains(s));
        }

        if (requested.Count == 0
            || requested.Any(s => !client.AllowedScopes.Contains(s) || (!isOpenId && IdentityScopes.All.Contains(s))))
        {
            return ScopeResolution.Fail("The requested scope is invalid or not allowed.");
        }

        var apiScopes = requested.Where(s => !IdentityScopes.All.Contains(s)).ToList();
        var resolved = await (
                from apiScope in db.ApiScopes
                join resource in db.ApiResources on apiScope.ApiResourceId equals resource.Id
                where resource.Enabled && apiScopes.Contains(apiScope.Name)
                select new { Scope = apiScope.Name, Resource = resource.Name })
            .ToListAsync(cancellationToken);
        if (resolved.Count != apiScopes.Count)
        {
            return ScopeResolution.Fail("The requested scope is invalid or not allowed.");
        }

        return new ScopeResolution(
            null,
            requested,
            [.. resolved.Select(r => r.Resource).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Sends the browser to the login page, which returns to this request afterwards. When a new login is forced,
    /// <c>prompt</c> is dropped from the return URL so the user isn't asked again after signing in.
    /// </summary>
    private static string LoginUrl(HttpContext context, Dictionary<string, StringValues> parameters, bool forceLogin)
    {
        var pathBase = context.Request.PathBase;
        var returnParameters = parameters.Where(p => !(forceLogin && p.Key == "prompt"));
        var returnUrl = pathBase + Path + QueryString.Create(returnParameters);

        List<KeyValuePair<string, string?>> query = [new("returnUrl", returnUrl)];
        if (forceLogin)
        {
            query.Add(new("prompt", "login"));
        }

        if (Get(parameters, "login_hint") is { } loginHint)
        {
            query.Add(new("login_hint", loginHint));
        }

        return pathBase + "/account/login" + QueryString.Create(query);
    }

    private static bool IsRepeated(Dictionary<string, StringValues> parameters, string name) =>
        parameters.TryGetValue(name, out var values) && values.Count > 1;

    /// <summary>OIDC Core A3.1.2.1 / RFC 6749 A3.1: parameters sent without a value are treated as omitted.</summary>
    private static string? Get(Dictionary<string, StringValues> parameters, string name) =>
        parameters.TryGetValue(name, out var values) && values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;

    private sealed record AuthorizationRequest(
        (string Code, string Description)? Error,
        string? CodeChallenge,
        IReadOnlySet<string> Prompt,
        long? MaxAge)
    {
        public static AuthorizationRequest Fail(string code, string description) =>
            new((code, description), null, new HashSet<string>(), null);
    }

    private sealed record ScopeResolution(string? Error, List<string> Scopes, List<string> Audiences)
    {
        public static ScopeResolution Fail(string error) => new(error, [], []);
    }
}

internal static class AuthorizeErrors
{
    public const string InvalidRequest = "invalid_request";
    public const string UnauthorizedClient = "unauthorized_client";
    public const string UnsupportedResponseType = "unsupported_response_type";
    public const string InvalidScope = "invalid_scope";
    public const string LoginRequired = "login_required";
    public const string RequestNotSupported = "request_not_supported";
    public const string RequestUriNotSupported = "request_uri_not_supported";
    public const string RegistrationNotSupported = "registration_not_supported";
}

/// <summary>Returns an authorization response to a validated redirect URI.</summary>
internal sealed class AuthorizationReply(string redirectUri, string? state, string responseMode, string issuer)
{
    public IResult Success(string code) => Send([new("code", code)]);

    /// <summary>RFC 6749 A4.1.2.1.</summary>
    public IResult Error(string error, string description) =>
        Send([new("error", error), new("error_description", description)]);

    private IResult Send(List<KeyValuePair<string, string>> parameters)
    {
        if (state is not null)
        {
            parameters.Add(new("state", state));
        }

        // RFC 9207 A2: iss is included in every authorization response, including errors.
        parameters.Add(new("iss", issuer));

        if (responseMode == AuthorizeEndpoint.FormPost)
        {
            return ProtocolPages.FormPost(redirectUri, parameters);
        }

        // RFC 6749 A3.1.2: the redirect URI may contain a query component, which must be retained.
        var query = QueryString.Create(parameters.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
        var location = redirectUri.Contains('?', StringComparison.Ordinal)
            ? redirectUri + "&" + query.Value![1..]
            : redirectUri + query.Value;
        return new NoStoreRedirect(location);
    }

    private sealed class NoStoreRedirect(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ProtocolHeaders.NoStore(httpContext.Response);
            httpContext.Response.StatusCode = StatusCodes.Status302Found;
            httpContext.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }
}
