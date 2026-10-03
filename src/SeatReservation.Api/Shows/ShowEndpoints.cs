using SeatReservation.Api.Health;
using SeatReservation.Api.Infrastructure.Errors;

namespace SeatReservation.Api.Shows;

public static class ShowEndpoints
{
    public static void MapShowEndpoints(this WebApplication app)
    {
        app.MapPost("/shows", async (CreateShowRequest request, HttpContext context, ShowService service, MigrationsState migrationsState, CancellationToken cancellationToken) =>
        {
            // MigrationRunnerHostedService applies migrations after the host starts accepting
            // connections (same gate /health/ready uses), so without this check a request landing
            // in that window would hit a MySqlException on a not-yet-created table and 500 (I2).
            if (!migrationsState.IsCompleted)
            {
                return ApiError.Write(context, StatusCodes.Status503ServiceUnavailable, "db_unavailable", "The service is not ready yet.");
            }

            var (success, errorMessage, show) = await service.CreateShowAsync(request, cancellationToken);
            if (!success)
            {
                return ApiError.Write(context, StatusCodes.Status400BadRequest, "invalid_request", errorMessage!);
            }

            return Results.Created($"/shows/{show!.ShowId}", show);
        }).RequireAuthorization("AdminOnly");

        // {id} is bound as string, not Guid: a malformed id must 404 show_not_found
        // (D-17), not fall through to minimal API's own 400 on a failed route-param bind.
        app.MapGet("/shows/{id}", async (string id, HttpContext context, ShowService service, MigrationsState migrationsState, CancellationToken cancellationToken) =>
        {
            if (!migrationsState.IsCompleted)
            {
                return ApiError.Write(context, StatusCodes.Status503ServiceUnavailable, "db_unavailable", "The service is not ready yet.");
            }

            if (!Guid.TryParse(id, out var showId))
            {
                return ApiError.Write(context, StatusCodes.Status404NotFound, "show_not_found", "No show with this id.");
            }

            var show = await service.GetShowAsync(showId, cancellationToken);
            return show is null
                ? ApiError.Write(context, StatusCodes.Status404NotFound, "show_not_found", "No show with this id.")
                : Results.Ok(show);
        });
    }
}
