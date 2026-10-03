namespace Burst;

// Polls GET /shows/{id} on a background loop (started before scenario B, stopped after C) and
// checks available+held+confirmed==total_seats on every sample. Scenario H folds one more sample
// (its own reconciliation read) into the same counters via SampleOnceAsync.
public sealed class InvariantSampler
{
    private readonly ApiClient client;
    private readonly string showId;
    private readonly TimeSpan interval;

    private CancellationTokenSource? cts;
    private Task? loopTask;

    private long sampleCount;
    private long violationCount;

    public InvariantSampler(ApiClient client, string showId, TimeSpan? interval = null)
    {
        this.client = client;
        this.showId = showId;
        this.interval = interval ?? TimeSpan.FromMilliseconds(200);
    }

    public long SampleCount => Interlocked.Read(ref sampleCount);

    public long ViolationCount => Interlocked.Read(ref violationCount);

    public ShowCounts? LastCounts { get; private set; }

    public int TotalSeats { get; private set; }

    public void Start()
    {
        cts = new CancellationTokenSource();
        loopTask = Task.Run(() => LoopAsync(cts.Token));
    }

    public async Task StopAsync()
    {
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await (loopTask ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public Task SampleOnceAsync(CancellationToken ct) => TakeSampleAsync(ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await TakeSampleAsync(ct);
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TakeSampleAsync(CancellationToken ct)
    {
        var result = await client.GetShowAsync(showId, ct);
        if (!result.Success || result.Value is null)
        {
            // Already tallied as a 5xx/transport error by ApiClient; don't double-count it here.
            return;
        }

        Interlocked.Increment(ref sampleCount);
        TotalSeats = result.Value.TotalSeats;
        LastCounts = result.Value.Counts;

        var sum = result.Value.Counts.Available + result.Value.Counts.Held + result.Value.Counts.Confirmed;
        if (sum != result.Value.TotalSeats)
        {
            Interlocked.Increment(ref violationCount);
        }
    }
}
