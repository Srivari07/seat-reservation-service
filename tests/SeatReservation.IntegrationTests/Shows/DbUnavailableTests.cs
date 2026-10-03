using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

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

// A separate class from DbUnavailableTests: IClassFixture is shared across every [Fact] in one
// class, so a second DB-stopping test in that class would find the container already stopped by
// whichever test happened to run first. This gets its own ShowsApiFactory/MySQL container instead.
[Collection("ApiHost")]
public sealed class DbUnavailableMetricsTests : IClassFixture<ShowsApiFactory>
{
    private readonly ShowsApiFactory _factory;
    private readonly HttpClient _client;

    public DbUnavailableMetricsTests(ShowsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // Program.cs: UseExceptionHandler clears the resolved routing endpoint before ApiExceptionHandler
    // runs, so without the IExceptionHandlerFeature fallback this 503 would record as endpoint="" -
    // exactly the kind of handled error 05-observability.md's per-endpoint latency/error alert needs
    // attributed correctly. Also proves ShowGaugeCollector clears a show's gauges (not just freezes
    // them) once the DB is unreachable.
    [Fact]
    public async Task GetShow_WhenMySqlIsUnreachable_RecordsEndpointLabelAndClearsGauges()
    {
        var showId = await CreateShowAsync(["A1"]);

        await _factory.StopDatabaseAsync();

        var response = await _client.GetAsync($"/shows/{showId}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var scrapeResponse = await _client.GetAsync("/metrics");
        scrapeResponse.EnsureSuccessStatusCode();
        var scrape = await scrapeResponse.Content.ReadAsStringAsync();

        Assert.True(
            MetricLineHasLabels(scrape, "http_requests_received_total", ("code", "503"), ("method", "GET"), ("endpoint", "/shows/{id}")),
            "Expected a 503 http_requests_received_total line labelled with the real endpoint, not endpoint=\"\".");
        Assert.DoesNotContain($"seats_available{{show_id=\"{showId}\"}}", scrape, StringComparison.Ordinal);
    }

    private static bool MetricLineHasLabels(string scrape, string name, params (string Key, string Val)[] labels)
    {
        // Greedy ".*", not "[^}]*": an endpoint label value like "/shows/{id}" contains its own "}"
        // before the label block's real closing brace, so a non-greedy/negated-class match would
        // stop there and never match the line at all. Greedy backtracks to the rightmost "} value".
        foreach (Match line in Regex.Matches(scrape, $@"^{Regex.Escape(name)}\{{.*\}} \S+$", RegexOptions.Multiline))
        {
            if (labels.All(l => line.Value.Contains($"{l.Key}=\"{l.Val}\"", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> CreateShowAsync(string[] seats)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = JsonContent.Create(new { name = "db-unavailable-test", seats, price_paise = 1000 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("show_id").GetString()!;
    }

    private async Task<string> AdminTokenAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/auth/token", new { user_id = "admin-1", role = "admin", admin_secret = ShowsApiFactory.AdminSecret });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }
}
