using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.IntegrationTests.Shows;

namespace SeatReservation.IntegrationTests.Reservations;

/// <summary>
/// Functional coverage of POST /shows/{id}/reserve: every outcome in 03-api-contract.md and the
/// check precedence, asserted against both the HTTP response and the DB. Concurrency (C1, C4, C5,
/// C6, overlapping multi-seat) is proven separately. Each test creates its own show.
/// </summary>
[Collection("ApiHost")]
public sealed class ReserveTests : IClassFixture<ShowsApiFactory>
{
    private readonly ShowsApiFactory _factory;
    private readonly HttpClient _client;

    public ReserveTests(ShowsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Reserve_ConfirmsSeats_AndCountsReconcile()
    {
        var showId = await CreateShowAsync(["A1", "A2", "A3"], pricePaise: 25000);
        var token = await UserTokenAsync("u-happy");

        var response = await ReserveAsync(token, showId, new { seats = new[] { "a2", " A1 " }, idempotency_key = "k-1" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var reservationId = body.GetProperty("reservation_id").GetString()!;
        Assert.Equal(showId, body.GetProperty("show_id").GetString());
        Assert.Equal("u-happy", body.GetProperty("user_id").GetString());
        Assert.Equal(["A1", "A2"], Seats(body));
        Assert.Equal(50000, body.GetProperty("amount_paise").GetInt64());
        Assert.Equal("confirmed", body.GetProperty("status").GetString());

        await using var connection = await OpenAsync();
        var owners = (await connection.QueryAsync<(string SeatNo, string Status, Guid? ReservationId)>(
            "SELECT seat_no, status, reservation_id FROM seats WHERE show_id = @ShowId ORDER BY seat_no",
            new { ShowId = Guid.Parse(showId) })).ToList();
        Assert.Equal(("A1", "confirmed", (Guid?)Guid.Parse(reservationId)), owners[0]);
        Assert.Equal(("A2", "confirmed", (Guid?)Guid.Parse(reservationId)), owners[1]);
        Assert.Equal(("A3", "available", (Guid?)null), owners[2]);
        Assert.Equal(2, await QuotaAsync(connection, "u-happy", showId));

        var counts = (await (await _client.GetAsync($"/shows/{showId}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("available").GetInt32());
        Assert.Equal(0, counts.GetProperty("held").GetInt32());
        Assert.Equal(2, counts.GetProperty("confirmed").GetInt32());
    }

    [Fact]
    public async Task IdempotencyKey_FromHeaderOnly_IsAccepted()
    {
        var showId = await CreateShowAsync(["A1"]);

        var response = await ReserveAsync(await UserTokenAsync("u-header"), showId, new { seats = new[] { "A1" } }, headerKey: "hk-1");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task IdempotencyKey_HeaderAndBodyDiffer_Returns400Conflict()
    {
        var showId = await CreateShowAsync(["A1"]);

        var response = await ReserveAsync(
            await UserTokenAsync("u-conflict"), showId, new { seats = new[] { "A1" }, idempotency_key = "body" }, headerKey: "header");

        await AssertError(response, HttpStatusCode.BadRequest, "idempotency_key_conflict");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("café")]
    public async Task IdempotencyKey_MissingOrMalformed_Returns400(string? key)
    {
        var showId = await CreateShowAsync(["A1"]);

        var response = await ReserveAsync(await UserTokenAsync("u-badkey"), showId, new { seats = new[] { "A1" }, idempotency_key = key });

        await AssertError(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task IdempotencyKey_Of129Chars_Returns400_But128IsAccepted()
    {
        var showId = await CreateShowAsync(["A1"]);
        var token = await UserTokenAsync("u-longkey");

        var tooLong = await ReserveAsync(token, showId, new { seats = new[] { "A1" }, idempotency_key = new string('k', 129) });
        var maxLength = await ReserveAsync(token, showId, new { seats = new[] { "A1" }, idempotency_key = new string('k', 128) });

        await AssertError(tooLong, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(HttpStatusCode.Created, maxLength.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A-1")]
    [InlineData("A1,a1")]
    [InlineData("ABCDEFGHIJK")]
    public async Task InvalidSeats_Return400(string seatsCsv)
    {
        var showId = await CreateShowAsync(["A1"]);
        var seats = seatsCsv.Length == 0 ? [] : seatsCsv.Split(',');

        var response = await ReserveAsync(await UserTokenAsync("u-badseat"), showId, new { seats, idempotency_key = "k" });

        await AssertError(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("01a10127-8485-7ca6-adf8-000000000000")]
    public async Task UnknownOrMalformedShow_Returns404(string showId)
    {
        var response = await ReserveAsync(await UserTokenAsync("u-noshow"), showId, new { seats = new[] { "A1" }, idempotency_key = "k" });

        await AssertError(response, HttpStatusCode.NotFound, "show_not_found");
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var showId = await CreateShowAsync(["A1"]);

        var response = await _client.PostAsJsonAsync($"/shows/{showId}/reserve", new { seats = new[] { "A1" }, idempotency_key = "k" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MoreSeatsThanLimit_Returns409PerUserLimit_Early()
    {
        var showId = await CreateShowAsync(["A1", "A2", "A3"], perUserLimit: 2);

        var response = await ReserveAsync(await UserTokenAsync("u-early"), showId, new { seats = new[] { "A1", "A2", "A3" }, idempotency_key = "k" });

        var body = await AssertError(response, HttpStatusCode.Conflict, "per_user_limit");
        Assert.Equal(2, body.GetProperty("limit").GetInt32());
        Assert.Equal(0, body.GetProperty("current").GetInt32());
    }

    [Fact]
    public async Task ExceedingQuotaAcrossRequests_Returns409PerUserLimit_WithCurrent()
    {
        var showId = await CreateShowAsync(["A1", "A2", "A3", "A4", "A5"]);
        var token = await UserTokenAsync("u-quota");
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(token, showId, new { seats = new[] { "A1", "A2", "A3" }, idempotency_key = "k-1" })).StatusCode);

        var response = await ReserveAsync(token, showId, new { seats = new[] { "A4", "A5" }, idempotency_key = "k-2" });

        var body = await AssertError(response, HttpStatusCode.Conflict, "per_user_limit");
        Assert.Equal(4, body.GetProperty("limit").GetInt32());
        Assert.Equal(3, body.GetProperty("current").GetInt32());
        await using var connection = await OpenAsync();
        Assert.Equal(3, await QuotaAsync(connection, "u-quota", showId));
        Assert.Equal("available", await SeatStatusAsync(connection, showId, "A4"));
    }

    [Fact]
    public async Task UserAtLimit_AskingForUnknownSeat_GetsPerUserLimit()
    {
        // Precedence (03-api-contract.md): quota (6) is checked before unknown_seat (7).
        var showId = await CreateShowAsync(["A1"], perUserLimit: 1);
        var token = await UserTokenAsync("u-precedence");
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(token, showId, new { seats = new[] { "A1" }, idempotency_key = "k-1" })).StatusCode);

        var response = await ReserveAsync(token, showId, new { seats = new[] { "Z9" }, idempotency_key = "k-2" });

        await AssertError(response, HttpStatusCode.Conflict, "per_user_limit");
    }

    [Fact]
    public async Task UnknownSeat_Returns400_ListingOnlyMissingSeats_AndReservesNothing()
    {
        var showId = await CreateShowAsync(["A1", "A2"]);

        var response = await ReserveAsync(await UserTokenAsync("u-unknown"), showId, new { seats = new[] { "A1", "Z9", "Z8" }, idempotency_key = "k" });

        var body = await AssertError(response, HttpStatusCode.BadRequest, "unknown_seat");
        Assert.Equal(["Z8", "Z9"], Seats(body));
        await using var connection = await OpenAsync();
        Assert.Equal("available", await SeatStatusAsync(connection, showId, "A1"));
        Assert.Equal(0, await QuotaAsync(connection, "u-unknown", showId));
    }

    [Fact]
    public async Task PartlyTakenRequest_Returns409SeatTaken_AndReservesNothing()
    {
        var showId = await CreateShowAsync(["A1", "A2"]);
        Assert.Equal(
            HttpStatusCode.Created,
            (await ReserveAsync(await UserTokenAsync("u-first"), showId, new { seats = new[] { "A1" }, idempotency_key = "k" })).StatusCode);

        var response = await ReserveAsync(await UserTokenAsync("u-second"), showId, new { seats = new[] { "A1", "A2" }, idempotency_key = "k" });

        var body = await AssertError(response, HttpStatusCode.Conflict, "seat_taken");
        Assert.Equal(["A1"], Seats(body));
        await using var connection = await OpenAsync();
        Assert.Equal("available", await SeatStatusAsync(connection, showId, "A2"));
        Assert.Equal(0, await QuotaAsync(connection, "u-second", showId));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM reservations WHERE user_id = 'u-second'"));
    }

    [Fact]
    public async Task SameKeySameBody_ReplaysOriginal_WithoutReservingAgain()
    {
        var showId = await CreateShowAsync(["A1", "A2"]);
        var token = await UserTokenAsync("u-replay");
        var first = await (await ReserveAsync(token, showId, new { seats = new[] { "A1" }, idempotency_key = "k" })).Content.ReadFromJsonAsync<JsonElement>();

        var response = await ReserveAsync(token, showId, new { seats = new[] { "a1" }, idempotency_key = "k" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("Idempotent-Replayed").Single());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("reservation_id").GetString(), body.GetProperty("reservation_id").GetString());
        Assert.Equal(["A1"], Seats(body));
        Assert.Equal("confirmed", body.GetProperty("status").GetString());
        await using var connection = await OpenAsync();
        Assert.Equal(1, await QuotaAsync(connection, "u-replay", showId));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM reservations WHERE user_id = 'u-replay'"));
    }

    [Fact]
    public async Task SameKeyDifferentSeats_Returns409Mismatch()
    {
        var showId = await CreateShowAsync(["A1", "A2"]);
        var token = await UserTokenAsync("u-mismatch");
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(token, showId, new { seats = new[] { "A1" }, idempotency_key = "k" })).StatusCode);

        var response = await ReserveAsync(token, showId, new { seats = new[] { "A2" }, idempotency_key = "k" });

        await AssertError(response, HttpStatusCode.Conflict, "idempotency_mismatch");
        await using var connection = await OpenAsync();
        Assert.Equal("available", await SeatStatusAsync(connection, showId, "A2"));
    }

    [Fact]
    public async Task SameKeySameSeatsOnAnotherShow_Returns409Mismatch()
    {
        var firstShow = await CreateShowAsync(["A1"]);
        var secondShow = await CreateShowAsync(["A1"]);
        var token = await UserTokenAsync("u-othershow");
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(token, firstShow, new { seats = new[] { "A1" }, idempotency_key = "k" })).StatusCode);

        var response = await ReserveAsync(token, secondShow, new { seats = new[] { "A1" }, idempotency_key = "k" });

        await AssertError(response, HttpStatusCode.Conflict, "idempotency_mismatch");
    }

    [Fact]
    public async Task DeclinedAttempt_DoesNotStoreKey_SoARetryCanSucceed()
    {
        // D-08: the key row is inserted inside the transaction, so a decline rolls it back.
        var showId = await CreateShowAsync(["A1", "A2"]);
        Assert.Equal(
            HttpStatusCode.Created,
            (await ReserveAsync(await UserTokenAsync("u-holder"), showId, new { seats = new[] { "A1" }, idempotency_key = "k" })).StatusCode);
        var token = await UserTokenAsync("u-retry");

        var declined = await ReserveAsync(token, showId, new { seats = new[] { "A1" }, idempotency_key = "k-retry" });
        var retried = await ReserveAsync(token, showId, new { seats = new[] { "A2" }, idempotency_key = "k-retry" });

        await AssertError(declined, HttpStatusCode.Conflict, "seat_taken");
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(retried.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task ShowNotInMetadataCache_IsLoadedFromDb()
    {
        // A show inserted straight into MySQL was never cached by POST /shows - the same state
        // as every show's first reserve after an app restart.
        var showId = Guid.CreateVersion7();
        await using (var connection = await OpenAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO shows (show_id, name, price_paise, per_user_limit, total_seats) VALUES (@ShowId, 'cold', 700, 4, 1)",
                new { ShowId = showId });
            await connection.ExecuteAsync("INSERT INTO seats (show_id, seat_no) VALUES (@ShowId, 'A1')", new { ShowId = showId });
        }

        var response = await ReserveAsync(await UserTokenAsync("u-cold"), showId.ToString(), new { seats = new[] { "A1" }, idempotency_key = "k" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(700, body.GetProperty("amount_paise").GetInt64());
    }

    [Fact]
    public async Task TakenAndUnknownSeats_Returns400UnknownSeat()
    {
        // Precedence: unknown_seat (7) before seat_taken (8).
        var showId = await CreateShowAsync(["A1"]);
        Assert.Equal(
            HttpStatusCode.Created,
            (await ReserveAsync(await UserTokenAsync("u-taker"), showId, new { seats = new[] { "A1" }, idempotency_key = "k" })).StatusCode);

        var response = await ReserveAsync(await UserTokenAsync("u-both"), showId, new { seats = new[] { "A1", "Z9" }, idempotency_key = "k" });

        var body = await AssertError(response, HttpStatusCode.BadRequest, "unknown_seat");
        Assert.Equal(["Z9"], Seats(body));
    }

    [Fact]
    public async Task EarlyPerUserLimit_BeatsIdempotencyMismatch()
    {
        // Precedence: early per_user_limit (4) before replay/mismatch (5).
        var showId = await CreateShowAsync(["A1", "A2"], perUserLimit: 1);
        var token = await UserTokenAsync("u-early-vs-key");
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(token, showId, new { seats = new[] { "A1" }, idempotency_key = "k" })).StatusCode);

        var response = await ReserveAsync(token, showId, new { seats = new[] { "A1", "A2" }, idempotency_key = "k" });

        await AssertError(response, HttpStatusCode.Conflict, "per_user_limit");
    }

    [Fact]
    public async Task IdempotencyKey_SameInHeaderAndBody_IsAccepted()
    {
        var showId = await CreateShowAsync(["A1"]);

        var response = await ReserveAsync(
            await UserTokenAsync("u-samekey"), showId, new { seats = new[] { "A1" }, idempotency_key = "k-same" }, headerKey: "k-same");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task IdempotencyKeyHeader_SentTwice_Returns400()
    {
        var showId = await CreateShowAsync(["A1"]);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{showId}/reserve")
        {
            Content = JsonContent.Create(new { seats = new[] { "A1" } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync("u-twoheaders"));
        request.Headers.Add("Idempotency-Key", ["key-a", "key-b"]);

        var response = await _client.SendAsync(request);

        var body = await AssertError(response, HttpStatusCode.BadRequest, "invalid_request");
        // Asserted on the message so the test can't pass via the key-format check instead.
        Assert.Contains("at most one Idempotency-Key header", body.GetProperty("message").GetString());
    }

    private async Task<HttpResponseMessage> ReserveAsync(string token, string showId, object payload, string? headerKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{showId}/reserve")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (headerKey is not null)
        {
            request.Headers.Add("Idempotency-Key", headerKey);
        }

        return await _client.SendAsync(request);
    }

    private async Task<string> CreateShowAsync(string[] seats, long pricePaise = 1000, int perUserLimit = 4)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = JsonContent.Create(new { name = "reserve-test", seats, price_paise = pricePaise, per_user_limit = perUserLimit }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await IssueTokenAsync("admin-1", "admin", ShowsApiFactory.AdminSecret));

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("show_id").GetString()!;
    }

    private Task<string> UserTokenAsync(string userId) => IssueTokenAsync(userId, "user");

    private async Task<string> IssueTokenAsync(string userId, string role, string? adminSecret = null)
    {
        var response = await _client.PostAsJsonAsync("/auth/token", new { user_id = userId, role, admin_secret = adminSecret });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

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

    private static Task<string?> SeatStatusAsync(MySqlConnection connection, string showId, string seatNo) =>
        connection.ExecuteScalarAsync<string?>(
            "SELECT status FROM seats WHERE show_id = @ShowId AND seat_no = @SeatNo",
            new { ShowId = Guid.Parse(showId), SeatNo = seatNo });
}
