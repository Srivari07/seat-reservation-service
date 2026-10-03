using SeatReservation.Api.Auth;
using SeatReservation.Api.Health;
using SeatReservation.Api.Infrastructure.Errors;

namespace SeatReservation.Api.Reservations;

public static class ReservationEndpoints
{
    public static void MapReservationEndpoints(this WebApplication app)
    {
        // {id} is bound as string, not Guid: a malformed id must 404 show_not_found (D-17), not
        // fall through to minimal API's own 400 on a failed route-param bind.
        app.MapPost("/shows/{id}/reserve", async (
            string id,
            ReserveRequest request,
            HttpContext context,
            ICurrentUser currentUser,
            ReservationService service,
            MigrationsState migrationsState,
            CancellationToken cancellationToken) =>
        {
            if (!migrationsState.IsCompleted)
            {
                return ApiError.Write(context, StatusCodes.Status503ServiceUnavailable, "db_unavailable", "The service is not ready yet.");
            }

            // user_id only ever comes from the token (I6).
            var outcome = await service.ReserveAsync(
                id, currentUser.UserId, request, context.Request.Headers["Idempotency-Key"], cancellationToken);

            switch (outcome)
            {
                case ReserveOutcome.Created created:
                    return Results.Json(created.Reservation, statusCode: StatusCodes.Status201Created);
                case ReserveOutcome.Replayed replayed:
                    context.Response.Headers["Idempotent-Replayed"] = "true";
                    return Results.Json(replayed.Reservation, statusCode: StatusCodes.Status201Created);
                case ReserveOutcome.Declined { Extra: null } declined:
                    return ApiError.Write(context, declined.StatusCode, declined.Reason, declined.Message);
                case ReserveOutcome.Declined declined:
                    return ApiError.Write(context, declined.StatusCode, declined.Reason, declined.Message, declined.Extra);
                default:
                    throw new InvalidOperationException($"Unhandled reserve outcome {outcome.GetType().Name}.");
            }
        }).RequireAuthorization();

        // {id} as string for the same reason: a malformed id is 404 reservation_not_found.
        app.MapPost("/reservations/{id}/cancel", async (
            string id,
            HttpContext context,
            ICurrentUser currentUser,
            CancelService service,
            MigrationsState migrationsState,
            CancellationToken cancellationToken) =>
        {
            if (!migrationsState.IsCompleted)
            {
                return ApiError.Write(context, StatusCodes.Status503ServiceUnavailable, "db_unavailable", "The service is not ready yet.");
            }

            // Only the token's user can cancel (I6).
            var outcome = await service.CancelAsync(id, currentUser.UserId, cancellationToken);

            return outcome switch
            {
                CancelOutcome.Cancelled cancelled => Results.Json(cancelled.Reservation),
                CancelOutcome.AlreadyCancelled already => Results.Json(already.Reservation),
                CancelOutcome.Declined { Extra: null } declined => ApiError.Write(context, declined.StatusCode, declined.Reason, declined.Message),
                CancelOutcome.Declined declined => ApiError.Write(context, declined.StatusCode, declined.Reason, declined.Message, declined.Extra),
                _ => throw new InvalidOperationException($"Unhandled cancel outcome {outcome.GetType().Name}."),
            };
        }).RequireAuthorization();
    }
}
