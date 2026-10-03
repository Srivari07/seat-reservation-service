using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Api.Auth;
using SeatReservation.Api.Infrastructure.Logging;
using Testcontainers.MySql;

namespace SeatReservation.IntegrationTests.Auth;

/// <summary>
/// Boots the real API host (JWT middleware, policies, /auth/token) against a Testcontainers
/// MySQL, and adds two throwaway diagnostic endpoints (never present in production Program.cs)
/// so the auth pipeline can be exercised before any real protected business endpoint exists.
/// </summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "ZmFrZS1zaWduaW5nLWtleS1mb3ItYXV0aC10ZXN0cy0zMish";
    public const string AdminSecret = "test-admin-secret";

    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.4")
        .WithDatabase("seats")
        .Build();

    Task IAsyncLifetime.InitializeAsync() => _mysql.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            // Host first: MigrationRunnerHostedService (or any in-flight request) can still be
            // using the database - stopping the host before the container avoids pulling it out
            // from under a connection that's still open.
            await base.DisposeAsync();
        }
        finally
        {
            // Always disposed, even if host shutdown above throws - an orphaned Testcontainer
            // is worse than a host-shutdown exception getting masked here.
            await _mysql.DisposeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mysql"] = _mysql.GetConnectionString(),
                ["JWT_SIGNING_KEY"] = SigningKey,
                ["ADMIN_SECRET"] = AdminSecret,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddTransient<IStartupFilter, DiagnosticEndpointsStartupFilter>();
        });
    }

    private sealed class DiagnosticEndpointsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // The real Program.cs pipeline (next(app)) is the already-built WebApplication
            // wired in as a single opaque middleware here, not something we can add routes to
            // from outside. So this is its own independent routing stage: it dispatches the two
            // test-only routes, and falls through to next(app) for everything else (e.g. /auth/token).
            // UseAuthentication/UseAuthorization are repeated here (the real app's own copies live
            // inside that opaque next(app) middleware) so [Authorize] is enforced for these routes
            // too, using the same JwtBearer scheme already registered in DI by Program.cs.
            // RequestIdMiddleware is repeated for the same reason, so error bodies from these
            // routes carry a request_id like every other response does.
            app.UseMiddleware<RequestIdMiddleware>();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/__test/whoami", (ICurrentUser currentUser) =>
                        Results.Ok(new { user_id = currentUser.UserId, role = currentUser.Role }))
                    .RequireAuthorization();

                endpoints.MapGet("/__test/admin-only", () => Results.Ok())
                    .RequireAuthorization("AdminOnly");
            });

            next(app);
        };
    }
}
