# WRITEUP — Seat Reservation Service

One .NET 10 service and one MySQL 8 (InnoDB) database. Decisions are numbered D-xx in [docs/kb/01-decisions.md](docs/kb/01-decisions.md), and invariants I1–I10 are in [AGENTS.md](AGENTS.md).

## 1. The atomic decision

**Where it lives: in MySQL, inside one READ COMMITTED transaction per reserve.** C# never reads state and then decides; no in-process lock is used for correctness. The transaction is in [ReservationService.cs](src/SeatReservation.Api/Reservations/ReservationService.cs) and runs through [DbRunner.cs](src/SeatReservation.Api/Infrastructure/Db/DbRunner.cs).

The transaction has three steps, always in this order:

1. **Claim the idempotency key.** `INSERT INTO reservations (…)` with the reservation already marked `confirmed`. The unique key `uq_reservations_user_key (user_id, idempotency_key)` makes a second insert of the same key wait on the first, then fail with 1062 (`ClaimIdempotencyKeyAsync`).
2. **Take quota.** Make sure the `user_show_quota` row exists, then a conditional update:
   ```sql
   UPDATE user_show_quota SET seat_count = seat_count + @N
    WHERE user_id = @UserId AND show_id = @ShowId AND seat_count + @N <= @Limit
   ```
   0 rows affected → roll back, 409 `per_user_limit` (`TakeQuotaAsync`).
3. **Lock the seats, then decide.**
   ```sql
   SELECT seat_id, seat_no, status FROM seats
    WHERE show_id = @ShowId AND seat_no IN @Seats ORDER BY seat_no FOR UPDATE;
   UPDATE seats SET status = 'confirmed', reservation_id = @Rid
    WHERE seat_id IN @SeatIds AND status = 'available';
   ```
   If any seat isn't `available`, roll back and return 409 `seat_taken`, listing those seats. The guarded `UPDATE` must touch exactly n rows, as defence in depth (`LockAndConfirmSeatsAsync`).

**Why it's race-free.**
- **Hot seat.** 500 requests for A12 queue on A12's row lock. The first to commit wins. Each later transaction's locking read sees the latest committed row, `confirmed`, and is declined. `FOR UPDATE` reads the latest committed version, not a snapshot, so the isolation level doesn't affect this.
- **Same user in parallel.** The parallel requests queue on that user's quota row, and the conditional update can never push `seat_count` past the limit.
- **All-or-nothing (D-04).** Everything is one transaction, so a multi-seat request either confirms every seat or rolls back every seat.
- **Database backstops.** Even a bug in the application code can't commit a bad state:
  - `ck_seats_owner CHECK ((status = 'available') = (reservation_id IS NULL))`
  - `uq_seats_show_seat`
  - `ck_quota_nonneg`

  These are in [0001_init.sql](src/SeatReservation.Api/Infrastructure/Migrations/0001_init.sql).

**Avoiding deadlocks on multi-seat requests: one global lock order (I10).** Every transaction takes its locks in the same order: reservation row → quota row → seats in `(show_id, seat_no)` order, via the `uq_seats_show_seat` index. Cancel follows the same order. In particular, it locks seats by `seat_no`, not through `ix_seats_reservation`, because that index would lock them in `seat_id` order. `Cancel_TakesLocksInGlobalOrder` in [CancelConcurrencyTests.cs](tests/SeatReservation.IntegrationTests/Reservations/CancelConcurrencyTests.cs) proves this deterministically: while cancel waits on a blocked seat, it checks `performance_schema.data_locks` to see exactly which locks cancel holds and which one it's waiting on.

Some deadlocks remain possible. One example is several parallel requests from a user with no quota row yet, when the request that creates the row rolls back; `OneUser_ParallelWithDeclines_QuotaMatchesSeats` in [ReserveConcurrencyTests.cs](tests/SeatReservation.IntegrationTests/Reservations/ReserveConcurrencyTests.cs) covers this. For those, `DbRunner` re-runs the **whole** transaction on 1213 (deadlock) or 1205 (lock wait timeout, 5 s per transaction): 4 attempts with 10–50 ms jitter. If all four fail, the answer is 409 `contention` with `retryable: true`. That is a domain decline, never a 5xx (D-12). In the live burst, `contention` was 0.

