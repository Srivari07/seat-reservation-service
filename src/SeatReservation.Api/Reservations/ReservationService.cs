using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Primitives;
using MySqlConnector;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.Api.Infrastructure.Errors;
using SeatReservation.Api.Shows;

namespace SeatReservation.Api.Reservations;

// POST /shows/{id}/reserve. Checks run in the precedence order of 03-api-contract.md; the
// transaction is the one in 04-concurrency.md, step for step. Every decision about seats, quota
// and idempotency is made by MySQL inside that transaction (I9), never by a read in C#.
public sealed class ReservationService(DbRunner db, ShowMetadataCache metadataCache, ILogger<ReservationService> logger)
{
    private const int MaxKeyLength = 128;
    private const string IdempotencyKeyIndex = "uq_reservations_user_key";

    public async Task<ReserveOutcome> ReserveAsync(
        string showIdRaw, string userId, ReserveRequest request, StringValues headerKeys, CancellationToken cancellationToken)
    {
        var trace = new ReserveTrace();
        try
        {
            var outcome = await DecideAsync(showIdRaw, userId, request, headerKeys, trace, cancellationToken);
            LogOutcome(outcome, showIdRaw, userId, trace);
            return outcome;
        }
        catch (DbContentionException)
        {
            // D-12: still a decline (ApiExceptionHandler turns it into 409 contention), so it gets
            // the same decision log line, with the user/show/seats the handler doesn't know.
            logger.LogWarning(
                DecisionLogTemplate,
                "declined", "contention", showIdRaw, userId, null, trace.Seats, trace.Attempts, trace.ElapsedMs);
            throw;
        }
    }

    private async Task<ReserveOutcome> DecideAsync(
        string showIdRaw, string userId, ReserveRequest request, StringValues headerKeys, ReserveTrace trace,
        CancellationToken cancellationToken)
    {
        // 1-2. Request shape, then header/body key agreement.
        if (Validate(request, headerKeys, out var seats, out var key) is { } invalid)
        {
            return invalid;
        }

        trace.Seats = seats;

        // 3. The show (D-17: a malformed id is the same 404 as an unknown one).
        if (!Guid.TryParse(showIdRaw, out var showId) || await LoadShowAsync(showId, cancellationToken) is not { } show)
        {
            return Decline(StatusCodes.Status404NotFound, "show_not_found", "No show with this id.");
        }

        // 4. Early per-user limit: asking for more seats than the limit can never succeed.
        if (seats.Count > show.PerUserLimit)
        {
            return await PerUserLimitAsync(userId, showId, show.PerUserLimit, cancellationToken);
        }

        var pending = new PendingReservation(
            Guid.CreateVersion7(), showId, userId, key, RequestHash(showId, seats), seats,
            checked(seats.Count * show.PricePaise));

        // 5-8. The transaction. DbRunner calls the callback once per attempt, so this counts
        // attempts (1 = no retry) without DbRunner having to report them.
        var decision = await db.WriteAsync(
            (connection, transaction) =>
            {
                trace.Attempts++;
                return ReserveTxAsync(connection, transaction, pending, show.PerUserLimit);
            },
            cancellationToken);

        // Follow-up reads run here, after WriteAsync has returned (never nested inside it).
        return decision.Kind switch
        {
            TxKind.Confirmed => new ReserveOutcome.Created(pending.ToResponse("confirmed")),
            TxKind.DuplicateKey => await ReplayOrMismatchAsync(pending, cancellationToken),
            TxKind.OverLimit => await PerUserLimitAsync(userId, showId, show.PerUserLimit, cancellationToken),
            TxKind.UnknownSeat => Decline(
                StatusCodes.Status400BadRequest, "unknown_seat", "Some requested seats don't exist in this show.", SeatsExtra(decision.Seats)),
            TxKind.SeatTaken => Decline(
                StatusCodes.Status409Conflict, "seat_taken", "Some requested seats are not available; nothing was reserved.", SeatsExtra(decision.Seats)),
            TxKind.UpdateMismatch => SeatTakenAfterUpdateMismatch(pending),
            _ => throw new InvalidOperationException($"Unhandled reserve decision {decision.Kind}."),
        };
    }

