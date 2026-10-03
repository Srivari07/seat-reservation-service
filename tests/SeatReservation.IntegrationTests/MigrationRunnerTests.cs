using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SeatReservation.Api.Infrastructure.Migrations;
using Testcontainers.MySql;

namespace SeatReservation.IntegrationTests;

public sealed class MigrationRunnerTests : IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.4")
        .WithDatabase("seats")
        .Build();

    private MigrationRunner _runner = null!;

    public async Task InitializeAsync()
    {
        await _mysql.StartAsync();
        _runner = new MigrationRunner(NullLogger<MigrationRunner>.Instance);
    }

    public Task DisposeAsync() => _mysql.DisposeAsync().AsTask();

    [Fact]
    public async Task Apply_ToFreshContainer_CreatesAllTables()
    {
        await _runner.RunAsync(_mysql.GetConnectionString(), CancellationToken.None);

        await using var connection = new MySqlConnection(_mysql.GetConnectionString());
        await connection.OpenAsync();

        var tableNames = await GetTableNamesAsync(connection);
        Assert.Equal(
            new[] { "reservations", "schema_migrations", "seats", "shows", "user_show_quota" },
            tableNames);

        var appliedCount = await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations");
        Assert.Equal(1L, appliedCount);

        var appliedVersion = await ExecuteScalarAsync(connection, "SELECT version FROM schema_migrations");
        Assert.Equal(1L, appliedVersion);
    }

    [Fact]
    public async Task Apply_Twice_IsNoOp()
    {
        await _runner.RunAsync(_mysql.GetConnectionString(), CancellationToken.None);
        await _runner.RunAsync(_mysql.GetConnectionString(), CancellationToken.None);

        await using var connection = new MySqlConnection(_mysql.GetConnectionString());
        await connection.OpenAsync();

        var appliedCount = await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations");
        Assert.Equal(1L, appliedCount);
    }

    [Fact]
    public async Task Apply_ConcurrentlyFromTwoRunners_AppliesExactlyOnce()
    {
        // Proves the D-11 claim: two instances starting at once (e.g. a rolling
        // deploy) don't race on GET_LOCK('schema_migrations', 60) and both end up
        // with a fully, singly migrated database.
        var runnerA = new MigrationRunner(NullLogger<MigrationRunner>.Instance);
        var runnerB = new MigrationRunner(NullLogger<MigrationRunner>.Instance);

        await Task.WhenAll(
            runnerA.RunAsync(_mysql.GetConnectionString(), CancellationToken.None),
            runnerB.RunAsync(_mysql.GetConnectionString(), CancellationToken.None));

        await using var connection = new MySqlConnection(_mysql.GetConnectionString());
        await connection.OpenAsync();

        var tableNames = await GetTableNamesAsync(connection);
        Assert.Equal(
            new[] { "reservations", "schema_migrations", "seats", "shows", "user_show_quota" },
            tableNames);

        var appliedCount = await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations");
        Assert.Equal(1L, appliedCount);
    }

    private static async Task<List<string>> GetTableNamesAsync(MySqlConnection connection)
    {
        var names = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY table_name";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<long> ExecuteScalarAsync(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}
