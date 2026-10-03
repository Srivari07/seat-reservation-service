using Prometheus;

namespace SeatReservation.Api.Infrastructure.Metrics;

// All metrics live in a registry owned by this instance, not Metrics.DefaultRegistry: a
// WebApplicationFactory-based test boots many hosts in one process (see ApiHostCollection), and the
// static default registry's before-collect callbacks are never removed - a disposed host's callback
// would still fire (and fail) on a later host's scrape. One registry per app instance keeps every
// host's metrics, and ShowGaugeCollector's callback, fully isolated.
public sealed class AppMetrics
{
    public CollectorRegistry Registry { get; }

    public Counter ReservationsConfirmedTotal { get; }
    public Counter ReservationsDeclinedTotal { get; }
    public Counter ReservationsCancelledTotal { get; }
    public Counter DbTxRetriesTotal { get; }
    public Gauge SeatsAvailable { get; }
    public Gauge SeatsByStatus { get; }
    public Gauge ReconciliationViolation { get; }

    public AppMetrics()
    {
        Registry = Prometheus.Metrics.NewCustomRegistry();
        var factory = Prometheus.Metrics.WithCustomRegistry(Registry);

        ReservationsConfirmedTotal = factory.CreateCounter(
            "reservations_confirmed_total", "Reservations confirmed, after commit.", ["show_id"]);
        ReservationsDeclinedTotal = factory.CreateCounter(
            "reservations_declined_total", "Reservations declined, after rollback.", ["show_id", "reason"]);
        ReservationsCancelledTotal = factory.CreateCounter(
            "reservations_cancelled_total", "Reservations cancelled, after commit.", ["show_id"]);
        DbTxRetriesTotal = factory.CreateCounter(
            "db_tx_retries_total", "DB transaction attempts that failed with a deadlock or lock-wait timeout.", ["error"]);
        SeatsAvailable = factory.CreateGauge(
            "seats_available", "Available seats for a show, computed from the DB at scrape time.", ["show_id"]);
        SeatsByStatus = factory.CreateGauge(
            "seats_by_status", "Seats by status for a show, computed from the DB at scrape time.", ["show_id", "status"]);
        ReconciliationViolation = factory.CreateGauge(
            "reconciliation_violation", "total_seats - (available+held+confirmed); must always be 0 (I3).", ["show_id"]);
    }
}
