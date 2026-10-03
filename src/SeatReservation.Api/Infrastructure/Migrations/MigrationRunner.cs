using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace SeatReservation.Api.Infrastructure.Migrations;

/// <summary>
/// Applies embedded .sql migrations in order, tracked in the `schema_migrations` table,
/// guarded by a named lock so two instances starting at once don't race.
/// </summary>
public sealed class MigrationRunner(ILogger<MigrationRunner> logger)
{
    // Anchored to this project's default namespace + folder path (how .NET names
    // embedded resources), so an unrelated ".N_name.sql" resource elsewhere in the
    // assembly can never be mistaken for a migration.
    private const string ResourcePrefix = "SeatReservation.Api.Infrastructure.Migrations.";
    private static readonly Regex MigrationFileName = new(@"^(\d{4})_[A-Za-z0-9]+\.sql$", RegexOptions.Compiled);

    // GET_LOCK('schema_migrations', 60) asks MySQL to wait up to 60s server-side;
    // the client-side command timeout must exceed that or ADO.NET times it out first.
    private const int LockCommandTimeoutSeconds = 70;
    private const int MigrationCommandTimeoutSeconds = 120;

    public async Task RunAsync(string connectionString, CancellationToken cancellationToken)
    {
        // Pooling=false guarantees the session - and with it the named lock - is
        // released the moment this connection is disposed, even if RELEASE_LOCK
        // itself never runs (e.g. a crash between acquiring the lock and releasing it).
        var nonPooledConnectionString = new MySqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        await using var connection = new MySqlConnection(nonPooledConnectionString);
        await connection.OpenAsync(cancellationToken);

        await AcquireLockAsync(connection, cancellationToken);
        try
        {
            // Once the lock is held, let the migration run to completion rather than
            // honoring a shutdown signal mid-DDL - interrupting a CREATE TABLE halfway
            // is worse than letting a few extra seconds pass during shutdown.
            await ApplyPendingMigrationsAsync(connection);
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    private static async Task AcquireLockAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK('schema_migrations', 60)";
        command.CommandTimeout = LockCommandTimeoutSeconds;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not long acquired || acquired != 1)
        {
            throw new MigrationLockUnavailableException(result);
        }
    }

    private async Task ReleaseLockAsync(MySqlConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT RELEASE_LOCK('schema_migrations')";
            command.CommandTimeout = LockCommandTimeoutSeconds;
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is MySqlException || (ex is InvalidOperationException && connection.State != ConnectionState.Open))
        {
            // Not rethrown: it must never mask an exception from the migration itself,
            // and disposing this (non-pooled) connection releases the lock regardless.
            // InvalidOperationException ("Connection must be Open") happens when the connection
            // has already gone Broken (e.g. under heavy parallel load) by the time we get here -
            // letting it escape would fault this BackgroundService and stop the whole host
            // (BackgroundServiceExceptionBehavior.StopHost), which is worse than a missed RELEASE_LOCK.
            // The State check keeps the filter narrow: an InvalidOperationException on a
            // genuinely open connection is a real bug and should still surface loudly.
            logger.LogWarning(ex, "RELEASE_LOCK('schema_migrations') failed; the lock will still be freed when the connection closes.");
        }
    }

    private async Task ApplyPendingMigrationsAsync(MySqlConnection connection)
    {
        var appliedVersions = await GetAppliedVersionsAsync(connection);
        var migrations = GetEmbeddedMigrations();

        foreach (var migration in migrations)
        {
            if (appliedVersions.Contains(migration.Version))
            {
                continue;
            }

            logger.LogInformation("Applying migration {Version} ({Name})", migration.Version, migration.Name);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = migration.Sql;
                command.CommandTimeout = MigrationCommandTimeoutSeconds;
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO schema_migrations (version) VALUES (@version)";
                insert.CommandTimeout = MigrationCommandTimeoutSeconds;
                insert.Parameters.AddWithValue("@version", migration.Version);
                await insert.ExecuteNonQueryAsync(CancellationToken.None);
            }

            logger.LogInformation("Applied migration {Version} ({Name})", migration.Version, migration.Name);
        }
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(MySqlConnection connection)
    {
        var tableExists = await ExecuteCountAsync(
            connection,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'schema_migrations'");

        if (tableExists == 0)
        {
            return [];
        }

        var applied = new HashSet<int>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            applied.Add(reader.GetInt32(0));
        }

        return applied;
    }

    private static List<(int Version, string Name, string Sql)> GetEmbeddedMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var migrationsByVersion = new Dictionary<int, string>();
        var migrations = new List<(int Version, string Name, string Sql)>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resourceName[ResourcePrefix.Length..];
            var match = MigrationFileName.Match(fileName);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var version))
            {
                throw new InvalidOperationException(
                    $"Embedded migration resource '{resourceName}' doesn't match the required 'NNNN_name.sql' naming pattern.");
            }

            if (!migrationsByVersion.TryAdd(version, resourceName))
            {
                throw new InvalidOperationException(
                    $"Duplicate migration version {version}: '{migrationsByVersion[version]}' and '{resourceName}'.");
            }

            migrations.Add((version, resourceName, ReadResource(assembly, resourceName)));
        }

        if (migrations.Count == 0)
        {
            throw new InvalidOperationException($"No embedded migrations found under '{ResourcePrefix}*.sql'.");
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        return migrations;
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<long> ExecuteCountAsync(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}
