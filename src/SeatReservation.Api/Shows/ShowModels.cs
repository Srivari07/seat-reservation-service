namespace SeatReservation.Api.Shows;

// PricePaise is decimal, not long, so a fractional JSON number (e.g. 25000.5) binds
// successfully and can be rejected by our own validation as 400 invalid_request,
// instead of failing ASP.NET's model binding with a non-contract-shaped response.
public sealed record CreateShowRequest(string? Name, List<string>? Seats, decimal? PricePaise, int? PerUserLimit);

public sealed record ShowResponse(
    string ShowId,
    string Name,
    long PricePaise,
    int PerUserLimit,
    int TotalSeats,
    ShowCounts Counts,
    IReadOnlyList<SeatDto> Seats);

public sealed record ShowCounts(int Available, int Held, int Confirmed);

public sealed record SeatDto(string Seat, string Status);
