namespace Burst;

// General stampede: overlaps across users are intentional (exercises all-or-nothing). Correctness
// here is covered by the live InvariantSampler and the global 5xx/transport counts, not by a
// per-request assertion, so this scenario's own check is informational.
public static class ScenarioC_Stampede
{
    public static async Task RunAsync(BurstContext ctx, CancellationToken ct)
    {
        var users = ctx.TakeUsers(ctx.Options.Users);
        var pool = ctx.SeatPlan.StampedePool;

        var requests = users.Select(user =>
        {
            var seatCount = Random.Shared.NextDouble() < 0.3 ? 2 : 1;
            var seats = new HashSet<string>();
            for (var attempt = 0; attempt < 5 && seats.Count < seatCount; attempt++)
            {
                seats.Add(SeatPlan.PickBiased(pool));
            }

            return (User: user, Seats: seats.ToList(), Key: Guid.NewGuid().ToString());
        }).ToList();

        await ConcurrentRunner.RunAsync(requests, async (req, token) =>
        {
            var result = await ctx.Client.ReserveAsync(req.User.Token, ctx.ShowId, new ReserveRequest(req.Seats, req.Key), token);
            ctx.Record(result);
            return result;
        }, ct);

        ctx.Checks.Add(new CheckResult("General stampede", true, $"{requests.Count} requests issued across {pool.Count} seats"));
    }
}
