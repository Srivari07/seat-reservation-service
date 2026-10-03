namespace SeatReservation.Api.Auth;

public sealed record TokenRequest(string? UserId, string? Role, string? AdminSecret);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string UserId, string Role);
