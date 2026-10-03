using System.Data;
using Dapper;
using MySqlConnector;
using SeatReservation.Api.Infrastructure.Errors;
using SeatReservation.Api.Infrastructure.Metrics;

namespace SeatReservation.Api.Infrastructure.Db;

// The one way the app talks to MySQL for request work. Every write transaction goes through
// WriteAsync, which applies the ground rules in 04-concurrency.md: READ COMMITTED (D-06), a 5 s
// lock wait set per transaction, and a retry of the whole transaction on 1213/1205 (D-12).
public sealed class DbRunner
{
    // 3 retries = 4 attempts in total (D-17).
    private const int MaxAttempts = 4;

    private readonly string _connectionString;
    private readonly DbGate _gate;
    private readonly ILogger<DbRunner> _logger;
    private readonly AppMetrics _metrics;
    private readonly int _lockWaitTimeoutSeconds;

    // True while this async flow holds a gate slot. A callback that calls back into DbRunner would
    // wait for a second slot while holding one: invisible in tests, but under a burst every slot
    // is held by such a caller and all DB work hangs. Fail loudly instead.
    private readonly AsyncLocal<bool> _holdsSlot = new();

    // lockWaitTimeoutSeconds is always 5 in the app; only tests shorten it, so a test that waits
    // out every attempt takes ~4 s instead of ~20 s.
    public DbRunner(string? connectionString, DbGate gate, ILogger<DbRunner> logger, AppMetrics metrics, int lockWaitTimeoutSeconds = 5)
    {
        _connectionString = MySqlConnectionStrings.WithGuidFormat(connectionString);

        // D-14: the gate exists so requests queue here instead of timing out on pool acquisition,
        // which only works if every request admitted by the gate can actually get a connection.
        var maxPoolSize = new MySqlConnectionStringBuilder(_connectionString).MaximumPoolSize;
        if (gate.MaxConcurrency > maxPoolSize)
        {
            throw new InvalidOperationException(
                $"DB_MAX_CONCURRENCY ({gate.MaxConcurrency}) must not exceed the connection string's Maximum Pool Size ({maxPoolSize}).");
        }

        _gate = gate;
        _logger = logger;
        _metrics = metrics;
        _lockWaitTimeoutSeconds = lockWaitTimeoutSeconds;
    }

    // `work` must not call back into this DbRunner (see _holdsSlot).
    public async Task<T> ReadAsync<T>(Func<MySqlConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        await WaitForSlotAsync(cancellationToken);
        // Set here, not inside WaitForSlotAsync: an AsyncLocal change made in an awaited async
        // method doesn't flow back to its caller. It flows down into `work`, and is undone
        // automatically when this method returns to its caller.
        _holdsSlot.Value = true;
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            try
            {
                return await work(connection);
            }
            catch (Exception ex) when (connection.State == ConnectionState.Broken)
            {
                throw new DbUnavailableException(ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Runs `work` in a transaction and commits or rolls back as its TxResult says. On a deadlock
    // or lock wait timeout the whole transaction is re-run, so `work` must be safe to repeat: no
    // side effects outside the DB, and nothing it depends on may change between attempts.
    //
    // Rules for `work`:
    // - Never commit or roll back the transaction itself; return TxResult.Commit/Rollback.
    // - Never swallow 1213/1205. After either one, carrying on would COMMIT a partial transaction
    //   (after 1213 InnoDB has already rolled everything back; after 1205 only the failing
    //   statement). Catch only specific, filtered errors, e.g.
    //   `when (MySqlErrors.IsDuplicateKey(ex, "uq_reservations_user_key"))`, and return Rollback.
    // - Never call back into this DbRunner (see _holdsSlot). Follow-up reads, such as the
    //   idempotent replay SELECT, run after WriteAsync returns.
    public async Task<T> WriteAsync<T>(
        Func<MySqlConnection, MySqlTransaction, Task<TxResult<T>>> work, CancellationToken cancellationToken)
    {
        // Held across all attempts, so a retrying request keeps its slot instead of re-queuing.
        await WaitForSlotAsync(cancellationToken);
        _holdsSlot.Value = true;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await RunTransactionAsync(work, cancellationToken);
                }
                catch (MySqlException ex) when (MySqlErrors.IsDeadlock(ex) || MySqlErrors.IsLockWaitTimeout(ex))
                {
                    var errorLabel = MySqlErrors.IsDeadlock(ex) ? "deadlock" : "lock_wait_timeout";
                    _metrics.DbTxRetriesTotal.WithLabels(errorLabel).Inc();

                    // The failed transaction is already disposed (rolled back) by the time we get
                    // here, so this log line and the delay hold no locks.
                    if (attempt == MaxAttempts)
                    {
                        throw new DbContentionException(ex);
                    }

                    _logger.LogWarning(
                        "DB transaction attempt {Attempt} failed with {DbError}; retrying.",
                        attempt,
                        errorLabel);
                    await Task.Delay(Random.Shared.Next(10, 51), cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> RunTransactionAsync<T>(
        Func<MySqlConnection, MySqlTransaction, Task<TxResult<T>>> work, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            // The request token is only honored up to here: once the transaction starts, a client
            // abort must not leave it half-done - the statements and the commit always run to
            // completion (or roll back together), never partway through on cancellation.
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            // Per transaction, not once per connection: MySqlConnector resets session state when a
            // pooled connection is reused.
            await connection.ExecuteAsync(
                "SET SESSION innodb_lock_wait_timeout = @Seconds",
                new { Seconds = _lockWaitTimeoutSeconds },
                transaction);

            var result = await work(connection, transaction);

            if (result.ShouldCommit)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            return result.Value;
        }
        catch (Exception ex) when (connection.State == ConnectionState.Broken)
        {
            // The connection to MySQL was lost mid-transaction (D-13: 503, fail closed). Not
            // retried: if it broke during COMMIT the outcome is unknown, and the client's retry
            // with the same idempotency key resolves it.
            throw new DbUnavailableException(ex);
        }
    }

    private Task WaitForSlotAsync(CancellationToken cancellationToken)
    {
        if (_holdsSlot.Value)
        {
            throw new InvalidOperationException("Nested DbRunner call: a callback must not call back into DbRunner.");
        }

        return _gate.WaitAsync(cancellationToken);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (MySqlException ex)
        {
            // Any failure to open (unreachable, 1040 too many connections, pool exhausted,
            // auth) means the DB is unusable right now: 503, fail closed (D-13).
            await connection.DisposeAsync();
            throw new DbUnavailableException(ex);
        }
    }
}
