using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using SeatReservation.Api.Health;

namespace SeatReservation.Api.Infrastructure.Migrations;

/// <summary>
/// Runs migrations in the background at startup so liveness can stay 200 while MySQL
/// is slow to wake; readiness stays 503 (via MigrationsState) until this finishes.
/// </summary>
public sealed class MigrationRunnerHostedService(
    MigrationRunner migrationRunner,
    MigrationsState migrationsState,
    IConfiguration configuration,
    ILogger<MigrationRunnerHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("Mysql")
            ?? throw new InvalidOperationException("ConnectionStrings__Mysql is not set.");

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await migrationRunner.RunAsync(connectionString, stoppingToken);
                migrationsState.IsCompleted = true;
                logger.LogInformation("Migrations completed.");
                return;
            }
            catch (MigrationLockUnavailableException ex)
            {
                // Another instance is very likely mid-migration; by the time we
                // retry it has probably finished, so this is always worth retrying.
                logger.LogWarning(ex, "Could not acquire the migration lock, retrying in {Backoff}.", backoff);
            }
            catch (MySqlException ex) when (ex.IsTransient)
            {
                logger.LogWarning(ex, "Transient MySQL error during migration, retrying in {Backoff}.", backoff);
            }
            catch (MySqlException ex)
            {
                // Not transient (e.g. bad credentials, unknown database, a syntax error
                // in a future migration) - this won't fix itself, but per 07-deploy.md
                // we still never crash-loop; readiness just stays 503 until someone fixes it.
                logger.LogError(ex, "Non-transient MySQL error {Number} during migration; readiness stays 503.", ex.Number);
            }

            try
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
                await Task.Delay(backoff + jitter, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = backoff * 2 < MaxBackoff ? backoff * 2 : MaxBackoff;
        }
    }
}
