using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace STFCCommunityMod.Launcher.Core;

public class BattleStorageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class BattleStorageConflictException(string message) : BattleStorageException(message);

public sealed record BattleHistoryCursor(long CapturedAtUnixMs, long BattleRecordId);

public sealed record BattleHistoryItem(
    long BattleRecordId,
    long CapturedAtUnixMs,
    string? BattleId,
    string JournalId,
    string? BattleType,
    string EvidenceState);

public sealed record BattleEvidenceReceipt(
    long EvidenceId,
    string BatchId,
    int BatchEventOrdinal,
    string Disposition,
    long AcceptedAtUnixMs);

internal static class BattleStorageQueries
{
    internal const string Recent =
        """
        SELECT b.battle_record_id, b.captured_at_unix_ms,
               (SELECT a.alias_value FROM battle_alias a
                WHERE a.battle_record_id = b.battle_record_id AND a.alias_kind = 'battle-id'),
               (SELECT a.alias_value FROM battle_alias a
                WHERE a.battle_record_id = b.battle_record_id AND a.alias_kind = 'journal-id'),
               b.battle_type, b.aggregate_evidence_state
        FROM battle_record b
        WHERE $cursor = 0
           OR b.captured_at_unix_ms < $time
           OR (b.captured_at_unix_ms = $time AND b.battle_record_id < $id)
        ORDER BY b.captured_at_unix_ms DESC, b.battle_record_id DESC
        LIMIT $limit;
        """;

    internal const string Alias =
        """
        SELECT b.battle_record_id, b.captured_at_unix_ms,
               (SELECT a2.alias_value FROM battle_alias a2
                WHERE a2.battle_record_id = b.battle_record_id AND a2.alias_kind = 'battle-id'),
               (SELECT a2.alias_value FROM battle_alias a2
                WHERE a2.battle_record_id = b.battle_record_id AND a2.alias_kind = 'journal-id'),
               b.battle_type, b.aggregate_evidence_state
        FROM battle_alias a
        JOIN battle_record b ON b.battle_record_id = a.battle_record_id
        WHERE a.source_namespace = $namespace AND a.alias_identity = $alias;
        """;

    internal const string Receipts =
        """
        SELECT e.evidence_id, b.batch_id, r.batch_event_ordinal, r.disposition, r.accepted_at_unix_ms
        FROM event_battle eb
        JOIN event_evidence e ON e.evidence_id = eb.evidence_id
        JOIN event_ingest_receipt r ON r.evidence_id = e.evidence_id
        JOIN ingest_batch b ON b.ingest_batch_id = r.ingest_batch_id
        WHERE eb.battle_record_id = $battle
        ORDER BY r.accepted_at_unix_ms, b.ingest_batch_id, r.batch_event_ordinal;
        """;

    internal const string CanonicalDetail =
        """
        SELECT e.evidence_id, e.original_raw_sha256, e.original_raw_byte_count,
               e.original_compressed_sha256, e.original_compressed_byte_count,
               e.active_event_blob_id, eb.codec, eb.codec_minimum_reader_version,
               eb.raw_sha256, eb.raw_byte_count, eb.compressed_sha256, eb.compressed_byte_count,
               e.payload_state
        FROM event_battle link
        JOIN event_evidence e ON e.evidence_id = link.evidence_id
        JOIN event_blob eb ON eb.event_blob_id = e.active_event_blob_id
        WHERE link.battle_record_id = $battle AND link.relation_role = 'canonical-capture';
        """;
}

/// <summary>
/// Process-local serialization boundary supplied by the lifecycle composition.
/// It owns no path, marker, connection, worker, or cross-process policy.
/// </summary>
public sealed class BattleStorageSession : IDisposable
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private int ownerThreadId;
    private bool disposed;

    internal IDisposable Enter(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref ownerThreadId) == Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "A Battle storage operation is already active on this thread; finish its detail lease first.");
        }
        operationGate.Wait(cancellationToken);
        if (disposed)
        {
            operationGate.Release();
            throw new ObjectDisposedException(nameof(BattleStorageSession));
        }
        Volatile.Write(ref ownerThreadId, Environment.CurrentManagedThreadId);
        return new OperationLease(this);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref ownerThreadId) == Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "The Battle storage session cannot close while this thread owns an active operation.");
        }
        operationGate.Wait();
        try
        {
            disposed = true;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private sealed class OperationLease(BattleStorageSession owner) : IDisposable
    {
        private BattleStorageSession? owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref owner, null) is { } session)
            {
                Volatile.Write(ref session.ownerThreadId, 0);
                session.operationGate.Release();
            }
        }
    }
}

/// <summary>
/// The Battle-only durable sink. Its constructor is passive: provider and file
/// work begin only when a lifecycle owner calls a read or commit operation.
/// </summary>
public sealed class BattleCaptureRepository : IBattleIngestSink, IDisposable
{
    private const int MaximumPageSize = 200;
    internal static int MaximumStoredEventBytes => BattleIngestLimits.Default.MaximumReassembledBytes;
    private readonly string databasePath;
    private readonly string distributionId;
    private readonly TimeProvider timeProvider;
    private readonly Action<BattleStorageCommitStage>? commitObserver;
    private readonly BattleStorageSession storageSession;
    private readonly bool ownsStorageSession;
    private BattleStorageInspection? storeInspection;
    private bool disposed;

    public BattleCaptureRepository(
        string databasePath,
        string distributionId,
        TimeProvider? timeProvider = null)
        : this(databasePath, distributionId, new BattleStorageSession(), true, timeProvider, null)
    {
    }

    public BattleCaptureRepository(
        string databasePath,
        string distributionId,
        BattleStorageSession storageSession,
        TimeProvider? timeProvider = null)
        : this(databasePath, distributionId, storageSession, false, timeProvider, null)
    {
    }

    internal BattleCaptureRepository(
        string databasePath,
        string distributionId,
        TimeProvider? timeProvider,
        Action<BattleStorageCommitStage>? commitObserver)
        : this(databasePath, distributionId, new BattleStorageSession(), true, timeProvider, commitObserver)
    {
    }

