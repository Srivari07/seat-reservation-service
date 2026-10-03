namespace SeatReservation.Api.Infrastructure.Errors;

// Thrown by DbRunner.WriteAsync when every attempt hit a deadlock (1213) or lock wait timeout
// (1205). Caught by ApiExceptionHandler and mapped to 409 contention, retryable (D-12): we don't
// know the outcome would have been a decline, so we don't claim the seat is taken.
public sealed class DbContentionException(Exception inner) : Exception("Database contention: retries exhausted.", inner);
