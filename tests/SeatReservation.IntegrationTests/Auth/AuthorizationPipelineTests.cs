using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SeatReservation.IntegrationTests.Auth;

public sealed class AuthorizationPipelineTests : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client;

    public AuthorizationPipelineTests(AuthApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var response = await _client.GetAsync("/__test/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized", body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("request_id").GetString()));
    }

    [Fact]
    public async Task UserToken_OnAdminRoute_Returns403()
    {
        var token = await IssueTokenAsync("u-1", "user");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/admin-only");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AdminToken_OnAdminRoute_Returns200()
    {
        var token = await IssueTokenAsync("admin-1", "admin", AuthApiFactory.AdminSecret);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/admin-only");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UserToken_OnWhoAmI_ReturnsSubAndRoleFromClaims()
    {
        var token = await IssueTokenAsync("u-42", "user");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("u-42", body.GetProperty("user_id").GetString());
        Assert.Equal("user", body.GetProperty("role").GetString());
    }

    private async Task<string> IssueTokenAsync(string userId, string role, string? adminSecret = null)
    {
        var response = await _client.PostAsJsonAsync("/auth/token", new
        {
            user_id = userId,
            role,
            admin_secret = adminSecret,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }
}
