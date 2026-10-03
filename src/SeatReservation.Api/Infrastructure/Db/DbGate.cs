namespace SeatReservation.Api.Infrastructure.Db;

// Caps how many requests do DB work at once (D-14), so a burst queues here in-process instead of
// failing on connection-pool acquisition. Throttling only, never correctness (I9): every
// decision still happens in MySQL under row locks and constraints.
public sealed class DbGate(int maxConcurrency)
{
    private const int DefaultMaxConcurrency = 50;

    private readonly SemaphoreSlim _semaphore = new(maxConcurrency, maxConcurrency);

    public int MaxConcurrency { get; } = maxConcurrency;

    public static DbGate FromConfiguration(IConfiguration configuration)
    {
        var raw = configuration["DB_MAX_CONCURRENCY"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new DbGate(DefaultMaxConcurrency);
        }

        if (!int.TryParse(raw, out var value) || value < 1)
        {
            throw new InvalidOperationException("DB_MAX_CONCURRENCY must be an integer of at least 1.");
        }

        return new DbGate(value);
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();
}
