using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;

namespace STFCCommunityMod.Launcher.Core;

public enum BattleStorageReadability
{
    Absent,
    Readable,
    Unavailable,
    TooNew,
    Unsupported,
    UnknownCodec,
    Corrupt,
}

public sealed record BattleStorageInspection(
    BattleStorageReadability State,
    string Message,
    int? SchemaVersion = null,
    string? StoreInstanceId = null,
    BattleStorageProviderStatus? Provider = null);

public enum BattleStorageMigrationDisposition
{
    Current,
    NoReviewedPath,
}

public sealed record BattleStorageMigrationDispatch(
    BattleStorageMigrationDisposition Disposition,
    int FromVersion,
    int TargetVersion,
    string Message);

internal sealed record BattleStorageSchemaObject(string Type, string Name, string Table, string? Sql)
{
    public string SqlSha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql ?? "<automatic>")))
        .ToLowerInvariant();
}

internal sealed record BattleStorageSchemaManifest(
    string Sha256,
    IReadOnlyList<BattleStorageSchemaObject> Objects);

public static class BattleStorageSchema
{
    public const int ApplicationId = 1_398_030_914;
    public const int CurrentVersion = 1;
    public const string FormatId = "stfc.battle-store.v1";
    public const string CaptureCodec = "brotli-json-utf8-q5-w22-v1";
    public const int MinimumReaderVersion = 1;
    internal const string ExpectedSchemaManifestSha256 =
        "9aeebd9b66e528a56024974e5d51248aff6b4994c1d2a4d8af0c81284ebd9253";

    private static readonly Dictionary<int, string> Migrations =
        new Dictionary<int, string>();

    public static BattleStorageInspection Inspect(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            return new(BattleStorageReadability.Absent, "No Battle history has been created.");
        }

        var provider = BattleSqliteProvider.Qualify();
        if (!provider.IsReady)
        {
            return new(BattleStorageReadability.Unavailable, provider.Message, Provider: provider);
        }

        try
        {
            using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
            var applicationId = ReadIntPragma(connection, "application_id");
            var schemaVersion = ReadIntPragma(connection, "user_version");
            if (applicationId != ApplicationId)
            {
                return new(
                    BattleStorageReadability.Unsupported,
                    "This file is not a recognized STFC Battle history database.",
                    schemaVersion,
                    Provider: provider);
            }
            if (schemaVersion > CurrentVersion)
            {
                return new(
                    BattleStorageReadability.TooNew,
                    "This Battle history was created by a newer Bridge. Update Bridge to read it; the file was not changed.",
                    schemaVersion,
                    Provider: provider);
            }
            if (schemaVersion != CurrentVersion)
            {
                return new(
                    BattleStorageReadability.Unsupported,
                    "This Battle history version has no reviewed migration in this Bridge.",
                    schemaVersion,
                    Provider: provider);
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT format_id, store_instance_id, schema_version, minimum_reader_version FROM store_meta WHERE singleton_id = 1;";
            using var reader = command.ExecuteReader();
            if (!reader.Read()
                || !string.Equals(reader.GetString(0), FormatId, StringComparison.Ordinal)
                || !Guid.TryParseExact(reader.GetString(1), "D", out _))
            {
                return new(
                    BattleStorageReadability.Unsupported,
                    "The Battle history identity is not recognized; the file was not changed.",
                    schemaVersion,
                    Provider: provider);
            }
            var storeInstanceId = reader.GetString(1);
            var metadataSchemaVersion = reader.GetInt32(2);
            var minimumReader = reader.GetInt32(3);
            if (metadataSchemaVersion != schemaVersion)
            {
                return new(
                    BattleStorageReadability.Corrupt,
                    "Battle history schema authorities disagree; use Diagnostics before recovery.",
                    schemaVersion,
                    storeInstanceId,
                    provider);
            }
            if (minimumReader > CurrentVersion)
            {
                return new(
                    BattleStorageReadability.TooNew,
                    "This Battle history requires a newer Bridge reader; the file was not changed.",
                    schemaVersion,
                    storeInstanceId,
                    provider);
            }
            if (minimumReader != MinimumReaderVersion)
            {
                return new(
                    BattleStorageReadability.Corrupt,
                    "Battle history has an invalid minimum-reader contract; use Diagnostics before recovery.",
                    schemaVersion,
                    storeInstanceId,
                    provider);
            }
            if (reader.Read())
            {
                return new(
                    BattleStorageReadability.Corrupt,
                    "The Battle history contains invalid store identity rows; use Diagnostics before recovery.",
                    schemaVersion,
                    storeInstanceId,
                    provider);
            }

            ValidateSchemaSurface(connection);
            ValidateIntegrity(connection);

            using var codecCommand = connection.CreateCommand();
            codecCommand.CommandText =
                """
                SELECT event_blob_id, compressed_sha256, compressed_byte_count
                FROM event_blob
                WHERE codec <> $codec OR codec_minimum_reader_version > $reader
                ORDER BY event_blob_id;
                """;
            codecCommand.Parameters.AddWithValue("$codec", CaptureCodec);
            codecCommand.Parameters.AddWithValue("$reader", CurrentVersion);
            using var codecReader = codecCommand.ExecuteReader();
            if (codecReader.Read())
            {
                using var verificationConnection = Open(databasePath, SqliteOpenMode.ReadOnly);
                do
                {
                    var blobId = codecReader.GetInt64(0);
                    var expectedHash = codecReader.GetString(1);
                    var expectedBytes = codecReader.GetInt64(2);
                    VerifyCompressedBlob(verificationConnection, blobId, expectedHash, expectedBytes);
                }
                while (codecReader.Read());

                return new(
                    BattleStorageReadability.UnknownCodec,
                    "Some Battle evidence needs a newer Bridge codec. Other supported history remains available.",
                    schemaVersion,
                    storeInstanceId,
                    provider);
            }

            return new(
                BattleStorageReadability.Readable,
                "Battle history is readable.",
                schemaVersion,
                storeInstanceId,
                provider);
        }
        catch (SqliteException exception) when (IsUnavailable(exception.SqliteErrorCode))
        {
            return new(
                BattleStorageReadability.Unavailable,
                $"Battle history is temporarily unavailable: {exception.Message}",
                Provider: provider);
        }
        catch (Exception exception) when (
            exception is SqliteException or InvalidOperationException or InvalidDataException)
        {
            return new(
                BattleStorageReadability.Corrupt,
                $"Battle history failed structural validation: {exception.Message}",
                Provider: provider);
        }
    }

