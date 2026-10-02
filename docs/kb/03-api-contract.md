# 03 — API contract

- **JSON naming:** snake_case.
- **Money:** integer paise.
- **Request id:** every response carries an `X-Request-Id` header (echoed from the request, or generated).
- **Error body (all errors):**
  ```json
  { "error": "<reason>", "message": "human readable", "request_id": "..." }
  ```
  Some errors add fields; these are listed per endpoint.
- **Fixed error reasons** (do not add new ones without updating this file):
  `invalid_request`, `unauthorized`, `forbidden`, `show_not_found`, `unknown_seat`, `seat_taken`, `per_user_limit`, `idempotency_mismatch`, `idempotency_key_conflict`, `contention`, `reservation_not_found`, `db_unavailable`.

---

## POST /auth/token — dev token issuer (D-10)

**Request**
```json
{ "user_id": "u-123", "role": "user" }
{ "user_id": "admin-1", "role": "admin", "admin_secret": "<ADMIN_SECRET>" }
```

**200**
```json
{ "access_token": "<jwt>", "token_type": "Bearer", "expires_in": 86400, "user_id": "u-123", "role": "user" }
```

**Errors**

| Status | Reason | When |
|---|---|---|
| 400 | `invalid_request` | Bad `user_id` format or unknown role |
| 403 | `forbidden` | Admin requested with a wrong or missing `admin_secret` |

---

## POST /shows — admin only

**Request**
```json
{ "name": "friday-night", "seats": ["A1","A2","A3"], "price_paise": 25000, "per_user_limit": 4 }
```
`per_user_limit` is optional and defaults to 4.

**201**
```json
{
  "show_id": "…", "name": "friday-night", "price_paise": 25000, "per_user_limit": 4,
  "total_seats": 3,
  "counts": { "available": 3, "held": 0, "confirmed": 0 },
  "seats": [ { "seat": "A1", "status": "available" }, { "seat": "A2", "status": "available" }, { "seat": "A3", "status": "available" } ]
}
```

**Errors**

| Status | Reason | When |
|---|---|---|
| 400 | `invalid_request` | Empty name; empty `seats`; more than 10,000 seats; a seat failing `^[A-Z0-9]{1,10}$` after trim+uppercase; duplicate seats after normalization; `price_paise` < 0 or non-integer; `per_user_limit` < 1 |
| 401 | `unauthorized` | Missing or invalid token |
| 403 | `forbidden` | Token role is not `admin` |

---

## POST /shows/{id}/reserve — authenticated user

The idempotency key can come from the body field `idempotency_key` or the header `Idempotency-Key` (1–128 chars). If both are present and differ, the response is 400 `idempotency_key_conflict`.

**Request**
```json
{ "seats": ["A12"], "idempotency_key": "9b1d…" }
```
Any `user_id` field in the body is ignored; identity comes from the JWT `sub` claim (I6).

**201: new reservation, or replay**
```json
{ "reservation_id": "…", "show_id": "…", "user_id": "u-123", "seats": ["A12"], "amount_paise": 25000, "status": "confirmed" }
```
A replay also sets the header `Idempotent-Replayed: true`. If the original reservation was cancelled since, the replay returns it with `status: "cancelled"`. The `seats` list is returned sorted.

**Errors**

| Status | Reason | When / extra fields |
|---|---|---|
| 400 | `invalid_request` | Empty or duplicate seats, bad seat format, missing or oversized key |
| 400 | `idempotency_key_conflict` | Header key and body key differ |
| 400 | `unknown_seat` | A requested seat doesn't exist in this show. Extra field: `"seats": [...]` |
| 401 | `unauthorized` | Missing or invalid token |
| 404 | `show_not_found` | No show with this id |
| 409 | `seat_taken` | All-or-nothing: at least one seat is not available, nothing was reserved. Extra field: `"seats": [unavailable…]` |
| 409 | `per_user_limit` | Would exceed the limit. Extra fields: `"limit": 4, "current": 3` |
| 409 | `idempotency_mismatch` | Same key, different show/seats |
| 409 | `contention` | Retries exhausted (D-12). Extra field: `"retryable": true` |
| 503 | `db_unavailable` | MySQL unreachable (fail closed) |

---

## POST /reservations/{id}/cancel — owner only

**200**: the reservation with `status: "cancelled"`. Cancelling an already-cancelled reservation returns 200 with the same body (idempotent cancel).

**Errors**

| Status | Reason | When |
|---|---|---|
| 401 | `unauthorized` | Missing or invalid token |
| 404 | `reservation_not_found` | Doesn't exist, malformed id, **or belongs to another user** (don't reveal existence) |
| 409 | `contention` | Retries exhausted |
| 503 | `db_unavailable` | MySQL unreachable |

---

## GET /shows/{id} — public, no auth

**200**
```json
{
  "show_id": "…", "name": "friday-night", "price_paise": 25000, "per_user_limit": 4,
  "total_seats": 300,
  "counts": { "available": 296, "held": 0, "confirmed": 4 },
  "seats": [ { "seat": "A1", "status": "confirmed" }, … ]
}
```
- Counts and the seat list come from **one** SQL statement, so they are a single consistent snapshot.
- Seats are ordered by `seat_no` (binary order).
- Owners are not exposed.

**Errors**

| Status | Reason | When |
|---|---|---|
| 404 | `show_not_found` | No show with this id |

---

## Operational endpoints

| Endpoint | Behaviour |
|---|---|
| `GET /health/live` | 200 if the process is up. No dependency checks. |
| `GET /health/ready` | 200 only if migrations have finished **and** `SELECT 1` succeeds within 2 s; otherwise 503 |
| `GET /metrics` | Prometheus text format (see `05-observability.md`) |
| `GET /swagger` | Swagger UI over the OpenAPI document |
