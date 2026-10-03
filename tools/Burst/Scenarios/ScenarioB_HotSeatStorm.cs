namespace Burst;

// C1: 500 users storm one hot seat - exactly one 201, the rest 409. All hot seats are stormed
// together under one start gate, which also gives the running InvariantSampler (B+C) real
// contention to observe.
public static class ScenarioB_HotSeatStorm
{
    public static async Task RunAsync(BurstContext ctx, CancellationToken ct)
    {
        var hotSeats = ctx.SeatPlan.HotSeats;
        var usersNeeded = hotSeats.Count * ctx.Options.Storm;
        var users = ctx.TakeUsers(usersNeeded);

        var requests = new List<(string Seat, string Token, string IdempotencyKey)>(usersNeeded);
        var userIndex = 0;
        foreach (var seat in hotSeats)
        {
            for (var i = 0; i < ctx.Options.Storm; i++)
            {
                requests.Add((seat, users[userIndex++].Token, Guid.NewGuid().ToString()));
            }
        }

        var results = await ConcurrentRunner.RunAsync(requests, async (req, token) =>
        {
            var result = await ctx.Client.ReserveAsync(req.Token, ctx.ShowId, new ReserveRequest([req.Seat], req.IdempotencyKey), token);
            ctx.Record(result);
            return (req.Seat, Result: result);
        }, ct);

        var summaries = new List<string>();
        var allPassed = true;
        foreach (var seat in hotSeats)
        {
            var winners = results.Count(r => r.Seat == seat && r.Result.Success && !r.Result.Replayed);
            var passed = winners == 1;
            allPassed &= passed;
            summaries.Add(passed ? $"{seat} ✔ 1 winner" : $"{seat} ✖ {winners} winners");
        }

        ctx.HotSeatSummary = string.Join(" | ", summaries);
        ctx.Checks.Add(new CheckResult("Hot seats", allPassed, ctx.HotSeatSummary));
    }
}
