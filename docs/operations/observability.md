# Observability

## Logging

ValkyrieID writes structured JSON logs to the console. Tokens, passwords, and secrets are never logged.

::: warning Keep request URLs out of the logs
Some URLs that reach ValkyrieID carry one-time secrets in the query string: password reset and email links (`?token=`), and the admin UI callback (`/admin/callback?code=`). The default log level for `Microsoft.AspNetCore` is `Warning`, which keeps ASP.NET Core's per-request logs (with the full URL) out. Don't lower it to `Information` or `Debug` in production.
:::

## OpenTelemetry

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces, metrics, and logs over OTLP, for example to an OpenTelemetry Collector, Jaeger, Grafana, or the .NET Aspire dashboard:

```sh
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
```

When it isn't set, OpenTelemetry is disabled entirely to keep the footprint small.

The service name is `valkyrieid`. Override it with `OTEL_SERVICE_NAME` or add attributes with `OTEL_RESOURCE_ATTRIBUTES`.

| Signal | Includes |
|---|---|
| Traces | Incoming HTTP requests (except `/health/*`), outgoing HTTP calls |
| Metrics | ASP.NET Core and HttpClient metrics, .NET runtime metrics (`System.Runtime`) |
| Logs | All application logs |

Server spans record the method, route or path, and status code, but no query string: ValkyrieID drops the `url.query` attribute before export, even if `OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION` is set. Outgoing HTTP spans redact query values, which is the instrumentation's default. Don't set `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION`.

## Health

See [Health endpoints](/reference/health).
