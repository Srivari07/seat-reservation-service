using System.Text.RegularExpressions;

namespace SeatReservation.Api.Shows;

// One normalizer for every endpoint that takes seat numbers (AGENTS.md "Seat numbers"), so a seat
// is stored, hashed and queried in exactly the same form everywhere.
public static partial class SeatNumbers
{
    public const string FormatDescription = "^[A-Z0-9]{1,10}$ after trim/uppercase";

    // Returns the trimmed, uppercased seat, or null if it doesn't match ^[A-Z0-9]{1,10}$.
    public static string? Normalize(string? raw)
    {
        var seat = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return Pattern().IsMatch(seat) ? seat : null;
    }

    [GeneratedRegex("^[A-Z0-9]{1,10}$")]
    private static partial Regex Pattern();
}