    /// <summary>
    /// Creates and validates a new database at a lifecycle-owned candidate path.
    /// This method neither selects the path nor promotes, backs up, or restores it.
    /// </summary>
    public static BattleStorageInspection CreateCandidate(string candidatePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        var provider = BattleSqliteProvider.Qualify();
        if (!provider.IsReady)
        {
            return new(BattleStorageReadability.Unavailable, provider.Message, Provider: provider);
        }

        var fullPath = Path.GetFullPath(candidatePath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The candidate path must have a parent directory.", nameof(candidatePath));
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                "The lifecycle owner must create the Battle storage candidate directory first.");
        }

        using var reservation = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        var ownedIdentity = BattleCandidateFileNative.ReadIdentity(reservation.SafeFileHandle);
        BattleStorageInspection? inspection = null;
        Exception? creationFailure = null;
        try
        {
            using var connection = Open(fullPath, SqliteOpenMode.ReadWrite);
            BattleSqliteConnection.ConfigureAndVerify(connection, includeFilePragmas: true);
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, Ddl);
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var storeId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();
            using (var meta = connection.CreateCommand())
            {
                meta.Transaction = transaction;
                meta.CommandText =
                    """
                    INSERT INTO store_meta (
                        singleton_id, format_id, store_instance_id, schema_version,
                        minimum_reader_version, created_at_utc, migrated_at_utc, active_policy_revision)
                    VALUES (1, $format, $store, 1, 1, $created, $migrated, 'policy-unset');
                    """;
                meta.Parameters.AddWithValue("$format", FormatId);
                meta.Parameters.AddWithValue("$store", storeId);
                meta.Parameters.AddWithValue("$created", now.ToString("O", CultureInfo.InvariantCulture));
                meta.Parameters.AddWithValue("$migrated", now.ToString("O", CultureInfo.InvariantCulture));
                _ = meta.ExecuteNonQuery();
            }
            Execute(connection, transaction, $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {CurrentVersion};");
            transaction.Commit();

            inspection = Inspect(fullPath);
            EnsureNoCandidateSidecars(fullPath);
        }
        catch (Exception exception)
        {
            creationFailure = exception;
        }
        reservation.Dispose();

