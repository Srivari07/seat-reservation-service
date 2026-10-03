using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using SeatReservation.IntegrationTests.Shows;

namespace SeatReservation.IntegrationTests.Metrics;

/// <summary>
/// Phase 7's stated Accept criterion: after a scripted set of requests, /metrics' deltas equal the
/// HTTP outcomes, and seats_available equals GET /shows/{id}. Reuses ShowsApiFactory (same pattern
/// as Reservations/ReserveTests and CancelTests) rather than a dedicated factory - it's already a
/// real host with real reserve/cancel/show endpoints, nothing metrics-specific needed from it.
/// </summary>
[Collection("ApiHost")]
public sealed class MetricsTests : IClassFixture<ShowsApiFactory>
{
    private readonly HttpClient _client;

    public MetricsTests(ShowsApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task MetricsReconcileWithHttpOutcomesAndShowState()
    {
        var showId = await CreateShowAsync(["A1", "A2"], pricePaise: 1000);

        // 1. Confirm a reservation for A1.
        var reserveResponse = await ReserveAsync(await UserTokenAsync("u-metrics-1"), showId, "A1", "k1");
        Assert.Equal(HttpStatusCode.Created, reserveResponse.StatusCode);
        var reservationId = (await reserveResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reservation_id").GetString()!;

        var afterConfirm = await ScrapeAsync();
        Assert.Contains("http_requests_received_total", afterConfirm);
        AssertMetric(1, afterConfirm, "reservations_confirmed_total", ("show_id", showId));
        AssertMetric(1, afterConfirm, "seats_available", ("show_id", showId));
        AssertMetric(1, afterConfirm, "seats_by_status", ("show_id", showId), ("status", "available"));
        AssertMetric(0, afterConfirm, "seats_by_status", ("show_id", showId), ("status", "held"));
        AssertMetric(1, afterConfirm, "seats_by_status", ("show_id", showId), ("status", "confirmed"));
        AssertMetric(0, afterConfirm, "reconciliation_violation", ("show_id", showId));
        AssertMetric(await AvailableCountAsync(showId), afterConfirm, "seats_available", ("show_id", showId));

        // 2. Decline: a second user wants the same seat.
        var declineResponse = await ReserveAsync(await UserTokenAsync("u-metrics-2"), showId, "A1", "k2");
        Assert.Equal(HttpStatusCode.Conflict, declineResponse.StatusCode);

        var afterDecline = await ScrapeAsync();
        AssertMetric(1, afterDecline, "reservations_declined_total", ("show_id", showId), ("reason", "seat_taken"));

        // 3. Idempotent replay: the first user repeats their original request.
        var replayResponse = await ReserveAsync(await UserTokenAsync("u-metrics-1"), showId, "A1", "k1");
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.True(replayResponse.Headers.Contains("Idempotent-Replayed"));

        var afterReplay = await ScrapeAsync();
        // D-08: a replay is 201 over the wire but counts as a decline for metrics.
        AssertMetric(1, afterReplay, "reservations_declined_total", ("show_id", showId), ("reason", "idempotent_replay"));
        AssertMetric(1, afterReplay, "reservations_confirmed_total", ("show_id", showId));

        // 4. Cancel the original reservation.
        var cancelResponse = await CancelAsync("u-metrics-1", reservationId);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        var afterCancel = await ScrapeAsync();
        AssertMetric(1, afterCancel, "reservations_cancelled_total", ("show_id", showId));
        AssertMetric(2, afterCancel, "seats_available", ("show_id", showId));
        AssertMetric(0, afterCancel, "reconciliation_violation", ("show_id", showId));
        AssertMetric(await AvailableCountAsync(showId), afterCancel, "seats_available", ("show_id", showId));

        // 5. A second cancel of the same reservation is idempotent (CancelOutcome.AlreadyCancelled)
        // and must not double-count: the counter only moves on the first, state-changing cancel.
        var secondCancelResponse = await CancelAsync("u-metrics-1", reservationId);
        Assert.Equal(HttpStatusCode.OK, secondCancelResponse.StatusCode);

        var afterSecondCancel = await ScrapeAsync();
        AssertMetric(1, afterSecondCancel, "reservations_cancelled_total", ("show_id", showId));
    }

    // HttpMethods.IsGet/IsPost/IsHead (the first fix) ignore case, so a differently-cased or wholly
    // made-up method could still add its own http_requests_received_total series; Program.cs now
    // matches the method string exactly, which this proves end to end.
    [Fact]
    public async Task NonCanonicalOrUnknownHttpMethods_AddNoNewMetricSeries()
    {
        using (var lowerCasePost = new HttpRequestMessage(new HttpMethod("post"), "/health/live"))
        {
            await _client.SendAsync(lowerCasePost);
        }

        using (var bogus = new HttpRequestMessage(new HttpMethod("FOOBAR"), "/health/live"))
        {
            await _client.SendAsync(bogus);
        }

        var scrape = await ScrapeAsync();
        Assert.DoesNotContain("method=\"post\"", scrape, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"FOOBAR\"", scrape, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclineReasons_PerUserLimitAndUnknownSeatAndIdempotencyMismatch_AreMetered()
    {
        var showId = await CreateShowAsync(["A1", "A2", "A3"], pricePaise: 1000, perUserLimit: 1);
        var token = await UserTokenAsync("u-metrics-reasons");

        // Early per_user_limit decline: asking for more seats than the limit.
        var overLimit = await ReserveAsync(token, showId, ["A1", "A2"], "k-limit");
        Assert.Equal(HttpStatusCode.Conflict, overLimit.StatusCode);

        // unknown_seat: the seat doesn't exist in this show.
        var unknownSeat = await ReserveAsync(token, showId, ["Z9"], "k-unknown");
        Assert.Equal(HttpStatusCode.BadRequest, unknownSeat.StatusCode);

        // idempotency_mismatch: same key as a prior successful reservation, different seats.
        var first = await ReserveAsync(token, showId, ["A1"], "k-mismatch");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var mismatch = await ReserveAsync(token, showId, ["A2"], "k-mismatch");
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);

        var scrape = await ScrapeAsync();
        AssertMetric(1, scrape, "reservations_declined_total", ("show_id", showId), ("reason", "per_user_limit"));
        AssertMetric(1, scrape, "reservations_declined_total", ("show_id", showId), ("reason", "unknown_seat"));
        AssertMetric(1, scrape, "reservations_declined_total", ("show_id", showId), ("reason", "idempotency_mismatch"));
    }

    // H1 regression: Guid.TryParse accepts more than the canonical "D" format (upper-case, "N" with
    // no hyphens, etc.). The metric label must always be the canonical lower-case hyphenated form,
    // not whatever format the caller happened to send - otherwise one real show's series fragments
    // across every format variant, and since tokens are free (D-10), an attacker could grow the
    // label set without bound.
    [Fact]
    public async Task ReserveWithNonCanonicalShowIdFormat_MetersUnderCanonicalLabel()
    {
        var showId = await CreateShowAsync(["A1"], pricePaise: 1000);
        var nonCanonicalRoute = Guid.Parse(showId).ToString("N").ToUpperInvariant();
        Assert.NotEqual(showId, nonCanonicalRoute, StringComparer.Ordinal);

        var response = await ReserveAsync(await UserTokenAsync("u-metrics-format"), nonCanonicalRoute, "A1", "k-format");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var scrape = await ScrapeAsync();
        AssertMetric(1, scrape, "reservations_confirmed_total", ("show_id", showId));
        Assert.Null(MetricValueOrNull(scrape, "reservations_confirmed_total", ("show_id", nonCanonicalRoute)));
    }

    private async Task<string> ScrapeAsync()
    {
        var response = await _client.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    // Prometheus text exposition: `name{k="v",k2="v2"} 123`. A counter/gauge child is only emitted
    // once something has called WithLabels(...) for that exact label combination at least once, so
    // a missing series (e.g. ShowGaugeCollector never having set a gauge) must be distinguishable
    // from a genuine 0 - callers that need "present and equal to N" use AssertMetric.
    private static double? MetricValueOrNull(string scrape, string name, params (string Key, string Value)[] labels)
    {
        var labelPattern = string.Join(",", labels.Select(l => $"{Regex.Escape(l.Key)}=\"{Regex.Escape(l.Value)}\""));
        var pattern = $@"^{Regex.Escape(name)}\{{{labelPattern}\}} (?<value>\S+)$";
        var match = Regex.Match(scrape, pattern, RegexOptions.Multiline);
        return match.Success ? double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture) : null;
    }

    private static void AssertMetric(double expected, string scrape, string name, params (string Key, string Value)[] labels)
    {
        var labelText = string.Join(",", labels.Select(l => $"{l.Key}=\"{l.Value}\""));
        var value = MetricValueOrNull(scrape, name, labels);
        Assert.True(value.HasValue, $"Expected metric {name}{{{labelText}}} to be present in the scrape, but it was missing.");
        Assert.Equal(expected, value!.Value);
    }

    private async Task<int> AvailableCountAsync(string showId)
    {
        var response = await _client.GetAsync($"/shows/{showId}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("counts").GetProperty("available").GetInt32();
    }

    private Task<HttpResponseMessage> ReserveAsync(string token, string showId, string seat, string idempotencyKey) =>
        ReserveAsync(token, showId, [seat], idempotencyKey);

    private async Task<HttpResponseMessage> ReserveAsync(string token, string showId, string[] seats, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{showId}/reserve")
        {
            Content = JsonContent.Create(new { seats, idempotency_key = idempotencyKey }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> CancelAsync(string userId, string reservationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/reservations/{reservationId}/cancel");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync(userId));
        return await _client.SendAsync(request);
    }

    private async Task<string> CreateShowAsync(string[] seats, long pricePaise, int perUserLimit = 4)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/shows")
        {
            Content = JsonContent.Create(new { name = "metrics-test", seats, price_paise = pricePaise, per_user_limit = perUserLimit }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("show_id").GetString()!;
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
