namespace SeatReservation.Api.Infrastructure.Db;

// What a DbRunner.WriteAsync callback returns: a value, plus whether to COMMIT or ROLL BACK.
// A domain decline (seat_taken, per_user_limit) must roll back statements that already
// succeeded, yet still hand its outcome back to the caller.
public readonly record struct TxResult<T>(T Value, bool ShouldCommit);

public static class TxResult
{
    public static TxResult<T> Commit<T>(T value) => new(value, ShouldCommit: true);

    public static TxResult<T> Rollback<T>(T value) => new(value, ShouldCommit: false);
}
