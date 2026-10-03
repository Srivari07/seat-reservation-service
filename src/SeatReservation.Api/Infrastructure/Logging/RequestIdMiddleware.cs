using Serilog.Context;

namespace SeatReservation.Api.Infrastructure.Logging;

public sealed class RequestIdMiddleware(RequestDelegate next)
{
    private const string HeaderName = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : Guid.NewGuid().ToString("n");

        context.Response.Headers[HeaderName] = requestId;

        using (LogContext.PushProperty("request_id", requestId))
        {
            await next(context);
        }
    }
}
