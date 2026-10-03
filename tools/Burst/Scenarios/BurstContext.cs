using System.Collections.Concurrent;

namespace Burst;

// Mutable state threaded through scenarios A->H. ConfirmedSeats/CancelledSeats are seat ids (not
// reservation ids): a seat can only ever be confirmed by one reservation at a time, so they're
// enough on their own for scenario H to reconcile against the API's own confirmed count.
public sealed class BurstContext
{
    public required ApiClient Client { get; init; }

    public required BurstOptions Options { get; init; }

    public required string AdminToken { get; init; }

    public required IReadOnlyList<(string UserId, string Token)> UserPool { get; init; }

    public required string ShowId { get; init; }

    public required int TotalSeats { get; init; }

    public required int PerUserLimit { get; init; }

    public required SeatPlan SeatPlan { get; init; }

    public required MetricsSnapshot BaselineMetrics { get; init; }

    public required GlobalErrorTally GlobalErrors { get; init; }

    public ReserveOutcomeTally Tally { get; } = new();

    public ConcurrentBag<string> ConfirmedSeats { get; } = new();

    public ConcurrentBag<string> CancelledSeats { get; } = new();

    public ConcurrentBag<CheckResult> Checks { get; } = new();

    public string HotSeatSummary { get; set; } = string.Empty;

    public InvariantSampler? Sampler { get; set; }

    private int nextUserIndex;

    // Thread-safe: hands out disjoint slices of the pre-minted user pool so concurrent scenarios
    // never share a user id.
    public IReadOnlyList<(string UserId, string Token)> TakeUsers(int count)
    {
        var start = Interlocked.Add(ref nextUserIndex, count) - count;
        if (start < 0 || start + count > UserPool.Count)
        {
            throw new BurstFatalException("Exhausted the pre-minted user token pool — increase --users or the pool sizing in Program.cs");
        }

        return UserPool.Skip(start).Take(count).ToList();
    }

    public void Record(ReserveResult result)
    {
        Tally.Record(result);
        if (result.Success && !result.Replayed && result.Value is not null)
        {
            RecordConfirmed(result.Value);
        }
    }

    public void RecordConfirmed(ReservationResponse reservation)
    {
        foreach (var seat in reservation.Seats)
        {
            ConfirmedSeats.Add(seat);
        }
    }

    public void RecordCancelled(ReservationResponse reservation)
    {
        foreach (var seat in reservation.Seats)
        {
            CancelledSeats.Add(seat);
        }
    }
}