**What I rejected (D-07).**
- `FOR UPDATE NOWAIT`: if the current holder rolls back (say its other seat was taken), every NOWAIT loser has already been told 409, so the seat could end the storm with **zero** winners. `HotSeat_HoldersThatRollBack_StillExactlyOneWinner` in [ReserveConcurrencyTests.cs](tests/SeatReservation.IntegrationTests/Reservations/ReserveConcurrencyTests.cs) covers exactly this case.
- SERIALIZABLE: it adds serialization failures that would need retrying.
- REPEATABLE READ: it adds gap locks, which mean more deadlocks under a stampede (D-06).
- In-process locks: they stop working with a second instance, and they aren't the system of record.

## 2. Idempotency

- **Where the key is stored:** on the reservation row itself, `reservations.idempotency_key`, with `UNIQUE (user_id, idempotency_key)`. The key is scoped per user, so one user's key can never return another user's reservation (D-08). The key comes from the body `idempotency_key` or the `Idempotency-Key` header. If both are sent and they differ, the answer is 400 `idempotency_key_conflict`.
- **How exactly-once is enforced:** the unique index, inside the same transaction that takes the seats. A concurrent duplicate waits on the index entry. If the first request commits, the duplicate gets 1062. There is never a window where both can insert.
- **Same key, same body:** on 1062 we read the stored row and compare `request_hash = SHA-256(show_id + "|" + sorted normalized seats)`. If the hashes are equal, we return 201 with the **original** reservation and the header `Idempotent-Replayed: true`. Nothing new is reserved, and the metric counts it as `reason="idempotent_replay"`. A replay after a cancel still returns the original reservation, now with `status: "cancelled"`.
- **Same key, different body:** different seats, or the same seats on a different show, give 409 `idempotency_mismatch`.
- **Declines aren't stored.** The key is inserted in the same transaction that gets rolled back for `seat_taken` or `per_user_limit`. A later retry with the same key therefore runs again, and can succeed if seats have been freed. That's deliberate: the key records a reservation, not an attempt (`DeclinedAttempt_DoesNotStoreKey_SoARetryCanSucceed`).
- **A known edge case:** when several parallel retries of a request that is being declined wait on the same key, they can deadlock on each other's gap locks. They're retried, and some can end as 409 `contention`, but never as a 5xx and never with a stored row (`SameKey_20Parallel_OnTakenSeat_DeclinesWithout5xx`).

## 3. Holds & expiry

**Model: explicit cancel, with no TTL (D-03).** `POST /reservations/{id}/cancel` is owner-only. A reservation confirms immediately, which matches the assignment's `"status": "confirmed"` response. `held` exists in the seat status enum and in `counts`, but it is always 0. With no expiry clock, there's no race between a hold expiring and a confirm, and no background worker to deploy and monitor.

**Cancel** ([CancelService.cs](src/SeatReservation.Api/Reservations/CancelService.cs)) takes the same lock order as reserve, in one transaction:
1. Lock the reservation row, with the owner check in the `WHERE`. Another user's reservation looks exactly like a missing one: 404 (I6).
2. Decrement the quota, guarded by `seat_count >= n`.
3. Lock the seats by `seat_no`, then release them **only where `reservation_id` is this reservation** (I8).

So a cancel can never resurrect a seat that someone else now owns. If any row count is off, the transaction rolls back, logs `invariant_violation` at Error, and returns 409. Cancelling twice returns 200 with the same body, and the quota is decremented only once.

**TTL holds are the first item under "next"** (section 7).

## 4. Consistency vs availability under a partition

**CP (D-13).** MySQL is the only system of record, so if the service can't reach it, the service refuses rather than guessing:
- `/health/ready` returns 503, so the platform stops routing traffic.
- Every endpoint that touches the DB returns 503 `db_unavailable`.

