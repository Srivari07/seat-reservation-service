namespace Burst;

// Cancel then rebook: a dedicated fresh user+seat (not reused from C/F) so this doesn't depend on
// C's random outcome or F's deliberately-never-cancelled reservation.
public static class ScenarioG_CancelRebook
{
    public static async Task RunAsync(BurstContext ctx, CancellationToken ct)
    {
        var owner = ctx.TakeUsers(1)[0];
        var rebooker = ctx.TakeUsers(1)[0];
        var seat = ctx.SeatPlan.TakeFromTail(1)[0];

        var reserveResult = await ctx.Client.ReserveAsync(owner.Token, ctx.ShowId, new ReserveRequest([seat], Guid.NewGuid().ToString()), ct);
        ctx.Record(reserveResult);
        if (!reserveResult.Success || reserveResult.Value is null)
        {
            ctx.Checks.Add(new CheckResult("Cancel + rebook", false, $"initial reserve failed: status={reserveResult.StatusCode}"));
            return;
        }

        var cancelResult = await ctx.Client.CancelAsync(owner.Token, reserveResult.Value.ReservationId, ct);
        if (!cancelResult.Success || cancelResult.Value is null)
        {
            ctx.Checks.Add(new CheckResult("Cancel + rebook", false, $"cancel failed: status={cancelResult.StatusCode}"));
            return;
        }

        ctx.RecordCancelled(cancelResult.Value);

        var rebookResult = await ctx.Client.ReserveAsync(rebooker.Token, ctx.ShowId, new ReserveRequest([seat], Guid.NewGuid().ToString()), ct);
        ctx.Record(rebookResult);

        var passed = rebookResult.Success && !rebookResult.Replayed;
        ctx.Checks.Add(new CheckResult(
            "Cancel + rebook",
            passed,
            $"cancel status={cancelResult.StatusCode}, rebook status={rebookResult.StatusCode}"));
    }
}
