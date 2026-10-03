using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SeatReservation.IntegrationTests.Shows;

namespace SeatReservation.IntegrationTests.Docs;

// D-15. ShowsApiFactory runs as Production, so this also proves the docs aren't Development-only.
[Collection("ApiHost")]
public sealed class OpenApiTests : IClassFixture<ShowsApiFactory>
{
    private readonly HttpClient _client;

    public OpenApiTests(ShowsApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task OpenApiDocument_ListsEveryApiPath_WithBearerScheme()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        foreach (var path in new[] { "/auth/token", "/shows", "/shows/{id}", "/shows/{id}/reserve", "/reservations/{id}/cancel" })
        {
            Assert.True(paths.TryGetProperty(path, out _), $"missing path {path}");
        }

        var bearer = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task SwaggerUi_IsServed()
    {
        var response = await _client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("swagger-ui", await response.Content.ReadAsStringAsync());
    }
}
