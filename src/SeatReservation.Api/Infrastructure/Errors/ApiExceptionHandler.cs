using Microsoft.AspNetCore.Diagnostics;

namespace SeatReservation.Api.Infrastructure.Errors;

// Maps specific, expected exception types to the contract's {error,message,request_id} body
// (03-api-contract.md: "Error body (all errors)"). Anything else falls through unhandled (returns
// false) and stays a genuine 500 - this must never grow a catch-all branch that turns an
// unexpected bug into a fake 2xx/4xx/503.
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        IResult result;
        switch (exception)
        {
            case BadHttpRequestException:
                logger.LogInformation(exception, "Rejecting malformed request body.");
                result = ApiError.Write(httpContext, StatusCodes.Status400BadRequest, "invalid_request", "The request body is malformed or has the wrong shape.");
                break;
            case DbUnavailableException:
                // A .NET IExceptionHandler that returns true suppresses the framework's own
                // unhandled-exception log, so without this the MySQL-unreachable cause would
                // never appear anywhere - just a 503 with no trace of why.
                logger.LogWarning(exception, "Database unreachable; returning 503.");
                result = ApiError.Write(httpContext, StatusCodes.Status503ServiceUnavailable, "db_unavailable", "The service is not ready yet.");
                break;
            default:
                return false;
        }

        await result.ExecuteAsync(httpContext);
        return true;
    }
}
