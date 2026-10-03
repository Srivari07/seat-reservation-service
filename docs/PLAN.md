# PLAN — build order

Work top to bottom. One phase per Claude Code session is a good rhythm.
Each task ends with: green build and tests, a ticked checkbox, a draft row in `ai-usage-log.md`, and one commit.

Legend: **KB** = files to read first · **Accept** = how we know it's done · **Commit** = suggested message

---

## Phase 0 — Repo bootstrap
**KB:** AGENTS.md
- [x] Filled the existing `.gitignore` (dotnet build/IDE/.env entries, on top of the local-note lines), added `.editorconfig`, `.gitattributes` (LF line endings, so `burst.sh` survives a Windows checkout), filled the `README.md` stub, and added `.env.example` and `global.json` (sdk 10.0.201, `rollForward: latestFeature`). Confirmed via Microsoft's docs (not guessed) that `dotnet new sln` defaults to `.slnx` on the .NET 10 SDK, so `SeatReservation.slnx` was created with that. The docs were already committed in an earlier session.
  - **Accept:** `git log` shows the docs commit; `dotnet build` succeeds on the empty solution. Both verified.
  - **Commit:** `chore: bootstrap solution file, editorconfig, gitattributes, env template and sdk pin`

## Phase 1 — Skeleton that runs in Docker
**KB:** 05-observability, 07-deploy
- [x] Create the `SeatReservation.Api` minimal API project (net10.0), snake_case JSON, and `PORT` binding.
- [x] Serilog JSON logging plus request-id middleware (`X-Request-Id`).
- [x] `/health/live`, plus `/health/ready` (`SELECT 1` and the migrations flag), using MySqlConnector. The migrations flag is a stub (`MigrationsState.IsCompleted = true`) until Phase 2's migration runner wires the real value.
- [x] Dockerfile, `docker-compose.yml` (mysql:8.4 with healthcheck) and `.dockerignore`.
  - **Accept:** `docker compose up --build` → `/health/ready` 200. Stop MySQL → ready returns 503 while live stays 200. Both verified.
  - **Commit:** `feat: api skeleton with health checks, structured logging, docker compose`

## Phase 2 — Schema and migrations
**KB:** 02-schema
- [x] Embedded-SQL migration runner (`schema_migrations` table, `GET_LOCK`, retry on startup).
- [x] `0001_init.sql`, exactly as in `02-schema.md`.
- [x] Integration test project with a Testcontainers MySQL fixture. First test: migrations apply twice without error.
  - **Accept:** a fresh container gets all tables; restarting the app doesn't re-apply migrations. Both verified via `docker compose up --build` (clean volume) and a restart.
  - **Commit:** `feat: mysql schema and startup migration runner`

