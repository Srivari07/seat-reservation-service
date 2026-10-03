using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using SeatReservation.Api.Auth;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.IntegrationTests.Shows;

namespace SeatReservation.IntegrationTests.Reservations;

/// <summary>
/// Cancel under concurrency, against real MySQL: cancels racing reserves on the same seats and on
/// the same quota row, and parallel cancels of one reservation. Cancel takes its locks in the same
/// global order as reserve (I10), so these are strict: no 5xx, no contention and, where cancel and
/// reserve race on the same seats or quota row, no deadlocks (not even retried ones). Every test asserts
/// I3 and checks DB truth. Same start-gate pattern as ReserveConcurrencyTests.
/// </summary>
[Collection("ApiHost")]
public sealed class CancelConcurrencyTests : IClassFixture<ShowsApiFactory>
{
    private readonly ShowsApiFactory _factory;
    private readonly HttpClient _client;

    public CancelConcurrencyTests(ShowsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CancelRacingReserve_SameSeats_NoDeadlock5xx()
    {
        // 5 owners hold all 10 seats in pairs. In one burst: each owner cancels and also reserves
        // one more seat (both take that owner's quota row), while 200 other users ask for 1-3
        // random seats in random order. The request mix is seeded; the interleaving is not.
        // Seat ids run opposite to seat_no, and no deadlock may happen even if DbRunner would
        // retry it away. The counter reliably catches cancel taking seats before the quota row,
        // but only sometimes a seat_id-order regression (the window is tiny);
        // Cancel_TakesLocksInGlobalOrder checks the whole order deterministically.
        var showId = await CreateShowWithReversedSeatIdsAsync(SeatRange(10));
        var owned = new List<(string Owner, string ReservationId)>();
        for (var i = 1; i <= 5; i++)
        {
            var owner = $"owner-{i}";
            var result = Assert.Single(await FireAsync([Reserve(owner, showId, [$"A{(2 * i) - 1}", $"A{2 * i}"], "k")]));
            Assert.Equal(HttpStatusCode.Created, result.Status);
            owned.Add((owner, result.ReservationId!));
        }

        var random = new Random(7);
        var calls = owned.Select(o => Cancel(o.Owner, o.ReservationId))
            .Concat(owned.Select(o => Reserve(o.Owner, showId, [$"A{random.Next(1, 11)}"], "k-again")))
            .Concat(Enumerable.Range(0, 200).Select(i =>
                Reserve($"racer-{i}", showId, SeatRange(10).OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToArray(), "k")))
            .OrderBy(_ => random.Next())
            .ToList();
        var lockWaitsBefore = await LockWaitsAsync();
        var deadlocksBefore = await DeadlocksAsync();

        var results = await FireAsync(calls);

        await AssertContendedAsync(lockWaitsBefore);
        await AssertNoDeadlocksAsync(deadlocksBefore);
        AssertNo5xx(results);
        Assert.All(results.Where(r => r.Call.IsCancel), r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.All(results.Where(r => !r.Call.IsCancel), r => Assert.True(
            r.Status == HttpStatusCode.Created || r.Is(HttpStatusCode.Conflict, "seat_taken"),
            $"unexpected {(int)r.Status} {r.Error}"));

        await using var connection = await OpenAsync();
        var reservations = (await connection.QueryAsync<(Guid ReservationId, string UserId, string SeatsJson, string Status)>(
            "SELECT reservation_id, user_id, seats, status FROM reservations WHERE show_id = @ShowId",
            new { ShowId = Guid.Parse(showId) })).ToDictionary(row => row.ReservationId);
        var seatOwners = (await connection.QueryAsync<(string SeatNo, Guid ReservationId)>(
            "SELECT seat_no, reservation_id FROM seats WHERE show_id = @ShowId AND status = 'confirmed'",
            new { ShowId = Guid.Parse(showId) })).ToList();

        // Every cancelled reservation released all its seats; every confirmed one holds all of
        // its seats; and every confirmed seat belongs to a confirmed reservation that lists it.
        Assert.All(owned, o => Assert.Equal("cancelled", reservations[Guid.Parse(o.ReservationId)].Status));
        Assert.DoesNotContain(seatOwners, s => owned.Any(o => Guid.Parse(o.ReservationId) == s.ReservationId));
        var confirmed = reservations.Values.Where(r => r.Status == "confirmed").ToList();
        Assert.Equal(results.Count(r => r.Status == HttpStatusCode.Created), confirmed.Count);
        Assert.All(confirmed, r => Assert.All(
            JsonSerializer.Deserialize<List<string>>(r.SeatsJson)!,
            seat => Assert.Contains((seat, r.ReservationId), seatOwners)));
        Assert.Equal(confirmed.Sum(r => JsonSerializer.Deserialize<List<string>>(r.SeatsJson)!.Count), seatOwners.Count);

        // I5 / D-09: each user's quota equals the seats they actually hold.
        var mismatched = await connection.QueryAsync<string>(
            """
            SELECT q.user_id
              FROM user_show_quota q
             WHERE q.show_id = @ShowId
               AND q.seat_count <> (SELECT COUNT(*)
                                      FROM seats s
                                      JOIN reservations r ON r.reservation_id = s.reservation_id
                                     WHERE s.show_id = @ShowId AND r.user_id = q.user_id)
            """,
            new { ShowId = Guid.Parse(showId) });
        Assert.Empty(mismatched);
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task CancelHotSeat_RacingRebooks_AtMostOneWinner()
    {
        // I1 across a cancel: 100 users wait on a seat that is being released. The seat's row lock
        // serializes them, so at most one gets it. Zero is legitimate in one round (every reserve
        // ran before the cancel committed), but waiters wrongly told "taken" while the cancel holds
        // the lock (the D-07 failure) would make zero the norm, so at least one round must have a winner.
        var winnersPerRound = new List<int>();
        for (var round = 0; round < 3; round++)
        {
            winnersPerRound.Add(await HotSeatRebookRoundAsync(round));
        }

        Assert.Contains(1, winnersPerRound);
    }

    [Fact]
    public async Task CancelAtLimit_RacingSameUserReserves_NeverExceedsLimit()
    {
        // I5 / D-09 with the limit binding while a cancel runs: the user holds 2 + 2 of a limit of
        // 4, cancels one pair, and fires 8 single-seat reserves for free seats at the same time.
        // Cancel and reserves serialize on the user's quota row; whatever the order, the quota
        // must equal the seats held and at most 2 of the reserves can win.
        var showId = await CreateShowAsync(SeatRange(12));
        var cancelled = Assert.Single(await FireAsync([Reserve("at-limit", showId, ["A1", "A2"], "k-1")]));
        Assert.Equal(HttpStatusCode.Created, cancelled.Status);
        Assert.Equal(HttpStatusCode.Created, Assert.Single(await FireAsync([Reserve("at-limit", showId, ["A3", "A4"], "k-2")])).Status);
        var lockWaitsBefore = await LockWaitsAsync();
        var deadlocksBefore = await DeadlocksAsync();

        var results = await FireAsync(
            [Cancel("at-limit", cancelled.ReservationId!), .. Enumerable.Range(5, 8).Select(i => Reserve("at-limit", showId, [$"A{i}"], $"k-r{i}"))]);

        await AssertContendedAsync(lockWaitsBefore);
        await AssertNoDeadlocksAsync(deadlocksBefore);
        AssertNo5xx(results);
        Assert.Equal(HttpStatusCode.OK, Assert.Single(results, r => r.Call.IsCancel).Status);
        var reserves = results.Where(r => !r.Call.IsCancel).ToList();
        Assert.All(reserves, r => Assert.True(
            r.Status == HttpStatusCode.Created || r.Is(HttpStatusCode.Conflict, "per_user_limit"),
            $"unexpected {(int)r.Status} {r.Error}"));
        var winners = reserves.Count(r => r.Status == HttpStatusCode.Created);
        Assert.InRange(winners, 0, 2);

        await using var connection = await OpenAsync();
        var quota = await connection.ExecuteScalarAsync<int>(
            "SELECT seat_count FROM user_show_quota WHERE user_id = 'at-limit' AND show_id = @ShowId", new { ShowId = Guid.Parse(showId) });
        var held = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
              FROM seats s
              JOIN reservations r ON r.reservation_id = s.reservation_id
             WHERE s.show_id = @ShowId AND r.user_id = 'at-limit'
            """,
            new { ShowId = Guid.Parse(showId) });
        Assert.Equal(2 + winners, quota);
        Assert.Equal(quota, held);
        Assert.Null(await connection.ExecuteScalarAsync<Guid?>(
            "SELECT reservation_id FROM seats WHERE show_id = @ShowId AND seat_no = 'A1'", new { ShowId = Guid.Parse(showId) }));
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task Cancel_TakesLocksInGlobalOrder()
    {
        // I10, deterministically. A test session holds A1's row lock, and A2 has the lower seat_id
        // but A1 the lower seat_no. While cancel waits, its own locks must be exactly: the
        // reservation row, then the quota row, and no seat yet - it is waiting on A1, the first
        // seat by seat_no. Cancel locking seats before the quota row, or via reservation_id
        // (seat_id order: A2 first), each shows up here as a wrong lock set.
        var showId = await CreateShowWithReversedSeatIdsAsync(["A1", "A2"]);
        var held = Assert.Single(await FireAsync([Reserve("order-owner", showId, ["A1", "A2"], "k")]));
        Assert.Equal(HttpStatusCode.Created, held.Status);

        await using var blocker = await OpenAsync();
        Assert.True(
            await blocker.ExecuteScalarAsync<long>(
                "SELECT seat_id FROM seats WHERE show_id = @ShowId AND seat_no = 'A2'", new { ShowId = Guid.Parse(showId) })
            < await blocker.ExecuteScalarAsync<long>(
                "SELECT seat_id FROM seats WHERE show_id = @ShowId AND seat_no = 'A1'", new { ShowId = Guid.Parse(showId) }));
        await using var root = new MySqlConnection(_factory.RootConnectionString);
        await root.OpenAsync();
        var schema = new MySqlConnectionStringBuilder(_factory.DirectConnectionString).Database;

        await using var blockerTx = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT seat_id FROM seats WHERE show_id = @ShowId AND seat_no = 'A1' FOR UPDATE", new { ShowId = Guid.Parse(showId) }, blockerTx);
        var cancel = FireAsync([Cancel("order-owner", held.ReservationId!)]);
        List<LockRow> cancelLocks;
        try
        {
            await WaitForSeatLockWaitAsync(root, schema);
            // The only waiter on this container is the cancel; take its transaction's record locks.
            cancelLocks = (await root.QueryAsync<LockRow>(
                """
                SELECT OBJECT_NAME AS ObjectName, INDEX_NAME AS IndexName, LOCK_STATUS AS LockStatus, LOCK_DATA AS LockData
                  FROM performance_schema.data_locks
                 WHERE LOCK_TYPE = 'RECORD'
                   AND ENGINE_TRANSACTION_ID = (SELECT ENGINE_TRANSACTION_ID
                                                  FROM performance_schema.data_locks
                                                 WHERE OBJECT_SCHEMA = @Schema AND LOCK_STATUS = 'WAITING')
                """,
                new { Schema = schema })).AsList();
        }
        finally
        {
            // Always release the blocker and let the cancel finish, even if the snapshot failed,
            // so a failing test can't leave a request in flight. WhenAny never throws.
            await blockerTx.RollbackAsync();
            await Task.WhenAny(cancel, Task.Delay(TimeSpan.FromSeconds(30)));
        }

        var granted = cancelLocks.Where(l => l.LockStatus == "GRANTED").ToList();
        Assert.Single(granted, l => l.ObjectName == "reservations" && l.IndexName == "PRIMARY");
        Assert.Single(granted, l => l.ObjectName == "user_show_quota" && l.IndexName == "PRIMARY");
        Assert.DoesNotContain(granted, l => l.ObjectName == "seats");
        Assert.Equal(2, granted.Count);
        var waiting = Assert.Single(cancelLocks, l => l.LockStatus == "WAITING");
        Assert.Equal(("seats", "uq_seats_show_seat"), (waiting.ObjectName, waiting.IndexName));
        Assert.Contains("'A1'", waiting.LockData);

        Assert.Equal(HttpStatusCode.OK, Assert.Single(await cancel).Status);
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task SameReservation_20ParallelCancels_AllReturn200_QuotaDecrementedOnce()
    {
        // Idempotent cancel under concurrency: the reservation row lock serializes them; one
        // cancels, the rest see "cancelled" and return the same body without touching the quota.
        var showId = await CreateShowAsync(SeatRange(5));
        var target = Assert.Single(await FireAsync([Reserve("multi-cancel", showId, ["A1", "A2"], "k-1")]));
        Assert.Equal(HttpStatusCode.Created, Assert.Single(await FireAsync([Reserve("multi-cancel", showId, ["A3"], "k-2")])).Status);
        var lockWaitsBefore = await LockWaitsAsync();

        var results = await FireAsync(Enumerable.Range(0, 20).Select(_ => Cancel("multi-cancel", target.ReservationId!)));

        await AssertContendedAsync(lockWaitsBefore);
        AssertNo5xx(results);
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Single(results.Select(r => r.Body).Distinct());
        Assert.All(results, r => Assert.Equal(["A1", "A2"], r.Seats));

        await using var connection = await OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT seat_count FROM user_show_quota WHERE user_id = 'multi-cancel' AND show_id = @ShowId",
            new { ShowId = Guid.Parse(showId) }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM seats WHERE show_id = @ShowId AND status = 'confirmed'", new { ShowId = Guid.Parse(showId) }));
        await AssertReconcilesAsync(showId);
    }

    // One hot-seat round on a fresh show, with users unique to the round (a reused user + key on a
    // new show would be an idempotency mismatch). Returns how many rebooks won (0 or 1).
    private async Task<int> HotSeatRebookRoundAsync(int round)
    {
        var showId = await CreateShowAsync(SeatRange(3));
        var held = Assert.Single(await FireAsync([Reserve($"hot-owner-{round}", showId, ["A1"], "k")]));
        Assert.Equal(HttpStatusCode.Created, held.Status);
        var lockWaitsBefore = await LockWaitsAsync();

        var results = await FireAsync(
            [Cancel($"hot-owner-{round}", held.ReservationId!), .. Enumerable.Range(0, 100).Select(i => Reserve($"rebook-{round}-{i}", showId, ["A1"], "k"))]);

        await AssertContendedAsync(lockWaitsBefore);
        AssertNo5xx(results);
        Assert.Equal(HttpStatusCode.OK, Assert.Single(results, r => r.Call.IsCancel).Status);
        var rebooks = results.Where(r => !r.Call.IsCancel).ToList();
        Assert.All(rebooks, r => Assert.True(
            r.Status == HttpStatusCode.Created || r.Is(HttpStatusCode.Conflict, "seat_taken"),
            $"unexpected {(int)r.Status} {r.Error}"));
        var winners = rebooks.Where(r => r.Status == HttpStatusCode.Created).ToList();
        Assert.InRange(winners.Count, 0, 1);

        await using var connection = await OpenAsync();
        var owner = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT reservation_id FROM seats WHERE show_id = @ShowId AND seat_no = 'A1'", new { ShowId = Guid.Parse(showId) });
        Assert.Equal(winners.Count == 1 ? Guid.Parse(winners[0].ReservationId!) : null, owner);
        await AssertReconcilesAsync(showId);
        return winners.Count;
    }

    private sealed class LockRow
    {
        public string ObjectName { get; set; } = string.Empty;
        public string? IndexName { get; set; }
        public string LockStatus { get; set; } = string.Empty;
        public string? LockData { get; set; }
    }

    private sealed record Call(string UserId, string Path, object? Body)
    {
        public bool IsCancel => Body is null;
    }

    private sealed record CallResult(
        Call Call, HttpStatusCode Status, string? Error, string? ReservationId, List<string> Seats, string Body)
    {
        public bool Is(HttpStatusCode status, string error) => Status == status && Error == error;
    }

    private static Call Reserve(string userId, string showId, string[] seats, string key) =>
        new(userId, $"/shows/{showId}/reserve", new { seats, idempotency_key = key });

    private static Call Cancel(string userId, string reservationId) =>
        new(userId, $"/reservations/{reservationId}/cancel", null);

    // Builds every request first, then releases them all at once through one start gate.
    private async Task<List<CallResult>> FireAsync(IEnumerable<Call> calls)
    {
        var issuer = _factory.Services.GetRequiredService<JwtTokenIssuer>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(call =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, call.Path)
            {
                Content = call.Body is null ? null : JsonContent.Create(call.Body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issuer.IssueToken(call.UserId, "user").AccessToken);
            return Task.Run(async () =>
            {
                await start.Task;
                using (request)
                {
                    return await ParseAsync(call, await _client.SendAsync(request));
                }
            });
        }).ToList();

        start.SetResult();
        return [.. await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMinutes(2))];
    }

    private static async Task<CallResult> ParseAsync(Call call, HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            var body = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
            string? Text(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value.GetString() : null;
            var seats = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("seats", out var array)
                ? array.EnumerateArray().Select(seat => seat.GetString()!).ToList()
                : [];
            return new CallResult(call, response.StatusCode, Text("error"), Text("reservation_id"), seats, text);
        }
    }

    // InnoDB's global count of row-lock waits; each class fixture has its own MySQL container.
    private async Task<long> LockWaitsAsync()
    {
        await using var connection = await OpenAsync();
        var row = await connection.QuerySingleAsync<(string Name, string Value)>("SHOW GLOBAL STATUS LIKE 'Innodb_row_lock_waits'");
        return long.Parse(row.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Guards against a vacuous pass: the strict assertions prove nothing if requests never overlapped.
    private async Task AssertContendedAsync(long lockWaitsBefore) =>
        Assert.True(await LockWaitsAsync() > lockWaitsBefore, "requests never waited on a row lock - the test ran serially");

    // InnoDB's global deadlock count. DbRunner retries deadlocks, so a lock-order regression (I10)
    // can still end in all-green HTTP outcomes; only this counter shows it. Needs root (PROCESS).
    private async Task<long> DeadlocksAsync()
    {
        await using var connection = new MySqlConnection(_factory.RootConnectionString);
        await connection.OpenAsync();
        var metric = await connection.QuerySingleAsync<(long Count, string Status)>(
            "SELECT `COUNT`, STATUS FROM information_schema.INNODB_METRICS WHERE NAME = 'lock_deadlocks'");
        Assert.Equal("enabled", metric.Status);
        return metric.Count;
    }

    // Cancel and reserve take locks in the same global order, so none of them may deadlock.
    // Only valid while every user sending parallel requests already has a quota row and every
    // request has its own idempotency key: otherwise 04-concurrency.md documents legitimate,
    // retried deadlocks (new quota row, same-key waiters) that would trip this.
    private async Task AssertNoDeadlocksAsync(long deadlocksBefore) =>
        Assert.Equal(deadlocksBefore, await DeadlocksAsync());

    // Polls until some transaction is waiting for a row lock on `seats` (the request under test
    // has reached its locking statement). Bounded well inside the 5 s lock wait timeout.
    private static async Task WaitForSeatLockWaitAsync(MySqlConnection root, string schema)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (await root.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM performance_schema.data_locks WHERE OBJECT_SCHEMA = @Schema AND OBJECT_NAME = 'seats' AND LOCK_STATUS = 'WAITING'",
            new { Schema = schema }) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the cancel never waited on the blocked seat");
            await Task.Delay(20);
        }
    }

    // I2: no 5xx. A healthy Testcontainer never produces the one allowed 503.
    private static void AssertNo5xx(List<CallResult> results) =>
        Assert.All(results, r => Assert.True((int)r.Status < 500, $"got {(int)r.Status} {r.Error}"));

    // I3: available + held + confirmed == total_seats, and confirmed matches the seat rows.
    private async Task AssertReconcilesAsync(string showId)
    {
        var show = await _client.GetFromJsonAsync<JsonElement>($"/shows/{showId}");
        var counts = show.GetProperty("counts");
        var confirmed = counts.GetProperty("confirmed").GetInt32();
        Assert.Equal(
            show.GetProperty("total_seats").GetInt32(),
            counts.GetProperty("available").GetInt32() + counts.GetProperty("held").GetInt32() + confirmed);

        await using var connection = await OpenAsync();
        Assert.Equal(confirmed, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM seats WHERE show_id = @ShowId AND status = 'confirmed'", new { ShowId = Guid.Parse(showId) }));
    }

    private async Task<string> CreateShowAsync(string[] seats)
    {
        var admin = _factory.Services.GetRequiredService<JwtTokenIssuer>().IssueToken("admin-1", "admin").AccessToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = JsonContent.Create(new { name = "cancel-concurrency-test", seats, price_paise = 1000 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("show_id").GetString()!;
    }

    // POST /shows inserts seats sorted, so seat_id order always equals seat_no order there. This
    // seeds a show straight into MySQL with the seat rows inserted in reverse, so the two orders
    // differ - the case the I10 rule ("lock seats by seat_no, never via reservation_id") exists for.
    private async Task<string> CreateShowWithReversedSeatIdsAsync(string[] seats)
    {
        var showId = Guid.CreateVersion7();
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO shows (show_id, name, price_paise, per_user_limit, total_seats) VALUES (@ShowId, 'reversed-ids', 1000, 4, @Total)",
            new { ShowId = showId, Total = seats.Length });
        foreach (var seat in seats.Order(StringComparer.Ordinal).Reverse())
        {
            await connection.ExecuteAsync("INSERT INTO seats (show_id, seat_no) VALUES (@ShowId, @SeatNo)", new { ShowId = showId, SeatNo = seat });
        }

        return showId.ToString();
    }

    private static string[] SeatRange(int count) => Enumerable.Range(1, count).Select(i => $"A{i}").ToArray();

    private async Task<MySqlConnection> OpenAsync()
    {
        // Same Guid format as the app, so Guid parameters bind as BINARY(16).
        var connection = new MySqlConnection(MySqlConnectionStrings.WithGuidFormat(_factory.DirectConnectionString));
        await connection.OpenAsync();
        return connection;
    }
}
