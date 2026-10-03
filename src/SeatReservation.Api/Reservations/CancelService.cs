using System.Diagnostics;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.Api.Infrastructure.Errors;
using SeatReservation.Api.Infrastructure.Metrics;

namespace SeatReservation.Api.Reservations;

// POST /reservations/{id}/cancel. The transaction is the cancel transaction in 04-concurrency.md,
// step for step, in the same global lock order as reserve (I10). Only the owner can cancel (I6),
// and only seats still owned by this reservation are freed (I8).
public sealed class CancelService(DbRunner db, AppMetrics metrics, ILogger<CancelService> logger)
{
    private const int MaxLoggedIdLength = 64;

    public async Task<CancelOutcome> CancelAsync(string reservationIdRaw, string userId, CancellationToken cancellationToken)
    {
        var trace = new CancelTrace();
        // The id comes straight from the URL; cap what reaches the log.
        var loggedId = reservationIdRaw.Length <= MaxLoggedIdLength ? reservationIdRaw : reservationIdRaw[..MaxLoggedIdLength] + "...";
        try
        {
            var outcome = await DecideAsync(reservationIdRaw, userId, trace, cancellationToken);
            LogOutcome(outcome, loggedId, userId, trace);
            // Cancel visibility (05-observability.md); only the first-time cancel, after commit -
            // AlreadyCancelled changed nothing. Cancel-side contention isn't separately metered here:
            // db_tx_retries_total already covers contention visibility across both endpoints.
            if (outcome is CancelOutcome.Cancelled cancelled)
            {
                metrics.ReservationsCancelledTotal.WithLabels(cancelled.Reservation.ShowId).Inc();
            }

            return outcome;
        }
        catch (DbContentionException)
        {
            // D-12: ApiExceptionHandler turns it into 409 contention; this adds the decision line.
            logger.LogWarning(
                DecisionLogTemplate,
                "declined", "contention", trace.ShowId, userId, loggedId, trace.Seats, trace.Attempts, trace.ElapsedMs);
            throw;
        }
    }

    private async Task<CancelOutcome> DecideAsync(
        string reservationIdRaw, string userId, CancelTrace trace, CancellationToken cancellationToken)
    {
        // D-17: a malformed id is the same 404 as an unknown one.
        if (!Guid.TryParse(reservationIdRaw, out var reservationId))
        {
            return NotFound();
        }

        // DbRunner calls the callback once per attempt, so this counts attempts (1 = no retry).
        var decision = await db.WriteAsync(
            (connection, transaction) =>
            {
                trace.Attempts++;
                return CancelTxAsync(connection, transaction, reservationId, userId, trace);
            },
            cancellationToken);

        return decision.Kind switch
        {
            TxKind.NotFound => NotFound(),
            TxKind.AlreadyCancelled => new CancelOutcome.AlreadyCancelled(ToResponse(decision)),
            TxKind.Cancelled => new CancelOutcome.Cancelled(ToResponse(decision)),
            TxKind.QuotaMismatch => InvariantViolation(reservationId, "the user_show_quota row was missing or below the seat count"),
            TxKind.SeatMismatch => InvariantViolation(reservationId, "not every seat was still owned by the reservation"),
            _ => throw new InvalidOperationException($"Unhandled cancel decision {decision.Kind}."),
        };
    }

