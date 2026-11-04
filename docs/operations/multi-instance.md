# Multiple instances

ValkyrieID runs as a single instance by default. To run several instances behind a load balancer (for availability or load), they share:

- a **PostgreSQL** database (SQLite is for a single instance only), and
- a **Redis** server.

## What Redis is used for

| Data | Why it must be shared | How it is stored |
|---|---|---|
| Authorization codes | A code issued by one instance is often redeemed at another. Each code must work exactly once, even if two instances receive it at the same moment, and a replayed code must be detected so the tokens issued from it can be revoked ([RFC 6749 §4.1.2](https://www.rfc-editor.org/rfc/rfc6749#section-4.1.2)). | Under an ID derived from the code, encrypted (AES-256-GCM) with a key also derived from the code. Redis holds neither the code nor anything readable without it. Redeeming deletes the code and leaves a replay marker in one atomic step. Codes expire after 60 seconds, replay markers after 10 minutes. |
| Rate-limit counters | Without sharing, every instance would allow the full [limits](/reference/configuration#rate-limits), so N instances would allow N times as many guesses. | Fixed-window counters with the configured limits and windows. Their keys hold an HMAC of the client address or account name (with a key derived from the master key), not the address or name itself. |
| Change notifications | Lets other instances apply changes right away instead of at their next poll: runtime settings, new signing keys, queued back-channel logout notifications, and queued emails. | Pub/sub messages that only name what changed. Receivers re-read the database. |

Nothing else goes to Redis. Clients, users, sessions, refresh tokens, and settings always come from the database, so disabling a user or a client, or revoking a session, takes effect on every instance immediately. Redis doesn't need persistence: losing its contents only invalidates codes in flight (users sign in again) and resets rate-limit windows.

## Configuration

Give every instance the same configuration, in particular:

- `Database:Provider=PostgreSql` and the same connection string
- `Redis:ConnectionString` (and `Redis:KeyPrefix`, if you change it)
- `MasterKey:Current` (and `MasterKey:Previous`)
- `Issuer:Url`
- the [trusted proxies](/operations/reverse-proxy#trusted-proxies)

```sh
Database__Provider=PostgreSql
Database__ConnectionString="Host=db;Database=valkyrieid;Username=valkyrieid;Password=..."
Redis__ConnectionString="redis:6379,password=...,ssl=true"
```

See [Redis configuration](/reference/configuration#redis).

### Redis

- Any Redis-compatible server that supports Lua scripts and pub/sub works (Redis, Valkey).
- Use authentication and TLS (`ssl=true`) when Redis is reachable from anywhere other than the instances themselves.
- Set `maxmemory-policy noeviction` (or keep `maxmemory` large enough), on every primary. Evicting a replay marker would let a replayed code go undetected, and evicting a counter would reset a rate limit. The data is small: a few hundred bytes per code or counter, gone after at most 15 minutes for the default limits.
- A single primary, a primary with replicas and Sentinel, and **Redis Cluster** all work. For a cluster, list some of its nodes in the connection string; the client discovers the rest:

  ```sh
  Redis__ConnectionString="redis-1:6379,redis-2:6379,redis-3:6379,password=...,ssl=true"
  ```

  A code and its replay marker always share a hash slot (the key names contain a hash tag), so redeeming stays atomic. Codes and counters spread over the cluster. `Redis:KeyPrefix` can't contain `{` or `}` for that reason.

### Failover

Redis replicates to replicas asynchronously. If a primary fails right after a code was redeemed, before the replica received the change, the promoted replica still has the code, and it can be redeemed once more within its 60-second lifetime. This applies to Sentinel and Cluster alike. The window is a few milliseconds around an unplanned failover; ValkyrieID doesn't wait for replicas, which would slow every token request.

## Failure behavior

If Redis is configured but unreachable, ValkyrieID **fails closed** instead of falling back to per-instance state, which would break single use of codes and multiply the rate limits:

- Sign-in, the authorization endpoint, and the token endpoint answer `503 Service Unavailable` with `Retry-After`. If Redis fails between the rate-limit check and storing a code, the authorization endpoint sends the browser back to the client with `error=temporarily_unavailable`.
- `GET /health/ready` reports unhealthy, so the load balancer stops sending traffic; `/health/live` stays healthy, so the container isn't restarted.
- Change notifications are skipped. Instances still pick up changes at their poll intervals.

When Redis is back, the server recovers without a restart.

## Background work

Each instance runs the same background jobs. They coordinate through the database:

- **Signing key rotation** runs under a lease, so only one instance rotates at a time ([key rotation](/operations/key-rotation#multiple-instances)).
- **Back-channel logout and email queues:** each item is claimed with a lease, so it is sent once.
- **Cleanup** of expired refresh tokens and old audit log entries is safe to run on every instance.

## Docker Compose example

```yaml
services:
  valkyrieid:
    image: ghcr.io/valkyrieid/valkyrieid:latest
    deploy:
      replicas: 2
    environment:
      Issuer__Url: https://id.example.com
      MasterKey__Current: ${VALKYRIEID_MASTER_KEY}
      Database__Provider: PostgreSql
      Database__ConnectionString: Host=db;Database=valkyrieid;Username=valkyrieid;Password=${DB_PASSWORD}
      Redis__ConnectionString: redis:6379,password=${REDIS_PASSWORD}
      ReverseProxy__KnownNetworks__0: 172.16.0.0/12
    depends_on: [db, redis]

  db:
    image: postgres:18-alpine
    environment:
      POSTGRES_DB: valkyrieid
      POSTGRES_USER: valkyrieid
      POSTGRES_PASSWORD: ${DB_PASSWORD}
    volumes: [db:/var/lib/postgresql]

  redis:
    image: redis:8-alpine
    command: ["redis-server", "--requirepass", "${REDIS_PASSWORD}", "--maxmemory-policy", "noeviction", "--save", ""]

volumes:
  db:
```

Put a load balancer or reverse proxy in front of the replicas, and use `/health/ready` as its health check. Sessions don't need sticky routing.
