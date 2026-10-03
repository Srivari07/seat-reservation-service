using Microsoft.Extensions.Logging.Abstractions;
using SeatReservation.Api.Health;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.Api.Infrastructure.Metrics;

namespace SeatReservation.IntegrationTests.Metrics;

/// <summary>
/// Needs no real MySQL (the point is proving behaviour when there isn't one) and no
/// WebApplicationFactory host, so unlike the rest of the suite this runs with no Docker
/// dependency at all - same "unreachable DB" connection-string pattern as
/// Db/DbRunnerTests.UnreachableDatabase_ThrowsDbUnavailable_ForReadsAndWrites.
/// </summary>
public sealed class ShowGaugeCollectorTests
{
    [Fact]
    public async Task CollectAsync_WhenMySqlIsUnreachable_ClearsGauges()
    {
        var metrics = new AppMetrics();
        var db = new DbRunner(
            "Server=127.0.0.1;Port=1;Database=seats;User ID=app;Password=x;Connection Timeout=1",
            new DbGate(1),
            NullLogger<DbRunner>.Instance,
            metrics);
        var collector = new ShowGaugeCollector(
            db, metrics, new MigrationsState { IsCompleted = true }, NullLogger<ShowGaugeCollector>.Instance);

        // A prior, successful scrape left a value behind.
        metrics.SeatsAvailable.WithLabels("some-show").Set(3);

        await collector.CollectAsync(CancellationToken.None);

        Assert.Empty(metrics.SeatsAvailable.GetAllLabelValues());
        Assert.Empty(metrics.SeatsByStatus.GetAllLabelValues());
        Assert.Empty(metrics.ReconciliationViolation.GetAllLabelValues());
    }

    [Fact]
    public async Task CollectAsync_BeforeMigrationsComplete_SkipsSilentlyWithoutTouchingGauges()
    {
        var metrics = new AppMetrics();
        var db = new DbRunner(
            "Server=127.0.0.1;Port=1;Database=seats;User ID=app;Password=x;Connection Timeout=1",
            new DbGate(1),
            NullLogger<DbRunner>.Instance,
            metrics);
        var collector = new ShowGaugeCollector(
            db, metrics, new MigrationsState { IsCompleted = false }, NullLogger<ShowGaugeCollector>.Instance);

        metrics.SeatsAvailable.WithLabels("some-show").Set(3);

        await collector.CollectAsync(CancellationToken.None);

        // Not a failure - just the normal window before the first migration run finishes - so
        // nothing is cleared (there was never anything in the DB to disagree with).
        Assert.Equal(3, metrics.SeatsAvailable.WithLabels("some-show").Value);
    }
}
