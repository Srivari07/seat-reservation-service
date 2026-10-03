using Serilog.Context;

namespace SeatReservation.Api.Infrastructure.Logging;

public sealed class RequestIdMiddleware(RequestDelegate next)
{
    private const string HeaderName = "X-Request-Id";

    // ApiError.Write reads this to embed request_id in the error body. It can't read the
    // response header directly: ExceptionHandlerMiddleware calls Response.Clear() before
    // invoking a handler for an exception thrown downstream, which would wipe a header set
    // eagerly here. context.Items survives that Clear(), so it's the one source of truth;
    // the header below is set via OnStarting purely so it still reaches the client either way.
    public const string ItemsKey = "RequestId";

    public async Task InvokeAsync(HttpContext context)
    {
        // Guards against a second registration on the same request (e.g. a test fixture that
        // mounts its own copy of this middleware for routes outside the real Program.cs
        // pipeline): without this, OnStarting callbacks run last-registered-first while Items
        // keeps the last write, so the header and body would end up carrying different ids.
        if (context.Items.ContainsKey(ItemsKey))
        {
            await next(context);
            return;
        }

        var requestId = context.Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : Guid.NewGuid().ToString("n");

        context.Items[ItemsKey] = requestId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("request_id", requestId))
        {
            await next(context);
        }
    }
}
