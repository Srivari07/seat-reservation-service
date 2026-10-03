namespace Burst;

// Incremented from ApiClient's single request core, for every call kind (mint, show, reserve,
// cancel, metrics) - the assignment requires zero 5xx across the whole burst, not just reserves.
public sealed class GlobalErrorTally
{
    private long fiveXx;
    private long transportErrors;

    public long FiveXx => Interlocked.Read(ref fiveXx);

    public long TransportErrors => Interlocked.Read(ref transportErrors);

    public void RecordFiveXx() => Interlocked.Increment(ref fiveXx);

    public void RecordTransportError() => Interlocked.Increment(ref transportErrors);
}
