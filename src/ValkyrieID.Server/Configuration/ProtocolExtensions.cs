using Microsoft.Extensions.Options;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Protocol;
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

        services.AddScoped<AuditRecorder>();
        services.AddSingleton<SigningKeyStore>();
        services.AddScoped<SigningKeyRotator>();
        services.AddHostedService<SigningKeyRotationService>();
        services.AddScoped<AccessTokenIssuer>();
        services.AddScoped<ClientAuthenticator>();
        services.AddScoped<TokenEndpoint>();

        return services;
    }

    /// <summary>Makes sure a signing key exists before the server starts handling requests.</summary>
    public static async Task EnsureSigningKeysAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SigningKeyRotator>().RotateAsync(cancellationToken);
    }

    public static IEndpointRouteBuilder MapValkyrieProtocol(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(DiscoveryEndpoints.ConfigurationPath, DiscoveryEndpoints.Configuration);
        endpoints.MapGet(DiscoveryEndpoints.JwksPath, DiscoveryEndpoints.Jwks);
        endpoints.MapPost(TokenEndpoint.Path, (HttpContext context, TokenEndpoint endpoint) => endpoint.HandleAsync(context))
            .RequireRateLimiting(RateLimitPolicies.Token);
        return endpoints;
    }
}
