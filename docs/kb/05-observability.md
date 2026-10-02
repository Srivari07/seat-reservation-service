# 05 — Observability

The assignment weights this equally with correctness. The metrics must reconcile with the API state.

## Health

| Endpoint | Checks | Fail mode |
|---|---|---|
| `/health/live` | Nothing external | Only fails if the process is dead |
| `/health/ready` | Migrations completed flag **and** `SELECT 1` with a 2 s timeout | 503 when either fails (fail closed, D-13) |

Implement both with ASP.NET Core health checks, filtered by tags (`live`, `ready`).

## Metrics (prometheus-net, `/metrics`)

Use `prometheus-net.AspNetCore`: `app.UseHttpMetrics()` and `app.MapMetrics()`. Verify package compatibility with .NET 10.

| Metric | Type | Labels | Required by assignment? |
|---|---|---|---|
| `reservations_confirmed_total` | counter | `show_id` | **Yes** |
| `reservations_declined_total` | counter | `show_id`, `reason` = `seat_taken`, `per_user_limit`, `idempotent_replay`, `idempotency_mismatch`, `unknown_seat`, `contention` | **Yes** (seat-taken / per-user-limit / idempotent-replay) |
| `seats_available` | gauge | `show_id` | **Yes** |
| `seats_by_status` | gauge | `show_id`, `status` | Reconciliation |
| `reconciliation_violation` | gauge | `show_id` | `total_seats - (available+held+confirmed)`. Must always be 0. |
| `reservations_cancelled_total` | counter | `show_id` | Cancel visibility |
| `db_tx_retries_total` | counter | `error` = `deadlock`, `lock_wait_timeout` | Contention visibility |
| `http_requests_received_total`, `http_request_duration_seconds` | from `UseHttpMetrics` | `code`, `method`, `endpoint` | 5xx count and latency |

Rules:

- **Counters move only after the outcome is decided**: after COMMIT for confirmed and cancelled, after ROLLBACK for declines. Never before.
- **Gauges are computed from the DB at scrape time**, not tracked in memory. Use a before-collect callback in prometheus-net (verify the exact API in the installed version) that runs
  ```sql
  SELECT s.show_id, s.status, COUNT(*) FROM seats s GROUP BY s.show_id, s.status
  ```
  (limit to the most recent ~50 shows) and joins `shows.total_seats`. This keeps `seats_available` equal to `GET /shows/{id}`, even across restarts or multiple instances.
- **Counters are per-process and reset on restart.** Document this in README. The burst compares deltas captured before and after the run.
- **Label cardinality.** `show_id` as a label is fine at this scale (a handful of shows). Mention the trade-off in WRITEUP.

## Logs (Serilog, JSON to stdout)

- Packages: `Serilog.AspNetCore` and a compact JSON formatter.
- **Request-id middleware:** read `X-Request-Id` (or generate one), push it into `LogContext`, and set the response header.
- **One structured line per reservation decision:**
  `request_id, user_id, show_id, reservation_id, seats, outcome (confirmed|declined|replayed|cancelled), reason, attempt, duration_ms`
- **Request logging:** method, path, status, duration_ms, request_id.
- **Never log:** JWTs, `admin_secret`, connection strings.
- Under a 20k burst, keep per-request logging at Information, one line per request. Don't log SQL text at Information.

## What pages at 2 a.m. (for WRITEUP)

1. Any 5xx where the status is not 503 `db_unavailable`. Every such 5xx is a bug.
2. `reconciliation_violation != 0`, or a double-confirm detected (seat count confirmed ≠ sum of seats in confirmed reservations).
3. `/health/ready` failing for more than 1 minute.
4. `reservations_declined_total{reason="contention"}` rising: lock contention or DB saturation.
5. p99 latency of `/shows/{id}/reserve` above the SLO, or `db_tx_retries_total` spiking.

## Log access (deliverable)

Use the platform's public or shared log view if it has one. Otherwise record a short screen capture of live logs during `./burst.sh <LIVE_URL>`, and link it in README.
