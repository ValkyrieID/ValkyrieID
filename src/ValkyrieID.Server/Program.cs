using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ValkyrieID.Core.Security;
using ValkyrieID.Server.AdminApi;
using ValkyrieID.Server.AdminUi;
using ValkyrieID.Server.Cli;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Protocol;

// The container health check runs without building the host (no master key, database, or migrations).
if (HealthCheckCommand.IsCommand(args))
{
    return await HealthCheckCommand.RunAsync(HealthCheckCommand.LoadConfiguration(), args, Console.Out, Console.Error);
}

if (CliApp.TryRunWithoutHost(args, Console.Out, Console.Error) is { } exitCode)
{
    return exitCode;
}

// Maintenance commands (e.g. `valkyrieid client create ...`) take their own arguments; configuration then
// comes from appsettings.json and environment variables only.
var isCli = CliApp.IsCommand(args);
var builder = WebApplication.CreateBuilder(isCli ? [] : args);

builder.Services.AddSingleton(TimeProvider.System);
builder.AddValkyrieRuntimeSettings();
builder.Services.AddValkyrieMasterKey(builder.Configuration);
builder.Services.AddValkyrieDatabase(builder.Configuration);
builder.Services.AddValkyrieDataProtection();
builder.Services.AddValkyrieRedis(builder.Configuration);
builder.Services.AddValkyrieProtocol(builder.Configuration);
builder.Services.AddValkyrieReverseProxy(builder.Configuration);
builder.Services.AddValkyrieTransportSecurity(builder.Configuration);
builder.Services.AddValkyrieAccounts(builder.Configuration);
builder.Services.AddValkyrieAdministration();
builder.Services.AddValkyrieAdminOptions(builder.Configuration);
builder.Services.AddValkyrieAdminUi();
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
if (builder.Configuration.RedisEnabled())
{
    healthChecks.AddCheck<ValkyrieID.Server.Redis.RedisHealthCheck>("redis", tags: ["ready"]);
}

builder.AddValkyrieTelemetry();

var app = builder.Build();

// Fail fast on an invalid master key before touching the database.
app.Services.GetRequiredService<SecretProtector>();
await app.Services.MigrateDatabaseAsync(app.Lifetime.ApplicationStopping);
await app.Services.LoadRuntimeSettingsAsync(app.Lifetime.ApplicationStopping);

if (isCli)
{
    return await CliApp.RunAsync(app.Services, args, Console.Out, Console.Error);
}

await app.Services.EnsureSigningKeysAsync(app.Lifetime.ApplicationStopping);
await app.Services.SeedBuiltInEntitiesAsync(app.Lifetime.ApplicationStopping);

app.UseValkyrieReverseProxy();
app.UseSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRequireHttps();
app.UseSeeOtherAfterPost();

app.UseProtocolCors();
if (builder.Configuration.AdminUiEnabled())
{
    app.UseValkyrieAdminUi();
}

app.UseValkyrieAccounts();
if (builder.Configuration.AdminApiEnabled())
{
    app.UseAdminApiAuthentication();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapValkyrieProtocol();
app.MapValkyrieAccounts();
if (builder.Configuration.AdminApiEnabled())
{
    app.MapValkyrieAdminApi();
}

await app.RunAsync();
return 0;
