using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ValkyrieID.Server.Configuration;

internal static class TelemetryExtensions
{
    public const string ServiceName = "valkyrieid";

    /// <summary>
    /// Enables OpenTelemetry traces, metrics, and logs exported over OTLP.
    /// Only active when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured, to keep the idle footprint small.
    /// </summary>
    public static WebApplicationBuilder AddValkyrieTelemetry(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return builder;
        }

        var version = typeof(TelemetryExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? ServiceName, serviceVersion: version))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("System.Runtime"))
            .WithLogging()
            .UseOtlpExporter();

        return builder;
    }
}
