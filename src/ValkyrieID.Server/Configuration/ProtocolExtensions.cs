using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Coordination;
using ValkyrieID.Server.Logout;
using ValkyrieID.Server.Protocol;
using ValkyrieID.Server.Redis;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Configuration;

internal static class ProtocolExtensions
{
    public static IServiceCollection AddValkyrieProtocol(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IssuerOptions>().Bind(configuration.GetSection(IssuerOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<IssuerOptions>, IssuerOptionsValidator>();
        services.AddOptions<TokenOptions>().Bind(configuration.GetSection(TokenOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TokenOptions>, TokenOptionsValidator>();
        services.AddOptions<SigningKeyOptions>().Bind(configuration.GetSection(SigningKeyOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SigningKeyOptions>, SigningKeyOptionsValidator>();
        services.AddOptions<BackChannelLogoutOptions>().Bind(configuration.GetSection(BackChannelLogoutOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BackChannelLogoutOptions>, BackChannelLogoutOptionsValidator>();

        services.AddScoped<AuditRecorder>();
        services.AddScoped<DatabaseLease>();
        services.AddSingleton<SigningKeyStore>();
        services.AddScoped<SigningKeyRotator>();
        services.AddHostedService<SigningKeyRotationService>();
        services.AddScoped<AccessTokenIssuer>();
        services.AddScoped<ClientAuthenticator>();
        services.AddScoped<IdTokenIssuer>();
        services.AddSingleton<IAuthorizationCodeStore>(sp => sp.GetService<RedisContext>() is { } redis
            ? new RedisAuthorizationCodeStore(redis, sp.GetRequiredService<TimeProvider>())
            : new InMemoryAuthorizationCodeStore(sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<RefreshTokenStore>();
        services.AddHostedService<RefreshTokenCleanupService>();
        services.AddScoped<AccessTokenValidator>();
        services.AddScoped<TokenEndpoint>();
        services.AddScoped<AuthorizeEndpoint>();
        services.AddScoped<UserInfoEndpoint>();
        services.AddScoped<RevocationEndpoint>();
        services.AddScoped<IntrospectionEndpoint>();
        services.AddScoped<EndSessionEndpoint>();
        services.AddScoped<IdTokenHintValidator>();
        services.AddScoped<LogoutTokenIssuer>();

        services.AddSingleton<LogoutRequestProtector>();
        services.AddSingleton<BackChannelLogoutSignal>();
        services.AddSingleton<BackChannelLogoutDispatcher>();
        services.AddScoped<BackChannelLogoutQueue>();
        services.AddHostedService<BackChannelLogoutWorker>();
        // Back-channel logout URIs are registered by administrators. Redirects aren't followed (OIDC Back-Channel
        // Logout 1.0 A2.8: the response is 200 or 204), and no cookies are sent. Timeouts are set per request.
        services.AddHttpClient(BackChannelLogoutDispatcher.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

        return services;
    }

    /// <summary>
    /// Makes sure a signing key exists before the server starts handling requests. If another instance is rotating at
    /// the same moment, waits for it instead of creating a second key.
    /// </summary>
    public static async Task EnsureSigningKeysAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<SigningKeyRotator>().RotateAsync(cancellationToken))
            {
                return;
            }

            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            if (await scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>().SigningKeys.AnyAsync(k => k.ActivatesAt <= now, cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    public static IEndpointRouteBuilder MapValkyrieProtocol(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(DiscoveryEndpoints.ConfigurationPath, DiscoveryEndpoints.Configuration);
        endpoints.MapGet(DiscoveryEndpoints.JwksPath, DiscoveryEndpoints.Jwks);
        endpoints.MapPost(TokenEndpoint.Path, (HttpContext context, TokenEndpoint endpoint) => endpoint.HandleAsync(context))
            .RequireRateLimiting(RateLimitPolicies.Token);
        endpoints.MapMethods(AuthorizeEndpoint.Path, [HttpMethods.Get, HttpMethods.Post], (HttpContext context, AuthorizeEndpoint endpoint) => endpoint.HandleAsync(context))
            .RequireRateLimiting(RateLimitPolicies.Authorize);
        endpoints.MapMethods(UserInfoEndpoint.Path, [HttpMethods.Get, HttpMethods.Post], (HttpContext context, UserInfoEndpoint endpoint) => endpoint.HandleAsync(context))
            .RequireRateLimiting(RateLimitPolicies.Token);
        endpoints.MapPost(RevocationEndpoint.Path, (HttpContext context, RevocationEndpoint endpoint) => endpoint.HandleAsync(context))
            .RequireRateLimiting(RateLimitPolicies.Token);
        endpoints.MapPost(IntrospectionEndpoint.Path, (HttpContext context, IntrospectionEndpoint endpoint) => endpoint.HandleAsync(context))
            .RequireRateLimiting(RateLimitPolicies.Token);
        endpoints.MapMethods(EndSessionEndpoint.Path, [HttpMethods.Get, HttpMethods.Post], (HttpContext context, EndSessionEndpoint endpoint) => endpoint.HandleAsync(context))
            .RequireRateLimiting(RateLimitPolicies.Authorize);
        return endpoints;
    }
}
