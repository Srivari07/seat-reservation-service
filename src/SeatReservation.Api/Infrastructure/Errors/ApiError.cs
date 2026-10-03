using SeatReservation.Api.Infrastructure.Logging;

namespace SeatReservation.Api.Infrastructure.Errors;

public static class ApiError
{
    public static IResult Write(HttpContext context, int statusCode, string reason, string message)
    {
        return Results.Json(
            new { error = reason, message, request_id = RequestId(context) },
            statusCode: statusCode);
    }

    // For reasons that carry extra fields (03-api-contract.md), e.g. contention's "retryable".
    // Keys are written as given (the snake_case policy doesn't rename dictionary keys), and Add
    // throws on a key that collides with error/message/request_id rather than silently replacing it.
    public static IResult Write(
        HttpContext context, int statusCode, string reason, string message, IReadOnlyDictionary<string, object?> extra)
    {
        var body = new Dictionary<string, object?>
        {
            ["error"] = reason,
            ["message"] = message,
            ["request_id"] = RequestId(context),
        };
        foreach (var (key, value) in extra)
        {
            body.Add(key, value);
        }

        return Results.Json(body, statusCode: statusCode);
    }

    private static string RequestId(HttpContext context) =>
        context.Items.TryGetValue(RequestIdMiddleware.ItemsKey, out var value) ? value as string ?? string.Empty : string.Empty;
}
