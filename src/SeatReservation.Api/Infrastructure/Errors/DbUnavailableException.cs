namespace SeatReservation.Api.Infrastructure.Errors;

// Thrown when a connection cannot be opened (MySQL unreachable). Caught by ApiExceptionHandler
// and mapped to 503 db_unavailable (D-13: fail closed, never 500 on a real outage).
public sealed class DbUnavailableException(Exception inner) : Exception("The database is unreachable.", inner);