That 503 is the only 5xx by design. It's the correct answer to an outage, not a domain decline.

The tricky case is losing the connection **during COMMIT**: the outcome is unknown. `DbRunner` does **not** retry in that case; the connection is in `Broken` state and it raises 503. The client retries with the same idempotency key, and that settles it: if the commit landed, the retry replays the reservation; if it didn't, the retry reserves normally. Retrying blindly on the server could double-book.

With a single DB there is no split-brain to resolve. The cost is availability: while MySQL is unreachable, nobody can book.

## 5. Observability: what pages me at 2 a.m.

The metrics are defined in [AppMetrics.cs](src/SeatReservation.Api/Infrastructure/Metrics/AppMetrics.cs). The DB gauges are recomputed at every scrape in [ShowGaugeCollector.cs](src/SeatReservation.Api/Infrastructure/Metrics/ShowGaugeCollector.cs). Every alert below is backed by an existing series:

| Page | Query (PromQL, sketch) | Why |
|---|---|---|
| Any 5xx except a `db_unavailable` outage | `sum(rate(http_requests_received_total{code=~"5.."}[5m])) > 0` while `/health/ready` is up | Every other 5xx is an unmapped bug (I2) |
| Seats don't add up | `max(reconciliation_violation) != 0` | `available + held + confirmed ≠ total`, i.e. corrupted seat state (I3) |
| Gauges missing | `absent(seats_available)` | The scrape-time DB query is failing. The series are cleared on failure, not frozen, so an outage can't look healthy |
| Readiness down > 1 min | Platform health check, or `up == 0` | The DB is unreachable: the service is up but can't sell anything |
| Contention | `increase(reservations_declined_total{reason="contention"}[5m]) > 0` | Retries are being exhausted: lock pile-ups or a saturated DB |
| Slow reserves or a spike in retries | p99 of `http_request_duration_seconds{endpoint="/shows/{id}/reserve"}` above the SLO; `rate(db_tx_retries_total[5m])` spiking | Usually the leading signal for the contention alert |

To diagnose, every log line carries a `request_id`. It's also in the `X-Request-Id` response header and in every error body, so one id goes from a client's complaint to the decision line: outcome, reason, seats, attempt and duration.

**Trade-offs.**
- `show_id` is a metric label. That's fine for a handful of shows, but with thousands of shows it would need to become a log field or exemplar.
- The DB gauges cover only the 50 most recently created shows.
- Counters are per process and reset on restart, so alerts use `rate`/`increase`. Gauges come from the DB, so they agree across restarts and across instances.

## 6. AI usage (directed vs decided)

TODO: to be written by the author from [docs/ai-usage-log.md](docs/ai-usage-log.md).

## 7. What I'd do next

1. **TTL holds.** Reserve would create `held` seats with `held_until`; a separate confirm step (payment) would move them to `confirmed`. Expiry would be done lazily: the reserve transaction treats `held AND held_until < now` as available, inside the same row lock, so no sweeper is needed for correctness. A sweeper would only tidy the counts.
2. **Payment step.** `held → confirmed` driven by a payment callback, with an outbox table written in the same transaction, so the "charge" and the seat state can't diverge. The idempotency key would carry through to the payment provider.
3. **A real identity provider** instead of the dev `POST /auth/token`, plus rate limiting on token minting and on reserve per user.
4. **Bound `price_paise`** at show creation. Today `n × price` could overflow `long` and give a 500 (D-17, an accepted risk).
5. **Recover from a migration that fails halfway on first boot** (D-11). Today readiness stays 503 until a human fixes it.
6. **Capacity work:** tune the connection pool and `DB_MAX_CONCURRENCY` gate against the managed DB's real `max_connections`. Load-test past the burst size to find where `contention` starts.
7. **Response schemas in the OpenAPI document.** Today it describes routes and request bodies, but not the 201/409 response shapes.