        if (creationFailure is not null)
        {
            DeleteFailedCandidateOrThrow(fullPath, ownedIdentity, creationFailure);
            ExceptionDispatchInfo.Capture(creationFailure).Throw();
        }

        if (inspection!.State != BattleStorageReadability.Readable)
        {
            DeleteFailedCandidateOrThrow(fullPath, ownedIdentity, null);
        }
        return inspection;
    }

    public static bool HasMigration(int fromVersion) => Migrations.ContainsKey(fromVersion);

    public static BattleStorageMigrationDispatch DispatchMigration(int fromVersion) =>
        fromVersion == CurrentVersion
            ? new(
                BattleStorageMigrationDisposition.Current,
                fromVersion,
                CurrentVersion,
                "Battle history already uses the current schema; no migration ran.")
            : new(
                BattleStorageMigrationDisposition.NoReviewedPath,
                fromVersion,
                CurrentVersion,
                "No reviewed Battle history migration exists for this schema version; the file was not changed.");

    internal static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        BattleSqliteConnection.ConfigureAndVerify(connection, includeFilePragmas: mode != SqliteOpenMode.ReadOnly);
        return connection;
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static int ReadIntPragma(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool IsUnavailable(int code) => code is 5 or 6 or 10 or 14 or 23;

    private static void EnsureNoCandidateSidecars(string databasePath)
    {
        var residue = CandidateSidecars(databasePath).Where(File.Exists).ToArray();
        if (residue.Length != 0)
        {
            throw new BattleStorageException(
                "Battle history candidate left SQLite recovery files. The lifecycle owner must preserve and recover " +
                $"the candidate set: {string.Join(", ", residue.Select(Path.GetFileName))}.");
        }
    }

    private static void DeleteFailedCandidateOrThrow(
        string databasePath,
        BattleCandidateFileIdentity ownedIdentity,
        Exception? creationFailure)
    {
        var residue = CandidateSidecars(databasePath).Where(File.Exists).ToArray();
        if (residue.Length != 0)
        {
            throw new BattleStorageException(
                "Battle history candidate recovery files remain. No path-based cleanup was attempted; " +
                "the lifecycle owner must recover the exact candidate set.",
                creationFailure);
        }
        if (!BattleCandidateFileNative.TryDeleteExact(databasePath, ownedIdentity))
        {
            throw new BattleStorageException(
                "Battle history candidate cleanup could not prove and remove the exact reserved file. " +
                "The lifecycle owner must recover it before retrying.",
                creationFailure);
        }
    }

    private static IEnumerable<string> CandidateSidecars(string databasePath)
    {
        yield return databasePath + "-journal";
        yield return databasePath + "-wal";
        yield return databasePath + "-shm";
    }

    private static void ValidateSchemaSurface(SqliteConnection connection)
    {
        var manifest = DescribeSchema(connection);
        if (manifest.Objects.Any(item => item.Type is not ("table" or "index"))
            || manifest.Objects.Any(item =>
                item.Name.StartsWith("sqlite_", StringComparison.Ordinal)
                && !item.Name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Battle history contains an unexpected table, index, trigger, or view.");
        }
        if (!string.Equals(manifest.Sha256, ExpectedSchemaManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Battle history schema objects do not match the reviewed v1 manifest.");
        }
    }

    internal static BattleStorageSchemaManifest DescribeSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name, tbl_name;";
        using var reader = command.ExecuteReader();
        var objects = new List<BattleStorageSchemaObject>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (reader.Read())
        {
            var item = new BattleStorageSchemaObject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
            objects.Add(item);
            AppendManifestField(hash, item.Type);
            AppendManifestField(hash, item.Name);
            AppendManifestField(hash, item.Table);
            hash.AppendData(item.Sql is null ? [0] : [1]);
            if (item.Sql is not null)
            {
                AppendManifestField(hash, item.Sql);
            }
        }
        return new(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            objects);
    }

    private static void AppendManifestField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void ValidateIntegrity(SqliteConnection connection)
    {
        using (var quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check(1);";
            using var reader = quickCheck.ExecuteReader();
            if (!reader.Read() || !string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal) || reader.Read())
            {
                throw new InvalidDataException("SQLite quick-check rejected the Battle history structure.");
            }
        }

        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        using var foreignKeyReader = foreignKeys.ExecuteReader();
        if (foreignKeyReader.Read())
        {
            throw new InvalidDataException("Battle history contains a broken foreign-key association.");
        }
    }

    private static void VerifyCompressedBlob(
        SqliteConnection connection,
        long blobId,
        string expectedSha256,
        long expectedBytes)
    {
        using var blob = new SqliteBlob(connection, "event_blob", "compressed_bytes", blobId, readOnly: true);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32 * 1024);
        long count = 0;
        try
        {
            int read;
            while ((read = blob.Read(buffer, 0, buffer.Length)) != 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
                count = checked(count + read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (count != expectedBytes || !string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("An unsupported-codec Battle BLOB failed its stored-byte hash.");
        }
    }

    // SQLite 3.31-compatible DDL: deliberately no STRICT, JSON1, generated columns,
    // RETURNING, FTS, or provider-specific extensions.
    internal const string Ddl =
        """
        CREATE TABLE store_meta (
          singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
          format_id TEXT NOT NULL,
          store_instance_id TEXT NOT NULL,
          schema_version INTEGER NOT NULL,
          minimum_reader_version INTEGER NOT NULL,
          created_at_utc TEXT NOT NULL,
          migrated_at_utc TEXT NOT NULL,
          active_policy_revision TEXT NOT NULL
        );
        CREATE TABLE schema_migration (
          migration_id INTEGER PRIMARY KEY,
          from_version INTEGER NOT NULL,
          to_version INTEGER NOT NULL,
          implementation_version TEXT NOT NULL,
          started_at_utc TEXT NOT NULL,
          completed_at_utc TEXT NOT NULL,
          backup_id TEXT NOT NULL,
          UNIQUE (from_version, to_version)
        );
        CREATE TABLE ingest_batch (
          ingest_batch_id INTEGER PRIMARY KEY,
          source_namespace TEXT NOT NULL,
          producer_scope TEXT NOT NULL,
          batch_id TEXT NOT NULL,
          live_batch_key TEXT NOT NULL,
          envelope_sha256 TEXT NOT NULL,
          envelope_byte_count INTEGER NOT NULL CHECK (envelope_byte_count >= 0),
          producer_artifact TEXT NOT NULL,
          producer_version TEXT NOT NULL,
          produced_at_text TEXT NOT NULL,
          accepted_at_unix_ms INTEGER NOT NULL,
          result TEXT NOT NULL CHECK (result IN ('accepted', 'rejected')),
          accepted_count INTEGER NOT NULL,
          rejected_count INTEGER NOT NULL,
          bounded_error TEXT
        );
        CREATE TABLE import_receipt (
          import_receipt_id INTEGER PRIMARY KEY,
          attempt_id TEXT NOT NULL UNIQUE,
          import_namespace TEXT NOT NULL,
          source_artifact_sha256 TEXT NOT NULL,
          source_artifact_byte_count INTEGER NOT NULL,
          source_format TEXT NOT NULL,
          adapter_contract TEXT NOT NULL,
          adapter_version TEXT NOT NULL,
          requested_at_text TEXT NOT NULL,
          requested_at_unix_ms INTEGER NOT NULL,
          completed_at_text TEXT,
          completed_at_unix_ms INTEGER,
          result TEXT NOT NULL,
          accepted_count INTEGER NOT NULL,
          noop_count INTEGER NOT NULL,
          rejected_count INTEGER NOT NULL,
          bounded_error TEXT
        );
        CREATE TABLE event_blob (
          event_blob_id INTEGER PRIMARY KEY,
          codec TEXT NOT NULL,
          codec_minimum_reader_version INTEGER NOT NULL,
          compressed_sha256 TEXT NOT NULL,
          raw_sha256 TEXT NOT NULL,
          compressed_byte_count INTEGER NOT NULL CHECK (compressed_byte_count >= 0),
          raw_byte_count INTEGER NOT NULL CHECK (raw_byte_count >= 0),
          compressed_bytes BLOB NOT NULL,
          UNIQUE (codec, raw_sha256, raw_byte_count)
        );
        CREATE TABLE event_evidence (
          evidence_id INTEGER PRIMARY KEY,
          source_namespace TEXT NOT NULL,
          logical_event_key TEXT NOT NULL,
          occurrence_identity TEXT NOT NULL,
          family TEXT NOT NULL,
          schema_discriminator TEXT NOT NULL,
          evidence_role TEXT NOT NULL,
          protocol_version TEXT NOT NULL,
          producer_source TEXT NOT NULL,
          producer_version TEXT NOT NULL,
          source_timestamp_text TEXT NOT NULL,
          event_timestamp_unix_ms INTEGER NOT NULL,
          accepted_at_unix_ms INTEGER NOT NULL,
          session_id TEXT NOT NULL,
          battle_id TEXT,
          journal_id TEXT,
          original_codec TEXT NOT NULL,
          original_codec_minimum_reader_version INTEGER NOT NULL,
          original_compressed_sha256 TEXT NOT NULL,
          original_raw_sha256 TEXT NOT NULL,
          original_compressed_byte_count INTEGER NOT NULL,
          original_raw_byte_count INTEGER NOT NULL,
          active_event_blob_id INTEGER REFERENCES event_blob(event_blob_id),
          payload_state TEXT NOT NULL CHECK (payload_state IN ('retained', 'summary-only')),
          evidence_state TEXT NOT NULL,
          disposition TEXT NOT NULL
        );
        CREATE TABLE event_ingest_receipt (
          evidence_id INTEGER NOT NULL REFERENCES event_evidence(evidence_id),
          ingest_batch_id INTEGER NOT NULL REFERENCES ingest_batch(ingest_batch_id),
          batch_event_ordinal INTEGER NOT NULL,
          disposition TEXT NOT NULL CHECK (disposition IN ('accepted', 'exact-retry', 'rehydrated-hash-identity')),
          accepted_at_unix_ms INTEGER NOT NULL,
          PRIMARY KEY (evidence_id, ingest_batch_id, batch_event_ordinal)
        );
        CREATE TABLE event_import_receipt (
          evidence_id INTEGER NOT NULL REFERENCES event_evidence(evidence_id),
          import_receipt_id INTEGER NOT NULL REFERENCES import_receipt(import_receipt_id),
          entry_kind TEXT NOT NULL,
          entry_name TEXT,
          entry_ordinal INTEGER NOT NULL,
          disposition TEXT NOT NULL,
          accepted_at_unix_ms INTEGER NOT NULL,
          PRIMARY KEY (evidence_id, import_receipt_id, entry_ordinal)
        );
        CREATE TABLE battle_record (
          battle_record_id INTEGER PRIMARY KEY,
          battle_key TEXT NOT NULL UNIQUE,
          source_namespace TEXT NOT NULL,
          captured_at_unix_ms INTEGER NOT NULL,
          battle_type TEXT,
          compact_summary BLOB,
          aggregate_evidence_state TEXT NOT NULL
        );
        CREATE TABLE battle_alias (
          source_namespace TEXT NOT NULL,
          alias_identity TEXT NOT NULL,
          alias_kind TEXT NOT NULL CHECK (alias_kind IN ('battle-id', 'journal-id')),
          alias_value TEXT NOT NULL,
          battle_record_id INTEGER NOT NULL REFERENCES battle_record(battle_record_id)
        );
        CREATE TABLE event_battle (
          evidence_id INTEGER PRIMARY KEY REFERENCES event_evidence(evidence_id),
          battle_record_id INTEGER NOT NULL REFERENCES battle_record(battle_record_id),
          relation_role TEXT NOT NULL CHECK (relation_role IN ('canonical-capture', 'conflicting-capture', 'supplemental'))
        );
        CREATE UNIQUE INDEX ux_event_battle_canonical_capture
          ON event_battle(battle_record_id)
          WHERE relation_role = 'canonical-capture';
        CREATE TABLE catalog_blob (
          catalog_blob_id INTEGER PRIMARY KEY,
          catalog_schema TEXT NOT NULL,
          codec TEXT NOT NULL,
          codec_minimum_reader_version INTEGER NOT NULL,
          compressed_sha256 TEXT NOT NULL,
          raw_sha256 TEXT NOT NULL,
          compressed_byte_count INTEGER NOT NULL,
          raw_byte_count INTEGER NOT NULL,
          compressed_bytes BLOB NOT NULL,
          UNIQUE (catalog_schema, raw_sha256, raw_byte_count)
        );
        CREATE TABLE catalog_observation (
          catalog_observation_id INTEGER PRIMARY KEY,
          evidence_id INTEGER NOT NULL REFERENCES event_evidence(evidence_id),
          catalog_blob_id INTEGER NOT NULL REFERENCES catalog_blob(catalog_blob_id),
          source_namespace TEXT NOT NULL,
          session_id TEXT NOT NULL,
          observed_at_unix_ms INTEGER NOT NULL,
          battle_record_id INTEGER REFERENCES battle_record(battle_record_id)
        );
        CREATE TABLE battle_projection (
          battle_record_id INTEGER NOT NULL REFERENCES battle_record(battle_record_id),
          projection_kind TEXT NOT NULL,
          projection_schema TEXT NOT NULL,
          implementation_version TEXT NOT NULL,
          source_evidence_hash_set TEXT NOT NULL,
          compact_payload BLOB NOT NULL,
          updated_at_unix_ms INTEGER NOT NULL,
          PRIMARY KEY (battle_record_id, projection_kind, projection_schema, implementation_version, source_evidence_hash_set)
        );
        CREATE TABLE maintenance_ledger (
          operation_id TEXT PRIMARY KEY,
          operation_kind TEXT NOT NULL,
          completed_at_unix_ms INTEGER NOT NULL,
          affected_record_count INTEGER NOT NULL,
          affected_byte_count INTEGER NOT NULL,
          original_hash TEXT,
          new_hash TEXT
        );
        CREATE UNIQUE INDEX ux_event_occurrence ON event_evidence(source_namespace, occurrence_identity);
        CREATE INDEX ix_event_logical ON event_evidence(source_namespace, logical_event_key, event_timestamp_unix_ms, evidence_id);
        CREATE INDEX ix_event_hashes ON event_evidence(original_raw_sha256, original_compressed_sha256);
        CREATE UNIQUE INDEX ux_ingest_scope_batch ON ingest_batch(source_namespace, producer_scope, batch_id);
        CREATE UNIQUE INDEX ux_ingest_receipt_ordinal ON event_ingest_receipt(ingest_batch_id, batch_event_ordinal);
        CREATE INDEX ix_ingest_receipt_event ON event_ingest_receipt(evidence_id, ingest_batch_id);
        CREATE UNIQUE INDEX ux_import_receipt_ordinal ON event_import_receipt(import_receipt_id, entry_ordinal);
        CREATE INDEX ix_import_receipt_event ON event_import_receipt(evidence_id, import_receipt_id);
        CREATE INDEX ix_battle_recent ON battle_record(captured_at_unix_ms DESC, battle_record_id DESC);
        CREATE UNIQUE INDEX ux_battle_alias ON battle_alias(source_namespace, alias_identity);
        CREATE UNIQUE INDEX ux_battle_alias_kind ON battle_alias(battle_record_id, alias_kind);
        CREATE INDEX ix_event_session_family ON event_evidence(source_namespace, session_id, family, event_timestamp_unix_ms, evidence_id);
        CREATE INDEX ix_event_cleanup ON event_evidence(evidence_state, event_timestamp_unix_ms, accepted_at_unix_ms, evidence_id);
        CREATE INDEX ix_projection_lookup ON battle_projection(battle_record_id, projection_kind, projection_schema);
        CREATE INDEX ix_catalog_source_session ON catalog_observation(source_namespace, session_id, observed_at_unix_ms, catalog_observation_id);
        CREATE INDEX ix_catalog_battle ON catalog_observation(battle_record_id, catalog_observation_id);
        CREATE INDEX ix_event_battle_detail ON event_battle(battle_record_id, relation_role, evidence_id);
        """;
}

internal sealed record BattleCandidateFileIdentity(
    uint VolumeSerialNumber,
    uint FileIndexHigh,
    uint FileIndexLow);

internal static class BattleCandidateFileNative
{
    public static BattleCandidateFileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Battle storage candidates are Windows-only.");
        }
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        return new(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow);
    }

    public static bool TryDeleteExact(string path, BattleCandidateFileIdentity expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete);
            if (ReadIdentity(stream.SafeFileHandle) != expected)
            {
                return false;
            }
            var disposition = new FileDispositionInfo { DeleteFile = 1 };
            return SetFileInformationByHandle(
                stream.SafeFileHandle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>());
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
