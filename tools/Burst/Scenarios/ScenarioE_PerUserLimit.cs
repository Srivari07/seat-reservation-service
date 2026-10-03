namespace Burst;

// C5: one user fires 10 parallel single-seat reserves against a limit=4 show -> at most 4 succeed.
public static class ScenarioE_PerUserLimit
{
    public static async Task RunAsync(BurstContext ctx, CancellationToken ct)
    {
        var user = ctx.TakeUsers(1)[0];
        var seats = ctx.SeatPlan.TakeFromTail(10);
        var requests = seats.Select(seat => (Seat: seat, Key: Guid.NewGuid().ToString())).ToList();

        var results = await ConcurrentRunner.RunAsync(requests, async (req, token) =>
        {
            var result = await ctx.Client.ReserveAsync(user.Token, ctx.ShowId, new ReserveRequest([req.Seat], req.Key), token);
            ctx.Record(result);
            return result;
        }, ct);

        var created = results.Count(r => r.Success && !r.Replayed);
        var limited = results.Count(r => r.StatusCode == 409 && r.Error?.Error == "per_user_limit");
        var contention = results.Count(r => r.StatusCode == 409 && r.Error?.Error == "contention");

        var passed = created <= ctx.PerUserLimit && created + limited + contention == results.Count;
        ctx.Checks.Add(new CheckResult(
            "Per-user limit",
            passed,
            $"created={created} (limit={ctx.PerUserLimit}), per_user_limit={limited}, contention={contention}, total={results.Count}"));
    }
}
