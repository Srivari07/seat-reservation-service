using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.Api.Infrastructure.Errors;
using SeatReservation.Api.Infrastructure.Metrics;

namespace SeatReservation.IntegrationTests.Db;

/// <summary>
/// Proves DbRunner's transaction rules (04-concurrency.md "Ground rules", D-06, D-12, D-13, D-14)
/// against real MySQL: isolation level, lock wait, retry on a real deadlock and on lock wait
/// timeouts, no retry on other errors, the gate, and the unreachable-DB mapping.
/// </summary>
public sealed class DbRunnerTests(MigratedMySqlFixture fixture) : IClassFixture<MigratedMySqlFixture>
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Write_RunsInReadCommittedWithFiveSecondLockWait()
    {
        var marker = Guid.NewGuid().ToString("N");

        var (countBefore, countAfter, lockWait) = await CreateRunner().WriteAsync(async (connection, transaction) =>
        {
            var before = await CountShowsNamedAsync(connection, transaction, marker);

            // Committed by another connection while our transaction is open. READ COMMITTED
            // sees it on the next read; REPEATABLE READ would keep the first read's snapshot.
            await using (var other = await OpenRawAsync())
            {
                await InsertShowAsync(other, Guid.CreateVersion7(), name: marker);
            }

            var after = await CountShowsNamedAsync(connection, transaction, marker);
            var wait = await connection.ExecuteScalarAsync<int>(
                "SELECT @@SESSION.innodb_lock_wait_timeout", transaction: transaction);
            return TxResult.Rollback((before, after, wait));
        }, CancellationToken.None);

        Assert.Equal(0, countBefore);
        Assert.Equal(1, countAfter);
        Assert.Equal(5, lockWait);
    }

    [Fact]
    public async Task Write_CommitResult_PersistsWork()
    {
        var showId = Guid.CreateVersion7();

        await CreateRunner().WriteAsync(async (connection, transaction) =>
        {
            await InsertShowAsync(connection, showId, transaction: transaction);
            return TxResult.Commit(0);
        }, CancellationToken.None);

        Assert.True(await ShowExistsAsync(showId));
    }

    [Fact]
    public async Task Write_RollbackResult_DiscardsWork()
    {
        var showId = Guid.CreateVersion7();

        await CreateRunner().WriteAsync(async (connection, transaction) =>
        {
            await InsertShowAsync(connection, showId, transaction: transaction);
            return TxResult.Rollback(0);
        }, CancellationToken.None);

        Assert.False(await ShowExistsAsync(showId));
    }

    [Fact]
    public async Task Write_DeadlockVictimIsRetried_AndBothTransactionsSucceed()
    {
        var x = await InsertCommittedShowAsync();
        var y = await InsertCommittedShowAsync();
        var metrics = new AppMetrics();
        var runner = CreateRunner(metrics: metrics);

        var firstHoldsX = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHoldsY = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempts = 0;
        var secondAttempts = 0;

        // Opposite lock orders, synchronized on the first attempt only, so InnoDB is guaranteed
        // to see a cycle and kill one of them with 1213. The retry runs without the barrier and
        // just waits for the winner to commit.
        Task<int> LockBoth(Guid first, Guid second, TaskCompletionSource mine, TaskCompletionSource theirs, Func<int> nextAttempt) =>
            runner.WriteAsync(async (connection, transaction) =>
            {
                var attempt = nextAttempt();
                await LockShowAsync(connection, transaction, first);
                if (attempt == 1)
                {
                    mine.TrySetResult();
                    await theirs.Task;
                }

                await LockShowAsync(connection, transaction, second);
                return TxResult.Commit(attempt);
            }, CancellationToken.None);

        var firstTask = LockBoth(x, y, firstHoldsX, secondHoldsY, () => Interlocked.Increment(ref firstAttempts));
        var secondTask = LockBoth(y, x, secondHoldsY, firstHoldsX, () => Interlocked.Increment(ref secondAttempts));
        await Task.WhenAll(firstTask, secondTask).WaitAsync(TestTimeout);

        Assert.Equal(3, firstAttempts + secondAttempts);
        Assert.Equal(2, Math.Max(firstAttempts, secondAttempts));
        // One deadlock occurred (the extra attempt above the minimum 2), and db_tx_retries_total
        // records it (05-observability.md: "contention visibility").
        Assert.Equal(1, metrics.DbTxRetriesTotal.WithLabels("deadlock").Value);
    }

    [Fact]
    public async Task Write_LockWaitTimeoutOnEveryAttempt_ThrowsContentionAfterFourAttempts_AndKeepsNoWork()
    {
        var showId = await InsertCommittedShowAsync();
        var marker = Guid.NewGuid().ToString("N");

        // Another session holds the row lock for the whole test.
        await using var holder = await OpenRawAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await LockShowAsync(holder, holderTransaction, showId);

        var attempts = 0;
        var metrics = new AppMetrics();
        var runner = CreateRunner(maxConcurrency: 1, lockWaitTimeoutSeconds: 1, metrics: metrics);

        // Each attempt writes a marker row BEFORE the blocking lock. A 1205 only rolls back the
        // failing statement, so this proves each attempt's earlier work is discarded too.
        var ex = await Assert.ThrowsAsync<DbContentionException>(() => runner.WriteAsync(async (connection, transaction) =>
        {
            attempts++;
            await InsertShowAsync(connection, Guid.CreateVersion7(), name: marker, transaction: transaction);
            await LockShowAsync(connection, transaction, showId);
            return TxResult.Commit(0);
        }, CancellationToken.None).WaitAsync(TestTimeout));

        Assert.Equal(4, attempts);
        var inner = Assert.IsType<MySqlException>(ex.InnerException);
        Assert.True(MySqlErrors.IsLockWaitTimeout(inner));
        Assert.Equal(0, await CountShowsNamedAsync(marker));
        // All 4 failed attempts count, including the one that exhausts retries into contention -
        // each is a real lock-wait event (05-observability.md: "contention visibility").
        Assert.Equal(4, metrics.DbTxRetriesTotal.WithLabels("lock_wait_timeout").Value);

        // The gate slot (size 1) was released on the contention path.
        await runner.ReadAsync(_ => Task.FromResult(0), CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Write_SucceedsOnceLockIsReleased_AndAppliesWorkExactlyOnce()
    {
        var showId = await InsertCommittedShowAsync();
        var marker = Guid.NewGuid().ToString("N");

        await using var holder = await OpenRawAsync();
        var holderTransaction = await holder.BeginTransactionAsync();
        await LockShowAsync(holder, holderTransaction, showId);

        var attempts = 0;
        var runner = CreateRunner(lockWaitTimeoutSeconds: 1);

        var result = await runner.WriteAsync(async (connection, transaction) =>
        {
            attempts++;
            if (attempts == 3)
            {
                // Attempts 1 and 2 timed out; free the lock so attempt 3 gets it.
                await holderTransaction.RollbackAsync();
            }

            await InsertShowAsync(connection, Guid.CreateVersion7(), name: marker, transaction: transaction);
            await LockShowAsync(connection, transaction, showId);
            return TxResult.Commit(attempts);
        }, CancellationToken.None).WaitAsync(TestTimeout);

        await holderTransaction.DisposeAsync();
        Assert.Equal(3, result);
        Assert.Equal(1, await CountShowsNamedAsync(marker));
    }

    [Fact]
    public async Task Write_ConnectionLostMidTransaction_ThrowsDbUnavailable()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<DbUnavailableException>(() => CreateRunner().WriteAsync(async (connection, transaction) =>
        {
            attempts++;
            var connectionId = await connection.ExecuteScalarAsync<long>("SELECT CONNECTION_ID()", transaction: transaction);
            await using (var killer = await OpenRawAsync())
            {
                await killer.ExecuteAsync($"KILL CONNECTION {connectionId}");
            }

            await InsertShowAsync(connection, Guid.CreateVersion7(), transaction: transaction);
            return TxResult.Commit(0);
        }, CancellationToken.None).WaitAsync(TestTimeout));

        // Not retried: had it broken during COMMIT, the outcome would be unknown.
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task NestedCall_FromInsideWriteCallback_IsRejected()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.WriteAsync(async (_, _) =>
        {
            await runner.ReadAsync(_ => Task.FromResult(0), CancellationToken.None);
            return TxResult.Commit(0);
        }, CancellationToken.None).WaitAsync(TestTimeout));

        // A normal call afterwards still works: the flag doesn't leak out of the failed call.
        await runner.ReadAsync(_ => Task.FromResult(0), CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public void GateLargerThanPool_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new DbRunner(
            fixture.ConnectionString + ";Maximum Pool Size=5",
            new DbGate(6),
            NullLogger<DbRunner>.Instance,
            new AppMetrics()));

        Assert.Contains("DB_MAX_CONCURRENCY", ex.Message);
    }

    [Fact]
    public async Task Write_DuplicateKey_IsNotRetried_AndIsClassifiedByKeyName()
    {
        var showId = await InsertCommittedShowAsync();
        await using (var connection = await OpenRawAsync())
        {
            await InsertSeatAsync(connection, showId, "A1");
        }

        var attempts = 0;

        var ex = await Assert.ThrowsAsync<MySqlException>(() => CreateRunner().WriteAsync(async (connection, transaction) =>
        {
            attempts++;
            await InsertSeatAsync(connection, showId, "A1", transaction);
            return TxResult.Commit(0);
        }, CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.True(MySqlErrors.IsDuplicateKey(ex, "uq_seats_show_seat"));
        Assert.False(MySqlErrors.IsDuplicateKey(ex, "uq_reservations_user_key"));
    }

    [Fact]
    public async Task Gate_NeverLetsMoreThanItsSizeRunAtOnce()
    {
        var runner = CreateRunner(maxConcurrency: 1);
        var inFlight = 0;
        var observed = new ConcurrentBag<int>();

        async Task<int> Work(MySqlConnection _)
        {
            observed.Add(Interlocked.Increment(ref inFlight));
            await Task.Delay(200);
            Interlocked.Decrement(ref inFlight);
            return 0;
        }

        await Task.WhenAll(
            runner.ReadAsync(Work, CancellationToken.None),
            runner.ReadAsync(Work, CancellationToken.None)).WaitAsync(TestTimeout);

        Assert.Equal([1, 1], observed);
    }

    [Fact]
    public async Task Gate_WaitHonorsCancellation()
    {
        var runner = CreateRunner(maxConcurrency: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = runner.ReadAsync(async _ =>
        {
            holding.TrySetResult();
            await release.Task;
            return 0;
        }, CancellationToken.None);
        await holding.Task.WaitAsync(TestTimeout);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.ReadAsync(_ => Task.FromResult(0), cts.Token));

        release.TrySetResult();
        await holder.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task UnreachableDatabase_ThrowsDbUnavailable_ForReadsAndWrites()
    {
        // Nothing listens on port 1, so the open fails straight away. Gate size 1, so the second
        // and third calls would hang if a failed call leaked its slot.
        var runner = new DbRunner(
            "Server=127.0.0.1;Port=1;Database=seats;User ID=app;Password=x;Connection Timeout=1",
            new DbGate(1),
            NullLogger<DbRunner>.Instance,
            new AppMetrics());

        await Assert.ThrowsAsync<DbUnavailableException>(
            () => runner.ReadAsync(_ => Task.FromResult(0), CancellationToken.None).WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<DbUnavailableException>(
            () => runner.WriteAsync((_, _) => Task.FromResult(TxResult.Commit(0)), CancellationToken.None).WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<DbUnavailableException>(
            () => runner.ReadAsync(_ => Task.FromResult(0), CancellationToken.None).WaitAsync(TestTimeout));
    }

    private DbRunner CreateRunner(int maxConcurrency = 10, int lockWaitTimeoutSeconds = 5, AppMetrics? metrics = null) =>
        new(fixture.ConnectionString, new DbGate(maxConcurrency), NullLogger<DbRunner>.Instance, metrics ?? new AppMetrics(), lockWaitTimeoutSeconds);

    private async Task<MySqlConnection> OpenRawAsync()
    {
        // Same Guid format as the app, so Guid parameters bind as BINARY(16).
        var connection = new MySqlConnection(MySqlConnectionStrings.WithGuidFormat(fixture.ConnectionString));
        await connection.OpenAsync();
        return connection;
    }

    private async Task<Guid> InsertCommittedShowAsync()
    {
        var showId = Guid.CreateVersion7();
        await using var connection = await OpenRawAsync();
        await InsertShowAsync(connection, showId);
        return showId;
    }

    private async Task<bool> ShowExistsAsync(Guid showId)
    {
        await using var connection = await OpenRawAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM shows WHERE show_id = @ShowId", new { ShowId = showId }) == 1;
    }

    private static Task InsertShowAsync(
        MySqlConnection connection, Guid showId, string name = "db-runner-test", MySqlTransaction? transaction = null) =>
        connection.ExecuteAsync(
            """
            INSERT INTO shows (show_id, name, price_paise, per_user_limit, total_seats)
            VALUES (@ShowId, @Name, 0, 4, 1)
            """,
            new { ShowId = showId, Name = name },
            transaction);

    private static Task InsertSeatAsync(
        MySqlConnection connection, Guid showId, string seatNo, MySqlTransaction? transaction = null) =>
        connection.ExecuteAsync(
            "INSERT INTO seats (show_id, seat_no) VALUES (@ShowId, @SeatNo)",
            new { ShowId = showId, SeatNo = seatNo },
            transaction);

    private static Task LockShowAsync(MySqlConnection connection, MySqlTransaction transaction, Guid showId) =>
        connection.ExecuteAsync(
            "SELECT show_id FROM shows WHERE show_id = @ShowId FOR UPDATE",
            new { ShowId = showId },
            transaction);

    private async Task<long> CountShowsNamedAsync(string name)
    {
        await using var connection = await OpenRawAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM shows WHERE name = @Name", new { Name = name });
    }

    private static Task<long> CountShowsNamedAsync(MySqlConnection connection, MySqlTransaction transaction, string name) =>
        connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM shows WHERE name = @Name", new { Name = name }, transaction);
}
