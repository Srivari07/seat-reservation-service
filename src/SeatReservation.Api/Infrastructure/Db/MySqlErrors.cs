using MySqlConnector;

namespace SeatReservation.Api.Infrastructure.Db;

// Classifies the MySQL errors we expect (04-concurrency.md, "MySQL error mapping"). Anything not
// matched here is a bug and must stay a 500. "DB unreachable" isn't classified by error number:
// DbRunner maps any failure to open, and any loss of the connection mid-work, to 503.
public static class MySqlErrors
{
    // 1213: InnoDB picked this transaction as the deadlock victim and rolled it back.
    public static bool IsDeadlock(MySqlException ex) => ex.ErrorCode == MySqlErrorCode.LockDeadlock;

    // 1205: waited longer than innodb_lock_wait_timeout for a row lock.
    public static bool IsLockWaitTimeout(MySqlException ex) => ex.ErrorCode == MySqlErrorCode.LockWaitTimeout;

    // 1062 on one specific unique key. MySQL exposes the key name only in the message text; there
    // is no structured field for it. From 8.0.19 the name is table-qualified
    // ("... for key 'reservations.uq_reservations_user_key'"), before that it is bare
    // ("... for key 'uq_reservations_user_key'"). The leading '.' or quote stops a key whose name
    // merely ends the same way from matching.
    public static bool IsDuplicateKey(MySqlException ex, string keyName) =>
        ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry
        && (ex.Message.EndsWith($".{keyName}'", StringComparison.Ordinal)
            || ex.Message.EndsWith($" '{keyName}'", StringComparison.Ordinal));
}
