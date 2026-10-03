# 01 — Decisions (ADR log)

Each decision records what we chose, why, and what we rejected. Add new entries at the bottom; never silently change an old one. If you reverse an entry, mark it "Superseded by D-xx".

---

### D-01 Single service, single database
- **Chosen:** One ASP.NET Core (.NET 10) deployable and one MySQL 8 InnoDB instance.
- **Why:**
  - The assignment says a single DB is "fine and encouraged", and the time budget is ~1 day.
  - On a free tier, every extra service adds cold starts, network hops and failure modes, and "zero 5xx" is graded.
- **Rejected:** API gateway plus Show/Reservation microservices, as in the original HLD diagram. Those modules shared one DB anyway, so they weren't independent services.

### D-02 No payment integration
- **Chosen:** Reserve returns `status: "confirmed"` directly. `amount_paise` is recorded on the reservation.
- **Why:**
  - The assignment does not ask for payment.
  - "Never double-charge a retried request" is satisfied by idempotency, because the reservation is the charge.
  - An external call in the hot path would add latency and 5xx risk.
- **Next step (WRITEUP only):** A payment step would add a `held → confirmed` transition with an outbox pattern.

### D-03 Release model: explicit cancel
- **Chosen:** `POST /reservations/{id}/cancel`, owner only. No TTL holds and no background sweeper.
- **Why:**
  - It matches the 201 shape in the assignment (`"status": "confirmed"`).
  - There is no expiry clock to race against, and no worker to deploy or monitor.
  - The invariant stays trivially true.
- **Consequence:** `held` exists in the seat enum and appears in counts, but is always 0. WRITEUP explains this, and describes the TTL design as "what I'd do next".

### D-04 Partial requests: all-or-nothing
- **Chosen:** A multi-seat request confirms every requested seat in one transaction, or none. If any seat is unavailable, the response is 409 `seat_taken`, listing the unavailable seats.
- **Why:** It falls out of a single transaction and is easy to prove under concurrency. Best-effort would make the "original reservation" for a retried key depend on timing.

### D-05 Datastore: MySQL 8 (InnoDB)
- **Chosen:** MySQL 8.x, with the `mysql:8.4` image locally.
- **Why:** This was my choice; MySQL offers row locks, unique constraints, `SELECT ... FOR UPDATE` and CHECK constraints (8.0.16+).
- **Caveat:** Avoid "MySQL-compatible" distributed engines in hosting. The correctness argument relies on InnoDB locking semantics.

### D-06 Isolation: READ COMMITTED for write transactions
- **Why:** The default REPEATABLE READ adds gap/next-key locks, which mean more deadlocks under a stampede. Correctness does not depend on the isolation level, because the guards are locking reads and conditional updates, which always see the latest committed row.

### D-07 Atomic decision mechanism
- **Chosen:**
  - A unique key on `(user_id, idempotency_key)` handles exactly-once.
  - A conditional `UPDATE` on `user_show_quota` enforces the per-user limit.
  - `SELECT ... FOR UPDATE` on seats, in `(show_id, seat_no)` index order, then `UPDATE`, handles seat ownership.
  - Everything happens in one transaction, with a fixed global lock order (I10).
- **Rejected:**
  - `FOR UPDATE NOWAIT` on hot seats: if the lock holder rolls back, every NOWAIT loser has already been told 409, so a seat could end the storm with zero winners.
  - SERIALIZABLE: serialization failures would need retries and are a 5xx risk.
  - In-process locks: they break with more than one instance and aren't the system of record.

### D-08 Idempotency scope and replay semantics
- **Key scope:** The key is scoped per user, `UNIQUE(user_id, idempotency_key)`, so one user's key can never return another user's reservation.
- **Mismatch detection:** `request_hash = SHA-256(show_id + "|" + sorted normalized seats joined by ",")`. Same key with a different hash returns 409 `idempotency_mismatch`, including the same seats on a different show.
- **Replay:** Returns 201 with the stored reservation, plus the header `Idempotent-Replayed: true`, and counts as `reason="idempotent_replay"`. If the reservation was cancelled since, the replay returns it with `status: "cancelled"`.
- **Declines are not stored.** The key row is inserted inside the reservation transaction, so a declined attempt (seat_taken / limit) rolls it back. A later retry with the same key re-executes and may succeed if seats were freed. This is intentional; document it in WRITEUP.

### D-09 Per-user limit semantics
- **Default:** `per_user_limit` defaults to 4, and can optionally be set when creating a show.
- **What counts:** The count is of seats in confirmed reservations for that show, tracked in `user_show_quota.seat_count`.
- **Cancel:** Cancel decrements the count in the same transaction, so freed slots can be used again.
- **Early decline:** A request asking for more seats than the limit is declined early with 409 `per_user_limit`.

