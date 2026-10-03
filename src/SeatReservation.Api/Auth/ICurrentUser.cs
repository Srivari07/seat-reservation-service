namespace SeatReservation.Api.Auth;

public interface ICurrentUser
{
    string UserId { get; }

    string Role { get; }
}

public sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public string UserId => ClaimOrThrow("sub");

    public string Role => ClaimOrThrow("role");

    private string ClaimOrThrow(string claimType)
    {
        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No active HTTP context.");
        return context.User.FindFirst(claimType)?.Value
            ?? throw new InvalidOperationException($"Authenticated request is missing the '{claimType}' claim.");
    }
}
