using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ValkyrieID.Core.Security;
using ValkyrieID.Server.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddValkyrieMasterKey(builder.Configuration);
builder.Services.AddValkyrieDatabase(builder.Configuration);
builder.Services.AddValkyrieDataProtection();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
builder.AddValkyrieTelemetry();

var app = builder.Build();

// Fail fast on an invalid master key before touching the database.
app.Services.GetRequiredService<SecretProtector>();
await app.Services.MigrateDatabaseAsync(app.Lifetime.ApplicationStopping);

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync();
