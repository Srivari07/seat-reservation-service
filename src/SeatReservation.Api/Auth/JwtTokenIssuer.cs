using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace SeatReservation.Api.Auth;

public sealed class JwtTokenIssuer
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private readonly SymmetricSecurityKey _signingKey;

    public JwtTokenIssuer(IConfiguration configuration)
    {
        var signingKey = configuration["JWT_SIGNING_KEY"]
            ?? throw new InvalidOperationException("JWT_SIGNING_KEY is required");
        _signingKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey));
    }

    public TokenResponse IssueToken(string userId, string role)
    {
        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim("sub", userId),
            new Claim("role", role),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.Add(TokenLifetime),
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);
        return new TokenResponse(accessToken, "Bearer", (int)TokenLifetime.TotalSeconds, userId, role);
    }
}
