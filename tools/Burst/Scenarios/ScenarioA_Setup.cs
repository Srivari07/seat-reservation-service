namespace Burst;

public static class ScenarioA_Setup
{
    public static async Task<BurstContext> RunAsync(BurstOptions options, ApiClient client, GlobalErrorTally globalErrors, CancellationToken ct)
    {
        var adminToken = await TokenMinter.MintAdminAsync(client, options.AdminSecret, ct);

        // One flat pool sized for everything downstream (B's storm + C's stampede + a handful for
        // D/E/F/G), so later scenarios just draw disjoint slices by index instead of re-deriving
        // counts in four places.
        var poolSize = options.Users + (options.HotSeats * options.Storm) + 30;
        var userPool = await TokenMinter.MintUserPoolAsync(client, poolSize, ct);

        var seatPlan = SeatPlan.Generate(options.Seats, options.HotSeats);

        var showRequest = new CreateShowRequest(
            Name: $"burst-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}",
            Seats: seatPlan.AllSeats.ToList(),
            PricePaise: 2500,
            PerUserLimit: 4);

        var showResult = await client.CreateShowAsync(adminToken, showRequest, ct);
        if (!showResult.Success || showResult.Value is null)
        {
            var detail = showResult.TransportFailed
                ? $"transport error: {showResult.TransportError}"
                : $"{showResult.StatusCode} {showResult.Error?.Error} {showResult.Error?.Message}";
            throw new BurstFatalException($"Could not create the burst show: {detail}");
        }

        var show = showResult.Value;

        var metricsText = await client.ScrapeMetricsAsync(ct);
        if (string.IsNullOrWhiteSpace(metricsText))
        {
            throw new BurstFatalException("Could not scrape the baseline /metrics (transport failure)");
        }

        return new BurstContext
        {
            Client = client,
            Options = options,
            AdminToken = adminToken,
            UserPool = userPool,
            ShowId = show.ShowId,
            TotalSeats = show.TotalSeats,
            PerUserLimit = show.PerUserLimit,
            SeatPlan = seatPlan,
            BaselineMetrics = MetricsSnapshot.Parse(metricsText),
            GlobalErrors = globalErrors,
        };
    }
}
