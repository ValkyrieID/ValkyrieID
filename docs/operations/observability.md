# Observability

## Logging

ValkyrieID writes structured JSON logs to the console. Tokens, passwords, and secrets are never logged.

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

## Health

See [Health endpoints](/reference/health).
