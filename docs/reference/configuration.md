# Configuration

ValkyrieID reads configuration from `appsettings.json`, `appsettings.{Environment}.json`, environment variables, and command-line arguments, in increasing order of precedence.

Nested keys are separated by `:` in JSON and command-line arguments, and by `__` (double underscore) in environment variables. For example, `Database:Provider` becomes `Database__Provider`.

## Database

| Key | Default | Description |
|---|---|---|
| `Database:Provider` | `Sqlite` | `Sqlite` or `PostgreSql`. |
| `Database:ConnectionString` | `Data Source=data/valkyrieid.db` | Provider-specific connection string. For SQLite, the directory is created if it doesn't exist. |
| `Database:AutoMigrate` | `true` | Apply pending migrations on startup. Set to `false` to apply migrations manually. |

PostgreSQL example:

```sh
Database__Provider=PostgreSql
Database__ConnectionString="Host=db;Database=valkyrieid;Username=valkyrieid;Password=..."
```

## Master encryption key

| Key | Default | Description |
|---|---|---|
| `MasterKey:Current` | *(required)* | Base64-encoded 256-bit key used to encrypt secrets at rest. |
| `MasterKey:Previous:0`, `:1`, … | *(none)* | Previous keys, still accepted for decryption during rotation. |

The server fails to start if `MasterKey:Current` is missing or invalid. See [Master encryption key](/operations/master-key).

## Logging

Logs are written to the console as JSON by default (`Logging:Console:FormatterName` = `json`). The `Development` environment uses the human-readable `simple` formatter. Log levels use the standard [`Logging:LogLevel`](https://learn.microsoft.com/aspnet/core/fundamentals/logging/) settings.

## OpenTelemetry

| Key | Default | Description |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(none)* | OTLP endpoint. OpenTelemetry is enabled only when this is set. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` or `http/protobuf`. |
| `OTEL_EXPORTER_OTLP_HEADERS` | *(none)* | Extra headers, e.g. for authentication. |

The other standard `OTEL_*` environment variables are also supported. See [Observability](/operations/observability).
