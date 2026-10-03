namespace SeatReservation.Api.Reservations;

// Deliberately has no user_id: identity comes only from the JWT "sub" claim (I6), and
// System.Text.Json ignores unknown fields, so a spoofed "user_id" in the body is just dropped.
public sealed record ReserveRequest(List<string>? Seats, string? IdempotencyKey);

public sealed record ReservationResponse(
    string ReservationId,
    string ShowId,
    string UserId,
    IReadOnlyList<string> Seats,
    long AmountPaise,
    string Status);

// What ReservationService decided; ReservationEndpoints only maps it to HTTP.
public abstract record ReserveOutcome
{
    public sealed record Created(ReservationResponse Reservation) : ReserveOutcome;

    public sealed record Replayed(ReservationResponse Reservation) : ReserveOutcome;

    public sealed record Declined(
        int StatusCode, string Reason, string Message, IReadOnlyDictionary<string, object?>? Extra = null) : ReserveOutcome;
}

// What CancelService decided. Cancelled and AlreadyCancelled are both a 200 with the same body;
// they differ only in the decision log (and, from Phase 7, the cancelled counter).
public abstract record CancelOutcome
{
    public sealed record Cancelled(ReservationResponse Reservation) : CancelOutcome;

    public sealed record AlreadyCancelled(ReservationResponse Reservation) : CancelOutcome;

    public sealed record Declined(
        int StatusCode, string Reason, string Message, IReadOnlyDictionary<string, object?>? Extra = null) : CancelOutcome;
}
