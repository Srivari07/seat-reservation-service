using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

namespace SeatReservation.Api.Health;

public sealed class MySqlReadyHealthCheck(MigrationsState migrationsState, IConfiguration configuration)
    : IHealthCheck
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!migrationsState.IsCompleted)
        {
            return HealthCheckResult.Unhealthy("migrations not completed");
        }

        var connectionString = configuration.GetConnectionString("Mysql");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("ConnectionStrings__Mysql is not set");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(QueryTimeout);

        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(timeoutCts.Token);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(timeoutCts.Token);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is MySqlException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("mysql unreachable", ex);
        }
    }
}
