# Health endpoints

| Endpoint | Purpose | Checks |
|---|---|---|
| `GET /health/live` | Liveness: the process is running and serving requests. | none |
| `GET /health/ready` | Readiness: the server can handle traffic. | database connection |

Both return `200 Healthy` when healthy and `503 Unhealthy` otherwise. Use `/health/live` for container liveness probes and `/health/ready` for readiness probes and load balancer health checks.

Requests to `/health/*` are excluded from tracing.
