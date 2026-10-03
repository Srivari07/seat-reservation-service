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
/// Functional coverage of POST /reservations/{id}/cancel (03-api-contract.md, 04-concurrency.md):
/// owner only, idempotent, frees exactly the reservation's seats and quota, and rolls back on an
/// invariant violation. Asserted against both the HTTP response and the DB. Each test creates its
/// own show.
/// </summary>
[Collection("ApiHost")]
public sealed class CancelTests : IClassFixture<ShowsApiFactory>
{
    private readonly ShowsApiFactory _factory;
    private readonly HttpClient _client;

    public CancelTests(ShowsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Cancel_ReturnsCancelledBody_AndFreesSeats_QuotaAndReconciles()
    {
        var showId = await CreateShowAsync(["A1", "A2", "A3"], pricePaise: 25000);
        var reservationId = await ReserveAsync("u-cancel", showId, ["A2", "A1"]);

        var response = await CancelAsync("u-cancel", reservationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reservationId, body.GetProperty("reservation_id").GetString());
        Assert.Equal(showId, body.GetProperty("show_id").GetString());
        Assert.Equal("u-cancel", body.GetProperty("user_id").GetString());
        Assert.Equal(["A1", "A2"], Seats(body));
        Assert.Equal(50000, body.GetProperty("amount_paise").GetInt64());
        Assert.Equal("cancelled", body.GetProperty("status").GetString());

        await using var connection = await OpenAsync();
        var seats = (await connection.QueryAsync<(string SeatNo, string Status, Guid? ReservationId)>(
            "SELECT seat_no, status, reservation_id FROM seats WHERE show_id = @ShowId ORDER BY seat_no",
            new { ShowId = Guid.Parse(showId) })).ToList();
        Assert.All(seats, seat => Assert.Equal("available", seat.Status));
        Assert.All(seats, seat => Assert.Null(seat.ReservationId));
        Assert.Equal(0, await QuotaAsync(connection, "u-cancel", showId));
        var stored = await StoredStatusAsync(connection, reservationId);
        Assert.Equal("cancelled", stored.Status);
        Assert.NotNull(stored.CancelledAt);

        var counts = (await _client.GetFromJsonAsync<JsonElement>($"/shows/{showId}")).GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("available").GetInt32());
        Assert.Equal(0, counts.GetProperty("held").GetInt32());
        Assert.Equal(0, counts.GetProperty("confirmed").GetInt32());
    }

    [Fact]
    public async Task CancelOthersReservation_404()
    {
        // C6 / I6: another user's reservation is indistinguishable from a missing one.
        var showId = await CreateShowAsync(["A1"]);
        var reservationId = await ReserveAsync("u-owner", showId, ["A1"]);

        var others = await CancelAsync("u-intruder", reservationId);
        var unknown = await CancelAsync("u-intruder", Guid.CreateVersion7().ToString());

        var othersBody = await AssertError(others, HttpStatusCode.NotFound, "reservation_not_found");
        var unknownBody = await AssertError(unknown, HttpStatusCode.NotFound, "reservation_not_found");
        Assert.Equal(unknownBody.GetProperty("message").GetString(), othersBody.GetProperty("message").GetString());
        Assert.Equal(
            unknownBody.EnumerateObject().Select(p => p.Name).Order(),
            othersBody.EnumerateObject().Select(p => p.Name).Order());

        await using var connection = await OpenAsync();
        Assert.Equal(Guid.Parse(reservationId), await SeatOwnerAsync(connection, showId, "A1"));
        Assert.Equal(1, await QuotaAsync(connection, "u-owner", showId));
        Assert.Equal("confirmed", (await StoredStatusAsync(connection, reservationId)).Status);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("01a10127-8485-7ca6-adf8-000000000000")]
    public async Task UnknownOrMalformedId_404(string reservationId)
    {
        var response = await CancelAsync("u-unknown", reservationId);

        await AssertError(response, HttpStatusCode.NotFound, "reservation_not_found");
    }

    [Fact]
    public async Task NoToken_401()
    {
        var showId = await CreateShowAsync(["A1"]);
        var reservationId = await ReserveAsync("u-notoken", showId, ["A1"]);

        var response = await _client.PostAsync($"/reservations/{reservationId}/cancel", content: null);

        await AssertError(response, HttpStatusCode.Unauthorized, "unauthorized");
        await using var connection = await OpenAsync();
        Assert.Equal(Guid.Parse(reservationId), await SeatOwnerAsync(connection, showId, "A1"));
    }

