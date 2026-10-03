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
/// Proves the graded concurrency claims for POST /shows/{id}/reserve against real MySQL
/// (06-testing-and-burst.md, C1/C4/C5/C6 and all-or-nothing): requests are built up front and
/// released together by one start gate so they really overlap. Every test asserts zero 5xx (I2) and
/// reconciliation (I3), and checks DB truth, not just HTTP responses.
/// </summary>
[Collection("ApiHost")]
public sealed class ReserveConcurrencyTests : IClassFixture<ShowsApiFactory>
{
    private readonly ShowsApiFactory _factory;
    private readonly HttpClient _client;

    public ReserveConcurrencyTests(ShowsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HotSeat_500Parallel_ExactlyOneWinner()
    {
        // C1 / I1: 500 users, one seat. Exactly one 201; everyone else a clean "already taken".
        var showId = await CreateShowAsync(SeatRange(5));
        var lockWaitsBefore = await LockWaitsAsync();

        var results = await FireAsync(Enumerable.Range(0, 500).Select(i =>
            new ReserveCall($"hot-{i}", showId, new { seats = new[] { "A1" }, idempotency_key = "k" })));

        await AssertContendedAsync(lockWaitsBefore);
        AssertNo5xx(results);
        var winner = Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.Equal(499, results.Count(r => r.Is(HttpStatusCode.Conflict, "seat_taken")));

        await using var connection = await OpenAsync();
        Assert.Equal(Guid.Parse(winner.ReservationId!), await SeatOwnerAsync(connection, showId, "A1"));
        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM reservations WHERE show_id = @ShowId", showId));
        // Losers' quota rows were created inside their transactions and rolled back with them.
        Assert.Equal(winner.UserId, Assert.Single(await connection.QueryAsync<string>(
            "SELECT user_id FROM user_show_quota WHERE show_id = @ShowId", new { ShowId = Guid.Parse(showId) })));
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task HotSeat_HoldersThatRollBack_StillExactlyOneWinner()
    {
        // D-07, why not NOWAIT: half the contenders also ask for an already-taken seat, so whoever
        // holds A1's lock may roll back. Waiters must keep waiting for it rather than be told
        // "taken" early - otherwise A1 could end the storm with no winner at all.
        var showId = await CreateShowAsync(SeatRange(5));
        Assert.Equal(HttpStatusCode.Created, Assert.Single(await FireAsync(
            [new ReserveCall("pre", showId, new { seats = new[] { "A3" }, idempotency_key = "k" })])).Status);
        var lockWaitsBefore = await LockWaitsAsync();

        var results = await FireAsync(Enumerable.Range(0, 500).Select(i =>
            new ReserveCall($"rb-{i}", showId, new { seats = i % 2 == 0 ? new[] { "A3", "A1" } : new[] { "A1" }, idempotency_key = "k" })));

        await AssertContendedAsync(lockWaitsBefore);
        AssertNo5xx(results);
        var winner = Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.Equal(["A1"], winner.Seats);
        Assert.Equal(499, results.Count(r => r.Is(HttpStatusCode.Conflict, "seat_taken")));

        await using var connection = await OpenAsync();
        Assert.Equal(Guid.Parse(winner.ReservationId!), await SeatOwnerAsync(connection, showId, "A1"));
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task SameKey_20Parallel_OneReservation()
    {
        // C4 / I4: the same request 20x in parallel reserves exactly once; the rest replay it.
        var showId = await CreateShowAsync(SeatRange(5));
        var lockWaitsBefore = await LockWaitsAsync();

        var results = await FireAsync(Enumerable.Range(0, 20).Select(_ =>
            new ReserveCall("same-key", showId, new { seats = new[] { "A2", "A1" }, idempotency_key = "k" })));

        await AssertContendedAsync(lockWaitsBefore);
        AssertNo5xx(results);
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Single(results, r => !r.Replayed);
        var reservationId = Assert.Single(results.Select(r => r.ReservationId).Distinct());
        Assert.All(results, r => Assert.Equal(["A1", "A2"], r.Seats));

        await using var connection = await OpenAsync();
        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM reservations WHERE show_id = @ShowId", showId));
        Assert.Equal(2, await QuotaAsync(connection, "same-key", showId));
        Assert.Equal(Guid.Parse(reservationId!), await SeatOwnerAsync(connection, showId, "A1"));
        Assert.Equal(Guid.Parse(reservationId!), await SeatOwnerAsync(connection, showId, "A2"));

        // Same key, different seats: a mismatch, and nothing is reserved.
        var mismatch = Assert.Single(await FireAsync(
            [new ReserveCall("same-key", showId, new { seats = new[] { "A3" }, idempotency_key = "k" })]));
        Assert.True(mismatch.Is(HttpStatusCode.Conflict, "idempotency_mismatch"));
        Assert.Null(await SeatOwnerAsync(connection, showId, "A3"));
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task SameKey_DifferentSeats_409()
    {
        // C4: two different bodies race on one key. Exactly one body wins; its twins replay, the
        // other body is a mismatch.
        var showId = await CreateShowAsync(SeatRange(5));
        var lockWaitsBefore = await LockWaitsAsync();

        var results = await FireAsync(Enumerable.Range(0, 20).Select(i =>
            new ReserveCall("mixed", showId, new { seats = new[] { i % 2 == 0 ? "A1" : "A2" }, idempotency_key = "k" })));

        await AssertContendedAsync(lockWaitsBefore);
        AssertNo5xx(results);
        var fresh = Assert.Single(results, r => r.Status == HttpStatusCode.Created && !r.Replayed);
        var winningSeat = Assert.Single(fresh.Seats);
        for (var i = 0; i < results.Count; i++)
        {
            var requestedSeat = i % 2 == 0 ? "A1" : "A2";
            if (requestedSeat == winningSeat)
            {
                Assert.Equal(HttpStatusCode.Created, results[i].Status);
                Assert.Equal(fresh.ReservationId, results[i].ReservationId);
            }
            else
            {
                Assert.True(results[i].Is(HttpStatusCode.Conflict, "idempotency_mismatch"));
            }
        }

        await using var connection = await OpenAsync();
        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM reservations WHERE show_id = @ShowId", showId));
        Assert.Equal(1, await QuotaAsync(connection, "mixed", showId));
        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM seats WHERE show_id = @ShowId AND status = 'confirmed'", showId));
        Assert.Null(await SeatOwnerAsync(connection, showId, winningSeat == "A1" ? "A2" : "A1"));
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task SameKey_20Parallel_OnTakenSeat_DeclinesWithout5xx()
    {
        // C4, declined variant. Parallel waiters on a key whose first holder rolls back can deadlock
        // each other (04-concurrency.md), so some 409 contention is expected here - but never a 5xx
        // and never a stored row.
        var showId = await CreateShowAsync(SeatRange(5));
        Assert.Equal(HttpStatusCode.Created, Assert.Single(await FireAsync(
            [new ReserveCall("holder", showId, new { seats = new[] { "A1" }, idempotency_key = "k" })])).Status);

        var results = await FireAsync(Enumerable.Range(0, 20).Select(_ =>
            new ReserveCall("late", showId, new { seats = new[] { "A1" }, idempotency_key = "k" })));

        AssertNo5xx(results);
        Assert.All(results, r => Assert.True(
            r.Is(HttpStatusCode.Conflict, "seat_taken") || r.Is(HttpStatusCode.Conflict, "contention"),
            $"unexpected {(int)r.Status} {r.Error}"));
        // Contention is allowed, but must not be the only outcome (that would prove nothing).
        Assert.Contains(results, r => r.Is(HttpStatusCode.Conflict, "seat_taken"));

        await using var connection = await OpenAsync();
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM reservations WHERE user_id = 'late' AND show_id = @ShowId", showId));
        Assert.Equal(0, await QuotaAsync(connection, "late", showId));
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task OneUser_10ParallelSingleSeat_AtMost4()
    {
        // C5 / I5: ten parallel single-seat requests from one user, limit 4.
        var showId = await CreateShowAsync(SeatRange(10));
        var lockWaitsBefore = await LockWaitsAsync();

        var results = await FireAsync(Enumerable.Range(1, 10).Select(i =>
            new ReserveCall("c5", showId, new { seats = new[] { $"A{i}" }, idempotency_key = $"k-{i}" })));

        await AssertContendedAsync(lockWaitsBefore);
        AssertNo5xx(results);
        Assert.Equal(4, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(6, results.Count(r => r.Is(HttpStatusCode.Conflict, "per_user_limit")));

        await using var connection = await OpenAsync();
        Assert.Equal(4, await QuotaAsync(connection, "c5", showId));
        Assert.Equal(4, await OwnedSeatsAsync(connection, "c5", showId));
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task OneUser_10ParallelTwoSeat_AtMost2Reservations()
    {
        // C5 with n > 1 in the conditional quota UPDATE: ten parallel 2-seat requests, limit 4.
        var showId = await CreateShowAsync(SeatRange(20));
        var lockWaitsBefore = await LockWaitsAsync();

        var results = await FireAsync(Enumerable.Range(0, 10).Select(i =>
            new ReserveCall("c5-pairs", showId, new { seats = new[] { $"A{(2 * i) + 1}", $"A{(2 * i) + 2}" }, idempotency_key = $"k-{i}" })));

        await AssertContendedAsync(lockWaitsBefore);
        AssertNo5xx(results);
        Assert.Equal(2, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(8, results.Count(r => r.Is(HttpStatusCode.Conflict, "per_user_limit")));

        await using var connection = await OpenAsync();
        Assert.Equal(4, await QuotaAsync(connection, "c5-pairs", showId));
        Assert.Equal(4, await OwnedSeatsAsync(connection, "c5-pairs", showId));
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task OneUser_ParallelWithDeclines_QuotaMatchesSeats()
    {
        // C5 variant: the user has no quota row yet, and half the requests target taken seats, so
        // a freshly inserted quota row is rolled back. Whatever the interleaving, the quota must
        // equal the seats the user really holds, and never exceed the limit.
        var showId = await CreateShowAsync(SeatRange(10));
        Assert.Equal(HttpStatusCode.Created, Assert.Single(await FireAsync(
            [new ReserveCall("taker-1", showId, new { seats = new[] { "A1", "A2", "A3", "A4" }, idempotency_key = "k" })])).Status);
        Assert.Equal(HttpStatusCode.Created, Assert.Single(await FireAsync(
            [new ReserveCall("taker-2", showId, new { seats = new[] { "A5" }, idempotency_key = "k" })])).Status);

        var results = await FireAsync(Enumerable.Range(1, 10).Select(i =>
            new ReserveCall("c5-mixed", showId, new { seats = new[] { $"A{i}" }, idempotency_key = $"k-{i}" })));

        AssertNo5xx(results);
        Assert.All(results, r => Assert.True(
            r.Status == HttpStatusCode.Created
            || r.Is(HttpStatusCode.Conflict, "seat_taken")
            || r.Is(HttpStatusCode.Conflict, "per_user_limit")
            || r.Is(HttpStatusCode.Conflict, "contention"),
            $"unexpected {(int)r.Status} {r.Error}"));
        string[] freeSeats = ["A6", "A7", "A8", "A9", "A10"];
        Assert.Contains(results, r => r.Status == HttpStatusCode.Created);
        Assert.All(results.Where(r => r.Status == HttpStatusCode.Created), r => Assert.Contains(Assert.Single(r.Seats), freeSeats));

        await using var connection = await OpenAsync();
        var quota = await QuotaAsync(connection, "c5-mixed", showId);
        Assert.Equal(results.Count(r => r.Status == HttpStatusCode.Created), quota);
        Assert.Equal(quota, await OwnedSeatsAsync(connection, "c5-mixed", showId));
        Assert.InRange(quota, 0, 4);
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task SpoofedUserIdInBody_Ignored()
    {
        // C6 / I6: a "user_id" in the body, the query string or a header never decides who owns
        // the reservation.
        var showId = await CreateShowAsync(SeatRange(20));

        var results = await FireAsync(Enumerable.Range(1, 20).Select(i =>
            new ReserveCall($"real-{i}", showId, new { seats = new[] { $"A{i}" }, idempotency_key = "k", user_id = "victim" })
            {
                Query = "?user_id=victim",
                Headers = new Dictionary<string, string> { ["X-User-Id"] = "victim" },
            }));

        AssertNo5xx(results);
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.All(results, r => Assert.Equal(r.CallerId, r.UserId));

        await using var connection = await OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM reservations WHERE user_id = 'victim'"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM user_show_quota WHERE user_id = 'victim'"));
        var stored = (await connection.QueryAsync<(Guid ReservationId, string UserId)>(
            "SELECT reservation_id, user_id FROM reservations WHERE show_id = @ShowId", new { ShowId = Guid.Parse(showId) }))
            .ToDictionary(row => row.ReservationId, row => row.UserId);
        Assert.Equal(20, stored.Count);
        Assert.All(results, r => Assert.Equal(r.CallerId, stored[Guid.Parse(r.ReservationId!)]));
        await AssertReconcilesAsync(showId);
    }

    [Fact]
    public async Task MultiSeat_OverlappingRequests_NoPartials_NoDeadlock500()
    {
        // All-or-nothing under overlap (D-04) and the fixed lock order (I10): 200 users each ask for
        // 2-3 of the same 10 seats, in random (unsorted) order. Seeded, so a failure reproduces.
        var showId = await CreateShowAsync(SeatRange(10));
        var random = new Random(42);
        var calls = Enumerable.Range(0, 200).Select(i =>
        {
            var seats = SeatRange(10).OrderBy(_ => random.Next()).Take(random.Next(2, 4)).ToArray();
            return new ReserveCall($"overlap-{i}", showId, new { seats, idempotency_key = "k" });
        }).ToList();

        var lockWaitsBefore = await LockWaitsAsync();

        var results = await FireAsync(calls);

        await AssertContendedAsync(lockWaitsBefore);
        AssertNo5xx(results);
        // Lock order is fixed, so there are no deadlocks to retry: contention must be 0 here.
        Assert.All(results, r => Assert.True(
            r.Status == HttpStatusCode.Created || r.Is(HttpStatusCode.Conflict, "seat_taken"),
            $"unexpected {(int)r.Status} {r.Error}"));
        var winners = results.Where(r => r.Status == HttpStatusCode.Created).ToList();
        Assert.NotEmpty(winners);
        var wonSeats = winners.SelectMany(w => w.Seats.Select(seat => (Seat: seat, w.ReservationId))).ToList();
        Assert.Equal(wonSeats.Count, wonSeats.Select(x => x.Seat).Distinct().Count());

        await using var connection = await OpenAsync();
        var confirmed = (await connection.QueryAsync<(string SeatNo, Guid ReservationId)>(
            "SELECT seat_no, reservation_id FROM seats WHERE show_id = @ShowId AND status = 'confirmed'",
            new { ShowId = Guid.Parse(showId) })).ToDictionary(row => row.SeatNo, row => row.ReservationId);
        Assert.Equal(wonSeats.Count, confirmed.Count);
        Assert.All(wonSeats, x => Assert.Equal(Guid.Parse(x.ReservationId!), confirmed[x.Seat]));

        // No partials: every stored reservation holds every one of its seats, and only 201s stored one.
        var rows = (await connection.QueryAsync<(Guid ReservationId, string SeatsJson)>(
            "SELECT reservation_id, seats FROM reservations WHERE show_id = @ShowId", new { ShowId = Guid.Parse(showId) })).ToList();
        Assert.Equal(winners.Count, rows.Count);
        Assert.All(rows, row => Assert.All(
            JsonSerializer.Deserialize<List<string>>(row.SeatsJson)!,
            seat => Assert.Equal(row.ReservationId, confirmed[seat])));
        // Declined requests' quota increments rolled back too: quota adds up to the seats won.
        Assert.Equal(wonSeats.Count, await connection.ExecuteScalarAsync<long>(
            "SELECT COALESCE(SUM(seat_count), 0) FROM user_show_quota WHERE show_id = @ShowId", new { ShowId = Guid.Parse(showId) }));
        await AssertReconcilesAsync(showId);
    }

    private sealed record ReserveCall(string UserId, string ShowId, object Body)
    {
        public string Query { get; init; } = string.Empty;

        public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    }

    private sealed record ReserveResult(
        string CallerId, HttpStatusCode Status, string? Error, string? ReservationId, string? UserId, List<string> Seats, bool Replayed)
    {
        public bool Is(HttpStatusCode status, string error) => Status == status && Error == error;
    }

    // Builds every request first, then releases them all at once through one start gate, so they
    // genuinely overlap instead of trickling in as they're created.
    private async Task<List<ReserveResult>> FireAsync(IEnumerable<ReserveCall> calls)
    {
        var issuer = _factory.Services.GetRequiredService<JwtTokenIssuer>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(call =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{call.ShowId}/reserve{call.Query}")
            {
                Content = JsonContent.Create(call.Body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issuer.IssueToken(call.UserId, "user").AccessToken);
            foreach (var (name, value) in call.Headers)
            {
                request.Headers.Add(name, value);
            }
            return Task.Run(async () =>
            {
                await start.Task;
                using (request)
                {
                    return await ParseAsync(call.UserId, await _client.SendAsync(request));
                }
            });
        }).ToList();

        start.SetResult();
        return [.. await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMinutes(2))];
    }

    private static async Task<ReserveResult> ParseAsync(string callerId, HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            string? Text(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value.GetString() : null;
            var seats = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("seats", out var array)
                ? array.EnumerateArray().Select(seat => seat.GetString()!).ToList()
                : [];
            return new ReserveResult(
                callerId,
                response.StatusCode,
                Text("error"),
                Text("reservation_id"),
                Text("user_id"),
                seats,
                response.Headers.Contains("Idempotent-Replayed"));
        }
    }

    // InnoDB's global count of row-lock waits. Each class fixture has its own MySQL container and
    // tests in a class run one at a time, so a rise during FireAsync comes from that test.
    private async Task<long> LockWaitsAsync()
    {
        await using var connection = await OpenAsync();
        var row = await connection.QuerySingleAsync<(string Name, string Value)>("SHOW GLOBAL STATUS LIKE 'Innodb_row_lock_waits'");
        return long.Parse(row.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Guards against a vacuous pass: if requests stopped overlapping (a broken start gate, a gate
    // of 1), the strict assertions would still hold while proving nothing about concurrency.
    private async Task AssertContendedAsync(long lockWaitsBefore) =>
        Assert.True(await LockWaitsAsync() > lockWaitsBefore, "requests never waited on a row lock - the test ran serially");

    // I2: every outcome is a 2xx/4xx. The only allowed 5xx is 503 for a real DB outage, which a
    // healthy Testcontainer never has.
    private static void AssertNo5xx(List<ReserveResult> results) =>
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
        Assert.Equal(confirmed, await CountAsync(connection, "SELECT COUNT(*) FROM seats WHERE show_id = @ShowId AND status = 'confirmed'", showId));
    }

    private async Task<string> CreateShowAsync(string[] seats)
    {
        var admin = _factory.Services.GetRequiredService<JwtTokenIssuer>().IssueToken("admin-1", "admin").AccessToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = JsonContent.Create(new { name = "concurrency-test", seats, price_paise = 1000 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("show_id").GetString()!;
    }

    private static string[] SeatRange(int count) => Enumerable.Range(1, count).Select(i => $"A{i}").ToArray();

    private async Task<MySqlConnection> OpenAsync()
    {
        // Same Guid format as the app, so Guid parameters bind as BINARY(16).
        var connection = new MySqlConnection(MySqlConnectionStrings.WithGuidFormat(_factory.DirectConnectionString));
        await connection.OpenAsync();
        return connection;
    }

    private static Task<long> CountAsync(MySqlConnection connection, string sql, string showId) =>
        connection.ExecuteScalarAsync<long>(sql, new { ShowId = Guid.Parse(showId) });

    private static Task<Guid?> SeatOwnerAsync(MySqlConnection connection, string showId, string seatNo) =>
        connection.ExecuteScalarAsync<Guid?>(
            "SELECT reservation_id FROM seats WHERE show_id = @ShowId AND seat_no = @SeatNo",
            new { ShowId = Guid.Parse(showId), SeatNo = seatNo });

    private static Task<int> QuotaAsync(MySqlConnection connection, string userId, string showId) =>
        connection.ExecuteScalarAsync<int>(
            "SELECT COALESCE((SELECT seat_count FROM user_show_quota WHERE user_id = @UserId AND show_id = @ShowId), 0)",
            new { UserId = userId, ShowId = Guid.Parse(showId) });

    // Seats actually held: confirmed seats whose reservation belongs to this user.
    private static Task<int> OwnedSeatsAsync(MySqlConnection connection, string userId, string showId) =>
        connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
              FROM seats s
              JOIN reservations r ON r.reservation_id = s.reservation_id
             WHERE s.show_id = @ShowId AND s.status = 'confirmed' AND r.user_id = @UserId
            """,
            new { UserId = userId, ShowId = Guid.Parse(showId) });
}