    // The cancel transaction (04-concurrency.md). Lock order (I10): reservation row, then the
    // user_show_quota row, then seats in (show_id, seat_no) order. Follows DbRunner.WriteAsync's
    // callback rules: no commit/rollback here, no request token, only DB awaits, no catches.
    // `trace` only collects log fields; each attempt overwrites them, so a retry is still safe.
    private static async Task<TxResult<TxDecision>> CancelTxAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid reservationId, string userId, CancelTrace trace)
    {
        // Step 1. Lock the reservation row. The owner check is part of the WHERE, so another
        // user's reservation and a missing one are the same zero-row result (I6).
        var stored = await connection.QuerySingleOrDefaultAsync<StoredReservation>(
            """
            SELECT reservation_id AS ReservationId, show_id AS ShowId, user_id AS UserId,
                   seats AS SeatsJson, amount_paise AS AmountPaise, status AS Status
              FROM reservations
             WHERE reservation_id = @ReservationId AND user_id = @UserId
               FOR UPDATE
            """,
            new { ReservationId = reservationId, UserId = userId },
            transaction);

        if (stored is null)
        {
            return TxResult.Rollback(new TxDecision(TxKind.NotFound, null, []));
        }

        // Stored sorted and normalized by reserve.
        var seats = JsonSerializer.Deserialize<List<string>>(stored.SeatsJson)!;
        trace.ShowId = stored.ShowId.ToString();
        trace.Seats = seats;
        if (stored.Status == "cancelled")
        {
            return TxResult.Rollback(new TxDecision(TxKind.AlreadyCancelled, stored, seats));
        }

        if (!await ReturnQuotaAsync(connection, transaction, stored, seats.Count))
        {
            return TxResult.Rollback(new TxDecision(TxKind.QuotaMismatch, stored, seats));
        }

        if (!await ReleaseSeatsAsync(connection, transaction, stored, seats))
        {
            return TxResult.Rollback(new TxDecision(TxKind.SeatMismatch, stored, seats));
        }

        // Step 4. The reservation row is already locked by step 1.
        await connection.ExecuteAsync(
            "UPDATE reservations SET status = 'cancelled', cancelled_at = UTC_TIMESTAMP(6) WHERE reservation_id = @ReservationId",
            new { stored.ReservationId },
            transaction);

        return TxResult.Commit(new TxDecision(TxKind.Cancelled, stored, seats));
    }

    // Step 2. Give the seats back to the user's quota (D-09). The seat_count guard turns a missing
    // or short row into a rollback instead of committing a wrong count or tripping ck_quota_nonneg.
    // MySqlConnector reports matched rows; n >= 1, so a matching row always changes.
    private static async Task<bool> ReturnQuotaAsync(
        MySqlConnection connection, MySqlTransaction transaction, StoredReservation stored, int n)
    {
        var quotaRows = await connection.ExecuteAsync(
            """
            UPDATE user_show_quota
               SET seat_count = seat_count - @N
             WHERE user_id = @UserId AND show_id = @ShowId AND seat_count >= @N
            """,
            new { N = n, stored.UserId, stored.ShowId },
            transaction);
        return quotaRows == 1;
    }

    // Step 3. Lock the seats in the same index order as reserve. Never update through
    // reservation_id directly: that walks ix_seats_reservation and locks in seat_id order, which
    // can deadlock with reserve. The reservation_id guard frees only this reservation's seats (I8).
    private static async Task<bool> ReleaseSeatsAsync(
        MySqlConnection connection, MySqlTransaction transaction, StoredReservation stored, List<string> seats)
    {
        var seatIds = (await connection.QueryAsync<long>(
            """
            SELECT seat_id
              FROM seats
             WHERE show_id = @ShowId AND seat_no IN @Seats
             ORDER BY seat_no
               FOR UPDATE
            """,
            new { stored.ShowId, Seats = seats },
            transaction)).AsList();

        var released = await connection.ExecuteAsync(
            """
            UPDATE seats
               SET status = 'available', reservation_id = NULL
             WHERE seat_id IN @SeatIds AND reservation_id = @ReservationId
            """,
            new { SeatIds = seatIds, stored.ReservationId },
            transaction);
        return released == seats.Count;
    }

    // D-17: rows affected != n rolls back, logs an Error and returns 409 contention.
    private CancelOutcome.Declined InvariantViolation(Guid reservationId, string detail)
    {
        logger.LogError("invariant_violation: cancel of reservation {reservation_id}: {detail}; rolled back.", reservationId, detail);
        return new CancelOutcome.Declined(
            StatusCodes.Status409Conflict,
            "contention",
            "The request could not be completed because of contention. Retry it.",
            new Dictionary<string, object?> { ["retryable"] = true });
    }

    private static CancelOutcome.Declined NotFound() =>
        new(StatusCodes.Status404NotFound, "reservation_not_found", "No reservation with this id.");

    private static ReservationResponse ToResponse(TxDecision decision)
    {
        var stored = decision.Reservation!;
        return new ReservationResponse(
            stored.ReservationId.ToString(), stored.ShowId.ToString(), stored.UserId, decision.Seats, stored.AmountPaise, "cancelled");
    }

    // One decision line per request (05-observability.md); request_id comes from the LogContext.
    private const string DecisionLogTemplate =
        "Cancel {outcome} {reason}: show {show_id} user {user_id} reservation {reservation_id} seats {seats} attempt {attempt} in {duration_ms} ms";

    private void LogOutcome(CancelOutcome outcome, string reservationId, string userId, CancelTrace trace)
    {
        var (name, reason) = outcome switch
        {
            CancelOutcome.Cancelled => ("cancelled", null),
            CancelOutcome.AlreadyCancelled => ("replayed", null),
            CancelOutcome.Declined declined => ("declined", declined.Reason),
            _ => throw new InvalidOperationException($"Unhandled cancel outcome {outcome.GetType().Name}."),
        };

        logger.LogInformation(
            DecisionLogTemplate, name, reason, trace.ShowId, userId, reservationId, trace.Seats, trace.Attempts, trace.ElapsedMs);
    }

    private enum TxKind
    {
        NotFound,
        AlreadyCancelled,
        Cancelled,
        QuotaMismatch,
        SeatMismatch,
    }

    private sealed record TxDecision(TxKind Kind, StoredReservation? Reservation, IReadOnlyList<string> Seats);

    // Per-request details for the decision log line; only touched by one request's flow.
    private sealed class CancelTrace
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();

        public string? ShowId { get; set; }

        public IReadOnlyList<string> Seats { get; set; } = [];

        public int Attempts { get; set; }

        public long ElapsedMs => (long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
    }

    private sealed class StoredReservation
    {
        public Guid ReservationId { get; set; }
        public Guid ShowId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string SeatsJson { get; set; } = string.Empty;
        public long AmountPaise { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
