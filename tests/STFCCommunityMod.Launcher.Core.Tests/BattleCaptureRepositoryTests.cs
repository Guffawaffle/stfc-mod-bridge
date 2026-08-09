using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BattleCaptureRepositoryTests
{
    private const string Distribution = "dev.guffawaffle.stfc-community-mod";

    [TestMethod]
    public void FreshProcessProviderUsesRetainedSystem32ModuleDespiteWritableShadows()
    {
        using var temporary = new TemporaryDirectory();
        var result = RunProbe("provider-shadow", Path.Combine(temporary.Path, "unused.sqlite"));

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.AreEqual(BattleStorageProviderState.Ready.ToString(), root.GetProperty("state").GetString());
        Assert.AreEqual(
            Path.GetFullPath(root.GetProperty("expectedModulePath").GetString()!),
            Path.GetFullPath(root.GetProperty("modulePath").GetString()!),
            ignoreCase: true,
            CultureInfo.InvariantCulture);
        Assert.IsTrue(root.GetProperty("providerInitialized").GetBoolean());
        Assert.IsTrue(Version.Parse(root.GetProperty("sqliteVersion").GetString()!) >= new Version(3, 31));
    }

    [TestMethod]
    public void QualifiedProviderReportsTheRetainedSystem32ModuleInProcess()
    {
        using var temporary = new TemporaryDirectory();
        var priorDirectory = Environment.CurrentDirectory;
        var priorPath = Environment.GetEnvironmentVariable("PATH");
        File.WriteAllText(Path.Combine(temporary.Path, "winsqlite3.dll"), "hostile-app-shadow");
        try
        {
            Environment.CurrentDirectory = temporary.Path;
            Environment.SetEnvironmentVariable("PATH", temporary.Path + Path.PathSeparator + priorPath);
            var result = BattleSqliteProvider.Qualify();

            Assert.AreEqual(BattleStorageProviderState.Ready, result.State, result.Message);
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll")),
                Path.GetFullPath(result.ModulePath!),
                ignoreCase: true,
                CultureInfo.InvariantCulture);
            Assert.IsTrue(Version.Parse(result.SqliteVersion!) >= new Version(3, 31));
        }
        finally
        {
            Environment.CurrentDirectory = priorDirectory;
            Environment.SetEnvironmentVariable("PATH", priorPath);
        }
    }

    [TestMethod]
    public void CandidateFreezesV1AndInspectionDoesNotMutateTooNewStore()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "candidate.sqlite");
        var created = BattleStorageSchema.CreateCandidate(database);

        Assert.AreEqual(BattleStorageReadability.Readable, created.State, created.Message);
        Assert.AreEqual(1, created.SchemaVersion);
        Assert.IsTrue(Guid.TryParseExact(created.StoreInstanceId, "D", out _));
        Assert.IsFalse(BattleStorageSchema.HasMigration(0));
        Assert.IsFalse(File.Exists(database + "-wal"));
        Assert.IsFalse(File.Exists(database + "-shm"));
        Assert.IsFalse(File.Exists(database + "-journal"));

        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 2;";
            _ = command.ExecuteNonQuery();
        }
        var before = SHA256.HashData(File.ReadAllBytes(database));
        var inspected = BattleStorageSchema.Inspect(database);
        var after = SHA256.HashData(File.ReadAllBytes(database));

        Assert.AreEqual(BattleStorageReadability.TooNew, inspected.State);
        CollectionAssert.AreEqual(before, after, "Read-only inspection must not mutate a too-new store.");
    }

    [TestMethod]
    public void CandidateCreationNeverOverwritesOrCleansUpAnExistingPath()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "existing.sqlite");
        var sentinel = "lifecycle-owned-existing-file"u8.ToArray();
        File.WriteAllBytes(database, sentinel);

        Assert.ThrowsException<IOException>(() => BattleStorageSchema.CreateCandidate(database));

        CollectionAssert.AreEqual(sentinel, File.ReadAllBytes(database));
        Assert.IsFalse(File.Exists(database + "-journal"));
        Assert.IsFalse(File.Exists(database + "-wal"));
        Assert.IsFalse(File.Exists(database + "-shm"));
    }

    [TestMethod]
    public void EmptyMigrationDispatcherNeverInitializesOrMutatesAnExistingV0File()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 0;";
            _ = command.ExecuteNonQuery();
        }
        var before = SHA256.HashData(File.ReadAllBytes(database));

        var dispatch = BattleStorageSchema.DispatchMigration(0);
        var inspection = BattleStorageSchema.Inspect(database);
        var after = SHA256.HashData(File.ReadAllBytes(database));

        Assert.AreEqual(BattleStorageMigrationDisposition.NoReviewedPath, dispatch.Disposition);
        Assert.IsFalse(BattleStorageSchema.HasMigration(0));
        Assert.AreEqual(BattleStorageReadability.Unsupported, inspection.State);
        CollectionAssert.AreEqual(before, after, "The empty migration dispatcher must be non-mutating.");
    }

    [TestMethod]
    public void InspectionRejectsDivergentMetadataAndInvalidMinimumReaderContract()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE store_meta SET schema_version = 999 WHERE singleton_id = 1;";
            _ = command.ExecuteNonQuery();
        }
        Assert.AreEqual(BattleStorageReadability.Corrupt, BattleStorageSchema.Inspect(database).State);

        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE store_meta SET schema_version = 1, minimum_reader_version = 0 WHERE singleton_id = 1;";
            _ = command.ExecuteNonQuery();
        }
        Assert.AreEqual(BattleStorageReadability.Corrupt, BattleStorageSchema.Inspect(database).State);
    }

    [TestMethod]
    public void InspectionRejectsMissingSchemaSurfaceAndForeignKeyViolations()
    {
        using var temporary = new TemporaryDirectory();
        var incompleteDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "incomplete"));
        var incomplete = CreateStore(incompleteDirectory.FullName);
        using (var connection = BattleStorageSchema.Open(incomplete, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP INDEX ux_ingest_receipt_ordinal;";
            _ = command.ExecuteNonQuery();
        }
        Assert.AreEqual(BattleStorageReadability.Corrupt, BattleStorageSchema.Inspect(incomplete).State);

        var orphanDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "orphan"));
        var orphan = CreateStore(orphanDirectory.FullName);
        using (var connection = BattleStorageSchema.Open(orphan, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "PRAGMA foreign_keys = OFF; " +
                "INSERT INTO event_ingest_receipt " +
                "(evidence_id, ingest_batch_id, batch_event_ordinal, disposition, accepted_at_unix_ms) " +
                "VALUES (999, 999, 0, 'accepted', 0);";
            _ = command.ExecuteNonQuery();
        }
        Assert.AreEqual(BattleStorageReadability.Corrupt, BattleStorageSchema.Inspect(orphan).State);
    }

    [TestMethod]
    public void ComprehensiveManifestRejectsUnexpectedObjectsAndConstraintSqlDrift()
    {
        using var temporary = new TemporaryDirectory();
        var mutations = new[]
        {
            "CREATE TABLE unexpected_table (value INTEGER);",
            "CREATE VIEW unexpected_view AS SELECT 1 AS value;",
            "CREATE TRIGGER unexpected_trigger AFTER INSERT ON ingest_batch BEGIN SELECT 1; END;",
            "PRAGMA writable_schema = ON; " +
                "UPDATE sqlite_schema SET sql = replace(sql, " +
                "'CHECK (relation_role IN', 'CHECK (relation_role NOT IN') WHERE name = 'event_battle'; " +
                "PRAGMA writable_schema = OFF;",
        };

        for (var index = 0; index < mutations.Length; ++index)
        {
            var directory = Directory.CreateDirectory(Path.Combine(temporary.Path, $"schema-{index}"));
            var database = CreateStore(directory.FullName);
            using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = mutations[index];
                _ = command.ExecuteNonQuery();
            }
            var before = SHA256.HashData(File.ReadAllBytes(database));

            var inspection = BattleStorageSchema.Inspect(database);

            Assert.AreEqual(BattleStorageReadability.Corrupt, inspection.State, $"Schema mutation {index} passed.");
            CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(database)));
        }
    }

    [TestMethod]
    public void InspectionDistinguishesASharingViolationFromCorruption()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        using var exclusive = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var inspection = BattleStorageSchema.Inspect(database);

        Assert.AreEqual(BattleStorageReadability.Unavailable, inspection.State, inspection.Message);
    }

    [TestMethod]
    public void CandidateMatchesTheFrozenV1SchemaFixture()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "BattleBridge",
            "battle-storage-schema.v1.json");
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        var root = fixture.RootElement;
        Assert.AreEqual("stfc.battle-storage-schema-fixture.v1", root.GetProperty("schema").GetString());

        using var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadOnly);
        Assert.AreEqual(root.GetProperty("applicationId").GetInt32(), Scalar(connection, "PRAGMA application_id;"));
        Assert.AreEqual(root.GetProperty("userVersion").GetInt32(), Scalar(connection, "PRAGMA user_version;"));
        using (var format = connection.CreateCommand())
        {
            format.CommandText = "SELECT format_id FROM store_meta WHERE singleton_id = 1;";
            Assert.AreEqual(root.GetProperty("formatId").GetString(), format.ExecuteScalar());
        }
        var manifest = BattleStorageSchema.DescribeSchema(connection);
        Assert.AreEqual(root.GetProperty("manifestSha256").GetString(), manifest.Sha256);
        Assert.AreEqual(BattleStorageSchema.ExpectedSchemaManifestSha256, manifest.Sha256);
        Assert.AreEqual(root.GetProperty("objectCount").GetInt32(), manifest.Objects.Count);
        var expectedObjects = root.GetProperty("objects").EnumerateArray().ToArray();
        Assert.AreEqual(expectedObjects.Length, manifest.Objects.Count);
        for (var index = 0; index < expectedObjects.Length; ++index)
        {
            var expected = expectedObjects[index].EnumerateArray().ToArray();
            var actual = manifest.Objects[index];
            Assert.AreEqual(expected[0].GetString(), actual.Type, $"Object type drifted at manifest row {index}.");
            Assert.AreEqual(expected[1].GetString(), actual.Name, $"Object name drifted at manifest row {index}.");
            Assert.AreEqual(expected[2].GetString(), actual.Table, $"Object table drifted at manifest row {index}.");
            Assert.AreEqual(expected[3].GetString(), actual.SqlSha256, $"Object SQL drifted at manifest row {index}.");
        }
        var forbiddenTypes = root.GetProperty("unexpectedObjectTypes")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsFalse(manifest.Objects.Any(item => forbiddenTypes.Contains(item.Type)));
    }

    [TestMethod]
    public void IdentityVectorsFreezeBigEndianLengthPrefixedV1Tuples()
    {
        var sourceNamespace = BattleIdentity.RuntimeNamespace(
            "00000000-0000-4000-8000-000000000001",
            Distribution);
        var producerScope = BattleIdentity.ProducerScope("stfc-community-mod", "session-A");
        var logical = BattleIdentity.LogicalEventKey(
            "battle.capture",
            "stfc.battle.capture.v1",
            "journal-id",
            "9007199254740993123");

        Assert.AreEqual("runtime-v1/7495e01982a8a467d5cc70de2929e5845439d99066450c2a7208f2055f75a15e", sourceNamespace);
        Assert.AreEqual("producer-v1/5dc78ecea1aceab5f315035717049e55c65f89ba80ef86e33b4cdd4d0bffda7d", producerScope);
        Assert.AreEqual("logical-v1/84c6697cea760271f1a5196a08663f9f8d6d530306f3806671759a53a48b3954", logical);
        Assert.AreEqual(
            "occurrence-v1/329e30829f276bad23925db93275c5b03166c8aa26d03aa8efc9744747e9c14c",
            BattleIdentity.OccurrenceIdentity(logical, 123, new string('a', 64)));
        Assert.AreEqual(
            "alias-v1/3be91e2aecd70f9eb3961381bfaaab637f5a3af52a4e47223265e3e67b92ee9e",
            BattleIdentity.AliasIdentity("journal-id", "9007199254740993123"));
        Assert.AreEqual(
            "battle-v1/01d5dec5e1d4c0fc47f83477517625a4125926d6348f3c8e8958e47840dc926b",
            BattleIdentity.BattleKey(sourceNamespace, "battle-id", "9007199254740993555"));
        Assert.AreEqual(
            "batch-v1/849ddfbb25ddb603c7b3d3aea3b7d1c028806aa4f6c2df8180ef4d87979bbde4",
            BattleIdentity.LiveBatchKey(sourceNamespace, producerScope, "batch-1"));
    }

    [TestMethod]
    public async Task ExactCaptureRoundTripsAndDurableRetryAddsReceiptWithoutDuplicatingEvidence()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var exact =
            """
            { "protocolVersion":"stfc.sidecar.events.v0", "type":"battle.capture",
              "schemaVersion":"stfc.battle.capture.v1", "timestamp":"2026-05-18T12:05:00.000123Z",
              "sessionId":"session-A", "modVersion":"1.2.3", "source":"scopely.journal.battle",
              "journalId":"9007199254740993123", "battleId":"9007199254740993555", "battleType":1,
              "capture": { "message":"preserve \\u263A and whitespace", "largeId":"18446744073709551615" } }
            """;
        var first = Envelope("batch-1", "session-A", exact);
        var repository = new BattleCaptureRepository(database, Distribution);
        var committed = await repository.CommitAsync(first, CancellationToken.None);
        Assert.AreEqual(1, committed.AcceptedRecords);

        var history = repository.ReadRecent(20);
        Assert.AreEqual(1, history.Count);
        Assert.AreEqual("9007199254740993123", history[0].JournalId);
        Assert.AreEqual("9007199254740993555", history[0].BattleId);
        using (var detail = repository.OpenCanonicalDetail(history[0].BattleRecordId))
        using (var output = new MemoryStream())
        {
            detail.CopyExactEventTo(output);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(exact), output.ToArray());
        }

        var restarted = new BattleCaptureRepository(database, Distribution);
        await restarted.CommitAsync(Envelope("batch-2", "session-A", exact), CancellationToken.None);
        Assert.AreEqual(2, restarted.ReadReceipts(history[0].BattleRecordId).Count);
        Assert.AreEqual("exact-retry", restarted.ReadReceipts(history[0].BattleRecordId)[1].Disposition);
        AssertCounts(database, batches: 2, evidence: 1, blobs: 1, battles: 1);
    }

    [TestMethod]
    public async Task SameDurableBatchWithDifferentBytesFailsAtomically()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var repository = new BattleCaptureRepository(database, Distribution);
        var first = Capture("journal-1", "battle-1", "first");
        await repository.CommitAsync(Envelope("reused", "session-A", first), CancellationToken.None);

        await Assert.ThrowsExceptionAsync<BattleStorageConflictException>(async () =>
            await repository.CommitAsync(
                Envelope("reused", "session-A", Capture("journal-2", "battle-2", "different")),
                CancellationToken.None));

        AssertCounts(database, batches: 1, evidence: 1, blobs: 1, battles: 1);
    }

    [TestMethod]
    public async Task DurableNoOpFailsClosedOnBatchReceiptEvidenceOrBlobDrift()
    {
        using var temporary = new TemporaryDirectory();
        var exact = Capture("journal-1", "battle-1", new string('x', 16 * 1024));
        var envelope = Envelope("durable", "session-A", exact);
        var mutations = new[]
        {
            "UPDATE ingest_batch SET accepted_count = 0 WHERE ingest_batch_id = 1;",
            "DELETE FROM event_ingest_receipt WHERE ingest_batch_id = 1;",
            "INSERT INTO event_ingest_receipt " +
                "SELECT evidence_id, ingest_batch_id, 1, disposition, accepted_at_unix_ms " +
                "FROM event_ingest_receipt WHERE ingest_batch_id = 1 AND batch_event_ordinal = 0;",
            "UPDATE event_evidence SET occurrence_identity = 'occurrence-v1/drifted' WHERE evidence_id = 1;",
            "UPDATE event_evidence SET logical_event_key = 'logical-event-v1/drifted', " +
                "family = 'drifted', schema_discriminator = 'drifted', " +
                "evidence_role = 'drifted', protocol_version = 'drifted' WHERE evidence_id = 1;",
            "UPDATE event_evidence SET producer_source = 'drifted', producer_version = 'drifted', " +
                "source_timestamp_text = '2026-05-18T12:05:01.000Z', event_timestamp_unix_ms = 0, " +
                "session_id = 'drifted' WHERE evidence_id = 1;",
            "UPDATE event_evidence SET battle_id = 'drifted', journal_id = 'drifted' WHERE evidence_id = 1;",
            "UPDATE event_evidence SET evidence_state = 'drifted' WHERE evidence_id = 1;",
            "UPDATE event_evidence SET source_namespace = 'runtime-v1/cross-source' WHERE evidence_id = 1;",
            "UPDATE battle_record SET source_namespace = 'runtime-v1/cross-source' WHERE battle_record_id = 1;",
            "UPDATE battle_record SET aggregate_evidence_state = 'capture-conflict' WHERE battle_record_id = 1;",
            "DELETE FROM event_battle WHERE evidence_id = 1;",
            "UPDATE event_battle SET relation_role = 'conflicting-capture' WHERE evidence_id = 1;",
            "UPDATE event_battle SET relation_role = 'supplemental' WHERE evidence_id = 1;",
            "DELETE FROM battle_alias WHERE alias_kind = 'journal-id';",
            "UPDATE battle_alias SET alias_identity = 'battle-alias-v1/drifted', alias_value = 'drifted' " +
                "WHERE alias_kind = 'battle-id';",
            "UPDATE event_blob SET compressed_bytes = zeroblob(compressed_byte_count) WHERE event_blob_id = 1;",
            "PRAGMA foreign_keys = OFF; DELETE FROM event_evidence WHERE evidence_id = 1;",
        };

        for (var index = 0; index < mutations.Length; ++index)
        {
            var directory = Directory.CreateDirectory(Path.Combine(temporary.Path, $"mutation-{index}"));
            var database = CreateStore(directory.FullName);
            using (var initial = new BattleCaptureRepository(database, Distribution))
            {
                await initial.CommitAsync(envelope, CancellationToken.None);
            }
            using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = mutations[index];
                _ = command.ExecuteNonQuery();
            }
            var before = SHA256.HashData(File.ReadAllBytes(database));

            using var restarted = new BattleCaptureRepository(database, Distribution);
            Exception? failure = null;
            try
            {
                await restarted.CommitAsync(envelope, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Assert.IsNotNull(failure, $"Durable retry mutation {index} did not fail closed.");
            Assert.IsInstanceOfType<BattleStorageException>(failure);

            CollectionAssert.AreEqual(
                before,
                SHA256.HashData(File.ReadAllBytes(database)),
                $"Failed durable retry mutation {index} must not write.");
        }
    }

    [TestMethod]
    public async Task DurableAssociationConflictWithAliasesOnDistinctRecordsReplaysAsNoOp()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        using var repository = new BattleCaptureRepository(database, Distribution);
        await repository.CommitAsync(
            Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "first")),
            CancellationToken.None);
        await repository.CommitAsync(
            Envelope("batch-2", "session-A", Capture("journal-2", "battle-2", "second")),
            CancellationToken.None);
        var conflict = Envelope("batch-3", "session-A", Capture("journal-1", "battle-2", "conflict"));
        await repository.CommitAsync(conflict, CancellationToken.None);
        var before = SHA256.HashData(File.ReadAllBytes(database));

        var replay = await repository.CommitAsync(conflict, CancellationToken.None);

        Assert.AreEqual(1, replay.AcceptedRecords);
        CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(database)));
        AssertCounts(database, batches: 3, evidence: 3, blobs: 3, battles: 2);
        using var inspection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadOnly);
        Assert.AreEqual(1, Scalar(inspection, "SELECT COUNT(*) FROM event_evidence WHERE evidence_state = 'battle-association-conflict';"));
        Assert.AreEqual(0, Scalar(
            inspection,
            "SELECT COUNT(*) FROM event_battle eb JOIN event_evidence e ON e.evidence_id = eb.evidence_id " +
                "WHERE e.evidence_state = 'battle-association-conflict';"));
    }

    [TestMethod]
    public async Task DurableAssociationConflictWithDifferentOwnedAliasReplaysAsNoOp()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        using var repository = new BattleCaptureRepository(database, Distribution);
        await repository.CommitAsync(
            Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "first")),
            CancellationToken.None);
        var conflict = Envelope("batch-2", "session-A", Capture("journal-1", "battle-2", "conflict"));
        await repository.CommitAsync(conflict, CancellationToken.None);
        var before = SHA256.HashData(File.ReadAllBytes(database));

        var replay = await repository.CommitAsync(conflict, CancellationToken.None);

        Assert.AreEqual(1, replay.AcceptedRecords);
        CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(database)));
        AssertCounts(database, batches: 2, evidence: 2, blobs: 2, battles: 1);
        using var inspection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadOnly);
        Assert.AreEqual(1, Scalar(inspection, "SELECT COUNT(*) FROM event_evidence WHERE evidence_state = 'battle-association-conflict';"));
        Assert.AreEqual(0, Scalar(inspection, "SELECT COUNT(*) FROM battle_alias WHERE alias_value = 'battle-2';"));
    }

    [TestMethod]
    public async Task DurableAssociationConflictRejectsEvidenceStateDriftWithoutMutation()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var conflict = Envelope("batch-2", "session-A", Capture("journal-1", "battle-2", "conflict"));
        using (var repository = new BattleCaptureRepository(database, Distribution))
        {
            await repository.CommitAsync(
                Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "first")),
                CancellationToken.None);
            await repository.CommitAsync(conflict, CancellationToken.None);
        }
        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE event_evidence SET evidence_state = 'accepted' " +
                "WHERE evidence_state = 'battle-association-conflict';";
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        await AssertDurableRetryFailsWithoutMutation(database, conflict);
    }

    [TestMethod]
    public async Task DurableAssociationConflictRejectsCrossSourceAliasTargetWithoutMutation()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var conflict = Envelope("batch-2", "session-A", Capture("journal-1", "battle-2", "conflict"));
        using (var repository = new BattleCaptureRepository(database, Distribution))
        {
            await repository.CommitAsync(
                Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "first")),
                CancellationToken.None);
            await repository.CommitAsync(conflict, CancellationToken.None);
        }
        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE battle_record SET source_namespace = 'runtime-v1/cross-source' WHERE battle_record_id = 1;";
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        await AssertDurableRetryFailsWithoutMutation(database, conflict);
    }

    [TestMethod]
    public async Task DurableConflictingCaptureRejectsCanonicalSourceOrAggregateDriftWithoutMutation()
    {
        using var temporary = new TemporaryDirectory();
        var conflicting = Envelope("batch-2", "session-A", Capture("journal-1", "battle-1", "second"));
        var mutations = new[]
        {
            "UPDATE event_evidence SET source_namespace = 'runtime-v1/cross-source' WHERE evidence_id = 1;",
            "UPDATE battle_record SET aggregate_evidence_state = 'accepted' WHERE battle_record_id = 1;",
        };
        for (var index = 0; index < mutations.Length; ++index)
        {
            var directory = Directory.CreateDirectory(Path.Combine(temporary.Path, $"coherence-{index}"));
            var database = CreateStore(directory.FullName);
            using (var repository = new BattleCaptureRepository(database, Distribution))
            {
                await repository.CommitAsync(
                    Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "first")),
                    CancellationToken.None);
                await repository.CommitAsync(conflicting, CancellationToken.None);
            }
            using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = mutations[index];
                Assert.AreEqual(1, command.ExecuteNonQuery());
            }

            await AssertDurableRetryFailsWithoutMutation(database, conflicting);
        }
    }

    [TestMethod]
    public async Task DurableRoleSwapRejectsAReassignedCanonicalWithoutMutation()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var conflicting = Envelope("batch-2", "session-A", Capture("journal-1", "battle-1", "second"));
        using (var repository = new BattleCaptureRepository(database, Distribution))
        {
            await repository.CommitAsync(
                Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "first")),
                CancellationToken.None);
            await repository.CommitAsync(conflicting, CancellationToken.None);
        }
        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var transaction = connection.BeginTransaction())
        {
            using var demote = connection.CreateCommand();
            demote.Transaction = transaction;
            demote.CommandText = "UPDATE event_battle SET relation_role = 'supplemental' WHERE evidence_id = 1;";
            Assert.AreEqual(1, demote.ExecuteNonQuery());
            using var promote = connection.CreateCommand();
            promote.Transaction = transaction;
            promote.CommandText = "UPDATE event_battle SET relation_role = 'canonical-capture' WHERE evidence_id = 2;";
            Assert.AreEqual(1, promote.ExecuteNonQuery());
            transaction.Commit();
        }

        await AssertDurableRetryFailsWithoutMutation(database, conflicting);
    }

    [TestMethod]
    public async Task ByteDifferentCaptureCoexistsAndMarksCanonicalConflict()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var repository = new BattleCaptureRepository(database, Distribution);
        await repository.CommitAsync(
            Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "first")),
            CancellationToken.None);
        await repository.CommitAsync(
            Envelope("batch-2", "session-A", Capture("journal-1", "battle-1", "second")),
            CancellationToken.None);

        var found = repository.FindByAlias("journal-id", "journal-1");
        Assert.IsNotNull(found);
        Assert.AreEqual("capture-conflict", found.EvidenceState);
        Assert.AreEqual("battle-1", found.BattleId);
        AssertCounts(database, batches: 2, evidence: 2, blobs: 2, battles: 1);
    }

    [TestMethod]
    public async Task CrossDistributionKeepsEqualAliasesSeparateWhileSharingExactBlob()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var exact = Capture("same-journal", "same-battle", "same-bytes");
        var first = new BattleCaptureRepository(database, Distribution);
        var second = new BattleCaptureRepository(database, "netniv.stfc-community-mod");
        await first.CommitAsync(Envelope("batch-1", "session-A", exact), CancellationToken.None);
        await second.CommitAsync(Envelope("batch-1", "session-A", exact), CancellationToken.None);

        Assert.IsNotNull(first.FindByAlias("journal-id", "same-journal"));
        Assert.IsNotNull(second.FindByAlias("journal-id", "same-journal"));
        AssertCounts(database, batches: 2, evidence: 2, blobs: 1, battles: 2);
    }

    [TestMethod]
    public async Task ConcurrentSameBatchAcrossRepositorySessionsIsOneDurableCommit()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var envelope = Envelope("concurrent-batch", "session-A", Capture("journal-1", "battle-1", "same"));
        var first = new BattleCaptureRepository(database, Distribution);
        var second = new BattleCaptureRepository(database, Distribution);

        var results = await Task.WhenAll(
            Task.Run(async () => await first.CommitAsync(envelope, CancellationToken.None)),
            Task.Run(async () => await second.CommitAsync(envelope, CancellationToken.None)));

        Assert.IsTrue(results.All(result => result.AcceptedRecords == 1));
        AssertCounts(database, batches: 1, evidence: 1, blobs: 1, battles: 1);
    }

    [TestMethod]
    public async Task CommitAndDisposeSerializeWithoutTurningSuccessIntoFailure()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        using var reachedCommit = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        var repository = new BattleCaptureRepository(
            database,
            Distribution,
            null,
            stage =>
            {
                if (stage == BattleStorageCommitStage.AfterWritesBeforeCommit)
                {
                    reachedCommit.Set();
                    releaseCommit.Wait(TimeSpan.FromSeconds(10));
                }
            });
        var commit = Task.Run(async () => await repository.CommitAsync(
            Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "payload")),
            CancellationToken.None));
        Assert.IsTrue(reachedCommit.Wait(TimeSpan.FromSeconds(10)));

        var dispose = Task.Run(repository.Dispose);
        await Task.Delay(100);
        Assert.IsFalse(dispose.IsCompleted, "Dispose must drain the active transaction before owning the session.");
        releaseCommit.Set();

        var result = await commit;
        await dispose;
        Assert.AreEqual(1, result.AcceptedRecords);
        AssertCounts(database, batches: 1, evidence: 1, blobs: 1, battles: 1);
    }

    [TestMethod]
    public async Task ReadWaitsForTheSerializedWriteBoundary()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        using var reachedCommit = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        using var repository = new BattleCaptureRepository(
            database,
            Distribution,
            null,
            stage =>
            {
                if (stage == BattleStorageCommitStage.AfterWritesBeforeCommit)
                {
                    reachedCommit.Set();
                    releaseCommit.Wait(TimeSpan.FromSeconds(10));
                }
            });
        var commit = Task.Run(async () => await repository.CommitAsync(
            Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "payload")),
            CancellationToken.None));
        Assert.IsTrue(reachedCommit.Wait(TimeSpan.FromSeconds(10)));

        var read = Task.Run(() => repository.ReadRecent(10));
        await Task.Delay(100);
        Assert.IsFalse(read.IsCompleted, "Reads must not bypass the active repository write transaction.");
        releaseCommit.Set();

        await commit;
        Assert.AreEqual(1, (await read).Count);
    }

    [TestMethod]
    public async Task DetailLeaseRejectsSameThreadReentryAndBlocksConcurrentOperationsUntilReleased()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        using var repository = new BattleCaptureRepository(database, Distribution);
        await repository.CommitAsync(
            Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "payload")),
            CancellationToken.None);
        var battle = repository.ReadRecent(1)[0];
        var detail = repository.OpenCanonicalDetail(battle.BattleRecordId);

        Assert.ThrowsException<InvalidOperationException>(() => repository.ReadRecent(1));
        var blockedRead = Task.Run(() => repository.ReadRecent(1));
        await Task.Delay(100);
        Assert.IsFalse(blockedRead.IsCompleted, "A retained detail BLOB must own the total session gate.");

        detail.Dispose();
        Assert.AreEqual(1, (await blockedRead).Count);
    }

    [TestMethod]
    public async Task NonCanonicalSourceTimestampsRejectBeforeAnyTransaction()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var repository = new BattleCaptureRepository(database, Distribution);
        foreach (var timestamp in new[]
        {
            " 2026-05-18T12:05:00Z",
            "2026-05-18 12:05:00Z",
            "2026-05-18T12:05:00+01",
        })
        {
            var exact = Capture("journal-1", "battle-1", "bad")
                .Replace("2026-05-18T12:05:00.000Z", timestamp, StringComparison.Ordinal);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
                await repository.CommitAsync(Envelope("batch-" + timestamp.Length, "session-A", exact), CancellationToken.None));
        }
        AssertCounts(database, batches: 0, evidence: 0, blobs: 0, battles: 0);
    }

    [TestMethod]
    public async Task DetailVerificationNeverExposesCorruptBytesOrMetadata()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var repository = new BattleCaptureRepository(database, Distribution);
        await repository.CommitAsync(
            Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "payload")),
            CancellationToken.None);
        var battle = repository.ReadRecent(1)[0];

        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE event_blob SET compressed_bytes = zeroblob(compressed_byte_count) WHERE event_blob_id = 1;";
            _ = command.ExecuteNonQuery();
        }
        using (var detail = repository.OpenCanonicalDetail(battle.BattleRecordId))
        using (var destination = new MemoryStream())
        {
            destination.Write("unchanged"u8);
            Assert.ThrowsException<InvalidDataException>(() => detail.CopyExactEventTo(destination));
            CollectionAssert.AreEqual("unchanged"u8.ToArray(), destination.ToArray());
        }

        var metadataDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "metadata"));
        var metadataDatabase = CreateStore(metadataDirectory.FullName);
        var metadataRepository = new BattleCaptureRepository(metadataDatabase, Distribution);
        await metadataRepository.CommitAsync(
            Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "payload")),
            CancellationToken.None);
        var metadataBattle = metadataRepository.ReadRecent(1)[0];
        using (var connection = BattleStorageSchema.Open(metadataDatabase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE event_evidence SET original_raw_sha256 = $hash WHERE evidence_id = 1;";
            command.Parameters.AddWithValue("$hash", new string('0', 64));
            _ = command.ExecuteNonQuery();
        }
        using (var destination = new MemoryStream())
        {
            destination.Write("still-unchanged"u8);
            Assert.ThrowsException<InvalidDataException>(() =>
                metadataRepository.OpenCanonicalDetail(metadataBattle.BattleRecordId));
            CollectionAssert.AreEqual("still-unchanged"u8.ToArray(), destination.ToArray());
        }
    }

    [TestMethod]
    public async Task DetailRejectsHugeMetadataAndBrotliExpansionBeforeExposingBytes()
    {
        using var temporary = new TemporaryDirectory();
        var hugeDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "huge"));
        var hugeDatabase = CreateStore(hugeDirectory.FullName);
        using (var repository = new BattleCaptureRepository(hugeDatabase, Distribution))
        {
            await repository.CommitAsync(
                Envelope("batch-1", "session-A", Capture("journal-1", "battle-1", "payload")),
                CancellationToken.None);
        }
        using (var connection = BattleStorageSchema.Open(hugeDatabase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE event_evidence SET original_raw_byte_count = $huge, " +
                "original_compressed_byte_count = $huge WHERE evidence_id = 1; " +
                "UPDATE event_blob SET raw_byte_count = $huge, compressed_byte_count = $huge " +
                "WHERE event_blob_id = 1;";
            command.Parameters.AddWithValue("$huge", (long)BattleCaptureRepository.MaximumStoredEventBytes + 1);
            _ = command.ExecuteNonQuery();
        }
        using (var repository = new BattleCaptureRepository(hugeDatabase, Distribution))
        using (var destination = new MemoryStream())
        {
            destination.Write("huge-unchanged"u8);
            var battle = repository.ReadRecent(1)[0];
            Assert.ThrowsException<InvalidDataException>(() => repository.OpenCanonicalDetail(battle.BattleRecordId));
            CollectionAssert.AreEqual("huge-unchanged"u8.ToArray(), destination.ToArray());
        }

        var expansionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "expansion"));
        var expansionDatabase = CreateStore(expansionDirectory.FullName);
        using (var repository = new BattleCaptureRepository(expansionDatabase, Distribution))
        {
            await repository.CommitAsync(
                Envelope(
                    "batch-1",
                    "session-A",
                    Capture("journal-1", "battle-1", new string('z', 128 * 1024))),
                CancellationToken.None);
        }
        using (var connection = BattleStorageSchema.Open(expansionDatabase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE event_evidence SET original_raw_byte_count = 16 WHERE evidence_id = 1; " +
                "UPDATE event_blob SET raw_byte_count = 16 WHERE event_blob_id = 1;";
            _ = command.ExecuteNonQuery();
        }
        using (var repository = new BattleCaptureRepository(expansionDatabase, Distribution))
        {
            var battle = repository.ReadRecent(1)[0];
            using var detail = repository.OpenCanonicalDetail(battle.BattleRecordId);
            using var destination = new MemoryStream();
            destination.Write("expansion-unchanged"u8);

            Assert.ThrowsException<InvalidDataException>(() => detail.CopyExactEventTo(destination));

            CollectionAssert.AreEqual("expansion-unchanged"u8.ToArray(), destination.ToArray());
        }
    }

    [TestMethod]
    public async Task FleetRequiresItsSeparateSink()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var battle = new BattleCaptureRepository(database, Distribution);
        var fleet = new RecordingSink();
        var router = new BattleIngestSinkRouter(battle, fleet);
        var fleetEnvelope = new BattleIngestEnvelope(
            BattleIngestProtocol.Version,
            BattleIngestProtocol.FleetRuntimeKind,
            "fleet-1",
            DateTimeOffset.Parse("2026-05-18T12:05:00Z", CultureInfo.InvariantCulture),
            "session-A",
            "mod",
            "1.0",
            BattleIngestProtocol.FleetRuntimeVersion,
            "{}"u8.ToArray(),
            ["{}"u8.ToArray()]);

        await Assert.ThrowsExceptionAsync<BattleStorageException>(async () =>
            await battle.CommitAsync(fleetEnvelope, CancellationToken.None));
        await router.CommitAsync(fleetEnvelope, CancellationToken.None);
        Assert.AreEqual(1, fleet.Calls);
        AssertCounts(database, batches: 0, evidence: 0, blobs: 0, battles: 0);
    }

    [TestMethod]
    public void UnknownCodecRequiresValidStoredBlobHashAndCorruptionFailsClosed()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            var bytes = Encoding.UTF8.GetBytes("opaque future codec bytes");
            command.CommandText =
                """
                INSERT INTO event_blob (
                  codec, codec_minimum_reader_version, compressed_sha256, raw_sha256,
                  compressed_byte_count, raw_byte_count, compressed_bytes)
                VALUES ('future-codec-v2', 2, $hash, $rawHash, $count, 10, $bytes);
                """;
            command.Parameters.AddWithValue("$hash", LowerHex(SHA256.HashData(bytes)));
            command.Parameters.AddWithValue("$rawHash", new string('0', 64));
            command.Parameters.AddWithValue("$count", bytes.Length);
            command.Parameters.AddWithValue("$bytes", bytes);
            _ = command.ExecuteNonQuery();
        }
        Assert.AreEqual(BattleStorageReadability.UnknownCodec, BattleStorageSchema.Inspect(database).State);

        using (var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            var bytes = Encoding.UTF8.GetBytes("second future codec bytes");
            command.CommandText =
                """
                INSERT INTO event_blob (
                  codec, codec_minimum_reader_version, compressed_sha256, raw_sha256,
                  compressed_byte_count, raw_byte_count, compressed_bytes)
                VALUES ('future-codec-v2', 2, $hash, $rawHash, $count, 11, $bytes);
                UPDATE event_blob
                SET compressed_bytes = zeroblob(compressed_byte_count)
                WHERE event_blob_id = last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$hash", LowerHex(SHA256.HashData(bytes)));
            command.Parameters.AddWithValue("$rawHash", new string('1', 64));
            command.Parameters.AddWithValue("$count", bytes.Length);
            command.Parameters.AddWithValue("$bytes", bytes);
            _ = command.ExecuteNonQuery();
        }
        Assert.AreEqual(BattleStorageReadability.Corrupt, BattleStorageSchema.Inspect(database).State);
    }

    [TestMethod]
    public async Task ReviewedQueriesUseFrozenIndexesAtRealisticCardinality()
    {
        using var temporary = new TemporaryDirectory();
        var database = CreateStore(temporary.Path);
        var repository = new BattleCaptureRepository(database, Distribution);
        for (var index = 0; index < 306; ++index)
        {
            await repository.CommitAsync(
                Envelope($"batch-{index}", "session-A", Capture($"journal-{index}", $"battle-{index}", $"payload-{index}")),
                CancellationToken.None);
        }

        AssertPlanUses(
            database,
            BattleStorageQueries.Recent,
            ["ix_battle_recent", "ux_battle_alias_kind"],
            ("$cursor", 1), ("$time", long.MaxValue), ("$id", long.MaxValue), ("$limit", 20));
        AssertPlanUses(
            database,
            BattleStorageQueries.Alias,
            ["ux_battle_alias", "ux_battle_alias_kind"],
            ("$namespace", "runtime"), ("$alias", "alias"));
        AssertPlanUses(
            database,
            BattleStorageQueries.Receipts,
            ["ix_event_battle_detail", "ix_ingest_receipt_event"],
            ("$battle", 1));
        AssertPlanUses(
            database,
            BattleStorageQueries.CanonicalDetail,
            ["ix_event_battle_detail"],
            ("$battle", 1));
        AssertPlanUses(database,
            "SELECT * FROM event_evidence WHERE source_namespace = 'x' AND logical_event_key = 'y' ORDER BY event_timestamp_unix_ms, evidence_id;",
            ["ix_event_logical"]);
        AssertPlanUses(database,
            "SELECT * FROM event_ingest_receipt WHERE ingest_batch_id = 1 ORDER BY batch_event_ordinal;",
            ["ux_ingest_receipt_ordinal"]);
        AssertPlanUses(database,
            "SELECT * FROM event_evidence WHERE evidence_state = 'accepted' ORDER BY event_timestamp_unix_ms, accepted_at_unix_ms, evidence_id;",
            ["ix_event_cleanup"]);
    }

    [TestMethod]
    public void ResolvedPackageOutputContainsNoNativeSqliteLibrary()
    {
        var files = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(name => name is not null &&
                (name.StartsWith("sqlite3", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("winsqlite3.dll", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        CollectionAssert.AreEqual(Array.Empty<string>(), files);
    }

    [TestMethod]
    public void FreshProcessStaysDormantUntilStorageIsExplicitlyUsed()
    {
        using var temporary = new TemporaryDirectory();
        var result = RunProbe("inactive", Path.Combine(temporary.Path, "must-not-exist.sqlite"));

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.IsFalse(document.RootElement.GetProperty("providerInitialized").GetBoolean());
        Assert.IsFalse(document.RootElement.GetProperty("sqliteLoaded").GetBoolean());
        Assert.IsFalse(document.RootElement.GetProperty("databaseExists").GetBoolean());
    }

    [TestMethod]
    public void HardCrashRespectsTransactionCommitBoundary()
    {
        using var temporary = new TemporaryDirectory();
        var beforeDatabase = CreateStore(temporary.Path);
        var before = RunProbe("crash-before-commit", beforeDatabase);
        Assert.AreNotEqual(0, before.ExitCode, "The crash probe unexpectedly returned normally.");
        Assert.AreEqual(BattleStorageReadability.Readable, BattleStorageSchema.Inspect(beforeDatabase).State);
        AssertCounts(beforeDatabase, batches: 0, evidence: 0, blobs: 0, battles: 0);

        var afterDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "after"));
        var afterDatabase = CreateStore(afterDirectory.FullName);
        var after = RunProbe("crash-after-commit", afterDatabase);
        Assert.AreNotEqual(0, after.ExitCode, "The crash probe unexpectedly returned normally.");
        Assert.AreEqual(BattleStorageReadability.Readable, BattleStorageSchema.Inspect(afterDatabase).State);
        AssertCounts(afterDatabase, batches: 1, evidence: 1, blobs: 1, battles: 1);
    }

    [TestMethod]
    public void BoundedProbeRoundTripsAndRecordsMeasurementEvidence()
    {
        using var temporary = new TemporaryDirectory();
        var result = RunProbe("measure", Path.Combine(temporary.Path, "probe.sqlite"));

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        using var actual = JsonDocument.Parse(result.StandardOutput);
        var root = actual.RootElement;
        Assert.AreEqual("stfc.battle-storage-probe-result.v1", root.GetProperty("schema").GetString());
        Assert.AreEqual(64, root.GetProperty("events").GetInt32());
        Assert.AreEqual(root.GetProperty("sourceSha256").GetString(), root.GetProperty("roundTripSha256").GetString());
        Assert.IsTrue(root.GetProperty("compressedBytes").GetInt64() < root.GetProperty("rawBytes").GetInt64());
        Assert.IsTrue(root.GetProperty("finalDatabaseBytes").GetInt64() < 8 * 1024 * 1024);
        Assert.IsTrue(root.GetProperty("journalPeakBytes").GetInt64() > 0);
        Assert.IsTrue(root.GetProperty("allocatedBytes").GetInt64() < 32 * 1024 * 1024);

        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "BattleBridge",
            "battle-storage-probe-result.v1.json");
        using var recorded = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        Assert.AreEqual(
            recorded.RootElement.GetProperty("sourceSha256").GetString(),
            root.GetProperty("sourceSha256").GetString());
        Assert.AreEqual(
            recorded.RootElement.GetProperty("rawBytes").GetInt64(),
            root.GetProperty("rawBytes").GetInt64());
    }

    private static string CreateStore(string directory)
    {
        var database = Path.Combine(directory, "battle-candidate.sqlite");
        var result = BattleStorageSchema.CreateCandidate(database);
        Assert.AreEqual(BattleStorageReadability.Readable, result.State, result.Message);
        return database;
    }

    private static BattleIngestEnvelope Envelope(string batchId, string sessionId, string exactEvent)
    {
        var eventBytes = Encoding.UTF8.GetBytes(exactEvent);
        var envelopeBytes = Encoding.UTF8.GetBytes($"envelope:{batchId}:{sessionId}:{LowerHex(SHA256.HashData(eventBytes))}");
        return new(
            BattleIngestProtocol.Version,
            BattleIngestProtocol.BattleEventsKind,
            batchId,
            DateTimeOffset.Parse("2026-05-18T12:05:00Z", CultureInfo.InvariantCulture),
            sessionId,
            "stfc-community-mod",
            "fixture-only",
            BattleIngestProtocol.SidecarEventsVersion,
            envelopeBytes,
            [eventBytes]);
    }

    private static string Capture(string journalId, string battleId, string marker) =>
        "{\"protocolVersion\":\"stfc.sidecar.events.v0\",\"type\":\"battle.capture\"," +
        "\"schemaVersion\":\"stfc.battle.capture.v1\",\"timestamp\":\"2026-05-18T12:05:00.000Z\"," +
        $"\"journalId\":\"{journalId}\",\"battleId\":\"{battleId}\",\"battleType\":1," +
        $"\"capture\":{{\"marker\":\"{marker}\"}}}}";

    private static async Task AssertDurableRetryFailsWithoutMutation(
        string database,
        BattleIngestEnvelope envelope)
    {
        var before = SHA256.HashData(File.ReadAllBytes(database));
        using var restarted = new BattleCaptureRepository(database, Distribution);
        await Assert.ThrowsExceptionAsync<BattleStorageException>(async () =>
            await restarted.CommitAsync(envelope, CancellationToken.None));
        CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(database)));
    }

    private static void AssertCounts(string database, int batches, int evidence, int blobs, int battles)
    {
        using var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadOnly);
        Assert.AreEqual(batches, Scalar(connection, "SELECT COUNT(*) FROM ingest_batch;"));
        Assert.AreEqual(evidence, Scalar(connection, "SELECT COUNT(*) FROM event_evidence;"));
        Assert.AreEqual(blobs, Scalar(connection, "SELECT COUNT(*) FROM event_blob;"));
        Assert.AreEqual(battles, Scalar(connection, "SELECT COUNT(*) FROM battle_record;"));
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void AssertPlanUses(
        string database,
        string query,
        IReadOnlyList<string> indexes,
        params (string Name, object Value)[] parameters)
    {
        using var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + query;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
        {
            details.Add(reader.GetString(3));
        }
        foreach (var index in indexes)
        {
            Assert.IsTrue(
                details.Any(detail =>
                    detail.Contains("USING", StringComparison.Ordinal)
                    && detail.Contains(index, StringComparison.Ordinal)),
                $"Expected production plan to use semantic index {index}. Actual: {string.Join(" | ", details)}");
        }
    }

    private static string LowerHex(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();

    private static ProbeResult RunProbe(string mode, string database)
    {
        var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repositoryRoot is not null
            && !File.Exists(Path.Combine(repositoryRoot.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            repositoryRoot = repositoryRoot.Parent;
        }
        Assert.IsNotNull(repositoryRoot, "Repository root was not found from the test output path.");
        var probe = Path.Combine(
            repositoryRoot.FullName,
            "tests",
            "STFCCommunityMod.Launcher.StorageProbe",
            "bin",
            "Release",
            "net8.0",
            "STFCCommunityMod.Launcher.StorageProbe.dll");
        Assert.IsTrue(File.Exists(probe), $"Storage probe is missing: {probe}");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(probe);
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(database);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Battle storage probe could not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(30_000), "The Battle storage probe did not terminate.");
        return new(process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private sealed class RecordingSink : IBattleIngestSink
    {
        public int Calls { get; private set; }
        public ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken)
        {
            ++Calls;
            return ValueTask.FromResult(new BattleIngestCommitResult(1));
        }
    }

    private sealed record ProbeResult(int ExitCode, string StandardOutput, string StandardError);
}
