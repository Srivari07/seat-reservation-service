namespace Burst;

// Local copies of the API's request/response shapes (03-api-contract.md). Deliberately not a
// ProjectReference to SeatReservation.Api: this tool must also run standalone against a deployed
// BASE_URL, via the docker fallback in burst.sh, where the API source isn't available.
public sealed record TokenRequest(string UserId, string Role, string? AdminSecret);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string UserId, string Role);

public sealed record CreateShowRequest(string Name, List<string> Seats, long PricePaise, int PerUserLimit);

public sealed record ShowResponse(
    string ShowId,
    string Name,
    long PricePaise,
    int PerUserLimit,
    int TotalSeats,
    ShowCounts Counts,
    List<SeatDto> Seats);

public sealed record ShowCounts(int Available, int Held, int Confirmed);

public sealed record SeatDto(string Seat, string Status);

public sealed record ReserveRequest(List<string> Seats, string IdempotencyKey);

public sealed record ReservationResponse(
    string ReservationId,
    string ShowId,
    string UserId,
    List<string> Seats,
    long AmountPaise,
    string Status);

public sealed record ApiErrorBody(string Error, string Message, string? RequestId);
