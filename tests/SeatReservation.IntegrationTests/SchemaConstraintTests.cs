using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SeatReservation.Api.Infrastructure.Migrations;
using Testcontainers.MySql;

namespace SeatReservation.IntegrationTests;

/// <summary>
/// Exercises the DB-level backstops in 0001_init.sql directly (one shared, already-migrated
/// container for the whole class - each test uses its own random ids so rows never collide).
/// The business-logic enforcement these backstop (transactions, row locks) arrives in Phase 5/6.
/// </summary>
public sealed class SchemaConstraintTests : IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.4")
        .WithDatabase("seats")
        .Build();

    public async Task InitializeAsync()
    {
        await _mysql.StartAsync();
        await new MigrationRunner(NullLogger<MigrationRunner>.Instance)
            .RunAsync(_mysql.GetConnectionString(), CancellationToken.None);
    }

    public Task DisposeAsync() => _mysql.DisposeAsync().AsTask();

    [Fact]
    public async Task RejectsDuplicateSeatPerShow()
    {
        // Backs I1/I10: uq_seats_show_seat is the one-row-per-seat guarantee.
        await using var connection = await OpenConnectionAsync();
        var showId = await InsertShowAsync(connection);
        await InsertSeatAsync(connection, showId, "A1");

        var ex = await Assert.ThrowsAsync<MySqlException>(() => InsertSeatAsync(connection, showId, "A1"));
        Assert.Equal((int)MySqlErrorCode.DuplicateKeyEntry, ex.Number);
    }

    [Fact]
    public async Task RejectsSeatMarkedConfirmedWithNoOwner()
    {
        // Backs I1/I8: ck_seats_owner - a seat can't be confirmed without a reservation_id.
        await using var connection = await OpenConnectionAsync();
        var showId = await InsertShowAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO seats (show_id, seat_no, status, reservation_id) VALUES (@show_id, 'A1', 'confirmed', NULL)";
        command.Parameters.AddWithValue("@show_id", showId);

        await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task RejectsDuplicateIdempotencyKeyPerUser_ButAllowsSameKeyForDifferentUser()
    {
        // Backs I4: uq_reservations_user_key is scoped per user.
        await using var connection = await OpenConnectionAsync();
        var showId = await InsertShowAsync(connection);

        await InsertReservationAsync(connection, showId, userId: "user-a", idempotencyKey: "key-1");
        await InsertReservationAsync(connection, showId, userId: "user-b", idempotencyKey: "key-1");

        var ex = await Assert.ThrowsAsync<MySqlException>(
            () => InsertReservationAsync(connection, showId, userId: "user-a", idempotencyKey: "key-1"));
        Assert.Equal((int)MySqlErrorCode.DuplicateKeyEntry, ex.Number);
    }

    [Fact]
    public async Task RejectsNegativeQuota()
    {
        // Backs I5: ck_quota_nonneg.
        await using var connection = await OpenConnectionAsync();
        var showId = await InsertShowAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO user_show_quota (user_id, show_id, seat_count) VALUES ('user-a', @show_id, -1)";
        command.Parameters.AddWithValue("@show_id", showId);

        await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task MoneyColumnsAreBigint()
    {
        // Backs I7: money is BIGINT, never float/double/decimal.
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_name, column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = DATABASE()
              AND (table_name, column_name) IN (('shows', 'price_paise'), ('reservations', 'amount_paise'))
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var found = 0;
        while (await reader.ReadAsync())
        {
            found++;
            Assert.Equal("bigint", reader.GetString(2));
        }

        Assert.Equal(2, found);
    }

    private async Task<MySqlConnection> OpenConnectionAsync()
    {
        var connection = new MySqlConnection(_mysql.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<byte[]> InsertShowAsync(MySqlConnection connection)
    {
        var showId = Guid.CreateVersion7().ToByteArray();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO shows (show_id, name, price_paise, total_seats)
            VALUES (@show_id, 'Test Show', 2500, 100)
            """;
        command.Parameters.AddWithValue("@show_id", showId);
        await command.ExecuteNonQueryAsync();
        return showId;
    }

    private static async Task InsertSeatAsync(MySqlConnection connection, byte[] showId, string seatNo)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO seats (show_id, seat_no) VALUES (@show_id, @seat_no)";
        command.Parameters.AddWithValue("@show_id", showId);
        command.Parameters.AddWithValue("@seat_no", seatNo);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertReservationAsync(MySqlConnection connection, byte[] showId, string userId, string idempotencyKey)
    {
        var reservationId = Guid.CreateVersion7().ToByteArray();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reservations
                (reservation_id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status)
            VALUES
                (@reservation_id, @show_id, @user_id, @idempotency_key, REPEAT('0', 64), '["A1"]', 2500, 'confirmed')
            """;
        command.Parameters.AddWithValue("@reservation_id", reservationId);
        command.Parameters.AddWithValue("@show_id", showId);
        command.Parameters.AddWithValue("@user_id", userId);
        command.Parameters.AddWithValue("@idempotency_key", idempotencyKey);
        await command.ExecuteNonQueryAsync();
    }
}