    // The reserve transaction (04-concurrency.md), all-or-nothing. Lock order (I10): reservation
    // row, then the user_show_quota row, then seats in (show_id, seat_no) order. Follows
    // DbRunner.WriteAsync's callback rules: no commit/rollback here, no request token, only DB
    // awaits, and only the idempotency-key 1062 is caught.
    private static async Task<TxResult<TxDecision>> ReserveTxAsync(
        MySqlConnection connection, MySqlTransaction transaction, PendingReservation r, int perUserLimit)
    {
        if (!await ClaimIdempotencyKeyAsync(connection, transaction, r))
        {
            return TxResult.Rollback(new TxDecision(TxKind.DuplicateKey, []));
        }

        if (!await TakeQuotaAsync(connection, transaction, r, perUserLimit))
        {
            return TxResult.Rollback(new TxDecision(TxKind.OverLimit, []));
        }

        var decision = await LockAndConfirmSeatsAsync(connection, transaction, r);
        return decision.Kind == TxKind.Confirmed ? TxResult.Commit(decision) : TxResult.Rollback(decision);
    }

    // Step 1. Idempotency claim (I4). A concurrent request with the same key blocks on the unique
    // index until we commit (it then gets 1062 and replays) or roll back (it proceeds).
    private static async Task<bool> ClaimIdempotencyKeyAsync(
        MySqlConnection connection, MySqlTransaction transaction, PendingReservation r)
    {
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO reservations
                  (reservation_id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status)
                VALUES (@ReservationId, @ShowId, @UserId, @IdempotencyKey, @RequestHash, @SeatsJson, @AmountPaise, 'confirmed')
                """,
                new
                {
                    r.ReservationId, r.ShowId, r.UserId, r.IdempotencyKey, r.RequestHash,
                    SeatsJson = JsonSerializer.Serialize(r.Seats), r.AmountPaise,
                },
                transaction);
            return true;
        }
        catch (MySqlException ex) when (MySqlErrors.IsDuplicateKey(ex, IdempotencyKeyIndex))
        {
            return false;
        }
    }

    // Step 2. Per-user limit (I5). The INSERT makes sure the row exists and row-locks it; the
    // conditional UPDATE then admits the seats only if the total stays within the limit.
    private static async Task<bool> TakeQuotaAsync(
        MySqlConnection connection, MySqlTransaction transaction, PendingReservation r, int perUserLimit)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO user_show_quota (user_id, show_id, seat_count) VALUES (@UserId, @ShowId, 0)
              ON DUPLICATE KEY UPDATE seat_count = seat_count
            """,
            new { r.UserId, r.ShowId },
            transaction);

