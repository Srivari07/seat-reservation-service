namespace SeatReservation.Api.Infrastructure.Migrations;

/// <summary>
/// Thrown when GET_LOCK('schema_migrations', ...) doesn't return 1 — either another
/// instance held it past the wait, or the server returned NULL (e.g. the query was
/// killed). Distinct from MySqlException so the hosted service can always retry this
/// one as transient, without having to treat every MySqlException that way.
/// </summary>
public sealed class MigrationLockUnavailableException(object? lockResult)
    : Exception($"GET_LOCK('schema_migrations', 60) did not return 1 (got: {lockResult ?? "null"}).");
