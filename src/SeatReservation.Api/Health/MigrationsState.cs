namespace SeatReservation.Api.Health;

/// <summary>
/// Tracks whether startup migrations have finished. Phase 1 has no migration runner yet,
/// so this stays true; Phase 2's runner will flip it false until migrations complete.
/// </summary>
public sealed class MigrationsState
{
    public volatile bool IsCompleted = true;
}
