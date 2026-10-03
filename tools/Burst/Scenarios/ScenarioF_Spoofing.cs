namespace Burst;

// C6: a spoofed user_id in the body is ignored (identity is token-derived); a non-owner's cancel
// on the resulting reservation gets 404, not a silent success or a leak of its existence.
public static class ScenarioF_Spoofing
{
    public static async Task RunAsync(BurstContext ctx, CancellationToken ct)
    {
        var owner = ctx.TakeUsers(1)[0];
        var otherUser = ctx.TakeUsers(1)[0];
        var seat = ctx.SeatPlan.TakeFromTail(1)[0];
        var key = Guid.NewGuid().ToString();

        var spoofedBody = new { seats = new[] { seat }, idempotency_key = key, user_id = "someone-else" };
        var result = await ctx.Client.ReserveAsync(owner.Token, ctx.ShowId, spoofedBody, ct);
        ctx.Record(result);

        var identityPassed = result.Success && result.Value is not null && result.Value.UserId == owner.UserId;
        ctx.Checks.Add(new CheckResult(
            "Spoofed user_id ignored",
            identityPassed,
            $"response user_id={result.Value?.UserId}, token user_id={owner.UserId}"));

        if (!identityPassed || result.Value is null)
        {
            ctx.Checks.Add(new CheckResult("Owner-only cancel (404 for others)", false, "skipped: the reservation was never created"));
            return;
        }

        var cancelResult = await ctx.Client.CancelAsync(otherUser.Token, result.Value.ReservationId, ct);
        var cancelPassed = cancelResult.StatusCode == 404 && cancelResult.Error?.Error == "reservation_not_found";
        ctx.Checks.Add(new CheckResult(
            "Owner-only cancel (404 for others)",
            cancelPassed,
            $"status={cancelResult.StatusCode}, reason={cancelResult.Error?.Error}"));
    }
}
