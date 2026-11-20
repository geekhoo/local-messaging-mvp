using System.Globalization;
using LocalMessaging.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LocalMessaging.Infrastructure;

public enum RestoreFaultPoint { PreparedBeforePublication, Published }
public sealed record RestoreResult(string StoreEpoch, string? SafetyBackupPath, string DatabasePath);

/// <summary>Offline-only maintenance. The same exclusive lock is acquired before inspecting the active store.</summary>
public static class StoreMaintenance
{
    public static async Task<string> BackupAsync(string dataDirectory, string destination, CancellationToken cancellationToken = default)
    {
        var directory = Absolute(dataDirectory);
        destination = Absolute(destination);
        Directory.CreateDirectory(directory);
        using var held = MessagingStore.AcquireLock(directory);
        var database = Path.Combine(directory, "messaging.db");
        if (!File.Exists(database)) throw new FileNotFoundException("There is no store to back up.", database);
        if (SamePath(database, destination) || File.Exists(destination))
            throw new IOException("The backup destination must be a new file distinct from the live store.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        SqliteConnection.ClearAllPools();
        try
        {
            await CopyCoherentAsync(database, destination, cancellationToken);
            FlushFile(destination);
            return destination;
        }
        catch { TryDelete(destination); throw; }
    }

    public static async Task<RestoreResult> RestoreAsync(string dataDirectory, string backupPath,
        CancellationToken cancellationToken = default, Action<RestoreFaultPoint>? faultInjector = null)
    {
        var directory = Absolute(dataDirectory);
        backupPath = Absolute(backupPath);
        Directory.CreateDirectory(directory);
        using var held = MessagingStore.AcquireLock(directory);
        var database = Path.Combine(directory, "messaging.db");
        if (!File.Exists(backupPath)) throw new FileNotFoundException("The selected backup does not exist.", backupPath);
        if (SamePath(database, backupPath)) throw new IOException("Choose a backup distinct from the active database.");
        SqliteConnection.ClearAllPools();
        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        string? safety = null;
        var prepared = Path.Combine(directory, ".restore-" + suffix + ".db");
        try
        {
            if (File.Exists(database))
            {
                safety = Path.Combine(directory, "messaging.safety-restore-" + suffix + ".db");
                await CopyCoherentAsync(database, safety, cancellationToken);
                FlushFile(safety);
            }
            await CopyCoherentAsync(backupPath, prepared, cancellationToken);
            var epoch = Guid.NewGuid().ToString("D");
            await using (var connection = Open(prepared, SqliteOpenMode.ReadWrite))
            {
                await connection.OpenAsync(cancellationToken);
                await MessagingStore.ValidateIntegrityAsync(connection, cancellationToken);
                await ValidateApplicationSchemaAsync(connection, cancellationToken);
                await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        UPDATE AppMetadata SET StoreEpoch = $epoch WHERE Id = 1;
                        UPDATE Subscriptions SET State = 'closed', ClosedUtcMs = COALESCE(ClosedUtcMs, $now);
                        """;
                    command.Parameters.AddWithValue("$epoch", epoch);
                    command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                await CheckpointAsync(connection, cancellationToken);
                await using var journal = connection.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE;";
                var mode = Convert.ToString(await journal.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The prepared database could not be made self-contained.");
                await MessagingStore.ValidateIntegrityAsync(connection, cancellationToken);
            }
            if (File.Exists(database))
            {
                await using var old = Open(database, SqliteOpenMode.ReadWrite);
                await old.OpenAsync(cancellationToken);
                await CheckpointAsync(old, cancellationToken);
                await old.CloseAsync();
            }
            // No active connection exists and the exclusive directory lock is still held.
            DeleteSidecars(database);
            DeleteSidecars(prepared);
            FlushFile(prepared);
            cancellationToken.ThrowIfCancellationRequested();
            faultInjector?.Invoke(RestoreFaultPoint.PreparedBeforePublication);
            // Same-volume replacement preserves the old valid main DB until the prepared DB is published.
            File.Move(prepared, database, overwrite: true);
            faultInjector?.Invoke(RestoreFaultPoint.Published);
            return new RestoreResult(epoch, safety, database);
        }
        finally
        {
            TryDelete(prepared);
            TryDelete(prepared + "-wal");
            TryDelete(prepared + "-shm");
            // Safety backup is deliberately retained, including after any interrupted publication.
        }
    }

    private static async Task CopyCoherentAsync(string source, string destination, CancellationToken ct)
    {
        if (File.Exists(destination)) throw new IOException("The backup destination already exists.");
        await using var from = Open(source, SqliteOpenMode.ReadOnly);
        await using var to = Open(destination, SqliteOpenMode.ReadWriteCreate);
        await from.OpenAsync(ct);
        await MessagingStore.ValidateIntegrityAsync(from, ct);
        await to.OpenAsync(ct);
        ct.ThrowIfCancellationRequested();
        from.BackupDatabase(to);
        await MessagingStore.ValidateIntegrityAsync(to, ct);
        await using var journal = to.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=DELETE;";
        await journal.ExecuteScalarAsync(ct);
    }

    private static async Task CheckpointAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && reader.GetInt32(0) != 0)
            throw new IOException("The store could not be checkpointed. It remains unchanged; retry after all connections have stopped.");
    }

    private static async Task ValidateApplicationSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AppMetadata WHERE Id=1;";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("The backup does not contain one valid application metadata row.");
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var migrations = new List<string>();
        while (await reader.ReadAsync(ct)) migrations.Add(reader.GetString(0));
        await using var model = new MessagingDbContext(new DbContextOptionsBuilder<MessagingDbContext>().UseSqlite(connection).Options);
        // Allow known older backup schemas; startup migrates the restored store.
        if (migrations.Count == 0 || !migrations.SequenceEqual(model.Database.GetMigrations().Take(migrations.Count)))
            throw new InvalidDataException("The backup schema is not supported by this product version.");
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, ForeignKeys = true, Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 5
    }.ToString());

    private static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Maintenance paths must be absolute local paths.");
        return Path.GetFullPath(path);
    }
    private static bool SamePath(string left, string right) => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void DeleteSidecars(string database) { File.Delete(database + "-wal"); File.Delete(database + "-shm"); }
    private static void TryDelete(string file) { try { File.Delete(file); } catch (IOException) { } }
    private static void FlushFile(string file) { using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); stream.Flush(flushToDisk: true); }
}
