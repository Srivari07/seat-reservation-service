namespace Burst;

// Generates seat ids and partitions them so scenarios never collide over the same seat:
// - HotSeats: stormed by scenario B.
// - StampedePool: sampled (front-row biased) by scenario C.
// - ReservedTail: untouched by B/C, drawn from (via TakeFromTail) by scenarios D/E/F/G so their
//   assertions are deterministic regardless of how much of the pool B/C randomly consumed.
public sealed class SeatPlan
{
    private const int RowWidth = 40;

    private int tailCursor;

    public IReadOnlyList<string> AllSeats { get; }

    public IReadOnlyList<string> HotSeats { get; }

    public IReadOnlyList<string> StampedePool { get; }

    public IReadOnlyList<string> ReservedTail { get; }

    private SeatPlan(
        IReadOnlyList<string> allSeats,
        IReadOnlyList<string> hotSeats,
        IReadOnlyList<string> stampedePool,
        IReadOnlyList<string> reservedTail)
    {
        AllSeats = allSeats;
        HotSeats = hotSeats;
        StampedePool = stampedePool;
        ReservedTail = reservedTail;
    }

    public static SeatPlan Generate(int seatCount, int hotSeatCount)
    {
        var all = new List<string>(seatCount);
        for (var i = 0; i < seatCount; i++)
        {
            var row = i / RowWidth;
            var column = i % RowWidth + 1;
            all.Add($"{RowLetter(row)}{column}");
        }

        var hotSeats = all.Take(hotSeatCount).ToList();
        var reservedTailSize = Math.Max(20, hotSeatCount + 40);
        var reservedTail = all.Skip(seatCount - reservedTailSize).ToList();
        var stampedePool = all.Skip(hotSeatCount).Take(seatCount - hotSeatCount - reservedTailSize).ToList();

        return new SeatPlan(all, hotSeats, stampedePool, reservedTail);
    }

    // Thread-safe: carves out `count` disjoint, never-before-returned seats from the reserved tail.
    public IReadOnlyList<string> TakeFromTail(int count)
    {
        var start = Interlocked.Add(ref tailCursor, count) - count;
        if (start < 0 || start + count > ReservedTail.Count)
        {
            throw new BurstFatalException("Exhausted the reserved-tail seat pool — increase --seats or --hot-seats");
        }

        return ReservedTail.Skip(start).Take(count).ToList();
    }

    // Biases strongly towards the front of the pool (low index = "front row").
    public static string PickBiased(IReadOnlyList<string> pool)
    {
        var skewed = Math.Pow(Random.Shared.NextDouble(), 3);
        var index = (int)(pool.Count * skewed);
        return pool[Math.Min(index, pool.Count - 1)];
    }

    // Spreadsheet-style column letters: 0->A, 25->Z, 26->AA, 51->AZ, 52->BA, ...
    private static string RowLetter(int index)
    {
        var oneBased = index + 1;
        var letters = string.Empty;
        while (oneBased > 0)
        {
            var remainder = (oneBased - 1) % 26;
            letters = (char)('A' + remainder) + letters;
            oneBased = (oneBased - 1) / 26;
        }

        return letters;
    }
}
