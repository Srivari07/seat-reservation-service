# 06 — Testing and burst

## Correctness bar → how we prove it

| # | Assignment check | Integration test (Testcontainers MySQL) | Burst scenario |
|---|---|---|---|
| C1 | No seat confirmed to two users; exactly one 201 per hot seat | `HotSeat_500Parallel_ExactlyOneWinner` | B |
| C2 | Zero 5xx | Every concurrency test asserts no 5xx | All (fail if > 0) |
| C3 | available + held + confirmed == total | Assert after every concurrency test | B, C and H (also sampled *during* the burst) |
| C4 | Idempotent retries move nothing; different seats on the same key → 409 | `SameKey_20Parallel_OneReservation`, `SameKey_DifferentSeats_409` | D |
| C5 | Per-user limit under concurrency | `OneUser_10ParallelSingleSeat_AtMost4` | E |
| C6 | Token-derived identity; cancel only own | `SpoofedUserIdInBody_Ignored`, `CancelOthersReservation_404` | F |
| — | All-or-nothing under concurrency | `MultiSeat_OverlappingRequests_NoPartials_NoDeadlock500` | C |
| — | Cancel then rebook | `Cancel_FreesSeat_RebookByOtherUser_201` | G |

### Integration test setup

- **Framework:** xUnit with `Testcontainers.MySql` (image `mysql:8.4`). One container per test class fixture, plus a fresh show per test.
- **App under test:** run in-process via `WebApplicationFactory<Program>`, with the connection string pointing at the container.
- **Parallelism:** use `Task.WhenAll` with a start gate (`TaskCompletionSource`) so requests really overlap.
- **Assertions:** check both the HTTP outcomes and the DB truth. For example, `SELECT COUNT(*) FROM seats WHERE status='confirmed'` must equal the sum of seats in 201 responses minus cancelled ones.

## Burst tool (`tools/Burst`, run via `./burst.sh <BASE_URL>`)

`burst.sh` runs `dotnet run -c Release --project tools/Burst -- "$@"` if the `dotnet` SDK is present. Otherwise it runs the same project inside the `mcr.microsoft.com/dotnet/sdk:10.0` Docker image.

### Options (sensible defaults)

| Option | Default | Meaning |
|---|---|---|
| `--seats` | 1000 | Seats in the fresh show |
| `--hot-seats` | 5 | Number of hot seats |
| `--storm` | 500 | Users per hot seat |
| `--users` | 5000 | Users in the general stampede |
| `--concurrency` | 1000 | Max in-flight requests |
| `--admin-secret` | `$ADMIN_SECRET` | Used to mint the admin token |

The total is roughly 20,000 requests with the defaults.

### Scenarios, in order

| Step | Scenario |
|---|---|
| A. Setup | Mint an admin token and N user tokens via `/auth/token`. Create a fresh show (limit 4). Scrape `/metrics` as the baseline. |
| B. Hot-seat storm | For each hot seat, `--storm` distinct users reserve that one seat simultaneously. Expect exactly one 201 per seat, the rest 409 `seat_taken`. |
| C. General stampede | Users request 1–2 random seats, biased towards the first rows. Overlaps are intended, to exercise all-or-nothing. |
| D. Idempotency | One user fires the same key and body 20× in parallel: one reservation id across all 201s, no extra seats. Then the same key with different seats returns 409 `idempotency_mismatch`. |
| E. Per-user limit | One fresh user fires 10 parallel single-seat reserves on free seats. At most 4 succeed; the rest get 409 `per_user_limit`. |
| F. Spoofing | A body containing `"user_id":"someone-else"` produces a reservation whose `user_id` equals the token's user. Cancelling another user's reservation returns 404. |
| G. Cancel + rebook | Cancel one reservation; a different user rebooks that seat and gets 201. |
| H. Reconciliation | `GET /shows/{id}`: counts sum to the total; confirmed seats equal (seats in 201 responses − cancelled). Scrape `/metrics`: deltas of confirmed/declined match the client-side tallies, and `seats_available` matches the API. Also sample `GET /shows/{id}` in a loop during B and C, and assert the invariant on every sample. |

### Output

Print a table like this:

```
Outcome distribution
  201 confirmed ........ 1234
  201 replayed ......... 19
  409 seat_taken ....... 15000
  409 per_user_limit ... 6
  409 idempotency_mismatch 1
  409 contention ....... 0
  4xx other ............ 0
  5xx .................. 0   <-- must be 0
  transport errors ..... 0
Hot seats: A1 ✔ 1 winner | A2 ✔ 1 winner | …
Invariant: available 700 + held 0 + confirmed 300 = 1000 ✔ (and 0 violations in 87 live samples)
Metrics reconcile: ✔
RESULT: PASS
```

The process exits non-zero on any failed check, so it can be used in CI.

### Client notes

- Use one `HttpClient` with `SocketsHttpHandler { MaxConnectionsPerServer = concurrency, PooledConnectionLifetime = 2 min }` and a `SemaphoreSlim(concurrency)`.
- Release start gates per scenario so requests actually collide.
- Count transport errors (timeouts, resets) separately from HTTP statuses, and report them.
