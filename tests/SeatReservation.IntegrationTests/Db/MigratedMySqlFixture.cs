using Microsoft.Extensions.Logging.Abstractions;
using SeatReservation.Api.Infrastructure.Migrations;
using Testcontainers.MySql;

namespace SeatReservation.IntegrationTests.Db;

/// <summary>
/// One migrated MySQL container shared by every test in a class (IClassFixture). Tests use their
/// own random ids, so rows never collide.
/// </summary>
public sealed class MigratedMySqlFixture : IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.4")
        .WithDatabase("seats")
        .Build();

    public string ConnectionString => _mysql.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _mysql.StartAsync();
        await new MigrationRunner(NullLogger<MigrationRunner>.Instance)
            .RunAsync(_mysql.GetConnectionString(), CancellationToken.None);
    }

    public Task DisposeAsync() => _mysql.DisposeAsync().AsTask();
}
