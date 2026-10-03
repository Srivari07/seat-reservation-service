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
- [ ] JWT bearer validation (HS256 with `JWT_SIGNING_KEY`). `ICurrentUser` reads `sub` and `role`. Add an admin policy.
- [ ] `POST /auth/token` (user tokens for anyone; admin requires `ADMIN_SECRET`).
- [ ] Tests: no token → 401; user token on an admin route → 403; wrong admin secret → 403.
  - **Commit:** `feat: jwt auth with dev token issuer`

## Phase 4 — Shows
**KB:** 02-schema, 03-api-contract
- [ ] `POST /shows`: validation and normalization, show + seats inserted in one transaction (batched).
- [ ] `GET /shows/{id}`: a single query, with counts computed from the same rowset. Add the show metadata cache.
- [ ] Tests: validation cases; the invariant holds on a fresh show; seats are case-normalized.
  - **Commit:** `feat: create and get show`

## Phase 5 — Reserve (the core) ⚠️
**KB:** 04-concurrency, 03-api-contract, 01-decisions (D-04, D-07, D-08, D-09, D-12)
- [ ] DB infrastructure: transaction helper (ReadCommitted plus lock timeout), retry helper (1213/1205), MySQL error → domain outcome mapping, and the `SemaphoreSlim` DB gate.
- [ ] Reserve service, implementing the transaction exactly as specified. Idempotency replay/mismatch path. Error bodies with reasons.
- [ ] Integration tests:
  - C1 hot seat, 500 parallel requests
  - C4 same key, 20 parallel requests, plus the mismatch case
  - C5 10 parallel requests from one user
  - C6 spoofed body
  - all-or-nothing overlap
  - zero 5xx in all of them
- [ ] Run the **correctness-reviewer** subagent and fix its findings.
  - **Accept:** all tests green, repeated 5× in a row (`for i in 1..5; dotnet test`), with no flaky deadlock 500s.
  - **Commit(s):** `feat: db transaction and retry infrastructure`, `feat: reserve seats atomically with idempotency and per-user limit`, `test: concurrency tests for reserve`

## Phase 6 — Cancel
**KB:** 04-concurrency (cancel section)
- [ ] `POST /reservations/{id}/cancel`, with the same lock order, the `reservation_id` guard, quota decrement and idempotent cancel.
- [ ] Tests: owner only (404 for others); cancel then rebook by another user gives 201; double cancel gives 200; cancel racing with reserve on the same seats produces no deadlock 500s.
- [ ] Run the **correctness-reviewer** subagent.
  - **Commit:** `feat: owner-only cancel that safely releases seats`

## Phase 7 — Metrics
**KB:** 05-observability
- [ ] prometheus-net: counters (confirmed, declined{reason}, cancelled, retries), scrape-time DB gauges (`seats_available`, `seats_by_status`, `reconciliation_violation`), HTTP metrics.
- [ ] Test: after a scripted set of requests, the metric deltas equal the HTTP outcomes, and `seats_available` equals `GET /shows/{id}`.
  - **Commit:** `feat: prometheus metrics that reconcile with show state`

## Phase 8 — Burst tool
**KB:** 06-testing-and-burst
- [ ] `tools/Burst` console app implementing scenarios A–H, the output table and the exit code.
- [ ] `burst.sh` (dotnet, or a docker fallback).
- [ ] Run against `docker compose` locally. Fix anything it finds.
  - **Accept:** `./burst.sh http://localhost:8080` prints `RESULT: PASS` with `5xx = 0`.
  - **Commit:** `feat: one-command burst script with reconciliation report`

## Phase 9 — Deploy
**KB:** 07-deploy
- [ ] Pick the host and MySQL provider. Record the choice as D-18. Set env vars and the health check path.
- [ ] Deploy, run the cold-start test, then `./burst.sh <LIVE_URL>`. Save the output.
- [ ] Set up log access (public view or screen recording).
  - **Commit:** `chore: deployment config for <platform>` (plus any fixes as separate `fix:` commits)

## Phase 10 — README and WRITEUP
**KB:** 00-assignment (Deliverables), 01-decisions, ai-usage-log
- [ ] README: live URL, how to get tokens, curl examples, `./burst.sh` usage plus sample output, metrics and logs links, local run.
- [ ] WRITEUP.md sections, exactly as the assignment lists them:
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
