using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Testcontainers.MySql;

namespace SeatReservation.IntegrationTests.Shows;

/// <summary>
/// Boots the real API host against a Testcontainers MySQL. The connection string handed to the
/// app is the container's own, unmodified - Guid Format=Binary16 is NOT added here, so that
/// CreateShowTests.StoresShowIdInBigEndianByteOrder actually exercises production's own
/// enforcement (Infrastructure/Db/MySqlConnectionStrings.WithGuidFormat) instead of passing
/// only because the test fixture already forced it. Unlike AuthApiFactory, no diagnostic-
/// endpoints filter is needed: POST /shows and GET /shows/{id} are real endpoints to test directly.
/// </summary>
public sealed class ShowsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "ZmFrZS1zaWduaW5nLWtleS1mb3Itc2hvd3MtdGVzdHMtMzIh";
    public const string AdminSecret = "test-admin-secret";

    // For tests that verify DB-level storage directly (e.g. BINARY(16) byte order) rather than
    // through the API.
    public string DirectConnectionString => _mysql.GetConnectionString();

    // Root access for tests that read server-wide InnoDB counters (information_schema.INNODB_METRICS
    // needs the PROCESS privilege, which the app's user doesn't have). Never handed to the app.
    public string RootConnectionString =>
        new MySqlConnectionStringBuilder(_mysql.GetConnectionString()) { UserID = "root", Password = RootPassword }.ConnectionString;

    // For tests that exercise the db_unavailable path. Each test class gets its own factory
    // instance (IClassFixture), so stopping this container only ever affects that one class.
    public Task StopDatabaseAsync() => _mysql.StopAsync();

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    // Set explicitly rather than relying on whatever Testcontainers derives it from.
    private const string RootPassword = "test-root-password";

    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.4")
        .WithDatabase("seats")
        .WithEnvironment("MYSQL_ROOT_PASSWORD", RootPassword)
        .Build();

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _mysql.StartAsync();

        // Unlike Auth's /auth/token (no DB access), POST /shows and GET /shows/{id} touch the
        // DB on their very first call. Without this, a test's first request can race
        // MigrationRunnerHostedService (a BackgroundService that starts applying migrations
        // only after the host is already accepting connections) and legitimately get 503
        // db_unavailable - the same readiness gate a real deployment or burst.sh waits out.
        using var client = CreateClient();
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (true)
        {
            try
            {
                var response = await client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Keep polling until the deadline.
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("API did not become ready within the timeout.");
            }

            await Task.Delay(50);
        }
    }

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
        // Matches real deployments (ASPNETCORE_ENVIRONMENT=Production, 07-deploy.md), not
        // WebApplicationFactory's Development default: RouteHandlerOptions.ThrowOnBadRequest
        // defaults to IsDevelopment(), so a malformed-body test run under the default
        // environment would pass even if Program.cs never set it explicitly.
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mysql"] = _mysql.GetConnectionString(),
                ["JWT_SIGNING_KEY"] = SigningKey,
                ["ADMIN_SECRET"] = AdminSecret,
            });
        });
    }
}
