# 04 — Concurrency design

This is the heart of the assignment. Every rule here maps to an invariant in `AGENTS.md`.

## Ground rules

- Every write transaction:
  - `await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted)` (D-06)
  - First statement: `SET SESSION innodb_lock_wait_timeout = 5`. This must be per transaction, because MySqlConnector resets session state when a pooled connection is reused.
  - Runs inside `RetryHelper.ExecuteAsync(...)`, which re-runs the **whole** transaction on 1213/1205 (D-12).
- No HTTP calls, no logging I/O waits and no non-DB awaits inside a transaction. Keep transactions short.
- Global lock order (I10): **reservation row → `user_show_quota` row → seats by `(show_id, seat_no)`**.

## Pre-transaction (no locks)

1. Validate the request. Normalize seats (trim, uppercase, format check), reject duplicates, and sort with ordinal comparison.
2. Load the show (price, limit) from `IMemoryCache`, falling back to the DB. If it doesn't exist, return 404 `show_not_found`.
3. If `seats.Count > per_user_limit`, return 409 `per_user_limit` (no DB write needed).
4. Compute:
   - `request_hash = hex(SHA256(show_id + "|" + string.Join(",", sortedSeats)))`
   - `amount = checked(seats.Count * price_paise)`
   - `reservation_id = Guid.CreateVersion7()`

## Reserve transaction (all-or-nothing)

```sql
SET SESSION innodb_lock_wait_timeout = 5;

-- Step 1. Idempotency claim. A concurrent duplicate key blocks here until we commit/rollback.
INSERT INTO reservations
  (reservation_id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status)
VALUES (@rid, @show, @user, @key, @hash, @seatsJson, @amount, 'confirmed');
--   MySqlException 1062 on uq_reservations_user_key →
--     ROLLBACK; SELECT * FROM reservations WHERE user_id=@user AND idempotency_key=@key;
--     hash equal → 201 replay (Idempotent-Replayed: true), metric reason=idempotent_replay
--     hash differs → 409 idempotency_mismatch

-- Step 2. Per-user limit (I5). Ensure the row exists, then a conditional increment.
INSERT INTO user_show_quota (user_id, show_id, seat_count) VALUES (@user, @show, 0)
  ON DUPLICATE KEY UPDATE seat_count = seat_count;
UPDATE user_show_quota
   SET seat_count = seat_count + @n
 WHERE user_id = @user AND show_id = @show AND seat_count + @n <= @limit;
--   affected == 0 → ROLLBACK; 409 per_user_limit (read current count for the body, outside the tx)

-- Step 3. Lock the requested seats in index order (I10), then decide (I1).
SELECT seat_id, seat_no, status
  FROM seats
 WHERE show_id = @show AND seat_no IN @sortedSeats
 ORDER BY seat_no
   FOR UPDATE;
--   rows < n → ROLLBACK; 400 unknown_seat (list the missing ones)
--   any status <> 'available' → ROLLBACK; 409 seat_taken (list them)

UPDATE seats
   SET status = 'confirmed', reservation_id = @rid
 WHERE seat_id IN @seatIds AND status = 'available';
--   affected must == n (defence in depth); otherwise ROLLBACK and treat as seat_taken

COMMIT;
-- After COMMIT only: metrics reservations_confirmed_total++, log outcome=confirmed
```

The step order above fixes which decline wins when several apply (quota before seats, because of I10). The full precedence list is in `03-api-contract.md`.

### Why this is race-free

- **Hot seat with 500 contenders.** Step 3 serializes them on the seat's row lock. The first to commit wins. Each later transaction's locking read sees the latest committed `confirmed`, and is declined with 409. Waits are short because the winner's transaction is short.
- **Same user firing 10 parallel requests.** They serialize on the `user_show_quota` row. The conditional `UPDATE` admits at most `limit` seats.
- **Same idempotency key twice concurrently.** The second blocks on the unique index entry:
  - If the first commits, the second gets 1062 and replays.
  - If the first rolls back (declined), the second executes normally.
- **Multi-seat deadlock avoidance.** All transactions lock seats in the same `(show_id, seat_no)` index order, and take the quota row before any seat.
- **Database backstops.** `ck_seats_owner` and the unique keys make an inconsistent state impossible to commit, even if app code had a bug.

### Why not NOWAIT (D-07)

If the current holder of A12 rolls back (e.g. its other requested seat was taken), every NOWAIT loser has already been told 409. A12 could then end the storm unsold, and the "exactly one 201" check fails.

## Cancel transaction

```sql
SET SESSION innodb_lock_wait_timeout = 5;

-- Step 1. Lock the reservation row (first in lock order).
SELECT reservation_id, show_id, user_id, seats, status
  FROM reservations WHERE reservation_id = @rid FOR UPDATE;
--   not found OR user_id <> @user → ROLLBACK; 404 reservation_not_found (I6)
--   status = 'cancelled' → ROLLBACK; 200 with the existing body (idempotent cancel)

-- Step 2. Quota.
UPDATE user_show_quota SET seat_count = seat_count - @n
 WHERE user_id = @user AND show_id = @show;

-- Step 3. Lock seats in the SAME index order as reserve. Do NOT update via reservation_id directly:
--         that walks ix_seats_reservation and locks in seat_id order → deadlock risk with reserve.
SELECT seat_id FROM seats
 WHERE show_id = @show AND seat_no IN @seats
 ORDER BY seat_no
   FOR UPDATE;

UPDATE seats SET status = 'available', reservation_id = NULL
 WHERE seat_id IN @seatIds AND reservation_id = @rid;     -- guard (I8)
--   affected must == n; otherwise ROLLBACK, log level=Error "invariant_violation", 409 contention

UPDATE reservations SET status = 'cancelled', cancelled_at = UTC_TIMESTAMP(6)
 WHERE reservation_id = @rid;

COMMIT;  -- then reservations_cancelled_total++
```

## MySQL error mapping (I2)

Use `MySqlException.ErrorCode` (`MySqlErrorCode` enum) or `.Number`. Verify the enum member names in the installed MySqlConnector version.

| Error | Meaning | Action |
|---|---|---|
| 1062 `DuplicateKeyEntry` on `uq_reservations_user_key` | Same idempotency key | Replay or 409 `idempotency_mismatch` |
| 1062 on any other key | Bug | Let it surface as 500 and log Error. It must never happen. |
| 1213 `LockDeadlock` | Deadlock victim | Retry the whole transaction (3 retries = 4 attempts total, 10–50 ms jitter) |
| 1205 `LockWaitTimeout` | Waited more than 5 s | Retry the whole transaction (same budget) |
| Retries exhausted | | 409 `contention`, `retryable: true`, metric reason=contention |
| Connection failures / timeouts (`MySqlConnector` connect errors, 1040, 1042, `TimeoutException` on open) | DB unreachable | 503 `db_unavailable` (D-13) |
| Anything else | Bug | 500 plus an Error log with request_id. The burst must show 0 of these. |

## Throughput (D-14)

- **Connection string:** `Maximum Pool Size=<N>`, where N ≤ the DB plan's connection limit minus headroom. Also set `Connection Timeout` explicitly.
- **DB gate:** `SemaphoreSlim(N)` wraps DB work, waiting with the request's `CancellationToken`. This is throttling only, never correctness.
- **Show cache:** Show metadata is cached because it is immutable after creation.

## Reads

`GET /shows/{id}` does a single `SELECT seat_no, status FROM seats WHERE show_id=@show ORDER BY seat_no` and computes counts from the same rowset. One statement means one consistent snapshot, so I3 holds on every read.
