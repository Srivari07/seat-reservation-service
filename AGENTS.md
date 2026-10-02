# AGENTS.md — Seat Reservation Service

Instructions for any coding agent working in this repo.
The assignment (`docs/kb/00-assignment.md`) is the source of truth. This file and `docs/kb/` record how we interpret it.
If anything here conflicts with the assignment, the assignment wins: stop and flag the conflict instead of guessing.

## What we are building

One JSON HTTP service (.NET 10, single deployable) plus one MySQL 8 (InnoDB) database. It sells assigned seats for a show (concert / movie hall).

We are graded on two things, equally:
1. Correctness under a burst of ~20,000 concurrent reservations, including 500 users on one hot seat.
2. Being deployed, containerized and observable (health, metrics, structured logs, burst script).

The write-up and UI are not graded; the running service is.

## Non-negotiable invariants

- **I1 — No double-sell.** A seat is confirmed to at most one reservation. For a contested seat: exactly one 201, everyone else 409.
- **I2 — Zero 5xx during a burst.** Every domain outcome is 2xx/4xx. Every expected DB exception is mapped (see `docs/kb/04-concurrency.md`). The only allowed 5xx is 503 when MySQL is unreachable (fail closed).
- **I3 — Reconciliation.** For every show, at every read: `available + held + confirmed == total_seats`.
- **I4 — Idempotency.** `(user_id, idempotency_key)` reserves exactly once. A retry returns the original reservation. Same key with a different body returns 409 `idempotency_mismatch`.
- **I5 — Per-user limit.** A user never holds more than `per_user_limit` (default 4) seats for a show, even with parallel requests.
- **I6 — Identity from the token only.** `user_id` comes from the JWT `sub` claim. Never read it from body, query or headers. Only the owner can cancel; others get 404.
- **I7 — Money is integer paise.** `long` in C#, `BIGINT` in MySQL. Never `float`, `double` or `decimal` for money.
- **I8 — Cancel is guarded.** Cancel only frees seats whose `reservation_id` is that reservation. It can never free someone else's seat.
- **I9 — The atomic decision lives in the database.** Use row locks, conditional updates and unique constraints. Never do read-then-write in C#. Never use in-memory locks, `lock`, `SemaphoreSlim` or caches as a correctness mechanism (a semaphore for throttling DB load is fine).
- **I10 — Fixed lock order.** Locks are always taken as: reservation row → `user_show_quota` row → seats ordered by `(show_id, seat_no)`. Reserve and cancel both follow it.

## Settled decisions (do not relitigate; see `docs/kb/01-decisions.md`)

- **Architecture.** Single service: no API gateway, no microservices, no payment integration.
- **Reserve outcome.** Reserve confirms directly, returning `"status": "confirmed"`.
- **Release model.** Explicit `POST /reservations/{id}/cancel`. No TTL holds and no background sweeper. `held` exists in the enum and in counts, but stays 0.
- **Partial requests.** All-or-nothing.
- **Database.** MySQL 8 InnoDB. Write transactions use READ COMMITTED.
- **Data access.** MySqlConnector + Dapper with hand-written SQL. No EF Core.
- **Paths.** Exactly as in the assignment: `/shows`, `/shows/{id}/reserve`, `/reservations/{id}/cancel`. No `/api/v1` prefix.

## Out of scope — do NOT add without asking

- Payment gateway
- API gateway
- Redis or any cache as a source of truth
- Message queues or background workers
- TTL holds
- Best-effort partial booking
- Users table, registration or passwords
- UI
- Kubernetes
- EF Core
- A second service or container (other than MySQL)

If you believe one of these is needed, stop, explain why, and wait for a decision.

## Repo layout (target)

```
src/SeatReservation.Api/
  Program.cs
  Auth/              JWT validation, ICurrentUser, POST /auth/token (dev issuer)
  Shows/             POST /shows, GET /shows/{id}
  Reservations/      POST /shows/{id}/reserve, POST /reservations/{id}/cancel
  Health/            /health/live, /health/ready
  Infrastructure/
    Db/              connection factory, transaction + retry helper, MySQL error mapping
    Migrations/      numbered .sql files (embedded) + runner
    Metrics/         prometheus-net registrations, scrape-time gauges
    Logging/         request-id middleware, Serilog setup
tests/SeatReservation.IntegrationTests/   xUnit + Testcontainers MySQL (concurrency tests live here)
tools/Burst/                              .NET console burst tool
burst.sh  Dockerfile  docker-compose.yml  .env.example  README.md  WRITEUP.md
docs/  (kb/, PLAN.md, ai-usage-log.md)
```

## Commands

```bash
dotnet build
dotnet test                                  # needs Docker running (Testcontainers)
docker compose up --build                    # API on http://localhost:8080, MySQL with healthcheck
curl -s localhost:8080/health/ready
./burst.sh http://localhost:8080             # full stampede + reconciliation report
```

## Coding conventions

- **Structure.** Minimal APIs grouped by feature folder. The flow is endpoint → service → SQL. No business logic in endpoint lambdas.
- **JSON naming.** Use snake_case JSON (`JsonNamingPolicy.SnakeCaseLower`) so payloads match the assignment exactly.
- **Error body.** Always `{ "error": "<reason>", "message": "...", "request_id": "..." }`. The `reason` values are fixed in `docs/kb/03-api-contract.md`; don't invent new ones silently.
- **Seat numbers.** Trim and uppercase them; they must match `^[A-Z0-9]{1,10}$`. Normalize before hashing, storing or querying.
- **Async.** Async all the way. Pass `CancellationToken`.
- **Every write transaction:**
  - `BeginTransactionAsync(IsolationLevel.ReadCommitted)`
  - `SET SESSION innodb_lock_wait_timeout = 5` at the start
  - wrapped in the retry helper that handles MySQL errors 1213/1205
- **Exception handling.** Map specific MySQL error numbers. Never catch `Exception` broadly to turn a bug into a 200/409.
- **Secrets.** Only from environment variables. Commit `.env.example`, never `.env`.
- **Testing concurrency.** Concurrency behaviour is proven with integration tests against real MySQL (Testcontainers), not mocks.

## Definition of done (every task)

1. `dotnet build` succeeds and `dotnet test` is green.
2. `docker compose up --build` comes up, and `/health/ready` returns 200.
3. Invariants I1–I10 still hold. If SQL, transactions or locking changed, the correctness review was done.
4. Docs are updated if behaviour or the contract changed: `docs/kb/*`, and the checkbox in `docs/PLAN.md`.
5. One small, focused commit with a conventional message (`feat:`, `fix:`, `test:`, `chore:`, `docs:`). Never rewrite history; the graders read it.

## Knowledge base

| File | Read when working on |
|---|---|
| `docs/kb/00-assignment.md` | Anything. This is the original problem statement. |
| `docs/kb/01-decisions.md` | Any design question, or before proposing an alternative |
| `docs/kb/02-schema.md` | Migrations, SQL, data types |
| `docs/kb/03-api-contract.md` | Endpoints, DTOs, status codes, error reasons |
| `docs/kb/04-concurrency.md` | Reserve/cancel transactions, locking, retries, error mapping |
| `docs/kb/05-observability.md` | Health, metrics, logging |
| `docs/kb/06-testing-and-burst.md` | Integration tests, burst tool |
| `docs/kb/07-deploy.md` | Dockerfile, compose, env vars, hosting |
| `docs/PLAN.md` | Which phase/task is next |
