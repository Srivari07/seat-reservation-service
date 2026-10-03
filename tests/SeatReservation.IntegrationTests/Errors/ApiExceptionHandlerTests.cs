using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SeatReservation.Api.Infrastructure.Errors;
using SeatReservation.Api.Infrastructure.Logging;

namespace SeatReservation.IntegrationTests.Errors;

/// <summary>
/// No endpoint can trigger contention on demand yet, so the 409 mapping is checked directly on
/// the handler (03-api-contract.md: 409 contention, extra field "retryable": true).
/// </summary>
public sealed class ApiExceptionHandlerTests
{
    [Fact]
    public async Task DbContention_Maps409ContentionRetryable()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        context.Items[RequestIdMiddleware.ItemsKey] = "req-123";
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context, new DbContentionException(new InvalidOperationException("inner")), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
        Assert.Equal("contention", body.GetProperty("error").GetString());
        Assert.True(body.GetProperty("retryable").GetBoolean());
        Assert.Equal("req-123", body.GetProperty("request_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }
}
