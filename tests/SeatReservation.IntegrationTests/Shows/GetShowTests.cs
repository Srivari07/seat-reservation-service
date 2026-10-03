using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SeatReservation.IntegrationTests.Shows;

[Collection("ApiHost")]
public sealed class GetShowTests : IClassFixture<ShowsApiFactory>
{
    private readonly HttpClient _client;

    public GetShowTests(ShowsApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ReturnsShow_NoAuthRequired()
    {
        var showId = await CreateShowAsync(["A1", "A2"], pricePaise: 5000);

        var response = await _client.GetAsync($"/shows/{showId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(showId, body.GetProperty("show_id").GetString());
        Assert.Equal(2, body.GetProperty("total_seats").GetInt32());
        var counts = body.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("available").GetInt32());
        Assert.Equal(0, counts.GetProperty("held").GetInt32());
        Assert.Equal(0, counts.GetProperty("confirmed").GetInt32());

        // I3: available + held + confirmed == total_seats, straight off this single read.
        Assert.Equal(
            body.GetProperty("total_seats").GetInt32(),
            counts.GetProperty("available").GetInt32() + counts.GetProperty("held").GetInt32() + counts.GetProperty("confirmed").GetInt32());
    }

    [Fact]
    public async Task UnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/shows/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("show_not_found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MalformedId_Returns404()
    {
        var response = await _client.GetAsync("/shows/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("show_not_found", body.GetProperty("error").GetString());
    }

    private async Task<string> CreateShowAsync(string[] seats, long pricePaise)
    {
        var token = await IssueTokenAsync("admin-1", "admin", ShowsApiFactory.AdminSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = JsonContent.Create(new { name = "a-show", seats, price_paise = pricePaise }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("show_id").GetString()!;
    }

    private async Task<string> IssueTokenAsync(string userId, string role, string? adminSecret = null)
    {
        var response = await _client.PostAsJsonAsync("/auth/token", new { user_id = userId, role, admin_secret = adminSecret });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }
}
