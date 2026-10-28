using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ValkyrieID.Core.Security;
using ValkyrieID.Server.Cli;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Protocol;

// Maintenance commands (e.g. `valkyrieid client create ...`) take their own arguments; configuration then
// comes from appsettings.json and environment variables only.
var isCli = CliApp.IsCommand(args);
var builder = WebApplication.CreateBuilder(isCli ? [] : args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddValkyrieMasterKey(builder.Configuration);
builder.Services.AddValkyrieDatabase(builder.Configuration);
builder.Services.AddValkyrieDataProtection();
builder.Services.AddValkyrieProtocol(builder.Configuration);
builder.Services.AddValkyrieReverseProxy(builder.Configuration);
builder.Services.AddValkyrieAccounts(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
builder.AddValkyrieTelemetry();

var app = builder.Build();

// Fail fast on an invalid master key before touching the database.
app.Services.GetRequiredService<SecretProtector>();
await app.Services.MigrateDatabaseAsync(app.Lifetime.ApplicationStopping);

if (isCli)
{
    return await CliApp.RunAsync(app.Services, args, Console.Out, Console.Error);
}

await app.Services.EnsureSigningKeysAsync(app.Lifetime.ApplicationStopping);

app.UseValkyrieReverseProxy();
app.UseSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseProtocolCors();
app.UseValkyrieAccounts();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapValkyrieProtocol();
app.MapValkyrieAccounts();

await app.RunAsync();
return 0;
