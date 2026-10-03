using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MySqlConnector;
using SeatReservation.Api.Auth;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.IntegrationTests.Shows;

namespace SeatReservation.IntegrationTests.Auth;

/// <summary>
/// I6 / D-10: a validly signed token is only accepted if its "sub" is a well-formed user id
/// (the same rule as POST /auth/token). Otherwise it is 401 unauthorized, never a 500 from
/// MySQL rejecting the value (1267/1366) and never a match on another user under the column's
/// PAD SPACE collation ("u1 " = "u1"). Only a holder of the signing key can mint such a token.
/// </summary>
[Collection("ApiHost")]
public sealed class SubClaimValidationTests : IClassFixture<ShowsApiFactory>
{
    private readonly ShowsApiFactory _factory;
    private readonly HttpClient _client;

    public SubClaimValidationTests(ShowsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public static TheoryData<string?> MalformedSubs => new()
    {
        "u1 ",
        "u1\n",
        "ü",
        "",
        new string('a', 65),
        null,
    };

    [Theory]
    [MemberData(nameof(MalformedSubs))]
    public async Task Reserve_WithMalformedSub_Returns401(string? sub)
    {
        var showId = await CreateShowAsync();

        var response = await SendAsync(MintToken(sub), $"/shows/{showId}/reserve", new { seats = new[] { "A1" }, idempotency_key = UniqueKey() });

        await AssertUnauthorizedAsync(response);
        await using var connection = await OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM reservations WHERE show_id = @ShowId", new { ShowId = Guid.Parse(showId) }));
    }

    [Theory]
    [MemberData(nameof(MalformedSubs))]
    public async Task Cancel_WithMalformedSub_Returns401(string? sub)
    {
        var showId = await CreateShowAsync();
        var reservationId = await ReserveAsync("u1", showId);

        var response = await SendAsync(MintToken(sub), $"/reservations/{reservationId}/cancel", body: null);

        await AssertUnauthorizedAsync(response);
        await using var connection = await OpenAsync();
        Assert.Equal("confirmed", await connection.ExecuteScalarAsync<string>(
            "SELECT status FROM reservations WHERE reservation_id = @ReservationId", new { ReservationId = Guid.Parse(reservationId) }));
    }

    [Fact]
    public async Task WellFormedSub_IsStillAccepted()
    {
        var showId = await CreateShowAsync();

        var response = await SendAsync(MintToken("u_ok-1"), $"/shows/{showId}/reserve", new { seats = new[] { "A1" }, idempotency_key = UniqueKey() });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // Signs with the app's real key, bypassing /auth/token's own user_id check, the way a holder
    // of the signing key could. A null sub leaves the claim out entirely.
    private static string MintToken(string? sub)
    {
        var claims = new List<Claim> { new("role", "user") };
        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub));
        }

        var key = new SymmetricSecurityKey(Convert.FromBase64String(ShowsApiFactory.SigningKey));
        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<HttpResponseMessage> SendAsync(string token, string path, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    // Fresh per request: the same user + key on another show would be an idempotency mismatch.
    private static string UniqueKey() => Guid.NewGuid().ToString("N");

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized", body.GetProperty("error").GetString());
        Assert.Equal(response.Headers.GetValues("X-Request-Id").Single(), body.GetProperty("request_id").GetString());
    }

    private async Task<string> ReserveAsync(string userId, string showId)
    {
        var token = _factory.Services.GetRequiredService<JwtTokenIssuer>().IssueToken(userId, "user").AccessToken;
        var response = await SendAsync(token, $"/shows/{showId}/reserve", new { seats = new[] { "A1" }, idempotency_key = UniqueKey() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reservation_id").GetString()!;
    }

    private async Task<string> CreateShowAsync()
    {
        var admin = _factory.Services.GetRequiredService<JwtTokenIssuer>().IssueToken("admin-1", "admin").AccessToken;
        var response = await SendAsync(admin, "/shows", new { name = "sub-claim-test", seats = new[] { "A1" }, price_paise = 1000 });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("show_id").GetString()!;
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        // Same Guid format as the app, so Guid parameters bind as BINARY(16).
        var connection = new MySqlConnection(MySqlConnectionStrings.WithGuidFormat(_factory.DirectConnectionString));
        await connection.OpenAsync();
        return connection;
    }
}
