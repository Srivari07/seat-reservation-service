using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;

namespace SeatReservation.IntegrationTests.Shows;

[Collection("ApiHost")]
public sealed class CreateShowTests : IClassFixture<ShowsApiFactory>
{
    private readonly ShowsApiFactory _factory;
    private readonly HttpClient _client;

    public CreateShowTests(ShowsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreatesShow_WithAdminToken_AndNormalizesSeats()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "friday-night", seats = new[] { "a2", "A1", "a10" }, price_paise = 25000 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("show_id").GetString()));
        Assert.Equal("friday-night", body.GetProperty("name").GetString());
        Assert.Equal(25000, body.GetProperty("price_paise").GetInt64());
        Assert.Equal(4, body.GetProperty("per_user_limit").GetInt32());
        Assert.Equal(3, body.GetProperty("total_seats").GetInt32());

        var counts = body.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("available").GetInt32());
        Assert.Equal(0, counts.GetProperty("held").GetInt32());
        Assert.Equal(0, counts.GetProperty("confirmed").GetInt32());

        var seats = body.GetProperty("seats").EnumerateArray().ToList();
        Assert.Equal(3, seats.Count);
        // Normalized to uppercase and sorted (ascii order matches the binary collation seat_no is stored with).
        Assert.Equal(["A1", "A10", "A2"], seats.Select(s => s.GetProperty("seat").GetString()));
        Assert.All(seats, s => Assert.Equal("available", s.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task HonorsExplicitPerUserLimit()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "limited-show", seats = new[] { "A1" }, price_paise = 1000, per_user_limit = 2 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("per_user_limit").GetInt32());
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = JsonContent.Create(new { name = "n", seats = new[] { "A1" }, price_paise = 100 }),
        };
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UserToken_Returns403()
    {
        var response = await PostShowAsync(
            await UserTokenAsync("u-1"),
            new { name = "n", seats = new[] { "A1" }, price_paise = 100 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RejectsEmptyName()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "", seats = new[] { "A1" }, price_paise = 100 });

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsEmptySeats()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "n", seats = Array.Empty<string>(), price_paise = 100 });

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsMoreThan10000Seats()
    {
        var seats = Enumerable.Range(0, 10_001).Select(_ => "A1").ToArray();
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "n", seats, price_paise = 100 });

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsBadSeatFormat()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "n", seats = new[] { "seat with spaces" }, price_paise = 100 });

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsDuplicateSeatsAfterNormalization()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "n", seats = new[] { "A1", "a1" }, price_paise = 100 });

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsNegativePrice()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "n", seats = new[] { "A1" }, price_paise = -1 });

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsFractionalPrice()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "n", seats = new[] { "A1" }, price_paise = 100.5 });

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsPerUserLimitBelowOne()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "n", seats = new[] { "A1" }, price_paise = 100, per_user_limit = 0 });

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsNameLongerThan200Characters()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = new string('x', 201), seats = new[] { "A1" }, price_paise = 100 });

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsMalformedJson()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = new StringContent("{not valid json", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());

        var response = await _client.SendAsync(request);

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task RejectsNonNumericPrice()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = new StringContent("""{"name":"n","seats":["A1"],"price_paise":"abc"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());

        var response = await _client.SendAsync(request);

        await AssertInvalidRequest(response);
    }

    [Fact]
    public async Task Accepts10000DistinctSeats()
    {
        var seats = Enumerable.Range(0, 10_000).Select(i => $"S{i:D4}").ToArray();

        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "max-seats", seats, price_paise = 100 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10_000, body.GetProperty("total_seats").GetInt32());
    }

    [Fact]
    public async Task CreatesShowWith1001Seats_SpanningInsertBatches_AndReconciles()
    {
        // SeatInsertBatchSize is 500, so 1001 seats spans three INSERT batches.
        var seats = Enumerable.Range(0, 1001).Select(i => $"S{i:D4}").ToArray();

        var createResponse = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "big-show", seats, price_paise = 500 });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var showId = created.GetProperty("show_id").GetString();

        var getResponse = await _client.GetAsync($"/shows/{showId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1001, body.GetProperty("total_seats").GetInt32());
        Assert.Equal(1001, body.GetProperty("counts").GetProperty("available").GetInt32());
        Assert.Equal(1001, body.GetProperty("seats").GetArrayLength());
    }

    [Fact]
    public async Task StoresShowIdInBigEndianByteOrder()
    {
        var response = await PostShowAsync(
            await AdminTokenAsync(),
            new { name = "big-endian-check", seats = new[] { "A1" }, price_paise = 100 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var showId = body.GetProperty("show_id").GetString();

        // UUID_TO_BIN(x) (no swap) is what Guid Format=Binary16 is supposed to match (02-schema.md:
        // UUIDv7 is stored so its time-ordering holds). If the byte order were wrong (e.g. plain
        // Guid.ToByteArray()'s little-endian layout), this row would not be found by its own id.
        await using var connection = new MySqlConnection(_factory.DirectConnectionString);
        await connection.OpenAsync();
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM shows WHERE show_id = UUID_TO_BIN(@Id)",
            new { Id = showId });

        Assert.Equal(1, count);
    }

    private static async Task AssertInvalidRequest(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        var requestId = body.GetProperty("request_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestId));

        // Guards the context.Items/OnStarting plumbing RequestIdMiddleware and ApiError.Write
        // share: on an exception-handled path (malformed JSON, non-numeric price) these two
        // must still agree, not just each individually be non-empty.
        Assert.Equal(requestId, response.Headers.GetValues("X-Request-Id").Single());
    }

    private async Task<HttpResponseMessage> PostShowAsync(string token, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private Task<string> AdminTokenAsync() => IssueTokenAsync("admin-1", "admin", ShowsApiFactory.AdminSecret);

    private Task<string> UserTokenAsync(string userId) => IssueTokenAsync(userId, "user");

    private async Task<string> IssueTokenAsync(string userId, string role, string? adminSecret = null)
    {
        var response = await _client.PostAsJsonAsync("/auth/token", new { user_id = userId, role, admin_secret = adminSecret });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }
}
