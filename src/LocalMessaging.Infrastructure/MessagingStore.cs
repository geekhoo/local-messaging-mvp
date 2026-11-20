using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Application;
using LocalMessaging.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LocalMessaging.Infrastructure;

public sealed record StoreOptions(string DataDirectory, int WriteTimeoutSeconds = 5, int CommandTimeoutSeconds = 5, ProductLimits? Limits = null);
public enum StoreFaultPoint { BeforeCommit, AfterCommitBeforeNotification }

/// <summary>One process owns a store. Every operation uses its own short-lived context.</summary>
public sealed partial class MessagingStore : IAsyncDisposable
{
    private readonly StoreOptions _settings;
    private readonly ProductLimits _limits;
    private readonly DbContextOptions<MessagingDbContext> _dbOptions;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Action<StoreFaultPoint>? _faultInjector;
    private FileStream? _storeLock;
    private bool _initialized;
    private bool _disposed;
    private string _epoch = "";
    public string DatabasePath { get; }
    public string StoreEpoch => _epoch;
    public event Action? Committed;
    public event Action<Guid>? SubscriptionClosed;

    public MessagingStore(StoreOptions settings, Action<StoreFaultPoint>? faultInjector = null)
    {
        if (!Path.IsPathFullyQualified(settings.DataDirectory))
            throw new ArgumentException("The data directory must be an absolute local path.", nameof(settings));
        _limits = settings.Limits ?? new ProductLimits { WriteWaitSeconds = settings.WriteTimeoutSeconds, DatabaseTimeoutSeconds = settings.CommandTimeoutSeconds };
        settings = settings with { WriteTimeoutSeconds = _limits.WriteWaitSeconds, CommandTimeoutSeconds = _limits.DatabaseTimeoutSeconds };
        if (settings.WriteTimeoutSeconds is < 1 or > 60 || settings.CommandTimeoutSeconds is < 1 or > 60)
            throw new ArgumentException("Store timeouts must be between 1 and 60 seconds.", nameof(settings));
        _settings = settings;
        _faultInjector = faultInjector;
        DatabasePath = Path.Combine(Path.GetFullPath(settings.DataDirectory), "messaging.db");
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, ForeignKeys = true, Cache = SqliteCacheMode.Private,
            Pooling = true, DefaultTimeout = settings.CommandTimeoutSeconds
        };
        _dbOptions = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(connection.ToString(), sqlite => sqlite.CommandTimeout(settings.CommandTimeoutSeconds))
            .Options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;
        Directory.CreateDirectory(_settings.DataDirectory);
        _storeLock = AcquireLock(_settings.DataDirectory);
        try
        {
            await using var db = await CreateContextAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            var knownMigrations = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
            if (appliedMigrations.Any(id => !knownMigrations.Contains(id)))
                throw new InvalidDataException("This database was migrated by an unsupported product version; no data was changed.");
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            if (pending.Length > 0 && await HasUserSchemaAsync((SqliteConnection)db.Database.GetDbConnection(), cancellationToken))
            {
                var backup = DatabasePath + ".before-migration-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".db";
                using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
                target.Open();
                ((SqliteConnection)db.Database.GetDbConnection()).BackupDatabase(target);
            }
            await db.Database.MigrateAsync(cancellationToken);
            var metadata = await db.AppMetadata.SingleOrDefaultAsync(cancellationToken);
            if (metadata is null)
            {
                metadata = new MetadataRow { StoreEpoch = Guid.NewGuid().ToString("D"), InitializedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                db.AppMetadata.Add(metadata);
                await db.SaveChangesAsync(cancellationToken);
            }
            if (!Guid.TryParseExact(metadata.StoreEpoch, "D", out var epoch) || epoch == Guid.Empty)
                throw new InvalidDataException("Stored epoch is invalid; the existing database will not be recreated.");
            _epoch = metadata.StoreEpoch;
            await ValidateIntegrityAsync((SqliteConnection)db.Database.GetDbConnection(), cancellationToken);
            _initialized = true;
        }
        catch
        {
            _storeLock.Dispose();
            _storeLock = null;
            throw;
        }
    }

