using MySqlConnector;

namespace SeatReservation.Api.Infrastructure.Db;

public static class MySqlConnectionStrings
{
    // Enforced here, not left to the deploy-time connection string text: UUIDv7 show_id/
    // reservation_id columns are BINARY(16) specifically for their time-ordering (02-schema.md),
    // which only holds with big-endian byte order. Without this, every Guid-bound query
    // against those columns silently breaks (02-schema.md's own reasoning no longer applies,
    // and in practice: inserts fail on "Data too long for column", reads return no rows).
    public static string WithGuidFormat(string? connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString, nameof(connectionString));
        return new MySqlConnectionStringBuilder(connectionString) { GuidFormat = MySqlGuidFormat.Binary16 }.ConnectionString;
    }
}