## Phase 3 — Auth
**KB:** 01-decisions (D-10), 03-api-contract
- [x] JWT bearer validation (HS256 with `JWT_SIGNING_KEY`). `ICurrentUser` reads `sub` and `role`. Add an admin policy.
- [x] `POST /auth/token` (user tokens for anyone; admin requires `ADMIN_SECRET`).
- [x] Tests: no token → 401; user token on an admin route → 403; wrong admin secret → 403.
  - **Accept:** `dotnet test` 18/18 green (10 new auth tests, via the repo's first `WebApplicationFactory<Program>` fixture against a real Testcontainers MySQL). `docker compose up --build` → `/health/ready` 200, and a live curl smoke test of `/auth/token` matches `03-api-contract.md` exactly. Both verified.
  - **Commit:** `feat: jwt auth with dev token issuer`

## Phase 4 — Shows
**KB:** 02-schema, 03-api-contract
- [x] `POST /shows`: validation and normalization, show + seats inserted in one transaction (batched).
- [x] `GET /shows/{id}`: a single query, with counts computed from the same rowset. Add the show metadata cache.
- [x] Tests: validation cases; the invariant holds on a fresh show; seats are case-normalized.
  - **Commit:** `feat: create and get show`

## Phase 5 — Reserve (the core) ⚠️
**KB:** 04-concurrency, 03-api-contract, 01-decisions (D-04, D-07, D-08, D-09, D-12)
- [x] DB infrastructure: transaction helper (ReadCommitted plus lock timeout), retry helper (1213/1205), MySQL error → domain outcome mapping, and the `SemaphoreSlim` DB gate. Built as `Infrastructure/Db/DbRunner` (`WriteAsync`/`ReadAsync`) + `DbGate` + `TxResult` + `MySqlErrors`; `ShowService` moved onto it. Proven against real MySQL in `Db/DbRunnerTests` (real deadlock retried, lock-wait exhaustion → 409 `contention` with no leaked work, mid-transaction connection loss → 503, nested-call guard, gate ≤ pool size enforced at startup).
- [x] Reserve service, implementing the transaction exactly as specified. Idempotency replay/mismatch path. Error bodies with reasons. Built as `Reservations/ReservationService` (one `DbRunner.WriteAsync` transaction: idempotency claim → quota → seats locked in `seat_no` order; replay/mismatch and the `per_user_limit` body read after it), `ReservationEndpoints` (outcome → HTTP only) and a shared `Shows/SeatNumbers` normalizer. Functional tests in `Reservations/ReserveTests` cover every outcome and the check precedence against HTTP and DB truth; the concurrency suite is the next task.
- [x] Integration tests (`Reservations/ReserveConcurrencyTests`, 10 tests, start-gated, each asserting no 5xx, I3 reconciliation and DB truth; the strict ones also assert real row-lock contention so they can't pass serially; plus a D-07 hot-seat test where lock holders roll back and a multi-seat quota race):
  - C1 hot seat, 500 parallel requests
  - C4 same key, 20 parallel requests, plus the mismatch case
  - C5 10 parallel requests from one user
  - C6 spoofed body
  - all-or-nothing overlap
  - zero 5xx in all of them
- [x] Run the **correctness-reviewer** subagent and fix its findings. Closing review: no CRITICAL/HIGH; test findings fixed; 04-concurrency.md gained the new-quota-row deadlock note; amount overflow kept as D-17's accepted risk and the quota-row pre-insert declined (user's decisions).
  - **Accept:** all tests green, repeated 5× in a row (`for i in 1..5; dotnet test`), with no flaky deadlock 500s.
  - **Commit(s):** `feat: db transaction and retry infrastructure`, `feat: reserve seats atomically with idempotency and per-user limit`, `test: concurrency tests for reserve`

## Phase 6 — Cancel
**KB:** 04-concurrency (cancel section)
- [x] `POST /reservations/{id}/cancel`, with the same lock order, the `reservation_id` guard, quota decrement and idempotent cancel. Built as `Reservations/CancelService` (one `DbRunner.WriteAsync` transaction: reservation row `FOR UPDATE` with the owner check in the `WHERE` → guarded quota decrement → seats locked by `seat_no` → guarded seat release → status update). Any rows-affected mismatch rolls back with `invariant_violation` → 409 `contention` (D-17). The quota guard (`seat_count >= n`) was added to 04-concurrency.md.
- [x] Tests: `Reservations/CancelTests` (12, including both invariant-violation rollbacks and a late cancel after a rebook) and `Reservations/CancelConcurrencyTests` (5, strict and start-gated, asserting lock waits, no 5xx, no contention, I3 and DB truth; the cancel-vs-reserve races also assert that the InnoDB deadlock count did not rise):
  - owner only (404 for others)
  - cancel then rebook by another user gives 201
  - double cancel gives 200
  - cancel racing with reserve on the same seats produces no deadlock 500s
  - a hot seat rebooked during a cancel
  - the per-user limit binding during a cancel
  - a deterministic I10 check (`Cancel_TakesLocksInGlobalOrder`): while cancel waits on a blocked seat, `performance_schema.data_locks` shows it holds exactly the reservation row and the quota row, and is waiting on the first seat by `seat_no`. Mutations that lock via `reservation_id` or take seats before the quota both fail it every time.
- [x] Run the **correctness-reviewer** subagent. No CRITICAL/HIGH/MEDIUM. Fixed: the contention log fields, the logged-id cap, the 03-api-contract 409 wording and three test-strength items. A second review found no FAILs and four LOWs. Applied: the lock-order test above, the late-cancel test, and the 503 wording in 03-api-contract (cancel and reserve). Left as a separate `fix:` commit: validating `sub` at token validation (L1/LOW-1).
  - **Accept:** full suite 110/110 green, 5× in a row; `docker compose up --build` curl smoke test matches `03-api-contract.md`. Both verified.
  - **Commit:** `feat: owner-only cancel that safely releases seats`

## Phase 7 — Metrics
**KB:** 05-observability
- [x] prometheus-net: counters (confirmed, declined{reason}, cancelled, retries), scrape-time DB gauges (`seats_available`, `seats_by_status`, `reconciliation_violation`), HTTP metrics (restricted to exactly `GET`/`POST`/`HEAD`, case-sensitive - bounds the `method` label to the three values this API actually sends/expects; a lower-cased or otherwise-spelled method is simply not recorded, not routed around). The `endpoint` label falls back to `IExceptionHandlerFeature.Endpoint` when `UseExceptionHandler` has already cleared the resolved routing endpoint, so handled errors (409/503/400) still attribute correctly instead of recording as `endpoint=""`. Built as `Infrastructure/Metrics/AppMetrics` (its own `CollectorRegistry`, not the shared static default - see the class comment for why) and `Infrastructure/Metrics/ShowGaugeCollector` (a single-flight, before-collect callback querying the 50 most-recently-created shows; on `DbUnavailableException`/`MySqlException` it logs and clears the three gauge series rather than leaving them at a stale value, so a real outage is visible as missing series, not a falsely-healthy `reconciliation_violation = 0`; prometheus-net itself swallows the exception afterwards, which is what actually keeps `/metrics` at 200).
- [x] Test: after a scripted set of requests, the metric deltas equal the HTTP outcomes, and `seats_available` equals `GET /shows/{id}`. `Metrics/MetricsTests.cs` scripts confirm/decline/replay/cancel against one host and asserts every delta (plus per_user_limit/unknown_seat/idempotency_mismatch declines and a canonical-label regression test). Verified live via `docker compose`: curl-driven confirm/decline/replay/cancel matched `GET /shows/{id}` exactly; `/metrics` stayed 200 with MySQL stopped while `/health/ready` correctly went 503, and the affected gauges disappeared from the scrape rather than freezing; 20 bogus HTTP methods added zero new series.
  - **Commit:** `feat: prometheus metrics that reconcile with show state`

## Phase 8 — Burst tool
**KB:** 06-testing-and-burst
- [x] `tools/Burst` console app implementing scenarios A–H, the output table and the exit code.
- [x] `burst.sh` (dotnet, or a docker fallback).
- [x] Run against `docker compose` locally. Fix anything it finds.
  - **Accept:** `./burst.sh http://localhost:8080` prints `RESULT: PASS` with `5xx = 0`. Verified: a fast smoke run (100 seats) and the full default run (~1000 seats, ~15k requests) both pass against local `docker compose`, 5xx and transport errors both 0.
  - **Commit:** `feat: one-command burst script with reconciliation report`

## Phase 9 — Deploy
**KB:** 07-deploy
- [x] Pick the host and MySQL provider. Record the choice as D-18. Set env vars and the health check path.
- [x] Deploy, run the cold-start test, then `./burst.sh <LIVE_URL>`. Save the output.
- [x] Set up log access (public view or screen recording).
  - **Commit:** `chore: deployment config for <platform>` (plus any fixes as separate `fix:` commits)

## Phase 10 — README and WRITEUP
**KB:** 00-assignment (Deliverables), 01-decisions, ai-usage-log
- [x] D-15 OpenAPI + Swagger UI. Found unbuilt while planning this phase. `/openapi/v1.json` (`Microsoft.AspNetCore.OpenApi` 10.0.12) and `/swagger` (`Swashbuckle.AspNetCore.SwaggerUI` 10.2.3), served in Production too, with a Bearer scheme for "Authorize". `Docs/OpenApiTests` checks every API path is in the document and the UI is served.
  - **Commit:** `feat: openapi document and swagger ui`
- [x] README: live URL, how to get tokens, curl examples, `./burst.sh` usage plus sample output, metrics and logs links, local run. The curl examples and their outputs were run against local `docker compose`. The log-access link is a `<RECORDING_URL>` placeholder for the owner to fill in.
- [x] WRITEUP.md sections, exactly as the assignment lists them (the AI-usage section is a heading + TODO, see below):
  - atomic decision (mechanism + deadlock avoidance)
  - idempotency
  - holds & expiry (cancel model; TTL as next step)
  - CAP under partition
  - observability / 2 a.m. pages
  - AI usage (directed vs decided)
  - what next
- [ ] **Write the AI-usage section yourself**, from `docs/ai-usage-log.md`.
  - **Commit:** `docs: readme and writeup`

---

## Interview prep (not a commit)

Be ready to extend the service live. Practice explaining, without notes:
- the reserve transaction line by line
- why NOWAIT was rejected
- why cancel locks seats by `seat_no`
- what happens to an idempotency key when a request is declined

Likely extensions to practice:
- TTL holds with expiry
- a payment step (held → confirmed)
- an admin "release all"
- a per-show seat price
