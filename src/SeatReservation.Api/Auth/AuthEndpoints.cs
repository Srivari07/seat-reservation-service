using System.Security.Cryptography;
using System.Text;
using SeatReservation.Api.Infrastructure.Errors;

namespace SeatReservation.Api.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/token", (TokenRequest request, HttpContext context, JwtTokenIssuer issuer, IConfiguration configuration) =>
        {
            if (!UserIds.IsValid(request.UserId))
            {
                return ApiError.Write(context, StatusCodes.Status400BadRequest, "invalid_request", $"user_id must match {UserIds.FormatDescription}.");
            }

            if (request.Role is not ("user" or "admin"))
            {
                return ApiError.Write(context, StatusCodes.Status400BadRequest, "invalid_request", "role must be 'user' or 'admin'.");
            }

            if (request.Role == "admin")
            {
                var adminSecret = configuration["ADMIN_SECRET"] ?? string.Empty;
                if (!FixedTimeEquals(request.AdminSecret ?? string.Empty, adminSecret))
                {
                    return ApiError.Write(context, StatusCodes.Status403Forbidden, "forbidden", "admin_secret is missing or incorrect.");
                }
            }

            var response = issuer.IssueToken(request.UserId, request.Role);
            return Results.Ok(response);
        });
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length && CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
