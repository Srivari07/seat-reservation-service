using Dapper;
using MySqlConnector;
using Prometheus;
using SeatReservation.Api.Health;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.Api.Infrastructure.Errors;

namespace SeatReservation.Api.Infrastructure.Metrics;

// Registered as a before-collect callback (Program.cs), so seats_available / seats_by_status /
// reconciliation_violation are computed from the DB at scrape time (05-observability.md), never
// tracked in memory. Scoped to the most recently created 50 shows, same cardinality note as the KB.
public sealed class ShowGaugeCollector(DbRunner db, AppMetrics metrics, MigrationsState migrationsState, ILogger<ShowGaugeCollector> logger)
{
    private const int MaxShows = 50;

    // prometheus-net may run concurrent scrapes' before-collect callbacks concurrently (its own
    // docs say so); this is pure throttling of the DB work, not a correctness mechanism (I9) - the
    // gauge query itself takes no locks and decides nothing.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task CollectAsync(CancellationToken cancellationToken)
    {
        if (!migrationsState.IsCompleted)
        {
            // Not an error: this is the normal window before the first migration run finishes.
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<ShowStatusCountRow> rows;
            try
            {
                rows = await db.ReadAsync(async connection =>
                {
                    var result = await connection.QueryAsync<ShowStatusCountRow>(new CommandDefinition(
                        """
                        SELECT sh.show_id AS ShowId, sh.total_seats AS TotalSeats, se.status AS Status, COUNT(*) AS Cnt
                          FROM (SELECT show_id, total_seats FROM shows ORDER BY created_at DESC LIMIT @MaxShows) sh
                          JOIN seats se ON se.show_id = sh.show_id
                         GROUP BY sh.show_id, sh.total_seats, se.status
                        """,
                        new { MaxShows },
                        cancellationToken: cancellationToken));
                    return result.AsList();
                }, cancellationToken);
            }
            catch (DbUnavailableException ex)
            {
                logger.LogWarning(ex, "Gauge refresh skipped: database unreachable. Clearing seats_available/seats_by_status/reconciliation_violation.");
                ClearGauges();
                return;
            }
            catch (MySqlException ex)
            {
                logger.LogError(ex, "Gauge refresh failed. Clearing seats_available/seats_by_status/reconciliation_violation.");
                ClearGauges();
                throw;
            }

            // Stale series for a show that has fallen out of the top 50 are left as-is (same
            // cardinality note as 05-observability.md); a fresh scrape still only ever sets, never
            // removes, a show that's still in scope.
            foreach (var group in rows.GroupBy(row => row.ShowId))
            {
                var showId = group.Key.ToString();
                var totalSeats = group.First().TotalSeats;
                var available = 0;
                var held = 0;
                var confirmed = 0;
                foreach (var row in group)
                {
                    switch (row.Status)
                    {
                        case "available": available = row.Cnt; break;
                        case "held": held = row.Cnt; break;
                        case "confirmed": confirmed = row.Cnt; break;
                    }
                }

                metrics.SeatsAvailable.WithLabels(showId).Set(available);
                metrics.SeatsByStatus.WithLabels(showId, "available").Set(available);
                metrics.SeatsByStatus.WithLabels(showId, "held").Set(held);
                metrics.SeatsByStatus.WithLabels(showId, "confirmed").Set(confirmed);
                metrics.ReconciliationViolation.WithLabels(showId).Set(totalSeats - (available + held + confirmed));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // A frozen gauge after a failure would keep reporting its last (usually healthy-looking) value
    // - reconciliation_violation could sit at 0 throughout a real outage, defeating the "what pages
    // at 2 a.m." alert in 05-observability.md. Clearing removes the series instead, so an absent()
    // check (or just a stopped-moving seats_available) is the signal, not a stale number.
    private void ClearGauges()
    {
        Clear(metrics.SeatsAvailable);
        Clear(metrics.SeatsByStatus);
        Clear(metrics.ReconciliationViolation);

        static void Clear(Gauge gauge)
        {
            foreach (var labelValues in gauge.GetAllLabelValues().ToList())
            {
                gauge.RemoveLabelled(labelValues);
            }
        }
    }

    private sealed class ShowStatusCountRow
    {
        public Guid ShowId { get; set; }
        public int TotalSeats { get; set; }
        public string Status { get; set; } = string.Empty;
        public int Cnt { get; set; }
    }
}
