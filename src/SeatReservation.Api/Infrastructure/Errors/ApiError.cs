using SeatReservation.Api.Infrastructure.Logging;

namespace SeatReservation.Api.Infrastructure.Errors;

public static class ApiError
{
    public static IResult Write(HttpContext context, int statusCode, string reason, string message)
    {
        var requestId = context.Items.TryGetValue(RequestIdMiddleware.ItemsKey, out var value) ? value as string : null;
        return Results.Json(
            new { error = reason, message, request_id = requestId ?? string.Empty },
            statusCode: statusCode);
    }
}