    internal BattleCaptureRepository(
        string databasePath,
        string distributionId,
        BattleStorageSession storageSession,
        bool ownsStorageSession,
        TimeProvider? timeProvider,
        Action<BattleStorageCommitStage>? commitObserver)
    {
        this.databasePath = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(databasePath)
                ? databasePath
                : throw new ArgumentException("A database path is required.", nameof(databasePath)));
        this.distributionId = RequireIdentity(distributionId, nameof(distributionId));
        this.storageSession = storageSession ?? throw new ArgumentNullException(nameof(storageSession));
        this.ownsStorageSession = ownsStorageSession;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.commitObserver = commitObserver;
    }

    public ValueTask<BattleIngestCommitResult> CommitAsync(
        BattleIngestEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope.Kind == BattleIngestProtocol.FleetRuntimeKind)
        {
            throw new BattleStorageException(
                "Fleet runtime belongs to its separate ephemeral Fleet sink and cannot be written to Battle history.");
        }
        if (envelope.Kind != BattleIngestProtocol.BattleEventsKind
            || envelope.PayloadProtocol != BattleIngestProtocol.SidecarEventsVersion)
        {
            throw new BattleStorageException("Only capability-accepted battle.capture batches can enter Battle history v1.");
        }
        if (envelope.ExactEventBytes.Count == 0)
        {
            throw new BattleStorageException("A Battle capture batch must contain at least one event.");
        }
        if (envelope.ExactEventBytes.Count > BattleIngestLimits.Default.MaximumBatchEvents
            || envelope.ExactEventBytes.Any(bytes => bytes.Length is <= 0 || bytes.Length > MaximumStoredEventBytes))
        {
            throw new BattleStorageException("The Battle capture batch exceeds the accepted ingest bounds.");
        }

        using var operation = storageSession.Enter(cancellationToken);
        return CommitCore(envelope, cancellationToken);
    }

    private ValueTask<BattleIngestCommitResult> CommitCore(
        BattleIngestEnvelope envelope,
        CancellationToken cancellationToken)
    {
        EnsureWritableStore();
        using var connection = BattleStorageSchema.Open(databasePath, SqliteOpenMode.ReadWrite);
        var storeInstanceId = ReadStoreInstanceId(connection);
        var sourceNamespace = BattleIdentity.RuntimeNamespace(storeInstanceId, distributionId);
        var acceptedAt = timeProvider.GetUtcNow();
        var acceptedAtUnixMs = FloorUnixMilliseconds(acceptedAt);
        var envelopeHash = LowerHex(SHA256.HashData(envelope.ExactEnvelopeBytes.Span));
        var producerScope = BattleIdentity.ProducerScope(envelope.Source, envelope.SessionId);
        var liveBatchKey = BattleIdentity.LiveBatchKey(
            sourceNamespace,
            producerScope,
            envelope.BatchId);

        // Hash and compression sizing run before the write transaction. Each
        // descriptor retains only small metadata and the transport-owned slice.
        var captures = new CaptureDescriptor[envelope.ExactEventBytes.Count];
        for (var index = 0; index < captures.Length; ++index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            captures[index] = CaptureDescriptor.Create(
                envelope.ExactEventBytes[index],
                sourceNamespace,
                acceptedAtUnixMs);
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var existingBatch = FindBatch(connection, transaction, sourceNamespace, producerScope, envelope.BatchId);
            if (existingBatch is not null)
            {
                if (existingBatch.EnvelopeByteCount != envelope.ExactEnvelopeBytes.Length
                    || !string.Equals(existingBatch.EnvelopeSha256, envelopeHash, StringComparison.Ordinal)
                    || !string.Equals(existingBatch.LiveBatchKey, liveBatchKey, StringComparison.Ordinal)
                    || existingBatch.Result != "accepted"
                    || existingBatch.RejectedCount != 0
                    || existingBatch.AcceptedCount != captures.Length)
                {
                    throw new BattleStorageConflictException(
                        "This durable batch identity no longer matches the accepted envelope; no records were changed.");
                }

                VerifyExistingBatch(connection, transaction, existingBatch.IngestBatchId, captures, envelope);

                transaction.Rollback();
                return ValueTask.FromResult(new BattleIngestCommitResult(existingBatch.AcceptedCount));
            }

            var ingestBatchId = InsertBatch(
                connection,
                transaction,
                envelope,
                sourceNamespace,
                producerScope,
                liveBatchKey,
                envelopeHash,
                acceptedAtUnixMs);

            for (var index = 0; index < captures.Length; ++index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PersistCapture(connection, transaction, ingestBatchId, index, captures[index], envelope);
            }

            UpdateBatchAcceptedCount(connection, transaction, ingestBatchId, captures.Length);
            commitObserver?.Invoke(BattleStorageCommitStage.AfterWritesBeforeCommit);
            transaction.Commit();
            commitObserver?.Invoke(BattleStorageCommitStage.AfterCommit);
            return ValueTask.FromResult(new BattleIngestCommitResult(captures.Length));
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidDataException
                or CryptographicException)
        {
            throw new BattleStorageException(
                "The Battle capture transaction was rejected and rolled back.",
                exception);
        }
    }

    public IReadOnlyList<BattleHistoryItem> ReadRecent(
        int pageSize,
        BattleHistoryCursor? before = null)
    {
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page size must be 1-{MaximumPageSize}.");
        }
        using var operation = storageSession.Enter();
        EnsureReadableStore();
        using var connection = BattleStorageSchema.Open(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = BattleStorageQueries.Recent;
        command.Parameters.AddWithValue("$cursor", before is null ? 0 : 1);
        command.Parameters.AddWithValue("$time", before?.CapturedAtUnixMs ?? 0);
        command.Parameters.AddWithValue("$id", before?.BattleRecordId ?? 0);
        command.Parameters.AddWithValue("$limit", pageSize);
        using var reader = command.ExecuteReader();
        var results = new List<BattleHistoryItem>(pageSize);
        while (reader.Read())
        {
            results.Add(
                new(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5)));
        }
        return results;
    }

    public BattleHistoryItem? FindByAlias(string aliasKind, string aliasValue)
    {
        if (aliasKind is not ("battle-id" or "journal-id"))
        {
            throw new ArgumentOutOfRangeException(nameof(aliasKind));
        }
        aliasValue = RequireIdentity(aliasValue, nameof(aliasValue));
        using var operation = storageSession.Enter();
        EnsureReadableStore();
        using var connection = BattleStorageSchema.Open(databasePath, SqliteOpenMode.ReadOnly);
        var storeInstanceId = ReadStoreInstanceId(connection);
        var sourceNamespace = BattleIdentity.RuntimeNamespace(storeInstanceId, distributionId);
        using var command = connection.CreateCommand();
        command.CommandText = BattleStorageQueries.Alias;
        command.Parameters.AddWithValue("$namespace", sourceNamespace);
        command.Parameters.AddWithValue("$alias", BattleIdentity.AliasIdentity(aliasKind, aliasValue));
        using var reader = command.ExecuteReader();
        return !reader.Read()
            ? null
            : new(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5));
    }

    public IReadOnlyList<BattleEvidenceReceipt> ReadReceipts(long battleRecordId)
    {
        using var operation = storageSession.Enter();
        EnsureReadableStore();
        using var connection = BattleStorageSchema.Open(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = BattleStorageQueries.Receipts;
        command.Parameters.AddWithValue("$battle", battleRecordId);
        using var reader = command.ExecuteReader();
        var results = new List<BattleEvidenceReceipt>();
        while (reader.Read())
        {
            results.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt64(4)));
        }
        return results;
    }

    public BattleCaptureDetailLease OpenCanonicalDetail(long battleRecordId)
    {
        var operation = storageSession.Enter();
        try
        {
            EnsureReadableStore();
            return BattleCaptureDetailLease.Open(databasePath, battleRecordId, operation);
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            if (ownsStorageSession)
            {
                storageSession.Dispose();
            }
            disposed = true;
        }
    }

    private void EnsureWritableStore()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var inspection = storeInspection ??= BattleStorageSchema.Inspect(databasePath);
        if (inspection.State != BattleStorageReadability.Readable)
        {
            throw new BattleStorageException(inspection.Message);
        }
    }

    private void EnsureReadableStore()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var inspection = storeInspection ??= BattleStorageSchema.Inspect(databasePath);
        if (inspection.State is not (BattleStorageReadability.Readable or BattleStorageReadability.UnknownCodec))
        {
            throw new BattleStorageException(inspection.Message);
        }
    }

    private static string ReadStoreInstanceId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT store_instance_id FROM store_meta WHERE singleton_id = 1;";
        return (string?)command.ExecuteScalar()
            ?? throw new BattleStorageException("Battle history store identity is missing.");
    }

    private static ExistingBatch? FindBatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceNamespace,
        string producerScope,
        string batchId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT ingest_batch_id, envelope_sha256, envelope_byte_count, accepted_count,
                   live_batch_key, result, rejected_count
            FROM ingest_batch
            WHERE source_namespace = $namespace AND producer_scope = $scope AND batch_id = $batch;
            """;
        command.Parameters.AddWithValue("$namespace", sourceNamespace);
        command.Parameters.AddWithValue("$scope", producerScope);
        command.Parameters.AddWithValue("$batch", batchId);
        using var reader = command.ExecuteReader();
        return !reader.Read()
            ? null
            : new(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6));
    }

    private static void VerifyExistingBatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ingestBatchId,
        IReadOnlyList<CaptureDescriptor> captures,
        BattleIngestEnvelope envelope)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT r.batch_event_ordinal, r.disposition, e.evidence_id,
                   e.source_namespace, e.logical_event_key, e.occurrence_identity,
                   e.family, e.schema_discriminator, e.evidence_role, e.protocol_version,
                   e.producer_source, e.producer_version, e.source_timestamp_text,
                   e.event_timestamp_unix_ms, e.session_id, e.battle_id, e.journal_id,
                   e.original_raw_sha256, e.original_raw_byte_count,
                   e.original_compressed_sha256, e.original_compressed_byte_count,
                   e.active_event_blob_id, e.payload_state, e.evidence_state
            FROM event_ingest_receipt r
            JOIN event_evidence e ON e.evidence_id = r.evidence_id
            WHERE r.ingest_batch_id = $batch
            ORDER BY r.batch_event_ordinal;
            """;
        command.Parameters.AddWithValue("$batch", ingestBatchId);
        using var reader = command.ExecuteReader();
        var receipts = new List<ExistingReceipt>(captures.Count);
        while (reader.Read())
        {
            if (receipts.Count >= captures.Count)
            {
                throw new InvalidDataException("The durable Battle batch has extra receipt associations.");
            }
            receipts.Add(
                new(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetInt64(13),
                    reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.GetString(16),
                    reader.GetString(17),
                    reader.GetInt64(18),
                    reader.GetString(19),
                    reader.GetInt64(20),
                    reader.IsDBNull(21) ? null : reader.GetInt64(21),
                    reader.GetString(22),
                    reader.GetString(23)));
        }
        reader.Close();

        if (receipts.Count != captures.Count)
        {
            throw new InvalidDataException("The durable Battle batch has missing or extra receipt associations.");
        }
        for (var index = 0; index < captures.Count; ++index)
        {
            var receipt = receipts[index];
            var capture = captures[index];
            if (receipt.Ordinal != index
                || receipt.EvidenceId <= 0
                || receipt.Disposition is not ("accepted" or "exact-retry" or "rehydrated-hash-identity")
                || receipt.SourceNamespace != capture.SourceNamespace
                || receipt.LogicalEventKey != capture.LogicalEventKey
                || receipt.OccurrenceIdentity != capture.OccurrenceIdentity
                || receipt.Family != "battle.capture"
                || receipt.SchemaDiscriminator != "stfc.battle.capture.v1"
                || receipt.EvidenceRole != "canonical-capture-candidate"
                || receipt.ProtocolVersion != BattleIngestProtocol.SidecarEventsVersion
                || receipt.ProducerSource != (capture.EventSource ?? envelope.Source)
                || receipt.ProducerVersion != (capture.EventModVersion ?? envelope.ModVersion)
                || receipt.SourceTimestampText != capture.TimestampText
                || receipt.EventTimestampUnixMs != capture.EventTimestampUnixMs
                || receipt.SessionId != (capture.EventSessionId ?? envelope.SessionId)
                || receipt.BattleId != capture.BattleId
                || receipt.JournalId != capture.JournalId
                || receipt.RawSha256 != capture.RawSha256
                || receipt.RawByteCount != capture.ExactBytes.Length
                || receipt.CompressedSha256 != capture.CompressedSha256
                || receipt.CompressedByteCount != capture.CompressedByteCount
                || receipt.EventBlobId is null
                || receipt.PayloadState != "retained")
            {
                throw new InvalidDataException(
                    $"Durable Battle batch receipt ordinal {index} no longer matches its accepted occurrence.");
            }
            VerifyBlobEquals(connection, transaction, receipt.EventBlobId.Value, capture.ExactBytes, capture);
            VerifyBattleAssociation(connection, transaction, receipt.EvidenceId, receipt.EvidenceState, capture);
        }
    }

    private static void VerifyBattleAssociation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long evidenceId,
        string evidenceState,
        CaptureDescriptor capture)
    {
        using var relation = connection.CreateCommand();
        relation.Transaction = transaction;
        relation.CommandText =
            """
            SELECT eb.battle_record_id, eb.relation_role, b.source_namespace
            FROM event_battle eb
            JOIN battle_record b ON b.battle_record_id = eb.battle_record_id
            WHERE eb.evidence_id = $evidence;
            """;
        relation.Parameters.AddWithValue("$evidence", evidenceId);
        using var reader = relation.ExecuteReader();
        if (!reader.Read())
        {
            reader.Close();
            if (evidenceState == "battle-association-conflict"
                && HasCoherentAssociationConflict(connection, transaction, capture))
            {
                return;
            }
            throw new InvalidDataException("The durable Battle evidence has lost its battle association.");
        }
        var battleRecordId = reader.GetInt64(0);
        var relationRole = reader.GetString(1);
        var sourceNamespace = reader.GetString(2);
        if (reader.Read()
            || sourceNamespace != capture.SourceNamespace
            || evidenceState != "accepted")
        {
            throw new InvalidDataException("The durable Battle evidence has an incoherent battle association.");
        }
        reader.Close();

        VerifyCanonicalRole(
            connection,
            transaction,
            battleRecordId,
            evidenceId,
            relationRole,
            capture.SourceNamespace);
        VerifyRequiredAlias(connection, transaction, battleRecordId, capture.SourceNamespace, "journal-id", capture.JournalId);
        if (capture.BattleId is not null)
        {
            VerifyRequiredAlias(connection, transaction, battleRecordId, capture.SourceNamespace, "battle-id", capture.BattleId);
        }
    }

    private static bool HasCoherentAssociationConflict(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureDescriptor capture)
    {
        if (capture.BattleId is null)
        {
            return false;
        }
        var requestedAliases = new[]
        {
            (Kind: "journal-id", Value: capture.JournalId),
            (Kind: "battle-id", Value: capture.BattleId),
        };
        var resolved = requestedAliases
            .Select(alias => FindExactBattleAlias(
                connection,
                transaction,
                capture.SourceNamespace,
                alias.Kind,
                alias.Value))
            .ToArray();
        var distinct = resolved
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (distinct.Length > 1)
        {
            return true;
        }
        if (distinct.Length != 1)
        {
            return false;
        }

        for (var index = 0; index < requestedAliases.Length; ++index)
        {
            if (resolved[index].HasValue)
            {
                continue;
            }
            var requested = requestedAliases[index];
            var ownedValue = ReadExactOwnedAliasValue(
                connection,
                transaction,
                distinct[0],
                capture.SourceNamespace,
                requested.Kind);
            if (ownedValue is not null && ownedValue != requested.Value)
            {
                return true;
            }
        }
        return false;
    }

    private static long? FindExactBattleAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceNamespace,
        string aliasKind,
        string aliasValue)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT a.battle_record_id, a.alias_kind, a.alias_value, b.source_namespace
            FROM battle_alias a
            JOIN battle_record b ON b.battle_record_id = a.battle_record_id
            WHERE a.source_namespace = $namespace AND a.alias_identity = $identity;
            """;
        command.Parameters.AddWithValue("$namespace", sourceNamespace);
        command.Parameters.AddWithValue("$identity", BattleIdentity.AliasIdentity(aliasKind, aliasValue));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        var battleRecordId = reader.GetInt64(0);
        if (reader.GetString(1) != aliasKind
            || reader.GetString(2) != aliasValue
            || reader.GetString(3) != sourceNamespace
            || reader.Read())
        {
            throw new InvalidDataException("A requested durable Battle alias no longer has exact identity metadata.");
        }
        return battleRecordId;
    }

    private static string? ReadExactOwnedAliasValue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long battleRecordId,
        string sourceNamespace,
        string aliasKind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT a.source_namespace, a.alias_identity, a.alias_value, b.source_namespace
            FROM battle_alias a
            JOIN battle_record b ON b.battle_record_id = a.battle_record_id
            WHERE a.battle_record_id = $battle AND a.alias_kind = $kind;
            """;
        command.Parameters.AddWithValue("$battle", battleRecordId);
        command.Parameters.AddWithValue("$kind", aliasKind);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        var storedNamespace = reader.GetString(0);
        var aliasIdentity = reader.GetString(1);
        var aliasValue = reader.GetString(2);
        var targetNamespace = reader.GetString(3);
        if (reader.Read()
            || storedNamespace != sourceNamespace
            || targetNamespace != sourceNamespace
            || aliasIdentity != BattleIdentity.AliasIdentity(aliasKind, aliasValue))
        {
            throw new InvalidDataException("An owned durable Battle alias no longer has coherent identity metadata.");
        }
        return aliasValue;
    }

    private static void VerifyCanonicalRole(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long battleRecordId,
        long evidenceId,
        string relationRole,
        string sourceNamespace)
    {
        using var canonical = connection.CreateCommand();
        canonical.Transaction = transaction;
        canonical.CommandText =
            """
            SELECT eb.evidence_id, e.family, e.schema_discriminator, e.evidence_role,
                   e.evidence_state, e.source_namespace
            FROM event_battle eb
            JOIN event_evidence e ON e.evidence_id = eb.evidence_id
            WHERE eb.battle_record_id = $battle AND eb.relation_role = 'canonical-capture';
            """;
        canonical.Parameters.AddWithValue("$battle", battleRecordId);
        using var reader = canonical.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("The durable Battle record has no canonical capture.");
        }
        var canonicalEvidenceId = reader.GetInt64(0);
        if (reader.GetString(1) != "battle.capture"
            || reader.GetString(2) != "stfc.battle.capture.v1"
            || reader.GetString(3) != "canonical-capture-candidate"
            || reader.GetString(4) != "accepted"
            || reader.GetString(5) != sourceNamespace
            || reader.Read())
        {
            throw new InvalidDataException("The durable Battle record has an incoherent canonical capture.");
        }
        reader.Close();

        using var firstCapture = connection.CreateCommand();
        firstCapture.Transaction = transaction;
        firstCapture.CommandText =
            """
            SELECT MIN(eb.evidence_id)
            FROM event_battle eb
            JOIN event_evidence e ON e.evidence_id = eb.evidence_id
            WHERE eb.battle_record_id = $battle
              AND e.family = 'battle.capture'
              AND e.schema_discriminator = 'stfc.battle.capture.v1';
            """;
        firstCapture.Parameters.AddWithValue("$battle", battleRecordId);
        var firstCaptureEvidenceId = (long?)(firstCapture.ExecuteScalar());
        if (firstCaptureEvidenceId != canonicalEvidenceId
            || (relationRole == "canonical-capture" && canonicalEvidenceId != evidenceId)
            || (relationRole == "conflicting-capture" && canonicalEvidenceId == evidenceId)
            || relationRole is not ("canonical-capture" or "conflicting-capture"))
        {
            throw new InvalidDataException("The durable Battle evidence relation role is no longer coherent.");
        }

        VerifyBattleAggregate(connection, transaction, battleRecordId, sourceNamespace);
    }

    private static void VerifyBattleAggregate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long battleRecordId,
        string sourceNamespace)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT b.aggregate_evidence_state,
                   SUM(CASE WHEN eb.relation_role = 'canonical-capture' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN eb.relation_role = 'conflicting-capture' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN e.family = 'battle.capture'
                                  AND e.schema_discriminator = 'stfc.battle.capture.v1'
                            THEN 1 ELSE 0 END),
                   SUM(CASE WHEN eb.relation_role IN ('canonical-capture', 'conflicting-capture')
                                  AND e.family = 'battle.capture'
                                  AND e.schema_discriminator = 'stfc.battle.capture.v1'
                                  AND e.evidence_role = 'canonical-capture-candidate'
                                  AND e.evidence_state = 'accepted'
                                  AND e.source_namespace = b.source_namespace
                            THEN 1 ELSE 0 END)
            FROM battle_record b
            LEFT JOIN event_battle eb ON eb.battle_record_id = b.battle_record_id
            LEFT JOIN event_evidence e ON e.evidence_id = eb.evidence_id
            WHERE b.battle_record_id = $battle AND b.source_namespace = $namespace
            GROUP BY b.aggregate_evidence_state;
            """;
        command.Parameters.AddWithValue("$battle", battleRecordId);
        command.Parameters.AddWithValue("$namespace", sourceNamespace);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("The durable Battle aggregate has lost its source-qualified record.");
        }
        var aggregateState = reader.GetString(0);
        var canonicalCount = reader.GetInt64(1);
        var conflictingCount = reader.GetInt64(2);
        var captureCount = reader.GetInt64(3);
        var coherentRoleCount = reader.GetInt64(4);
        var expectedState = conflictingCount == 0 ? "accepted" : "capture-conflict";
        if (reader.Read()
            || canonicalCount != 1
            || captureCount != canonicalCount + conflictingCount
            || coherentRoleCount != canonicalCount + conflictingCount
            || aggregateState != expectedState)
        {
            throw new InvalidDataException("The durable Battle aggregate no longer matches its capture relations.");
        }
    }

    private static void VerifyRequiredAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long expectedBattleRecordId,
        string sourceNamespace,
        string aliasKind,
        string aliasValue)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT battle_record_id, alias_kind, alias_value
            FROM battle_alias
            WHERE source_namespace = $namespace AND alias_identity = $identity;
            """;
        command.Parameters.AddWithValue("$namespace", sourceNamespace);
        command.Parameters.AddWithValue("$identity", BattleIdentity.AliasIdentity(aliasKind, aliasValue));
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || reader.GetInt64(0) != expectedBattleRecordId
            || reader.GetString(1) != aliasKind
            || reader.GetString(2) != aliasValue
            || reader.Read())
        {
            throw new InvalidDataException($"The durable Battle evidence has lost its required {aliasKind} alias.");
        }
    }

    private static long InsertBatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BattleIngestEnvelope envelope,
        string sourceNamespace,
        string producerScope,
        string liveBatchKey,
        string envelopeHash,
        long acceptedAtUnixMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ingest_batch (
              source_namespace, producer_scope, batch_id, live_batch_key,
              envelope_sha256, envelope_byte_count, producer_artifact, producer_version,
              produced_at_text, accepted_at_unix_ms, result, accepted_count, rejected_count)
            VALUES ($namespace, $scope, $batch, $key, $hash, $bytes, $source, $version,
                    $produced, $accepted, 'accepted', 0, 0);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$namespace", sourceNamespace);
        command.Parameters.AddWithValue("$scope", producerScope);
        command.Parameters.AddWithValue("$batch", envelope.BatchId);
        command.Parameters.AddWithValue("$key", liveBatchKey);
        command.Parameters.AddWithValue("$hash", envelopeHash);
        command.Parameters.AddWithValue("$bytes", envelope.ExactEnvelopeBytes.Length);
        command.Parameters.AddWithValue("$source", envelope.Source);
        command.Parameters.AddWithValue("$version", envelope.ModVersion);
        command.Parameters.AddWithValue("$produced", envelope.ProducedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$accepted", acceptedAtUnixMs);
        return (long)(command.ExecuteScalar()
            ?? throw new BattleStorageException("The accepted batch did not receive an identity."));
    }

    private static void UpdateBatchAcceptedCount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ingestBatchId,
        int count)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE ingest_batch SET accepted_count = $count WHERE ingest_batch_id = $id;";
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$id", ingestBatchId);
        _ = command.ExecuteNonQuery();
    }

    private static void PersistCapture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ingestBatchId,
        int ordinal,
        CaptureDescriptor capture,
        BattleIngestEnvelope envelope)
    {
        var existing = FindOccurrence(connection, transaction, capture.SourceNamespace, capture.OccurrenceIdentity);
        long evidenceId;
        var disposition = "accepted";
        if (existing is not null)
        {
            if (existing.RawByteCount != capture.ExactBytes.Length
                || !string.Equals(existing.RawSha256, capture.RawSha256, StringComparison.Ordinal))
            {
                throw new BattleStorageConflictException("An occurrence identity collided with different Battle bytes.");
            }
            VerifyBlobEquals(connection, transaction, existing.EventBlobId, capture.ExactBytes, capture);
            evidenceId = existing.EvidenceId;
            disposition = "exact-retry";
        }
        else
        {
            var blobId = FindOrCreateBlob(connection, transaction, capture);
            evidenceId = InsertEvidence(connection, transaction, capture, envelope, blobId);
            AssociateBattle(connection, transaction, capture, evidenceId);
        }

        using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText =
            """
            INSERT INTO event_ingest_receipt (
              evidence_id, ingest_batch_id, batch_event_ordinal, disposition, accepted_at_unix_ms)
            VALUES ($evidence, $batch, $ordinal, $disposition, $accepted);
            """;
        receipt.Parameters.AddWithValue("$evidence", evidenceId);
        receipt.Parameters.AddWithValue("$batch", ingestBatchId);
        receipt.Parameters.AddWithValue("$ordinal", ordinal);
        receipt.Parameters.AddWithValue("$disposition", disposition);
        receipt.Parameters.AddWithValue("$accepted", capture.AcceptedAtUnixMs);
        _ = receipt.ExecuteNonQuery();
    }

    private static ExistingOccurrence? FindOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceNamespace,
        string occurrenceIdentity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT evidence_id, original_raw_sha256, original_raw_byte_count, active_event_blob_id
            FROM event_evidence
            WHERE source_namespace = $namespace AND occurrence_identity = $occurrence;
            """;
        command.Parameters.AddWithValue("$namespace", sourceNamespace);
        command.Parameters.AddWithValue("$occurrence", occurrenceIdentity);
        using var reader = command.ExecuteReader();
        return !reader.Read()
            ? null
            : new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static long FindOrCreateBlob(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureDescriptor capture)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText =
                """
                SELECT event_blob_id FROM event_blob
                WHERE codec = $codec AND raw_sha256 = $hash AND raw_byte_count = $bytes;
                """;
            find.Parameters.AddWithValue("$codec", BattleStorageSchema.CaptureCodec);
            find.Parameters.AddWithValue("$hash", capture.RawSha256);
            find.Parameters.AddWithValue("$bytes", capture.ExactBytes.Length);
            var found = find.ExecuteScalar();
            if (found is not null)
            {
                var blobId = (long)found;
                VerifyBlobEquals(connection, transaction, blobId, capture.ExactBytes, capture);
                return blobId;
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO event_blob (
              codec, codec_minimum_reader_version, compressed_sha256, raw_sha256,
              compressed_byte_count, raw_byte_count, compressed_bytes)
            VALUES ($codec, 1, $compressedHash, $rawHash, $compressedBytes, $rawBytes, zeroblob($compressedBytes));
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$codec", BattleStorageSchema.CaptureCodec);
        insert.Parameters.AddWithValue("$compressedHash", capture.CompressedSha256);
        insert.Parameters.AddWithValue("$rawHash", capture.RawSha256);
        insert.Parameters.AddWithValue("$compressedBytes", capture.CompressedByteCount);
        insert.Parameters.AddWithValue("$rawBytes", capture.ExactBytes.Length);
        var eventBlobId = (long)(insert.ExecuteScalar()
            ?? throw new BattleStorageException("The compressed Battle BLOB did not receive an identity."));

        using var blob = new SqliteBlob(connection, "event_blob", "compressed_bytes", eventBlobId, readOnly: false);
        var result = BattleBrotli.Write(capture.ExactBytes.Span, blob);
        if (result.ByteCount != capture.CompressedByteCount
            || !string.Equals(result.Sha256, capture.CompressedSha256, StringComparison.Ordinal))
        {
            throw new BattleStorageException("Streaming Brotli output changed between measurement and SQLite BLOB write.");
        }
        return eventBlobId;
    }

    private static long InsertEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureDescriptor capture,
        BattleIngestEnvelope envelope,
        long blobId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO event_evidence (
              source_namespace, logical_event_key, occurrence_identity, family,
              schema_discriminator, evidence_role, protocol_version, producer_source,
              producer_version, source_timestamp_text, event_timestamp_unix_ms,
              accepted_at_unix_ms, session_id, battle_id, journal_id,
              original_codec, original_codec_minimum_reader_version,
              original_compressed_sha256, original_raw_sha256,
              original_compressed_byte_count, original_raw_byte_count,
              active_event_blob_id, payload_state, evidence_state, disposition)
            VALUES ($namespace, $logical, $occurrence, 'battle.capture',
              'stfc.battle.capture.v1', 'canonical-capture-candidate',
              'stfc.sidecar.events.v0', $source, $version, $timestamp, $eventTime,
              $accepted, $session, $battle, $journal, $codec, 1,
              $compressedHash, $rawHash, $compressedBytes, $rawBytes,
              $blob, 'retained', 'accepted', 'original');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$namespace", capture.SourceNamespace);
        command.Parameters.AddWithValue("$logical", capture.LogicalEventKey);
        command.Parameters.AddWithValue("$occurrence", capture.OccurrenceIdentity);
        command.Parameters.AddWithValue("$source", capture.EventSource ?? envelope.Source);
        command.Parameters.AddWithValue("$version", capture.EventModVersion ?? envelope.ModVersion);
        command.Parameters.AddWithValue("$timestamp", capture.TimestampText);
        command.Parameters.AddWithValue("$eventTime", capture.EventTimestampUnixMs);
        command.Parameters.AddWithValue("$accepted", capture.AcceptedAtUnixMs);
        command.Parameters.AddWithValue("$session", capture.EventSessionId ?? envelope.SessionId);
        command.Parameters.AddWithValue("$battle", (object?)capture.BattleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$journal", capture.JournalId);
        command.Parameters.AddWithValue("$codec", BattleStorageSchema.CaptureCodec);
        command.Parameters.AddWithValue("$compressedHash", capture.CompressedSha256);
        command.Parameters.AddWithValue("$rawHash", capture.RawSha256);
        command.Parameters.AddWithValue("$compressedBytes", capture.CompressedByteCount);
        command.Parameters.AddWithValue("$rawBytes", capture.ExactBytes.Length);
        command.Parameters.AddWithValue("$blob", blobId);
        return (long)(command.ExecuteScalar()
            ?? throw new BattleStorageException("The Battle evidence did not receive an identity."));
    }

    private static void AssociateBattle(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureDescriptor capture,
        long evidenceId)
    {
        var aliases = new List<(string Kind, string Value)>(2);
        if (capture.BattleId is not null)
        {
            aliases.Add(("battle-id", capture.BattleId));
        }
        aliases.Add(("journal-id", capture.JournalId));

        var resolved = aliases
            .Select(alias => FindBattleByAlias(connection, transaction, capture.SourceNamespace, alias.Kind, alias.Value))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (resolved.Length > 1)
        {
            MarkEvidenceConflict(connection, transaction, evidenceId, "battle-association-conflict");
            return;
        }

        long battleRecordId;
        if (resolved.Length == 0)
        {
            var primary = aliases[0];
            battleRecordId = InsertBattle(connection, transaction, capture, primary.Kind, primary.Value);
            foreach (var alias in aliases)
            {
                InsertAlias(connection, transaction, battleRecordId, capture.SourceNamespace, alias.Kind, alias.Value);
            }
        }
        else
        {
            battleRecordId = resolved[0];
            foreach (var alias in aliases)
            {
                var existingValue = ReadBattleAliasValue(connection, transaction, battleRecordId, alias.Kind);
                if (existingValue is not null && !string.Equals(existingValue, alias.Value, StringComparison.Ordinal))
                {
                    MarkEvidenceConflict(connection, transaction, evidenceId, "battle-association-conflict");
                    return;
                }
                if (existingValue is null)
                {
                    InsertAlias(connection, transaction, battleRecordId, capture.SourceNamespace, alias.Kind, alias.Value);
                }
            }
        }

        using var canonical = connection.CreateCommand();
        canonical.Transaction = transaction;
        canonical.CommandText =
            "SELECT evidence_id FROM event_battle WHERE battle_record_id = $battle AND relation_role = 'canonical-capture';";
        canonical.Parameters.AddWithValue("$battle", battleRecordId);
        var canonicalId = canonical.ExecuteScalar();
        var role = canonicalId is null ? "canonical-capture" : "conflicting-capture";
        using var link = connection.CreateCommand();
        link.Transaction = transaction;
        link.CommandText =
            "INSERT INTO event_battle (evidence_id, battle_record_id, relation_role) VALUES ($evidence, $battle, $role);";
        link.Parameters.AddWithValue("$evidence", evidenceId);
        link.Parameters.AddWithValue("$battle", battleRecordId);
        link.Parameters.AddWithValue("$role", role);
        _ = link.ExecuteNonQuery();
        if (role == "conflicting-capture")
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE battle_record SET aggregate_evidence_state = 'capture-conflict' WHERE battle_record_id = $battle;";
            update.Parameters.AddWithValue("$battle", battleRecordId);
            _ = update.ExecuteNonQuery();
        }
    }

    private static long? FindBattleByAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceNamespace,
        string aliasKind,
        string aliasValue)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT battle_record_id FROM battle_alias WHERE source_namespace = $namespace AND alias_identity = $alias;";
        command.Parameters.AddWithValue("$namespace", sourceNamespace);
        command.Parameters.AddWithValue("$alias", BattleIdentity.AliasIdentity(aliasKind, aliasValue));
        return command.ExecuteScalar() is { } value ? (long)value : null;
    }

    private static string? ReadBattleAliasValue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long battleRecordId,
        string aliasKind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT alias_value FROM battle_alias WHERE battle_record_id = $battle AND alias_kind = $kind;";
        command.Parameters.AddWithValue("$battle", battleRecordId);
        command.Parameters.AddWithValue("$kind", aliasKind);
        return command.ExecuteScalar() as string;
    }

    private static long InsertBattle(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureDescriptor capture,
        string primaryAliasKind,
        string primaryAliasValue)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO battle_record (
              battle_key, source_namespace, captured_at_unix_ms, battle_type, aggregate_evidence_state)
            VALUES ($key, $namespace, $captured, $type, 'accepted');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue(
            "$key",
            BattleIdentity.BattleKey(capture.SourceNamespace, primaryAliasKind, primaryAliasValue));
        command.Parameters.AddWithValue("$namespace", capture.SourceNamespace);
        command.Parameters.AddWithValue("$captured", capture.EventTimestampUnixMs);
        command.Parameters.AddWithValue("$type", (object?)capture.BattleType ?? DBNull.Value);
        return (long)(command.ExecuteScalar()
            ?? throw new BattleStorageException("The Battle record did not receive an identity."));
    }

    private static void InsertAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long battleRecordId,
        string sourceNamespace,
        string aliasKind,
        string aliasValue)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO battle_alias (
              source_namespace, alias_identity, alias_kind, alias_value, battle_record_id)
            VALUES ($namespace, $identity, $kind, $value, $battle);
            """;
        command.Parameters.AddWithValue("$namespace", sourceNamespace);
        command.Parameters.AddWithValue("$identity", BattleIdentity.AliasIdentity(aliasKind, aliasValue));
        command.Parameters.AddWithValue("$kind", aliasKind);
        command.Parameters.AddWithValue("$value", aliasValue);
        command.Parameters.AddWithValue("$battle", battleRecordId);
        _ = command.ExecuteNonQuery();
    }

    private static void MarkEvidenceConflict(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long evidenceId,
        string state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE event_evidence SET evidence_state = $state WHERE evidence_id = $evidence;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$evidence", evidenceId);
        _ = command.ExecuteNonQuery();
    }

    private static void VerifyBlobEquals(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long blobId,
        ReadOnlyMemory<byte> expected,
        CaptureDescriptor descriptor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT codec, codec_minimum_reader_version, compressed_sha256,
                   raw_sha256, compressed_byte_count, raw_byte_count
            FROM event_blob WHERE event_blob_id = $id;
            """;
        command.Parameters.AddWithValue("$id", blobId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || reader.GetString(0) != BattleStorageSchema.CaptureCodec
            || reader.GetInt32(1) > BattleStorageSchema.CurrentVersion
            || reader.GetString(2) != descriptor.CompressedSha256
            || reader.GetString(3) != descriptor.RawSha256
            || reader.GetInt64(4) != descriptor.CompressedByteCount
            || reader.GetInt64(5) != descriptor.ExactBytes.Length)
        {
            throw new InvalidDataException("Stored Battle BLOB metadata does not match the accepted evidence.");
        }
        reader.Close();
        using var blob = new SqliteBlob(connection, "event_blob", "compressed_bytes", blobId, readOnly: true);
        BattleBrotli.VerifyAndCopy(
            blob,
            descriptor.CompressedSha256,
            descriptor.CompressedByteCount,
            descriptor.RawSha256,
            descriptor.ExactBytes.Length,
            new ComparingWriteStream(expected));
    }

    private static long FloorUnixMilliseconds(DateTimeOffset value)
    {
        var ticks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var quotient = Math.DivRem(ticks, TimeSpan.TicksPerMillisecond, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static string RequireIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            throw new ArgumentException("A bounded identity is required.", parameterName);
        }
        return value;
    }

    private static string LowerHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed record ExistingBatch(
        long IngestBatchId,
        string EnvelopeSha256,
        long EnvelopeByteCount,
        int AcceptedCount,
        string LiveBatchKey,
        string Result,
        int RejectedCount);

    private sealed record ExistingReceipt(
        int Ordinal,
        string Disposition,
        long EvidenceId,
        string SourceNamespace,
        string LogicalEventKey,
        string OccurrenceIdentity,
        string Family,
        string SchemaDiscriminator,
        string EvidenceRole,
        string ProtocolVersion,
        string ProducerSource,
        string ProducerVersion,
        string SourceTimestampText,
        long EventTimestampUnixMs,
        string SessionId,
        string? BattleId,
        string JournalId,
        string RawSha256,
        long RawByteCount,
        string CompressedSha256,
        long CompressedByteCount,
        long? EventBlobId,
        string PayloadState,
        string EvidenceState);

    private sealed record ExistingOccurrence(long EvidenceId, string RawSha256, long RawByteCount, long EventBlobId);

    private sealed record CaptureDescriptor(
        ReadOnlyMemory<byte> ExactBytes,
        string SourceNamespace,
        string LogicalEventKey,
        string OccurrenceIdentity,
        string RawSha256,
        string CompressedSha256,
        int CompressedByteCount,
        string TimestampText,
        long EventTimestampUnixMs,
        long AcceptedAtUnixMs,
        string JournalId,
        string? BattleId,
        string? BattleType,
        string? EventSessionId,
        string? EventSource,
        string? EventModVersion)
    {
        public static CaptureDescriptor Create(
            ReadOnlyMemory<byte> exactBytes,
            string sourceNamespace,
            long acceptedAtUnixMs)
        {
            var fields = CaptureFields.Parse(exactBytes.Span);
            var rawHash = LowerHex(SHA256.HashData(exactBytes.Span));
            var logical = BattleIdentity.LogicalEventKey(
                "battle.capture",
                "stfc.battle.capture.v1",
                "journal-id",
                fields.JournalId);
            var occurrence = BattleIdentity.OccurrenceIdentity(logical, exactBytes.Length, rawHash);
            var compressed = BattleBrotli.Measure(exactBytes.Span);
            BattleBrotli.ValidateStoredLengths(exactBytes.Length, compressed.ByteCount);
            return new(
                exactBytes,
                sourceNamespace,
                logical,
                occurrence,
                rawHash,
                compressed.Sha256,
                compressed.ByteCount,
                fields.TimestampText,
                fields.TimestampUnixMs,
                acceptedAtUnixMs,
                fields.JournalId,
                fields.BattleId,
                fields.BattleType,
                fields.SessionId,
                fields.Source,
                fields.ModVersion);
        }
    }

    private sealed record CaptureFields(
        string TimestampText,
        long TimestampUnixMs,
        string JournalId,
        string? BattleId,
        string? BattleType,
        string? SessionId,
        string? Source,
        string? ModVersion)
    {
        private static readonly Regex Rfc3339Timestamp = new(
            "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]+)?(?:Z|[+-][0-9]{2}:[0-9]{2})$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        public static CaptureFields Parse(ReadOnlySpan<byte> bytes)
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 48,
            });
            string? protocol = null;
            string? type = null;
            string? schema = null;
            string? timestamp = null;
            string? journal = null;
            string? battle = null;
            string? battleType = null;
            string? session = null;
            string? source = null;
            string? version = null;
            var hasCapture = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }
                var name = reader.GetString()!;
                if (!seen.Add(name) || !reader.Read())
                {
                    throw new InvalidDataException($"Battle capture contains duplicate or incomplete property '{name}'.");
                }
                switch (name)
                {
                    case "protocolVersion": protocol = ReadString(ref reader, name); break;
                    case "type": type = ReadString(ref reader, name); break;
                    case "schemaVersion": schema = ReadString(ref reader, name); break;
                    case "timestamp": timestamp = ReadString(ref reader, name); break;
                    case "journalId": journal = ReadString(ref reader, name); break;
                    case "battleId": battle = ReadString(ref reader, name); break;
                    case "battleType": battleType = ReadScalarText(ref reader, name); break;
                    case "sessionId": session = ReadString(ref reader, name); break;
                    case "source": source = ReadString(ref reader, name); break;
                    case "modVersion": version = ReadString(ref reader, name); break;
                    case "capture":
                        hasCapture = reader.TokenType == JsonTokenType.StartObject;
                        reader.Skip();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (protocol != BattleIngestProtocol.SidecarEventsVersion
                || type != "battle.capture"
                || schema != "stfc.battle.capture.v1"
                || !hasCapture
                || string.IsNullOrWhiteSpace(timestamp)
                || string.IsNullOrWhiteSpace(journal))
            {
                throw new InvalidDataException("The event is not an accepted complete battle.capture v1 object.");
            }
            if (timestamp.Length > 64
                || !Rfc3339Timestamp.IsMatch(timestamp)
                || !DateTimeOffset.TryParse(
                    timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                throw new InvalidDataException("The Battle source timestamp must contain an explicit UTC offset.");
            }
            return new(
                timestamp,
                FloorUnixMilliseconds(parsed),
                journal,
                battle,
                battleType,
                session,
                source,
                version);
        }

        private static string ReadString(ref Utf8JsonReader reader, string name) =>
            reader.TokenType == JsonTokenType.String && !string.IsNullOrWhiteSpace(reader.GetString())
                ? reader.GetString()!
                : throw new InvalidDataException($"Battle capture property '{name}' must be a non-empty string.");

        private static string ReadScalarText(ref Utf8JsonReader reader, string name) => reader.TokenType switch
        {
            JsonTokenType.String => ReadString(ref reader, name),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            _ => throw new InvalidDataException($"Battle capture property '{name}' must be a string or number."),
        };

    }
}

internal enum BattleStorageCommitStage
{
    AfterWritesBeforeCommit,
    AfterCommit,
}

public sealed class BattleCaptureDetailLease : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteBlob blob;
    private readonly IDisposable operation;
    private bool consumed;

    private BattleCaptureDetailLease(
        SqliteConnection connection,
        SqliteBlob blob,
        IDisposable operation,
        long evidenceId,
        string rawSha256,
        long rawByteCount,
        string compressedSha256,
        long compressedByteCount)
    {
        this.connection = connection;
        this.blob = blob;
        this.operation = operation;
        EvidenceId = evidenceId;
        RawSha256 = rawSha256;
        RawByteCount = rawByteCount;
        CompressedSha256 = compressedSha256;
        CompressedByteCount = compressedByteCount;
    }

    public long EvidenceId { get; }
    public string RawSha256 { get; }
    public long RawByteCount { get; }
    public string CompressedSha256 { get; }
    public long CompressedByteCount { get; }

    public void CopyExactEventTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(consumed, this);
        consumed = true;
        // Never expose bytes to the caller until the retained BLOB has passed a
        // complete compressed-hash, decode-length, and raw-hash verification.
        blob.Position = 0;
        BattleBrotli.VerifyAndCopy(
            blob,
            CompressedSha256,
            CompressedByteCount,
            RawSha256,
            RawByteCount,
            Stream.Null);
        blob.Position = 0;
        BattleBrotli.VerifyAndCopy(
            blob,
            CompressedSha256,
            CompressedByteCount,
            RawSha256,
            RawByteCount,
            destination);
    }

    public void Dispose()
    {
        consumed = true;
        try
        {
            blob.Dispose();
        }
        finally
        {
            try
            {
                connection.Dispose();
            }
            finally
            {
                operation.Dispose();
            }
        }
    }

    internal static BattleCaptureDetailLease Open(
        string databasePath,
        long battleRecordId,
        IDisposable operation)
    {
        var connection = BattleStorageSchema.Open(databasePath, SqliteOpenMode.ReadOnly);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = BattleStorageQueries.CanonicalDetail;
            command.Parameters.AddWithValue("$battle", battleRecordId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new BattleStorageException("This Battle has no retained canonical capture detail.");
            }
            if (reader.GetString(6) != BattleStorageSchema.CaptureCodec
                || reader.GetInt32(7) > BattleStorageSchema.CurrentVersion)
            {
                throw new BattleStorageException("This Battle detail needs a newer Bridge codec.");
            }
            var evidenceId = reader.GetInt64(0);
            var rawHash = reader.GetString(1);
            var rawBytes = reader.GetInt64(2);
            var compressedHash = reader.GetString(3);
            var compressedBytes = reader.GetInt64(4);
            var blobId = reader.GetInt64(5);
            if (reader.GetString(8) != rawHash
                || reader.GetInt64(9) != rawBytes
                || reader.GetString(10) != compressedHash
                || reader.GetInt64(11) != compressedBytes
                || reader.GetString(12) != "retained")
            {
                throw new InvalidDataException(
                    "Stored Battle evidence metadata no longer matches its active retained BLOB.");
            }
            BattleBrotli.ValidateStoredLengths(rawBytes, compressedBytes);
            reader.Close();
            var blob = new SqliteBlob(connection, "event_blob", "compressed_bytes", blobId, readOnly: true);
            if (blob.Length != compressedBytes)
            {
                blob.Dispose();
                throw new InvalidDataException("Stored Battle evidence BLOB length does not match its bounded metadata.");
            }
            return new(connection, blob, operation, evidenceId, rawHash, rawBytes, compressedHash, compressedBytes);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

public sealed class BattleIngestSinkRouter(
    IBattleIngestSink battleSink,
    IBattleIngestSink fleetSink) : IBattleIngestSink
{
    private readonly IBattleIngestSink battleSink = battleSink ?? throw new ArgumentNullException(nameof(battleSink));
    private readonly IBattleIngestSink fleetSink = fleetSink ?? throw new ArgumentNullException(nameof(fleetSink));

    public ValueTask<BattleIngestCommitResult> CommitAsync(
        BattleIngestEnvelope envelope,
        CancellationToken cancellationToken) =>
        envelope.Kind switch
        {
            BattleIngestProtocol.BattleEventsKind => battleSink.CommitAsync(envelope, cancellationToken),
            BattleIngestProtocol.FleetRuntimeKind => fleetSink.CommitAsync(envelope, cancellationToken),
            _ => ValueTask.FromException<BattleIngestCommitResult>(
                new BattleStorageException("The accepted ingest kind has no repository owner.")),
        };
}

internal static class BattleIdentity
{
    public static string RuntimeNamespace(string storeInstanceId, string distributionId) =>
        "runtime-v1/" + Hash("stfc.battle-runtime-namespace.v1", storeInstanceId, distributionId);

    public static string ProducerScope(string source, string sessionId) =>
        "producer-v1/" + Hash("stfc.battle-live-producer.v1", source, sessionId);

    public static string LiveBatchKey(string sourceNamespace, string producerScope, string batchId) =>
        "batch-v1/" + Hash("stfc.battle-live-batch.v1", sourceNamespace, producerScope, batchId);

    public static string LogicalEventKey(string type, string schema, string keyKind, string keyValue) =>
        "logical-v1/" + Hash("stfc.battle-logical-event.v1", type, schema, keyKind, keyValue);

    public static string OccurrenceIdentity(string logicalKey, int rawByteCount, string rawSha256) =>
        "occurrence-v1/" + Hash(
            "stfc.battle-event-occurrence.v1",
            logicalKey,
            rawByteCount.ToString(CultureInfo.InvariantCulture),
            rawSha256);

    public static string AliasIdentity(string aliasKind, string aliasValue) =>
        "alias-v1/" + Hash("stfc.battle-alias.v1", aliasKind, aliasValue);

    public static string BattleKey(string sourceNamespace, string aliasKind, string aliasValue) =>
        "battle-v1/" + Hash("stfc.source-qualified-battle.v1", sourceNamespace, aliasKind, aliasValue);

    private static string Hash(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

internal static class BattleBrotli
{
    private const int BufferSize = 32 * 1024;

    public static CompressedResult Measure(ReadOnlySpan<byte> input) => Write(input, Stream.Null);

    public static CompressedResult Write(ReadOnlySpan<byte> input, Stream destination)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var encoder = new BrotliEncoder(quality: 5, window: 22);
        var rented = ArrayPool<byte>.Shared.Rent(BufferSize);
        var total = 0;
        try
        {
            while (true)
            {
                var status = encoder.Compress(
                    input,
                    rented,
                    out var consumed,
                    out var written,
                    isFinalBlock: true);
                if (written > 0)
                {
                    destination.Write(rented, 0, written);
                    hash.AppendData(rented.AsSpan(0, written));
                    total = checked(total + written);
                }
                input = input[consumed..];
                if (status == OperationStatus.Done)
                {
                    break;
                }
                if (status != OperationStatus.DestinationTooSmall)
                {
                    throw new InvalidDataException($"Brotli encoder stopped with status '{status}'.");
                }
            }
            return new(total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public static void VerifyAndCopy(
        Stream compressed,
        string expectedCompressedHash,
        long expectedCompressedBytes,
        string expectedRawHash,
        long expectedRawBytes,
        Stream destination)
    {
        ValidateStoredLengths(expectedRawBytes, expectedCompressedBytes);
        if (compressed.CanSeek && compressed.Length != expectedCompressedBytes)
        {
            throw new InvalidDataException("Stored Battle evidence BLOB length does not match its bounded metadata.");
        }
        using var compressedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hashingSource = new HashingReadStream(compressed, compressedHash);
        using var decoder = new BrotliStream(hashingSource, CompressionMode.Decompress, leaveOpen: true);
        using var rawHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rented = ArrayPool<byte>.Shared.Rent(BufferSize);
        long rawBytes = 0;
        try
        {
            int read;
            while ((read = decoder.Read(rented, 0, rented.Length)) != 0)
            {
                if (read > expectedRawBytes - rawBytes)
                {
                    throw new InvalidDataException("Stored Battle evidence expands beyond its bounded raw length.");
                }
                destination.Write(rented, 0, read);
                rawHash.AppendData(rented.AsSpan(0, read));
                rawBytes = checked(rawBytes + read);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            throw new InvalidDataException("Stored Battle evidence is not valid Brotli data.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }

        var compressedDigest = Convert.ToHexString(compressedHash.GetHashAndReset()).ToLowerInvariant();
        var rawDigest = Convert.ToHexString(rawHash.GetHashAndReset()).ToLowerInvariant();
        if (hashingSource.BytesRead != expectedCompressedBytes
            || rawBytes != expectedRawBytes
            || !string.Equals(compressedDigest, expectedCompressedHash, StringComparison.Ordinal)
            || !string.Equals(rawDigest, expectedRawHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored Battle evidence failed compressed or raw byte verification.");
        }
    }

    internal static void ValidateStoredLengths(long rawBytes, long compressedBytes)
    {
        if (rawBytes is <= 0 || rawBytes > BattleCaptureRepository.MaximumStoredEventBytes
            || compressedBytes is <= 0 || compressedBytes > BattleCaptureRepository.MaximumStoredEventBytes)
        {
            throw new InvalidDataException("Stored Battle evidence declares byte counts outside accepted ingest bounds.");
        }
    }

    internal sealed record CompressedResult(int ByteCount, string Sha256);

    private sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                hash.AppendData(buffer.AsSpan(offset, read));
                BytesRead += read;
            }
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0)
            {
                hash.AppendData(buffer[..read]);
                BytesRead += read;
            }
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class ComparingWriteStream(ReadOnlyMemory<byte> expected) : Stream
{
    private readonly ReadOnlyMemory<byte> expected = expected;
    private int offset;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => offset;
    public override long Position { get => offset; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int bufferOffset, int count) => Write(buffer.AsSpan(bufferOffset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (offset + buffer.Length > expected.Length
            || !expected.Span.Slice(offset, buffer.Length).SequenceEqual(buffer))
        {
            throw new InvalidDataException("Stored Battle evidence differs from the accepted exact event bytes.");
        }
        offset += buffer.Length;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && offset != expected.Length)
        {
            throw new InvalidDataException("Stored Battle evidence ended before the accepted exact event bytes.");
        }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