### D-10 Identity and auth
- **Token:** JWT (HS256, secret from `JWT_SIGNING_KEY`). `sub` holds the user_id (string, `^[A-Za-z0-9_-]{1,64}$`) and `role` is `user` or `admin`.
- **No users table.** Users are whoever holds a valid token.
- **Token issuer:** `POST /auth/token` is a dev-grade issuer, enabled in the live deployment so graders can mint tokens:
  - Any caller can get a `user` token for any user_id.
  - An `admin` token requires `admin_secret` to match the `ADMIN_SECRET` env var.
  - This is documented in README as a deliberate shortcut, replaced by a real IdP in production.
- **Spoofing.** The request DTOs ignore any `user_id` field.
- **`sub` validation.** Token validation also requires `sub` to match the user_id format (`Auth/UserIds`, anchored with `\z`, the same rule `/auth/token` applies), otherwise 401 `unauthorized`. A validly signed token with a missing, empty, too-long, non-ASCII or space-padded `sub` would otherwise reach SQL as `user_id`. That gives a 500 from MySQL, or under the `ascii_bin` PAD SPACE collation `"u1 "` matching `"u1"` (I6). Added after Phase 6.
- **Implementation.** `Microsoft.AspNetCore.Authentication.JwtBearer` + `System.IdentityModel.Tokens.Jwt`, HS256 via `SymmetricSecurityKey`/`SigningCredentials`. `JwtBearerOptions.MapInboundClaims = false` so the `sub`/`role` claim names survive unchanged for `ICurrentUser` and the `AdminOnly` policy.

### D-11 Data access: MySqlConnector + Dapper, raw SQL
- **Why:**
  - The locking SQL must be explicit and explainable line by line in the interview.
  - It avoids depending on EF Core provider support for .NET 10.
- **Migrations:** Numbered `.sql` files embedded in the assembly, applied at startup by a small runner. The runner records versions in a `schema_migrations` table and is guarded by `GET_LOCK('schema_migrations', 60)`.
- **Accepted risk (partial first-boot failure):** MySQL DDL isn't transactional (each statement implicit-commits), so a migration's SQL isn't wrapped in `BEGIN`/`COMMIT`. If a migration fails partway through on its very first run (e.g. the process is killed mid-`CREATE TABLE`), the next attempt re-runs the same script from the top and fails with "table already exists" (1050) forever, since the version is only recorded in `schema_migrations` after the whole script succeeds. Readiness then stays 503 permanently, which is the correct fail-closed behavior (no double-sell, no incorrect 5xx — see D-13), but needs a human to fix (drop the partially-created table, or address the underlying cause) and redeploy. Not engineered around further: this only matters on a never-yet-successful first boot of a fresh database, and "fail loudly closed, let a human intervene" is the right tradeoff for that edge case at this project's scope.

### D-12 Contention after retries
- **Chosen:** Deadlock (1213) and lock wait timeout (1205) are retried up to 3 times with 10–50 ms jitter, re-running the whole transaction. If retries are exhausted, the response is 409 `{"error":"contention","retryable":true}`, counted as `reason="contention"`. Alert if this is ever non-zero.
- **Why:** The assignment requires zero 5xx, and a contended outcome is a domain decline. We don't claim the seat is taken when we don't know that.

### D-13 CAP stance
- **Chosen:** CP. If MySQL is unreachable:
  - `/health/ready` returns 503 (fail closed).
  - Write endpoints return 503 `db_unavailable`.
  - We never accept a reservation we can't durably record.
- **Why:** That 5xx is the correct response to a real outage, not a domain decline. It is not expected during a normal burst.

### D-14 Throughput protections
- **Pool size:** The MySqlConnector pool size is set explicitly (`Maximum Pool Size`), well below the server's `max_connections`.
- **DB gate:** A `SemaphoreSlim` sized to the pool gates DB work, so excess requests queue in-process instead of failing on connection acquisition. This is throttling only, not correctness (see I9).
- **Show cache:** Show metadata (`price_paise`, `per_user_limit`, existence) is immutable after creation, so it is cached in `IMemoryCache` to save a query per reserve.

### D-15 API docs
- **Chosen:** The built-in OpenAPI document (`Microsoft.AspNetCore.OpenApi`) plus Swagger UI at `/swagger`. Verify the package versions support .NET 10 before adding them.
- **Built (Phase 10):** `Microsoft.AspNetCore.OpenApi` 10.0.12 (the document at `/openapi/v1.json`, on Microsoft.OpenApi 2.12.0) and `Swashbuckle.AspNetCore.SwaggerUI` 10.2.3 (UI only, no SwaggerGen). Both ship net10.0 builds. A document transformer declares a Bearer (JWT) scheme so the UI's "Authorize" button works with a token from `POST /auth/token`.
- **Served in every environment, including Production,** for the same reason as `/auth/token` (D-10): graders explore the live URL. The document describes routes only, never secrets.

