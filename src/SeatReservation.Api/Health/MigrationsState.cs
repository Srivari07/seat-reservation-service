namespace SeatReservation.Api.Health;

/// <summary>
/// Tracks whether startup migrations have finished. False until
/// MigrationRunnerHostedService completes the first successful run.
/// </summary>
public sealed class MigrationsState
{
    public volatile bool IsCompleted;
}
