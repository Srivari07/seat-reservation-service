namespace Burst;

// Fed from exactly one place (BurstContext.Record), so no scenario can forget to tally a reserve
// call. Counts only reserve-call outcomes; 5xx/transport errors are global (see GlobalErrorTally).
public sealed class ReserveOutcomeTally
{
    private long confirmed;
    private long replayed;
    private long seatTaken;
    private long perUserLimit;
    private long idempotencyMismatch;
    private long contention;
    private long other4xx;

    public long Confirmed => Interlocked.Read(ref confirmed);

    public long Replayed => Interlocked.Read(ref replayed);

    public long SeatTaken => Interlocked.Read(ref seatTaken);

    public long PerUserLimit => Interlocked.Read(ref perUserLimit);

    public long IdempotencyMismatch => Interlocked.Read(ref idempotencyMismatch);

    public long Contention => Interlocked.Read(ref contention);

    public long Other4xx => Interlocked.Read(ref other4xx);

    public void Record(ReserveResult result)
    {
        if (result.TransportFailed)
        {
            return;
        }

        if (result.StatusCode is >= 200 and < 300)
        {
            if (result.Replayed)
            {
                Interlocked.Increment(ref replayed);
            }
            else
            {
                Interlocked.Increment(ref confirmed);
            }

            return;
        }

        if (result.StatusCode == 409)
        {
            switch (result.Error?.Error)
            {
                case "seat_taken": Interlocked.Increment(ref seatTaken); return;
                case "per_user_limit": Interlocked.Increment(ref perUserLimit); return;
                case "idempotency_mismatch": Interlocked.Increment(ref idempotencyMismatch); return;
                case "contention": Interlocked.Increment(ref contention); return;
            }
        }

        if (result.StatusCode is >= 400 and < 500)
        {
            Interlocked.Increment(ref other4xx);
        }
    }
}
