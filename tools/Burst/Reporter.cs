namespace Burst;

public static class Reporter
{
    public static int PrintAndGetExitCode(BurstContext ctx)
    {
        var tally = ctx.Tally;
        var errors = ctx.GlobalErrors;

        Console.WriteLine("Outcome distribution");
        Console.WriteLine($"  201 confirmed ........ {tally.Confirmed}");
        Console.WriteLine($"  201 replayed ......... {tally.Replayed}");
        Console.WriteLine($"  409 seat_taken ....... {tally.SeatTaken}");
        Console.WriteLine($"  409 per_user_limit ... {tally.PerUserLimit}");
        Console.WriteLine($"  409 idempotency_mismatch {tally.IdempotencyMismatch}");
        Console.WriteLine($"  409 contention ....... {tally.Contention}");
        Console.WriteLine($"  4xx other ............ {tally.Other4xx}");
        Console.WriteLine($"  5xx .................. {errors.FiveXx}   <-- must be 0");
        Console.WriteLine($"  transport errors ..... {errors.TransportErrors}");

        Console.WriteLine($"Hot seats: {ctx.HotSeatSummary}");

        var sampler = ctx.Sampler;
        var counts = sampler?.LastCounts;
        var invariantPassed = (sampler?.ViolationCount ?? 0) == 0;
        var invariantMark = invariantPassed ? "✔" : "✖";
        Console.WriteLine(
            $"Invariant: available {counts?.Available ?? 0} + held {counts?.Held ?? 0} + confirmed {counts?.Confirmed ?? 0} " +
            $"= {sampler?.TotalSeats ?? 0} {invariantMark} (and {sampler?.ViolationCount ?? 0} violations in {sampler?.SampleCount ?? 0} live samples)");

        var metricsCheck = ctx.Checks.FirstOrDefault(c => c.Name == "Metrics reconcile");
        Console.WriteLine($"Metrics reconcile: {(metricsCheck is { Passed: true } ? "✔" : "✖")}");

        var failed = ctx.Checks.Where(c => !c.Passed).ToList();
        var allPassed = failed.Count == 0 && errors.FiveXx == 0 && invariantPassed;

        if (failed.Count > 0)
        {
            Console.WriteLine("Failed checks:");
            foreach (var check in failed)
            {
                Console.WriteLine($"  - {check.Name}: {check.Detail}");
            }
        }

        Console.WriteLine(allPassed ? "RESULT: PASS" : "RESULT: FAIL");
        return allPassed ? 0 : 1;
    }
}
