# Health endpoints

| Endpoint | Purpose | Checks |
|---|---|---|
| `GET /health/live` | Liveness: the process is running and serving requests. | none |
| `GET /health/ready` | Readiness: the server can handle traffic. | database connection; Redis (`PING`) when [Redis](/reference/configuration#redis) is configured |

Both return `200 Healthy` when healthy and `503 Unhealthy` otherwise. Use `/health/live` for container liveness probes and `/health/ready` for readiness probes and load balancer health checks.

Requests to `/health/*` are excluded from tracing.

They answer over plain HTTP, although every other request needs HTTPS, so container probes can call them directly. In the Docker image, the health check is the [`valkyrieid healthcheck`](/reference/cli#health-check) command, since the image has no shell or `curl`.
