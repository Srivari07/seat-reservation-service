# 02 — Schema (MySQL 8, InnoDB)

The seat row is the **single source of truth** for seat state. The counts in `GET /shows/{id}` are computed from seat rows in one statement, so I3 holds on every read.

## Conventions

- **Public IDs** (`show_id`, `reservation_id`): UUIDv7 via `Guid.CreateVersion7()`, stored as `BINARY(16)`. They are time-ordered, so there are no random page splits in InnoDB's clustered index. Expose them as standard GUID strings in JSON.
- **`user_id`**: `VARCHAR(64)`, copied from the JWT `sub` claim.
- **Seat numbers**: stored in a case-sensitive binary collation. The app uppercases them before storing or querying (the default `utf8mb4_0900_ai_ci` would treat `a12` and `A12` as equal).
- **Money**: `BIGINT` paise only.
- **Timestamps**: `DATETIME(6)`, UTC.

## Migration 0001_init.sql

```sql
CREATE TABLE schema_migrations (
  version     INT          NOT NULL PRIMARY KEY,
  applied_at  DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB;

CREATE TABLE shows (
  show_id         BINARY(16)    NOT NULL PRIMARY KEY,
  name            VARCHAR(200)  NOT NULL,
  price_paise     BIGINT        NOT NULL,
  per_user_limit  INT           NOT NULL DEFAULT 4,
  total_seats     INT           NOT NULL,
  created_at      DATETIME(6)   NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CONSTRAINT ck_shows_price  CHECK (price_paise >= 0),
  CONSTRAINT ck_shows_limit  CHECK (per_user_limit >= 1),
  CONSTRAINT ck_shows_total  CHECK (total_seats >= 1)
) ENGINE=InnoDB;

CREATE TABLE seats (
  seat_id         BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,   -- internal only
  show_id         BINARY(16)    NOT NULL,
  seat_no         VARCHAR(10)   CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  status          ENUM('available','held','confirmed') NOT NULL DEFAULT 'available',
  reservation_id  BINARY(16)    NULL,
  UNIQUE KEY uq_seats_show_seat (show_id, seat_no),       -- lock-order index (I10)
  KEY ix_seats_reservation (reservation_id),
  CONSTRAINT fk_seats_show FOREIGN KEY (show_id) REFERENCES shows(show_id),
  -- a seat has an owner iff it is not available
  CONSTRAINT ck_seats_owner CHECK ((status = 'available') = (reservation_id IS NULL))
) ENGINE=InnoDB;

CREATE TABLE reservations (
  reservation_id   BINARY(16)    NOT NULL PRIMARY KEY,
  show_id          BINARY(16)    NOT NULL,
  user_id          VARCHAR(64)   CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  idempotency_key  VARCHAR(128)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  request_hash     CHAR(64)      CHARACTER SET ascii NOT NULL,          -- hex SHA-256
  seats            JSON          NOT NULL,                              -- ["A12","A13"], sorted
  amount_paise     BIGINT        NOT NULL,
  status           ENUM('confirmed','cancelled') NOT NULL,
  created_at       DATETIME(6)   NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  cancelled_at     DATETIME(6)   NULL,
  UNIQUE KEY uq_reservations_user_key (user_id, idempotency_key),     -- exactly-once (I4)
  KEY ix_reservations_show (show_id),
  CONSTRAINT fk_reservations_show FOREIGN KEY (show_id) REFERENCES shows(show_id),
  CONSTRAINT ck_reservations_amount CHECK (amount_paise >= 0)
) ENGINE=InnoDB;

CREATE TABLE user_show_quota (
  user_id     VARCHAR(64)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  show_id     BINARY(16)   NOT NULL,
  seat_count  INT          NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, show_id),
  CONSTRAINT ck_quota_nonneg CHECK (seat_count >= 0)
) ENGINE=InnoDB;
```

## Why each constraint exists

| Constraint | Protects |
|---|---|
| `uq_seats_show_seat` | One row per physical seat, and a deterministic lock order (I10) |
| `ck_seats_owner` | A seat can't be confirmed without an owner, or available with one. This is a DB-level backstop for I1/I8. |
| `uq_reservations_user_key` | Exactly-once per (user, key) (I4); a concurrent duplicate blocks, then gets 1062 |
| `ck_quota_nonneg` | Cancel can't drive the quota negative |
| `BIGINT` money columns | I7 |

## Creating a show

Insert the show row and all seat rows in **one transaction**. Use multi-row `INSERT` in batches of about 500. `total_seats` = the number of distinct normalized seats.
