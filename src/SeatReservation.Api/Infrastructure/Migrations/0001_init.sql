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