    [Fact]
    public async Task DoubleCancel_Returns200SameBody_DecrementsQuotaOnce()
    {
        var showId = await CreateShowAsync(["A1", "A2", "A3"]);
        var reservationId = await ReserveAsync("u-double", showId, ["A1", "A2"]);
        await ReserveAsync("u-double", showId, ["A3"], key: "k-other");

        var first = await CancelAsync("u-double", reservationId);
        await using var connection = await OpenAsync();
        var cancelledAt = (await StoredStatusAsync(connection, reservationId)).CancelledAt;
        var second = await CancelAsync("u-double", reservationId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        // Only the cancelled reservation's 2 seats came back; the other reservation's seat stays counted.
        Assert.Equal(1, await QuotaAsync(connection, "u-double", showId));
        Assert.Equal(cancelledAt, (await StoredStatusAsync(connection, reservationId)).CancelledAt);
        Assert.NotNull(await SeatOwnerAsync(connection, showId, "A3"));
    }

    [Fact]
    public async Task Cancel_FreesSeat_RebookByOtherUser_201()
    {
        var showId = await CreateShowAsync(["A1"]);
        var first = await ReserveAsync("u-first", showId, ["A1"]);
        Assert.Equal(HttpStatusCode.Conflict, (await PostReserveAsync("u-second", showId, ["A1"], "k")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await CancelAsync("u-first", first)).StatusCode);
        var rebooked = await PostReserveAsync("u-second", showId, ["A1"], "k");

        Assert.Equal(HttpStatusCode.Created, rebooked.StatusCode);
        var rebookedId = (await rebooked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reservation_id").GetString()!;
        await using var connection = await OpenAsync();
        Assert.Equal(Guid.Parse(rebookedId), await SeatOwnerAsync(connection, showId, "A1"));
        Assert.Equal(0, await QuotaAsync(connection, "u-first", showId));
        Assert.Equal(1, await QuotaAsync(connection, "u-second", showId));
    }

    [Fact]
    public async Task CancelAgainAfterRebook_Returns200_NewOwnerKeepsSeat()
    {
        // A late retry of a cancel must not touch the seat or quota of whoever rebooked it (I8).
        var showId = await CreateShowAsync(["A1"]);
        var first = await ReserveAsync("u-orig", showId, ["A1"]);
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync("u-orig", first)).StatusCode);
        var rebooked = await ReserveAsync("u-new", showId, ["A1"]);

        var again = await CancelAsync("u-orig", first);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("cancelled", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        await using var connection = await OpenAsync();
        Assert.Equal(Guid.Parse(rebooked), await SeatOwnerAsync(connection, showId, "A1"));
        Assert.Equal(0, await QuotaAsync(connection, "u-orig", showId));
        Assert.Equal(1, await QuotaAsync(connection, "u-new", showId));
        Assert.Equal("confirmed", (await StoredStatusAsync(connection, rebooked)).Status);
    }

    [Fact]
    public async Task CancelAtLimit_ThenReserveAgain_Succeeds()
    {
        // D-09: cancel gives the seats back to the per-user limit.
        var showId = await CreateShowAsync(["A1", "A2", "A3"], perUserLimit: 2);
        var reservationId = await ReserveAsync("u-limit", showId, ["A1", "A2"]);
        Assert.Equal(HttpStatusCode.Conflict, (await PostReserveAsync("u-limit", showId, ["A3"], "k-2")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await CancelAsync("u-limit", reservationId)).StatusCode);
        var again = await PostReserveAsync("u-limit", showId, ["A2", "A3"], "k-3");

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        await using var connection = await OpenAsync();
        Assert.Equal(2, await QuotaAsync(connection, "u-limit", showId));
    }

    [Fact]
    public async Task ReplayOriginalKeyAfterCancel_Returns201Cancelled_ReservesNothing()
    {
        // D-08: a retry of the original reserve returns the reservation as it is now.
        var showId = await CreateShowAsync(["A1"]);
        var reservationId = await ReserveAsync("u-replay", showId, ["A1"], key: "k-orig");
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync("u-replay", reservationId)).StatusCode);