        // MySqlConnector reports *matched* rows by default (UseAffectedRows=false). Here matched
        // equals changed: n >= 1, so a matching row always changes.
        var quotaRows = await connection.ExecuteAsync(
            """
            UPDATE user_show_quota
               SET seat_count = seat_count + @N
             WHERE user_id = @UserId AND show_id = @ShowId AND seat_count + @N <= @Limit
            """,
            new { N = r.Seats.Count, r.UserId, r.ShowId, Limit = perUserLimit },
            transaction);
        return quotaRows == 1;
    }

    // Step 3. Lock the requested seats in index order (I10), then decide (I1). A locking read
    // always sees the latest committed row, even under READ COMMITTED.
    private static async Task<TxDecision> LockAndConfirmSeatsAsync(
        MySqlConnection connection, MySqlTransaction transaction, PendingReservation r)
    {
        var rows = (await connection.QueryAsync<SeatRow>(
            """
            SELECT seat_id AS SeatId, seat_no AS SeatNo, status AS Status
              FROM seats
             WHERE show_id = @ShowId AND seat_no IN @Seats
             ORDER BY seat_no
               FOR UPDATE
            """,
            new { r.ShowId, r.Seats },
            transaction)).AsList();

        if (rows.Count < r.Seats.Count)
        {
            var found = rows.Select(row => row.SeatNo).ToHashSet(StringComparer.Ordinal);
            return new TxDecision(TxKind.UnknownSeat, r.Seats.Where(seat => !found.Contains(seat)).ToList());
        }

        var unavailable = rows.Where(row => row.Status != "available").Select(row => row.SeatNo).ToList();
        if (unavailable.Count > 0)
        {
            return new TxDecision(TxKind.SeatTaken, unavailable);
        }

        var updated = await connection.ExecuteAsync(
            """
            UPDATE seats
               SET status = 'confirmed', reservation_id = @ReservationId
             WHERE seat_id IN @SeatIds AND status = 'available'
            """,
            new { r.ReservationId, SeatIds = rows.Select(row => row.SeatId).ToList() },
            transaction);

        // Defence in depth: under the row locks above this can't differ. If it ever does, the
        // caller rolls back and nothing is committed.
        return new TxDecision(updated == r.Seats.Count ? TxKind.Confirmed : TxKind.UpdateMismatch, r.Seats);
    }

    private static ReserveOutcome.Declined? Validate(
        ReserveRequest request, StringValues headerKeys, out List<string> seats, out string key)
    {
        seats = [];
        key = string.Empty;

        if (request.Seats is null || request.Seats.Count == 0)
        {
            return Invalid("seats must not be empty.");
        }

        var normalized = new List<string>(request.Seats.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < request.Seats.Count; i++)
        {
            var seat = SeatNumbers.Normalize(request.Seats[i]);
            if (seat is null)
            {
                return Invalid($"seats[{i}] must match {SeatNumbers.FormatDescription}.");
            }

            if (!seen.Add(seat))
            {
                return Invalid($"duplicate seat '{seat}' after normalization.");
            }

            normalized.Add(seat);
        }

        // A repeated header has no single meaning; reject it rather than pick or combine values.
        if (headerKeys.Count > 1)
        {
            return Invalid("Send at most one Idempotency-Key header.");
        }

        var headerKey = headerKeys.Count == 1 ? headerKeys[0] : null;
        var bodyKey = request.IdempotencyKey;
        if ((bodyKey is not null && !IsValidKey(bodyKey)) || (headerKey is not null && !IsValidKey(headerKey)))
        {
            return Invalid($"The idempotency key must be 1-{MaxKeyLength} printable ASCII characters (0x21-0x7E).");
        }

        if (bodyKey is null && headerKey is null)
        {
            return Invalid("An idempotency key is required: body field idempotency_key or header Idempotency-Key.");
        }

        if (bodyKey is not null && headerKey is not null && !string.Equals(bodyKey, headerKey, StringComparison.Ordinal))
        {
            return Decline(
                StatusCodes.Status400BadRequest, "idempotency_key_conflict", "The Idempotency-Key header and the idempotency_key field differ.");
        }

        normalized.Sort(StringComparer.Ordinal);
        seats = normalized;
        key = bodyKey ?? headerKey!;
        return null;
    }

    // D-17: the column is ascii, so anything outside 0x21-0x7E would be a MySQL 1366 (a 500).
    private static bool IsValidKey(string key) =>
        key.Length is >= 1 and <= MaxKeyLength && key.All(c => c is >= '!' and <= '~');

    private async Task<ShowMetadata?> LoadShowAsync(Guid showId, CancellationToken cancellationToken)
    {
        if (metadataCache.TryGet(showId, out var cached))
        {
            return cached;
        }

        var loaded = await db.ReadAsync(
            connection => connection.QuerySingleOrDefaultAsync<ShowMetadata>(new CommandDefinition(
                "SELECT price_paise AS PricePaise, per_user_limit AS PerUserLimit FROM shows WHERE show_id = @ShowId",
                new { ShowId = showId },
                cancellationToken: cancellationToken)),
            cancellationToken);

        // Only hits are cached: a show is immutable once created, but a miss could be a show
        // that is created a moment later.
        if (loaded is not null)
        {
            metadataCache.Set(showId, loaded);
        }

        return loaded;
    }

    private async Task<ReserveOutcome> PerUserLimitAsync(
        string userId, Guid showId, int perUserLimit, CancellationToken cancellationToken)
    {
        // Read after any transaction has ended, for the response body only - not a decision.
        var current = await db.ReadAsync(
            async connection => await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT seat_count FROM user_show_quota WHERE user_id = @UserId AND show_id = @ShowId",
                new { UserId = userId, ShowId = showId },
                cancellationToken: cancellationToken)) ?? 0,
            cancellationToken);

        return Decline(
            StatusCodes.Status409Conflict,
            "per_user_limit",
            $"At most {perUserLimit} seats per user for this show.",
            new Dictionary<string, object?> { ["limit"] = perUserLimit, ["current"] = current });
    }

    private async Task<ReserveOutcome> ReplayOrMismatchAsync(PendingReservation pending, CancellationToken cancellationToken)
    {
        // The 1062 only happens once the other transaction has committed, and reservations are
        // never deleted, so the row is always there.
        var existing = await db.ReadAsync(
            connection => connection.QuerySingleOrDefaultAsync<StoredReservation>(new CommandDefinition(
                """
                SELECT reservation_id AS ReservationId, show_id AS ShowId, user_id AS UserId,
                       request_hash AS RequestHash, seats AS SeatsJson, amount_paise AS AmountPaise, status AS Status
                  FROM reservations
                 WHERE user_id = @UserId AND idempotency_key = @IdempotencyKey
                """,
                new { pending.UserId, pending.IdempotencyKey },
                cancellationToken: cancellationToken)),
            cancellationToken)
            ?? throw new InvalidOperationException("Duplicate idempotency key reported, but no reservation row was found.");

        if (!string.Equals(existing.RequestHash, pending.RequestHash, StringComparison.Ordinal))
        {
            return Decline(
                StatusCodes.Status409Conflict, "idempotency_mismatch", "This idempotency key was already used for a different request.");
        }

        // D-08: the stored reservation as it is now, including status "cancelled" if it was.
        return new ReserveOutcome.Replayed(new ReservationResponse(
            existing.ReservationId.ToString(),
            existing.ShowId.ToString(),
            existing.UserId,
            JsonSerializer.Deserialize<List<string>>(existing.SeatsJson)!,
            existing.AmountPaise,
            existing.Status));
    }

    private ReserveOutcome SeatTakenAfterUpdateMismatch(PendingReservation pending)
    {
        logger.LogError(
            "invariant_violation: locked seats {seats} were not all updated for reservation {reservation_id}; rolled back.",
            pending.Seats, pending.ReservationId);
        return Decline(
            StatusCodes.Status409Conflict, "seat_taken", "Some requested seats are not available; nothing was reserved.", SeatsExtra(pending.Seats));
    }

    // One decision line per request, with the fields 05-observability.md lists (request_id comes
    // from the LogContext pushed by RequestIdMiddleware). Written after any transaction has ended.
    private const string DecisionLogTemplate =
        "Reserve {outcome} {reason}: show {show_id} user {user_id} reservation {reservation_id} seats {seats} attempt {attempt} in {duration_ms} ms";

    private void LogOutcome(ReserveOutcome outcome, string showId, string userId, ReserveTrace trace)
    {
        var (name, reason, reservationId) = outcome switch
        {
            ReserveOutcome.Created created => ("confirmed", null, created.Reservation.ReservationId),
            ReserveOutcome.Replayed replayed => ("replayed", null, replayed.Reservation.ReservationId),
            ReserveOutcome.Declined declined => ("declined", declined.Reason, (string?)null),
            _ => throw new InvalidOperationException($"Unhandled reserve outcome {outcome.GetType().Name}."),
        };

        logger.LogInformation(
            DecisionLogTemplate, name, reason, showId, userId, reservationId, trace.Seats, trace.Attempts, trace.ElapsedMs);
    }

    // D-08: SHA-256 over the show id and the sorted, normalized seats.
    private static string RequestHash(Guid showId, IReadOnlyList<string> sortedSeats) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{showId}|{string.Join(',', sortedSeats)}")));

    private static ReserveOutcome.Declined Invalid(string message) =>
        Decline(StatusCodes.Status400BadRequest, "invalid_request", message);

    private static ReserveOutcome.Declined Decline(
        int statusCode, string reason, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        new(statusCode, reason, message, extra);

    private static Dictionary<string, object?> SeatsExtra(IReadOnlyList<string> seats) => new() { ["seats"] = seats };

    private enum TxKind
    {
        Confirmed,
        DuplicateKey,
        OverLimit,
        UnknownSeat,
        SeatTaken,
        UpdateMismatch,
    }

    private sealed record TxDecision(TxKind Kind, IReadOnlyList<string> Seats);

    // Per-request details for the decision log line. Only ever touched by one request's flow
    // (DbRunner runs attempts one after another), so no synchronization is needed.
    private sealed class ReserveTrace
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();

        public IReadOnlyList<string> Seats { get; set; } = [];

        public int Attempts { get; set; }

        public long ElapsedMs => (long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
    }

    private sealed record PendingReservation(
        Guid ReservationId,
        Guid ShowId,
        string UserId,
        string IdempotencyKey,
        string RequestHash,
        List<string> Seats,
        long AmountPaise)
    {
        public ReservationResponse ToResponse(string status) =>
            new(ReservationId.ToString(), ShowId.ToString(), UserId, Seats, AmountPaise, status);
    }

    private sealed class SeatRow
    {
        public long SeatId { get; set; }
        public string SeatNo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    private sealed class StoredReservation
    {
        public Guid ReservationId { get; set; }
        public Guid ShowId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public string SeatsJson { get; set; } = string.Empty;
        public long AmountPaise { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
