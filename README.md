# Seat Reservation Service

A JSON HTTP service (.NET 10 + MySQL 8 InnoDB) that sells assigned seats for a show. It never sells a seat twice, never lets a user go over their per-show limit, and never double-books a retried request, even when thousands of buyers hit the same seat at once. Built for the Paytm Money "Deploy & Observe" take-home exercise.

- **Live URL:** https://seat-reservation-service-production-0151.up.railway.app
- **Design write-up:** [WRITEUP.md](WRITEUP.md)
- **API docs:** [`/swagger`](https://seat-reservation-service-production-0151.up.railway.app/swagger) (OpenAPI document at `/openapi/v1.json`)
- **Metrics:** [`/metrics`](https://seat-reservation-service-production-0151.up.railway.app/metrics)
- **Logs:** screen recording of the live logs during a burst: `<RECORDING_URL>`

## Try it against the live URL

Tokens come from a dev-grade issuer, `POST /auth/token` (a deliberate shortcut, D-10; a real deployment would use an identity provider). Anyone can mint a **user** token for any `user_id`. An **admin** token, needed to create shows, also needs the `ADMIN_SECRET`. **The live admin secret is sent separately with the submission; it is never in this repo.**

```bash
BASE=https://seat-reservation-service-production-0151.up.railway.app
ADMIN_SECRET=<sent separately>
J='content-type: application/json'

# Tokens
ADMIN=$(curl -s -X POST $BASE/auth/token -H "$J" \
  -d '{"user_id":"admin-1","role":"admin","admin_secret":"'$ADMIN_SECRET'"}' | jq -r .access_token)
ALICE=$(curl -s -X POST $BASE/auth/token -H "$J" -d '{"user_id":"alice","role":"user"}' | jq -r .access_token)
BOB=$(curl -s -X POST $BASE/auth/token -H "$J" -d '{"user_id":"bob","role":"user"}' | jq -r .access_token)

# Create a show (admin). per_user_limit is optional and defaults to 4.
SHOW=$(curl -s -X POST $BASE/shows -H "authorization: Bearer $ADMIN" -H "$J" \
  -d '{"name":"friday-night","seats":["A1","A2","A3","A12"],"price_paise":25000}' | jq -r .show_id)

# Reserve. The idempotency key can go in the body or in an Idempotency-Key header.
curl -s -i -X POST $BASE/shows/$SHOW/reserve -H "authorization: Bearer $ALICE" -H "$J" \
  -d '{"seats":["A12"],"idempotency_key":"alice-try-1"}'
```

What you get back (captured from a local `docker compose` run):

```text
# reserve → 201
{"reservation_id":"01a10361-…","show_id":"01a10361-…","user_id":"alice","seats":["A12"],"amount_paise":25000,"status":"confirmed"}

# same key, same body again → 201 again, same reservation, header Idempotent-Replayed: true

# same key, different seats → 409
{"error":"idempotency_mismatch","message":"This idempotency key was already used for a different request.","request_id":"…"}

# bob asks for ["A12","A1"] → 409, all-or-nothing, so A1 stays free
{"error":"seat_taken","message":"Some requested seats are not available; nothing was reserved.","request_id":"…","seats":["A12"]}

# GET /shows/$SHOW (public) → counts always add up to total_seats
{"counts":{"available":3,"held":0,"confirmed":1},"seats":[{"seat":"A1","status":"available"},{"seat":"A12","status":"confirmed"},…]}

# bob cancels alice's reservation → 404 (only the owner can cancel, and others can't tell it exists)
{"error":"reservation_not_found","message":"No reservation with this id.","request_id":"…"}

# alice cancels → 200, seat A12 is available again; cancelling twice is also 200
{"reservation_id":"01a10361-…",…,"status":"cancelled"}
```

The rest of the calls:

```bash
curl -s $BASE/shows/$SHOW | jq '.counts'
curl -s -X POST $BASE/reservations/<reservation_id>/cancel -H "authorization: Bearer $ALICE"
```

Every error has the same shape: `{"error": "<reason>", "message": "…", "request_id": "…"}`. The full contract (every status code and reason, and which check wins when several apply) is in [docs/kb/03-api-contract.md](docs/kb/03-api-contract.md). Identity always comes from the token's `sub`: a `user_id` in the body is ignored.

## Burst: one command

```bash
./burst.sh <BASE_URL> --admin-secret <ADMIN_SECRET>      # or export ADMIN_SECRET first
./burst.sh http://localhost:8080 --admin-secret local-dev-admin-secret   # local docker compose
```

`burst.sh` runs [tools/Burst](tools/Burst) with `dotnet run` if the .NET 10 SDK is installed, and otherwise inside the `mcr.microsoft.com/dotnet/sdk:10.0` Docker image. It creates a fresh show and runs, in order:

| | Scenario | Pass condition |
|---|---|---|
| A | Setup: admin + user tokens, fresh show (limit 4), baseline `/metrics` scrape | |
| B | Hot-seat storm: `--storm` users per hot seat, all at once | exactly one 201 per hot seat, the rest 409 `seat_taken` |
| C | General stampede: 1–2 seats each, biased to the front rows | all-or-nothing, no 5xx |
| D | Same key and body 20× in parallel, then the same key with other seats | one reservation; then 409 `idempotency_mismatch` |
| E | One user, 10 parallel single-seat reserves | at most 4 succeed |
| F | Spoofed `user_id` in the body; cancelling another user's reservation | token's user wins; 404 |
| G | Cancel, then another user rebooks the seat | 201 |
| H | Reconciliation: `GET /shows/{id}` and `/metrics` deltas vs client tallies, plus the invariant sampled live during B and C | everything matches |

| Option | Default | Meaning |
|---|---|---|
| `--seats` | 1000 | Seats in the fresh show (max 10,000) |
| `--hot-seats` | 5 | Number of hot seats |
| `--storm` | 500 | Users per hot seat |
| `--users` | 5000 | Users in the general stampede |
| `--concurrency` | 1000 | Max in-flight requests |
| `--admin-secret` | `$ADMIN_SECRET` | Used to mint the admin token |

It exits non-zero if any check fails. Output of the full default run against the live URL:

```
Outcome distribution
  201 confirmed ........ 750
  201 replayed ......... 19
  409 seat_taken ....... 6758
  409 per_user_limit ... 6
  409 idempotency_mismatch 1
  409 contention ....... 0
  4xx other ............ 0
  5xx .................. 0   <-- must be 0
  transport errors ..... 1
Hot seats: A1 ✔ 1 winner | A2 ✔ 1 winner | A3 ✔ 1 winner | A4 ✔ 1 winner | A5 ✔ 1 winner
Invariant: available 133 + held 0 + confirmed 867 = 1000 ✔ (and 0 violations in 5 live samples)
Metrics reconcile: ✔
RESULT: PASS
```

The one transport error was a client-side connection failure over the public internet while 500 requests hit one seat. It was not a 5xx from the server, and local runs never show it.

## Observability

- **Health:** `GET /health/live` only checks the process is up. `GET /health/ready` returns 200 only when migrations have finished **and** `SELECT 1` succeeds within 2 s; otherwise it returns 503 (fail closed). Railway's health check uses `/health/ready` (`railway.json`).
- **Metrics** (`GET /metrics`, Prometheus text format):

  | Metric | Labels | Meaning |
  |---|---|---|
  | `reservations_confirmed_total` | `show_id` | Counter, after COMMIT |
  | `reservations_declined_total` | `show_id`, `reason` = `seat_taken` / `per_user_limit` / `idempotent_replay` / `idempotency_mismatch` / `unknown_seat` / `contention` | Counter, after ROLLBACK |
  | `reservations_cancelled_total` | `show_id` | Counter |
  | `seats_available` | `show_id` | Gauge, read from the DB at scrape time, so it always equals `GET /shows/{id}` |
  | `seats_by_status` | `show_id`, `status` | Gauge, from the DB |
  | `reconciliation_violation` | `show_id` | `total_seats − (available + held + confirmed)`; must always be 0 |
  | `db_tx_retries_total` | `error` = `deadlock` / `lock_wait_timeout` | Deadlocks and lock waits that were retried |
  | `http_requests_received_total`, `http_request_duration_seconds` | `code`, `method`, `endpoint` | Request count and latency |

  Things to know when reading them:
  - **Counters are per process and reset when the service restarts.** Compare deltas over a window, the way the burst tool does. Gauges come from the DB, so they survive restarts.
  - The gauges cover the 50 most recently created shows. `show_id` is a label, which is fine for a handful of shows (see WRITEUP).
  - If the DB is unreachable at scrape time, `/metrics` still returns 200, but the DB gauges are missing rather than frozen at an old value. Alert on them being absent.
- **Logs:** compact JSON (Serilog) to stdout. Every request gets an `X-Request-Id` (echoed if you send one), and that id is on every log line and in every error body. There is one summary line per request, plus one decision line per reserve or cancel. For example:

  ```json
  {"@t":"2026-10-03T20:08:26.938Z","@mt":"Cancel {outcome} {reason}: show {show_id} user {user_id} reservation {reservation_id} seats {seats} attempt {attempt} in {duration_ms} ms","outcome":"cancelled","show_id":"01a10361-…","user_id":"alice","reservation_id":"01a10361-…","seats":["A12"],"attempt":1,"duration_ms":12,"request_id":"94b0f24afcda4b1689446f358575786e"}
  ```

  Tokens, the admin secret and connection strings are never logged.
- **Log access:** Railway logs are private to the project, so there is a screen recording of the live logs during a burst: `<RECORDING_URL>`.

## Run locally

Prerequisites: Docker. The [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`) is only needed for the tests, and for the burst tool if you don't want it to run in Docker.

A clean clone runs with no setup:

```bash
docker compose up --build      # API on http://localhost:8080, MySQL 8.4 with a healthcheck
curl -s localhost:8080/health/ready
./burst.sh http://localhost:8080 --admin-secret local-dev-admin-secret
```

`docker-compose.yml` has **local-only** defaults for `JWT_SIGNING_KEY` and `ADMIN_SECRET` (admin secret `local-dev-admin-secret`). They are public in this repo, so never use them for a shared or deployed instance. To override them, `cp .env.example .env` and uncomment the two lines.

Tests run against a real MySQL in Docker (Testcontainers), including the concurrency tests (500 parallel requests on one seat, parallel same-key retries, and so on):

```bash
dotnet build
dotnet test                    # needs Docker running
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

Cold start: after a manual restart of the live service, `/health/ready` came back to 200 without crash-looping. The app retries the DB connection with backoff, and liveness stays 200 while it waits.

## Repo map

| Path | What |
|---|---|
| [src/SeatReservation.Api](src/SeatReservation.Api) | The service: `Reservations/` (reserve + cancel transactions), `Shows/`, `Auth/`, `Health/`, `Infrastructure/` (DB runner and retries, migrations, metrics, logging) |
| [tests/SeatReservation.IntegrationTests](tests/SeatReservation.IntegrationTests) | xUnit + Testcontainers MySQL, including the concurrency suite |
| [tools/Burst](tools/Burst) | The burst tool behind `burst.sh` |
| [AGENTS.md](AGENTS.md) | Invariants I1–I10 and conventions |
| [docs/kb/](docs/kb) | Decisions (ADR log), schema, API contract, concurrency design, observability, deploy |
| [docs/PLAN.md](docs/PLAN.md) | Build order, phase by phase |
| [docs/ai-usage-log.md](docs/ai-usage-log.md) | What AI did in each phase |
