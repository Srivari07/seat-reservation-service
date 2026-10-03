# 07 — Containerize and deploy

Goal: a clean clone runs with `docker compose up --build`, and the live URL survives a cold start.

## Dockerfile (multi-stage)

- **Build stage:** `mcr.microsoft.com/dotnet/sdk:10.0`. Run `dotnet publish src/SeatReservation.Api -c Release -o /app`.
- **Runtime stage:** `mcr.microsoft.com/dotnet/aspnet:10.0`. Run as a non-root user. The image listens on 8080 by default.
- **PORT binding:** If a `PORT` env var is set (Render, Railway and Fly inject one), bind to it in `Program.cs` with `builder.WebHost.UseUrls($"http://0.0.0.0:{port}")`.
- Add a `.dockerignore` excluding `bin/`, `obj/`, `.git/`, `tests/` and `tools/`.

## docker-compose.yml

- **`mysql` service:**
  - Image `mysql:8.4`, with a named volume.
  - Healthcheck: `mysqladmin ping -h localhost` (interval 5 s, retries 20).
  - Raise `max_connections` (e.g. `--max-connections=500`) so the burst isn't capped locally.
- **`api` service:**
  - Built from the Dockerfile; port `8080:8080`.
  - `depends_on: mysql: condition: service_healthy`.
  - env from `.env` (with `.env.example` committed).

## Environment variables

| Var | Example | Notes |
|---|---|---|
| `ConnectionStrings__Mysql` | `Server=mysql;Port=3306;Database=seats;User ID=app;Password=…;Maximum Pool Size=50;Connection Timeout=5;SslMode=Preferred` | Hosted DBs usually need `SslMode=Required`. The app also forces `Guid Format=Binary16` itself (`Infrastructure/Db/MySqlConnectionStrings.cs`), so `show_id`/`reservation_id` round-trip through `BINARY(16)` correctly even if this var omits it — but set it here too for clarity/consistency with docker-compose.yml |
| `JWT_SIGNING_KEY` | 32+ random bytes, base64 | Required; refuse to start without it |
| `ADMIN_SECRET` | random string | Needed to mint admin tokens (D-10) |
| `DB_MAX_CONCURRENCY` | `50` | Equal to or below the pool size (D-14) |
| `PORT` | set by the platform | Optional locally |
| `ASPNETCORE_ENVIRONMENT` | `Production` | |

## Startup and cold start

1. Retry connecting to MySQL with backoff (1 s doubling to a 5 s cap, plus jitter), with no overall limit (`MigrationRunnerHostedService`). Liveness stays 200 while this happens; readiness returns 503.
2. Run migrations under `GET_LOCK('schema_migrations', 60)`.
3. Set the `MigrationsCompleted` flag. Readiness turns 200 once `SELECT 1` also passes.
4. Never crash-loop because the DB is slow to wake. Free-tier DBs often sleep.

## Hosting checklist (choose, then record the choice as D-18 in `01-decisions.md`)

- [ ] The app host runs a Docker image from the repo (Render / Railway / Fly.io or similar free tier).
- [ ] The managed MySQL is **real MySQL/InnoDB** (not a MySQL-compatible distributed engine). Check its free-tier connection limit and set the pool size below it.
- [ ] App and DB are in the same region (latency inside transactions = lock hold time).
- [ ] The platform health check path is set to `/health/ready`.
- [ ] Env vars and secrets are set in the platform, not in the repo.
- [ ] Log access: a public or shared log view, or record a screen capture during the live burst.
- [ ] Cold-start test: let the service sleep, then hit `/health/ready` and confirm it returns 200 within the platform's timeout.
- [ ] Run `./burst.sh <LIVE_URL>`, and save the output for README.

## Smoke test after deploy

```bash
BASE=https://<live-url>
curl -s $BASE/health/live; curl -s $BASE/health/ready
ADMIN=$(curl -s -X POST $BASE/auth/token -H 'content-type: application/json' \
  -d '{"user_id":"admin-1","role":"admin","admin_secret":"'$ADMIN_SECRET'"}' | jq -r .access_token)
SHOW=$(curl -s -X POST $BASE/shows -H "authorization: Bearer $ADMIN" -H 'content-type: application/json' \
  -d '{"name":"smoke","seats":["A1","A2"],"price_paise":25000}' | jq -r .show_id)
curl -s $BASE/shows/$SHOW | jq .counts
curl -s $BASE/metrics | grep -E 'reservations_|seats_available'
```
