using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SeatReservation.Api.Auth;

// The one user_id format (D-10). /auth/token only issues ids that match it, and token validation
// rejects any other "sub", so every user_id that reaches SQL fits the ascii VARCHAR(64) column:
// no 500 from MySQL (1267/1366/1406), and no PAD SPACE match of "u1 " on "u1" (I6).
public static partial class UserIds
{
    public const string FormatDescription = "^[A-Za-z0-9_-]{1,64}$";

    public static bool IsValid([NotNullWhen(true)] string? userId) => userId is not null && Pattern().IsMatch(userId);

    // \z, not $: in .NET, $ also matches before a trailing newline, which would accept "u1\n".
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex Pattern();
}
