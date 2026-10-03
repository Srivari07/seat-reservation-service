using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SeatReservation.IntegrationTests.Auth;

public sealed class TokenEndpointTests : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client;

    public TokenEndpointTests(AuthApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task IssuesUserToken()
    {
        var response = await _client.PostAsJsonAsync("/auth/token", new { user_id = "u-123", role = "user" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("access_token").GetString()));
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(86400, body.GetProperty("expires_in").GetInt32());
        Assert.Equal("u-123", body.GetProperty("user_id").GetString());
        Assert.Equal("user", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task IssuesAdminToken_WithCorrectSecret()
    {
        var response = await _client.PostAsJsonAsync("/auth/token", new
        {
            user_id = "admin-1",
            role = "admin",
            admin_secret = AuthApiFactory.AdminSecret,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task RejectsAdminToken_WithWrongSecret()
    {
        var response = await _client.PostAsJsonAsync("/auth/token", new
        {
            user_id = "admin-1",
            role = "admin",
            admin_secret = "not-the-real-secret",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RejectsAdminToken_WithMissingSecret()
    {
        var response = await _client.PostAsJsonAsync("/auth/token", new { user_id = "admin-1", role = "admin" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("not an id!", "user")]
    [InlineData("u-1", "superuser")]
    public async Task RejectsInvalidRequest(string userId, string role)
    {
        var response = await _client.PostAsJsonAsync("/auth/token", new { user_id = userId, role });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
    }
}
