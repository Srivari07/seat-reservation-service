# Seat Reservation Service

A JSON HTTP service (.NET 10 + MySQL 8) that sells assigned seats for a show. Built for the Paytm Money "Deploy & Observe" take-home exercise. Full spec: [AGENTS.md](AGENTS.md). Build progress: [docs/PLAN.md](docs/PLAN.md).

## Status

🚧 Bootstrapping — no runnable service yet.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned via `global.json`)
- Docker Desktop (for MySQL and containerized runs, from Phase 1 onward)

## Build

```bash
dotnet build
```

## Deploy to Railway

Hosting choice recorded as D-18 in `docs/kb/01-decisions.md`: Railway for the app (Docker build from this repo's `Dockerfile`, config-as-code in `railway.json`) plus Railway's MySQL plugin in the same project.

1. Create a Railway project and add a service from this repo (Railway detects `railway.json` and builds the `Dockerfile` automatically; no manual Dockerfile path setting needed).
2. Add the **MySQL** plugin to the same project.
3. On the **api** service, set these variables (Settings → Variables):

   | Var | Value |
   |---|---|
   | `ConnectionStrings__Mysql` | `Server=${{MySQL.MYSQLHOST}};Port=${{MySQL.MYSQLPORT}};Database=${{MySQL.MYSQLDATABASE}};User ID=${{MySQL.MYSQLUSER}};Password=${{MySQL.MYSQLPASSWORD}};Maximum Pool Size=50;Connection Timeout=5;SslMode=Required;Guid Format=Binary16` |
   | `JWT_SIGNING_KEY` | your own 32+ random bytes, base64 |
   | `ADMIN_SECRET` | your own random string |
   | `DB_MAX_CONCURRENCY` | `50` (check the MySQL plugin's `max_connections` first; lower both this and the pool size above together if it's capped lower) |
   | `ASPNETCORE_ENVIRONMENT` | `Production` |

   Don't set `PORT` — Railway injects it, and `Program.cs` binds to it automatically. Don't set a health check path manually — `railway.json` already points it at `/health/ready`. Secrets live only in Railway's dashboard, never in the repo.
4. Deploy, let the service cold-start, then confirm `GET /health/ready` returns 200 within Railway's health check timeout.
5. Run `./burst.sh <live-url>` against the deployed service and save the output.
