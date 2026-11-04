using System.Text.Json.Serialization;
using ValkyrieID.Server.Administration;

namespace ValkyrieID.Server.AdminApi;

internal static class AdminApiExtensions
{
    public static IServiceCollection AddValkyrieAdministration(this IServiceCollection services)
    {
        services.AddScoped<ClientAdministration>();
        services.AddScoped<ResourceAdministration>();
        services.AddScoped<RoleAdministration>();
        services.AddScoped<BuiltInSeeder>();
        services.AddProblemDetails();

        // The OpenAPI document describes the Admin API only; it is served in Development (see MapValkyrieAdminApi).
        services.AddOpenApi(options =>
        {
            options.ShouldInclude = description =>
                description.RelativePath?.StartsWith(AdminApiAuthentication.PathPrefix[1..], StringComparison.OrdinalIgnoreCase) == true;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "ValkyrieID Admin API";
                document.Info.Description =
                    $"Requires an access token for the {BuiltIn.AdminResource} audience with the {BuiltIn.AdminScope} scope, "
                    + "issued to a user with the admin role, sent as `Authorization: Bearer <token>`.";
                // Paths are relative to the issuer URL; the request's host must not leak into the published document.
                document.Servers?.Clear();
                return Task.CompletedTask;
            });
        });

        // Request bodies are read strictly: unknown properties, missing required properties, and nulls for
        // non-nullable properties are rejected instead of silently ignored or defaulted.
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
            options.SerializerOptions.RespectNullableAnnotations = true;
        });
        return services;
    }

    /// <summary>Creates or updates the Admin API resource and the admin UI client.</summary>
    public static async Task SeedBuiltInEntitiesAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BuiltInSeeder>().SeedAsync(cancellationToken);
    }

    /// <summary>Authenticates Admin API requests; must run after routing and before the endpoints.</summary>
    public static IApplicationBuilder UseAdminApiAuthentication(this IApplicationBuilder app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(AdminApiAuthentication.PathPrefix, StringComparison.OrdinalIgnoreCase),
            branch => branch.UseMiddleware<AdminApiAuthentication>());

    public static WebApplication MapValkyrieAdminApi(this WebApplication app)
    {
        var api = app.MapGroup(AdminApiAuthentication.PathPrefix).AddEndpointFilter<AdminErrorFilter>();
        UserEndpoints.Map(api);
        RoleEndpoints.Map(api);
        ClientEndpoints.Map(api);
        ResourceEndpoints.Map(api);
        SystemEndpoints.Map(api);
        SettingsEndpoints.Map(api);

        // /openapi/v1.json; not served in production, where the document is published in the docs instead.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        return app;
    }
}
