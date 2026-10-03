namespace Burst;

// I3 (available+held+confirmed==total) on a final read, client-tallied confirmed seats against the
// API's own count, and a /metrics diff against A's baseline per outcome bucket.
public static class ScenarioH_Reconciliation
{
    public static async Task RunAsync(BurstContext ctx, InvariantSampler sampler, CancellationToken ct)
    {
        await sampler.SampleOnceAsync(ct);

        var showResult = await ctx.Client.GetShowAsync(ctx.ShowId, ct);
        var show = showResult.Success ? showResult.Value : null;

        if (show is null)
        {
            ctx.Checks.Add(new CheckResult("Reconciliation (GET /shows/{id})", false, $"could not fetch show: status={showResult.StatusCode}"));
        }
        else
        {
            var counts = show.Counts;
            var sumOk = counts.Available + counts.Held + counts.Confirmed == show.TotalSeats;
            // Raw counts, not distinct: a seat that's confirmed, cancelled, then reconfirmed (scenario
            // G's rebook) appears twice in ConfirmedSeats, and each add is a real event that must net
            // out against its own cancel, not collapse into one.
            var clientConfirmed = ctx.ConfirmedSeats.Count - ctx.CancelledSeats.Count;
            var countOk = clientConfirmed == counts.Confirmed;

            ctx.Checks.Add(new CheckResult(
                "Reconciliation (GET /shows/{id})",
                sumOk && countOk,
                $"available={counts.Available} held={counts.Held} confirmed={counts.Confirmed} total={show.TotalSeats}; client-tallied confirmed={clientConfirmed}"));
        }

        var metricsText = await ctx.Client.ScrapeMetricsAsync(ct);
        if (string.IsNullOrWhiteSpace(metricsText))
        {
            ctx.Checks.Add(new CheckResult("Metrics reconcile", false, "could not scrape /metrics"));
            return;
        }

        var after = MetricsSnapshot.Parse(metricsText);
        var before = ctx.BaselineMetrics;
        var showId = ctx.ShowId;
        var tally = ctx.Tally;

        double Delta(string name, string? reason = null) => after.Counter(name, showId, reason) - before.Counter(name, showId, reason);

        var confirmedDelta = Delta("reservations_confirmed_total");
        var seatTakenDelta = Delta("reservations_declined_total", "seat_taken");
        var perUserLimitDelta = Delta("reservations_declined_total", "per_user_limit");
        var mismatchDelta = Delta("reservations_declined_total", "idempotency_mismatch");
        var replayDelta = Delta("reservations_declined_total", "idempotent_replay");
        var contentionDelta = Delta("reservations_declined_total", "contention");
        var cancelledDelta = Delta("reservations_cancelled_total");
        var seatsAvailableNow = after.Gauge("seats_available", showId);
        var cancelledSeatCount = ctx.CancelledSeats.Count;

        var metricsOk =
            confirmedDelta == tally.Confirmed &&
            seatTakenDelta == tally.SeatTaken &&
            perUserLimitDelta == tally.PerUserLimit &&
            mismatchDelta == tally.IdempotencyMismatch &&
            replayDelta == tally.Replayed &&
            contentionDelta == tally.Contention &&
            cancelledDelta == cancelledSeatCount &&
            (show is null || Math.Abs(seatsAvailableNow - show.Counts.Available) < 0.5);

        ctx.Checks.Add(new CheckResult(
            "Metrics reconcile",
            metricsOk,
            $"confirmed Δ{confirmedDelta}/{tally.Confirmed}, seat_taken Δ{seatTakenDelta}/{tally.SeatTaken}, " +
            $"per_user_limit Δ{perUserLimitDelta}/{tally.PerUserLimit}, idempotency_mismatch Δ{mismatchDelta}/{tally.IdempotencyMismatch}, " +
            $"idempotent_replay Δ{replayDelta}/{tally.Replayed}, contention Δ{contentionDelta}/{tally.Contention}, " +
            $"cancelled Δ{cancelledDelta} (client={cancelledSeatCount}), seats_available now={seatsAvailableNow}"));
    }
}