        var replay = await PostReserveAsync("u-replay", showId, ["A1"], "k-orig");

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        var body = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reservationId, body.GetProperty("reservation_id").GetString());
        Assert.Equal("cancelled", body.GetProperty("status").GetString());
        await using var connection = await OpenAsync();
        Assert.Null(await SeatOwnerAsync(connection, showId, "A1"));
        Assert.Equal(0, await QuotaAsync(connection, "u-replay", showId));
    }

    [Fact]
    public async Task SeatNoLongerOwned_Returns409Contention_AndChangesNothing()
    {
        // I8 / D-17: if a seat is (somehow) owned by another reservation, cancel must not free it,
        // and must not free the rest either: the whole transaction rolls back.
        var showId = await CreateShowAsync(["A1", "A2"]);
        var reservationId = await ReserveAsync("u-guard", showId, ["A1", "A2"]);
        var otherReservation = Guid.CreateVersion7();
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE seats SET reservation_id = @Other WHERE show_id = @ShowId AND seat_no = 'A2'",
            new { Other = otherReservation, ShowId = Guid.Parse(showId) });

        var response = await CancelAsync("u-guard", reservationId);

        var body = await AssertError(response, HttpStatusCode.Conflict, "contention");
        Assert.True(body.GetProperty("retryable").GetBoolean());
        Assert.Equal(Guid.Parse(reservationId), await SeatOwnerAsync(connection, showId, "A1"));
        Assert.Equal(otherReservation, await SeatOwnerAsync(connection, showId, "A2"));
        Assert.Equal(2, await QuotaAsync(connection, "u-guard", showId));
        Assert.Equal("confirmed", (await StoredStatusAsync(connection, reservationId)).Status);
    }

    [Fact]
    public async Task QuotaBelowSeatCount_Returns409Contention_AndChangesNothing()
    {
        // The quota guard: a short quota row rolls back instead of tripping ck_quota_nonneg (a 500).
        var showId = await CreateShowAsync(["A1", "A2"]);
        var reservationId = await ReserveAsync("u-short", showId, ["A1", "A2"]);
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE user_show_quota SET seat_count = 1 WHERE user_id = 'u-short' AND show_id = @ShowId",
            new { ShowId = Guid.Parse(showId) });

        var response = await CancelAsync("u-short", reservationId);

        await AssertError(response, HttpStatusCode.Conflict, "contention");
        Assert.Equal(Guid.Parse(reservationId), await SeatOwnerAsync(connection, showId, "A1"));
        Assert.Equal(Guid.Parse(reservationId), await SeatOwnerAsync(connection, showId, "A2"));
        Assert.Equal(1, await QuotaAsync(connection, "u-short", showId));
        Assert.Equal("confirmed", (await StoredStatusAsync(connection, reservationId)).Status);
    }

    private async Task<HttpResponseMessage> CancelAsync(string userId, string reservationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/reservations/{reservationId}/cancel");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(userId, "user"));
        return await _client.SendAsync(request);
    }

    private async Task<string> ReserveAsync(string userId, string showId, string[] seats, string key = "k")
    {
        var response = await PostReserveAsync(userId, showId, seats, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reservation_id").GetString()!;
    }

    private async Task<HttpResponseMessage> PostReserveAsync(string userId, string showId, string[] seats, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{showId}/reserve")
        {
            Content = JsonContent.Create(new { seats, idempotency_key = key }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(userId, "user"));
        return await _client.SendAsync(request);
    }

    private async Task<string> CreateShowAsync(string[] seats, long pricePaise = 1000, int perUserLimit = 4)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = JsonContent.Create(new { name = "cancel-test", seats, price_paise = pricePaise, per_user_limit = perUserLimit }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("admin-1", "admin"));

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("show_id").GetString()!;
    }

    private string Token(string userId, string role) =>
        _factory.Services.GetRequiredService<JwtTokenIssuer>().IssueToken(userId, role).AccessToken;

    private static async Task<JsonElement> AssertError(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reason, body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.Equal(response.Headers.GetValues("X-Request-Id").Single(), body.GetProperty("request_id").GetString());
        return body;
    }

    private static List<string> Seats(JsonElement body) =>
        body.GetProperty("seats").EnumerateArray().Select(seat => seat.GetString()!).ToList();

    private async Task<MySqlConnection> OpenAsync()
    {
        // Same Guid format as the app, so Guid parameters bind as BINARY(16).
        var connection = new MySqlConnection(MySqlConnectionStrings.WithGuidFormat(_factory.DirectConnectionString));
        await connection.OpenAsync();
        return connection;
    }

    private static Task<int> QuotaAsync(MySqlConnection connection, string userId, string showId) =>
        connection.ExecuteScalarAsync<int>(
            "SELECT COALESCE((SELECT seat_count FROM user_show_quota WHERE user_id = @UserId AND show_id = @ShowId), 0)",
            new { UserId = userId, ShowId = Guid.Parse(showId) });

    private static Task<Guid?> SeatOwnerAsync(MySqlConnection connection, string showId, string seatNo) =>
        connection.ExecuteScalarAsync<Guid?>(
            "SELECT reservation_id FROM seats WHERE show_id = @ShowId AND seat_no = @SeatNo",
            new { ShowId = Guid.Parse(showId), SeatNo = seatNo });

    private static Task<(string Status, DateTime? CancelledAt)> StoredStatusAsync(MySqlConnection connection, string reservationId) =>
        connection.QuerySingleAsync<(string Status, DateTime? CancelledAt)>(
            "SELECT status, cancelled_at FROM reservations WHERE reservation_id = @ReservationId",
            new { ReservationId = Guid.Parse(reservationId) });
}
