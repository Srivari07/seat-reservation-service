using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SeatReservation.IntegrationTests.Shows;

// Gets its own ShowsApiFactory instance (IClassFixture is per-class), so stopping its MySQL
// container has no effect on any other test class.
[Collection("ApiHost")]
public sealed class DbUnavailableTests : IClassFixture<ShowsApiFactory>
{
    private readonly ShowsApiFactory _factory;
    private readonly HttpClient _client;

    public DbUnavailableTests(ShowsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetShow_WhenMySqlIsUnreachable_Returns503WithMatchingRequestId()
    {
        await _factory.StopDatabaseAsync();

        var response = await _client.GetAsync($"/shows/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("db_unavailable", body.GetProperty("error").GetString());
        var requestId = body.GetProperty("request_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        Assert.Equal(requestId, response.Headers.GetValues("X-Request-Id").Single());
    }
}
