using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Burst;

public sealed record ApiResult<T>(int StatusCode, T? Value, ApiErrorBody? Error, bool TransportFailed, string? TransportError)
{
    public bool Success => !TransportFailed && StatusCode is >= 200 and < 300;
}

public sealed record ReserveResult(
    int StatusCode, ReservationResponse? Value, ApiErrorBody? Error, bool Replayed, bool TransportFailed, string? TransportError)
{
    public bool Success => !TransportFailed && StatusCode is >= 200 and < 300;
}

// One HttpClient for the whole run, gated by one shared semaphore so total in-flight requests
// across every call kind (mint, show, reserve, cancel, metrics) never exceeds --concurrency.
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate;
    private readonly GlobalErrorTally errors;

    public ApiClient(Uri baseUrl, int concurrency, GlobalErrorTally errors)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = concurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        http = new HttpClient(handler) { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
        gate = new SemaphoreSlim(concurrency);
        this.errors = errors;
    }

    public void Dispose()
    {
        http.Dispose();
        gate.Dispose();
    }

    public Task<ApiResult<TokenResponse>> MintTokenAsync(string userId, string role, string? adminSecret, CancellationToken ct) =>
        SendJsonAsync<TokenResponse>(HttpMethod.Post, "/auth/token", new TokenRequest(userId, role, adminSecret), null, ct);

    public Task<ApiResult<ShowResponse>> CreateShowAsync(string adminToken, CreateShowRequest request, CancellationToken ct) =>
        SendJsonAsync<ShowResponse>(HttpMethod.Post, "/shows", request, adminToken, ct);

    public Task<ApiResult<ShowResponse>> GetShowAsync(string showId, CancellationToken ct) =>
        SendJsonAsync<ShowResponse>(HttpMethod.Get, $"/shows/{showId}", null, null, ct);

    public async Task<ReserveResult> ReserveAsync(string userToken, string showId, object body, CancellationToken ct)
    {
        var raw = await SendRawAsync(HttpMethod.Post, $"/shows/{showId}/reserve", body, userToken, ct);
        if (raw.TransportFailed)
        {
            return new ReserveResult(0, null, null, false, true, raw.TransportError);
        }

        var value = raw.StatusCode is >= 200 and < 300 ? Deserialize<ReservationResponse>(raw.Body) : null;
        var error = raw.StatusCode is >= 200 and < 300 ? null : Deserialize<ApiErrorBody>(raw.Body);
        return new ReserveResult(raw.StatusCode, value, error, raw.ReplayedHeader, false, null);
    }

    public Task<ApiResult<ReservationResponse>> CancelAsync(string userToken, string reservationId, CancellationToken ct) =>
        SendJsonAsync<ReservationResponse>(HttpMethod.Post, $"/reservations/{reservationId}/cancel", null, userToken, ct);

    public async Task<string> ScrapeMetricsAsync(CancellationToken ct)
    {
        var raw = await SendRawAsync(HttpMethod.Get, "/metrics", null, null, ct);
        return raw.TransportFailed ? string.Empty : raw.Body;
    }

    private async Task<ApiResult<T>> SendJsonAsync<T>(HttpMethod method, string path, object? body, string? bearerToken, CancellationToken ct)
    {
        var raw = await SendRawAsync(method, path, body, bearerToken, ct);
        if (raw.TransportFailed)
        {
            return new ApiResult<T>(0, default, null, true, raw.TransportError);
        }

        if (raw.StatusCode is >= 200 and < 300)
        {
            return new ApiResult<T>(raw.StatusCode, Deserialize<T>(raw.Body), null, false, null);
        }

        return new ApiResult<T>(raw.StatusCode, default, Deserialize<ApiErrorBody>(raw.Body), false, null);
    }

    private sealed record RawResult(int StatusCode, string Body, bool ReplayedHeader, bool TransportFailed, string? TransportError);

    private async Task<RawResult> SendRawAsync(HttpMethod method, string path, object? body, string? bearerToken, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json.Options);
            }

            if (bearerToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var replayed = response.Headers.TryGetValues("Idempotent-Replayed", out var values) && values.Contains("true");

            if ((int)response.StatusCode >= 500)
            {
                errors.RecordFiveXx();
            }

            return new RawResult((int)response.StatusCode, text, replayed, false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            errors.RecordTransportError();
            return new RawResult(0, string.Empty, false, true, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