    public async Task<JsonObject> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await using var db = await CreateContextAsync(cancellationToken);
        var head = await HeadAsync(db, cancellationToken);
        return new JsonObject { ["contractVersion"] = 1, ["storeEpoch"] = _epoch, ["headCursor"] = Cursor(head) };
    }

    public async Task<JsonObject> GetReceiptAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await using var db = await CreateContextAsync(cancellationToken);
        var row = await db.RequestReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.RequestId == requestId.ToString("D"), cancellationToken)
            ?? throw new ApiException(404, "request_not_committed", "No committed receipt is visible. Retry the same request ID and payload if its outcome is uncertain.");
        return ProjectReceipt(row.ResultJson, replayed: null);
    }

    public async Task<ValidatedImage> GetImageAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await using var db = await CreateContextAsync(cancellationToken);
        var row = await db.ImageAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id.ToString("D"), cancellationToken)
            ?? throw new ApiException(404, "image_not_found", "The image does not exist.");
        return new ValidatedImage(row.Bytes, row.ContentType, row.Width, row.Height, row.Sha256);
    }

    private async Task<MessagingDbContext> CreateContextAsync(CancellationToken cancellationToken)
    {
        var db = new MessagingDbContext(_dbOptions);
        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            // These are connection settings, so apply them for each leased connection.
            await using var configure = db.Database.GetDbConnection().CreateCommand();
            configure.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout={_settings.CommandTimeoutSeconds * 1000};";
            await configure.ExecuteNonQueryAsync(cancellationToken);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private async Task<JsonObject> WriteAsync(string operation, JsonObject normalized,
        Func<MessagingDbContext, string, long, CancellationToken, Task<WriteResult>> create,
        string? imageDigest, CancellationToken cancellationToken)
    {
        EnsureReady();
        var requestId = normalized["requestId"]!.GetValue<string>();
        var caller = normalized["callerUserId"]?.GetValue<string>();
        var hash = CommandNormalizer.Hash(operation, normalized, imageDigest);
        if (!await _writeGate.WaitAsync(TimeSpan.FromSeconds(_settings.WriteTimeoutSeconds), cancellationToken))
            throw new ApiException(503, "database_busy", "The bounded database write wait expired. Reconcile or retry the same request.");
        WriteResult? committed = null;
        JsonObject result;
        try
        {
            await using var db = await CreateContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var old = await db.RequestReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.RequestId == requestId, cancellationToken);
            if (old is not null)
            {
                if (old.Operation != operation || old.CallerUserId != caller || old.RequestHash != hash)
                    throw new ApiException(409, "idempotency_conflict", "This request ID already committed a different caller, operation, or normalized payload.");
                return ProjectReceipt(old.ResultJson, replayed: true);
            }
            if (caller is not null) await RequireUserAsync(db, caller, cancellationToken);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            committed = await create(db, requestId, now, cancellationToken);
            result = committed.Result;
            result["contractVersion"] = 1;
            result["requestId"] = requestId;
            result["operation"] = operation;
            result["state"] = "committed";
            result["storeEpoch"] = _epoch;
            result["replayed"] = false;
            result["links"] = new JsonObject { ["receipt"] = "requests/" + requestId };
            db.RequestReceipts.Add(new ReceiptRow
            {
                RequestId = requestId, CallerUserId = caller, Operation = operation, RequestHash = hash,
                ResultJson = result.ToJsonString(), FirstSequence = committed.FirstSequence,
                LastSequence = committed.LastSequence, CommittedUtcMs = now
            });
            await db.SaveChangesAsync(cancellationToken);
            _faultInjector?.Invoke(StoreFaultPoint.BeforeCommit);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6)
        {
            throw new ApiException(503, "database_busy", "SQLite remained busy beyond the finite timeout.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw new ApiException(503, "database_busy", "SQLite remained busy beyond the finite timeout.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlite && sqlite.SqliteErrorCode is 10 or 13 or 14)
        {
            throw new ApiException(503, "storage_unavailable", "The transaction could not be committed to local storage.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 10 or 13 or 14)
        {
            throw new ApiException(503, "storage_unavailable", "The transaction could not be committed to local storage.");
        }
        finally { _writeGate.Release(); }
        _faultInjector?.Invoke(StoreFaultPoint.AfterCommitBeforeNotification);
        // Notifications are a latency optimization. A subscriber failure must not turn a known commit into failure.
        if (committed!.LastSequence is not null) InvokeSafely(Committed);
        if (committed.ClosedSubscription is { } closed) InvokeSafely(SubscriptionClosed, closed);
        return result;
    }

    private JsonObject ProjectReceipt(string json, bool? replayed)
    {
        var result = JsonNode.Parse(json)!.AsObject();
        result["storeEpoch"] = _epoch;
        if (replayed.HasValue) result["replayed"] = replayed.Value; else result.Remove("replayed");
        RebaseCursors(result);
        return result;
    }

    private void RebaseCursors(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Key.EndsWith("Cursor", StringComparison.Ordinal) && property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    var parsed = CommandNormalizer.ParseCursor(text);
                    obj[property.Key] = Cursor(parsed.Sequence);
                }
                else RebaseCursors(property.Value);
            }
        }
        else if (node is JsonArray array) foreach (var item in array) RebaseCursors(item);
    }

    private long ValidateCursor(string value, long head, long minimum = 0)
    {
        var parsed = CommandNormalizer.ParseCursor(value);
        if (parsed.Epoch.ToString("D") != _epoch) throw new ApiException(409, "store_changed", "The cursor belongs to another store history. Reload the profile and snapshot.");
        if (parsed.Sequence > head) throw new ApiException(409, "cursor_ahead_of_store", "The cursor is ahead of the committed event head.");
        if (parsed.Sequence < minimum) throw new ApiException(400, "cursor_before_subscription", "The cursor precedes this subscription's start.");
        return parsed.Sequence;
    }

    private string Cursor(long sequence) => CommandNormalizer.Cursor(_epoch, sequence);
    private static async Task<long> HeadAsync(MessagingDbContext db, CancellationToken ct) => await db.EventLog.Select(x => (long?)x.Sequence).MaxAsync(ct) ?? 0;
    private static async Task<UserRow> RequireUserAsync(MessagingDbContext db, string id, CancellationToken ct) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException(404, "user_not_found", "The selected user does not exist.");
    private static async Task<ChannelRow> RequireChannelAsync(MessagingDbContext db, string id, CancellationToken ct) =>
        await db.Channels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException(404, "channel_not_found", "The channel does not exist.");
    private static async Task<SessionRow> RequireSessionAsync(MessagingDbContext db, string id, CancellationToken ct) =>
        await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException(404, "session_not_found", "The session does not exist.");
    private static async Task<MessageRow> RequireMessageAsync(MessagingDbContext db, string id, CancellationToken ct) =>
        await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException(404, "message_not_found", "The message does not exist.");
    private static async Task<SubscriptionRow> RequireSubscriptionAsync(MessagingDbContext db, string id, CancellationToken ct) =>
        await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException(404, "subscription_not_found", "The subscription does not exist.");

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new ApiException(503, "host_not_ready", "The database has not completed initialization.");
    }

    internal static FileStream AcquireLock(string directory)
    {
        try { return new FileStream(Path.Combine(directory, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ApiException(503, "storage_unavailable", "The data directory is already in use or its exclusive lock is unavailable."); }
    }

    internal static async Task ValidateIntegrityAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await integrity.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        if (result != "ok") throw new InvalidDataException("SQLite integrity validation failed; current data was preserved.");
        await using var fk = connection.CreateCommand();
        fk.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await fk.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct)) throw new InvalidDataException("SQLite foreign-key validation failed; current data was preserved.");
    }

    private static async Task<bool> HasUserSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='AppMetadata');";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0;
    }

    private static void InvokeSafely(Action? handler)
    {
        if (handler is null) return;
        foreach (Action callback in handler.GetInvocationList()) { try { callback(); } catch { /* Durable fallback polling still observes the commit. */ } }
    }
    private static void InvokeSafely(Action<Guid>? handler, Guid id)
    {
        if (handler is null) return;
        foreach (Action<Guid> callback in handler.GetInvocationList()) { try { callback(id); } catch { /* Durable state is rechecked on each stream poll. */ } }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _writeGate.WaitAsync();
        try
        {
            _initialized = false;
            _disposed = true;
            _storeLock?.Dispose();
            _storeLock = null;
        }
        finally { _writeGate.Release(); }
    }

    private sealed record WriteResult(JsonObject Result, long? FirstSequence = null, long? LastSequence = null, Guid? ClosedSubscription = null);
}
