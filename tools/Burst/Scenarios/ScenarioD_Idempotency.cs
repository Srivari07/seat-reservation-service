namespace Burst;

// C4: same key + body fired 20x in parallel -> one reservation, 19 replays, zero extra seats.
// Then the same key with a different seat -> 409 idempotency_mismatch.
public static class ScenarioD_Idempotency
{
    public static async Task RunAsync(BurstContext ctx, CancellationToken ct)
    {
        var user = ctx.TakeUsers(1)[0];
        var seat = ctx.SeatPlan.TakeFromTail(1)[0];
        var key = Guid.NewGuid().ToString();

        var results = await ConcurrentRunner.RunAsync(Enumerable.Range(0, 20).ToList(), async (_, token) =>
        {
            var result = await ctx.Client.ReserveAsync(user.Token, ctx.ShowId, new ReserveRequest([seat], key), token);
            ctx.Record(result);
            return result;
        }, ct);

        var created = results.Count(r => r.Success && !r.Replayed);
        var replayed = results.Count(r => r.Success && r.Replayed);
        var reservationIds = results.Where(r => r.Success && r.Value is not null).Select(r => r.Value!.ReservationId).Distinct().Count();

        var passed = created == 1 && replayed == 19 && reservationIds == 1;
        ctx.Checks.Add(new CheckResult(
            "Idempotency (20x parallel replay)",
            passed,
            $"created={created}, replayed={replayed}, distinct reservation ids={reservationIds}"));

        var mismatchSeat = ctx.SeatPlan.TakeFromTail(1)[0];
        var mismatch = await ctx.Client.ReserveAsync(user.Token, ctx.ShowId, new ReserveRequest([mismatchSeat], key), ct);
        ctx.Record(mismatch);

        var mismatchPassed = mismatch.StatusCode == 409 && mismatch.Error?.Error == "idempotency_mismatch";
        ctx.Checks.Add(new CheckResult(
            "Idempotency (different seats, same key)",
            mismatchPassed,
            $"status={mismatch.StatusCode}, reason={mismatch.Error?.Error}"));
    }
}
