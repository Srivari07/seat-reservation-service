namespace SeatReservation.Api.Infrastructure.Errors;

public static class ApiError
{
    public static IResult Write(HttpContext context, int statusCode, string reason, string message)
    {
        var requestId = context.Response.Headers["X-Request-Id"].ToString();
        return Results.Json(
            new { error = reason, message, request_id = requestId },
            statusCode: statusCode);
    }
}