### D-16 Superseded items from my original understanding doc
- "Scale: 1,00,000 users" → the real target is about 20,000 concurrent reservations, with 500 on one hot seat.
- `price_paise decimal(10,2)` → `BIGINT`.
- Separate SeatHold, Reservation and Seat statuses → the seat row is the single source of truth for seat state.
- `/api/v1/...` paths → exact assignment paths.
- Payment Gateway, API Gateway, microservices → removed (D-01, D-02).

### D-17 KB clarifications (pre-Phase 0 review)
Ambiguities found while reviewing the KB against the assignment, and how each was settled:
- **Replay status (D-08):** stays 201 plus `Idempotent-Replayed: true`, including when the original reservation is now cancelled. A winner's retry therefore produces a second 201 for that seat; the burst tool must count replays separately.
- **Seat lock access path (I10):** rely on the optimizer using `uq_seats_show_seat` for `WHERE show_id = ? AND seat_no IN (...)`. No `FORCE INDEX`.
- **Cancel rows-affected ≠ n:** stays ROLLBACK, an Error log `invariant_violation`, and 409 `contention`, as in `04-concurrency.md`.
- **Idempotency key format:** 1–128 printable ASCII characters (0x21–0x7E), otherwise 400 `invalid_request`. This prevents MySQL 1366 on the `ascii` column, which would be a 500.
- **Retry budget (D-12):** 3 retries, i.e. 4 attempts in total.
- **Check precedence on reserve:** follows the transaction step order (listed in `03-api-contract.md`).
- **Malformed show id:** 404 `show_not_found`, the same as cancel's rule.
- **Accepted risk:** `price_paise` has no upper bound, so `checked(n * price_paise)` can throw (a 500) if the product exceeds `long.MaxValue` (~9.2e18). This is considered unrealistic and is left unguarded.
- **Local notes** (HANDOFF.md, the HLD png, the assignment .txt, my-understanding.txt) are gitignored, not committed.

### D-19 `decimal` for `price_paise` input parsing only (Phase 4)
- **Chosen:** `CreateShowRequest.PricePaise` is `decimal?`, not `long?`, so a fractional JSON number (e.g. `25000.5`) binds successfully and is rejected as 400 `invalid_request` by `ShowService.Validate` instead of failing ASP.NET model binding with a non-contract-shaped error.
- **Scope of the exception:** `decimal` never reaches storage, persistence, or arithmetic. `Validate` converts it to `long` immediately after checking it's non-negative and integral; the `shows.price_paise` column, `ShowResponse.PricePaise`, and every other money field stay `long`/`BIGINT`, per I7.
- **Why this doesn't need a `JsonElement`-based parser instead:** the global `ApiExceptionHandler` (added in the same phase) already maps a genuinely non-numeric value (e.g. `"abc"`) to 400 `invalid_request` via `BadHttpRequestException`, so `decimal` isn't covering for a parsing gap — it only exists to let a fractional *number* reach domain validation instead of bombing out at the framework level.
- **Verified in both environments:** minimal APIs only throw `BadHttpRequestException` (letting `ApiExceptionHandler` see it) when `RouteHandlerOptions.ThrowOnBadRequest` is true, which defaults to `IsDevelopment()` only — `Program.cs` sets it explicitly (`Configure<RouteHandlerOptions>`) so this holds in Production too, not just under `dotnet test`/`WebApplicationFactory`'s Development default. `ShowsApiFactory` forces `UseEnvironment("Production")` specifically so the test suite would catch a regression here.

### D-18 Hosting: Railway (app + MySQL plugin) (Phase 9)
- **Chosen:** Railway for the app host (Docker image built from the repo's `Dockerfile`, picked up via `railway.json`), and Railway's own MySQL plugin in the same project for the database.
- **Why:**
  - Real MySQL/InnoDB, not a distributed-compatible engine, satisfying the `07-deploy.md` checklist and the D-05 caveat.
  - Same project means app and DB share a region by construction — no separate region-matching step, and latency inside a lock-holding transaction stays low.
  - Railway injects a `PORT` env var, which `Program.cs` already binds to (`builder.WebHost.UseUrls` when `PORT` is set).
  - Config-as-code (`railway.json`) lets the health check path (`/health/ready`) and Dockerfile build live in the repo instead of a manual dashboard click.
- **Rejected:**
  - Render: Docker hosting is fine, but its managed DB offering is Postgres only — would still need a second provider for MySQL, with no benefit over Railway doing both.
  - Fly.io: good Docker hosting, no managed MySQL at all.
  - PlanetScale: MySQL-compatible but built on Vitess (sharded) — exactly what D-05 says to avoid, since I1/I9's correctness argument relies on InnoDB row-locking semantics.
  - Aiven: a real, fine MySQL offering, but a separate provider/region from the app host for no gain over Railway's own plugin.
- **Open item:** once the MySQL plugin is provisioned, confirm its `max_connections` (`SHOW VARIABLES LIKE 'max_connections'`) and lower `Maximum Pool Size` / `DB_MAX_CONCURRENCY` from the local default of 50 if the free tier caps lower (D-14's pool-below-server-limit requirement).
