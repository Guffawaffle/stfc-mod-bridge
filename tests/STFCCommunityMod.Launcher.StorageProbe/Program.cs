using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using STFCCommunityMod.Launcher.Core;

if (args.Length != 2)
{
    return 64;
}

var mode = args[0];
var database = Path.GetFullPath(args[1]);
switch (mode)
{
    case "schema-manifest":
        {
            var provider = BattleSqliteProvider.Qualify();
            if (!provider.IsReady || File.Exists(database))
            {
                return 7;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            BattleSqliteConnection.ConfigureAndVerify(connection, includeFilePragmas: true);
            using (var transaction = connection.BeginTransaction())
            {
                BattleStorageSchema.Execute(connection, transaction, BattleStorageSchema.Ddl);
                transaction.Commit();
            }
            var manifest = BattleStorageSchema.DescribeSchema(connection);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "stfc.battle-storage-schema-fixture.v1",
                applicationId = BattleStorageSchema.ApplicationId,
                userVersion = BattleStorageSchema.CurrentVersion,
                formatId = BattleStorageSchema.FormatId,
                manifestSha256 = manifest.Sha256,
                objectCount = manifest.Objects.Count,
                objects = manifest.Objects.Select(item => new
                {
                    item.Type,
                    item.Name,
                    item.Table,
                    item.SqlSha256,
                    item.Sql,
                }),
            }));
            return 0;
        }

    case "provider-shadow":
        {
            var shadowRoot = Path.GetDirectoryName(database)!;
            Directory.CreateDirectory(shadowRoot);
            var currentShadow = Path.Combine(shadowRoot, "current");
            var pathShadow = Path.Combine(shadowRoot, "path");
            var localAppDataShadow = Path.Combine(shadowRoot, "local-app-data");
            Directory.CreateDirectory(currentShadow);
            Directory.CreateDirectory(pathShadow);
            Directory.CreateDirectory(localAppDataShadow);
            var appAdjacentShadow = Path.Combine(AppContext.BaseDirectory, "winsqlite3.dll");
            if (File.Exists(appAdjacentShadow))
            {
                Console.Error.WriteLine($"Refusing to overwrite an app-adjacent provider candidate: {appAdjacentShadow}");
                return 5;
            }

            var priorDirectory = Environment.CurrentDirectory;
            var priorPath = Environment.GetEnvironmentVariable("PATH");
            var priorLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            try
            {
                File.WriteAllText(Path.Combine(currentShadow, "winsqlite3.dll"), "hostile-current-shadow");
                File.WriteAllText(Path.Combine(pathShadow, "winsqlite3.dll"), "hostile-path-shadow");
                File.WriteAllText(Path.Combine(localAppDataShadow, "winsqlite3.dll"), "hostile-local-app-data-shadow");
                File.WriteAllText(appAdjacentShadow, "hostile-app-adjacent-shadow");
                Environment.CurrentDirectory = currentShadow;
                Environment.SetEnvironmentVariable("PATH", pathShadow + Path.PathSeparator + priorPath);
                Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppDataShadow);

                var result = BattleSqliteProvider.Qualify();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    state = result.State.ToString(),
                    modulePath = result.ModulePath,
                    sqliteVersion = result.SqliteVersion,
                    expectedModulePath = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll")),
                    providerInitialized = BattleSqliteProvider.IsInitialized,
                }));
                return result.State == BattleStorageProviderState.Ready ? 0 : 6;
            }
            finally
            {
                Environment.CurrentDirectory = priorDirectory;
                Environment.SetEnvironmentVariable("PATH", priorPath);
                Environment.SetEnvironmentVariable("LOCALAPPDATA", priorLocalAppData);
                if (File.Exists(appAdjacentShadow)
                    && File.ReadAllText(appAdjacentShadow) == "hostile-app-adjacent-shadow")
                {
                    File.Delete(appAdjacentShadow);
                }
            }
        }

    case "inactive":
        _ = new BattleCaptureRepository(database, "dev.guffawaffle.stfc-community-mod");
        var sqliteLoaded = Process.GetCurrentProcess().Modules
            .Cast<ProcessModule>()
            .Any(module => string.Equals(
                Path.GetFileName(module.FileName),
                "winsqlite3.dll",
                StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            providerInitialized = BattleSqliteProvider.IsInitialized,
            sqliteLoaded,
            databaseExists = File.Exists(database),
        }));
        return sqliteLoaded || File.Exists(database) ? 1 : 0;

    case "crash-before-commit":
    case "crash-after-commit":
        var crashStage = mode == "crash-before-commit"
            ? BattleStorageCommitStage.AfterWritesBeforeCommit
            : BattleStorageCommitStage.AfterCommit;
        var crashing = new BattleCaptureRepository(
            database,
            "dev.guffawaffle.stfc-community-mod",
            null,
            stage =>
            {
                if (stage == crashStage)
                {
                    Environment.FailFast($"Intentional Battle storage probe crash at {stage}.");
                }
            });
        await crashing.CommitAsync(Envelope("crash-batch", Capture(0)), CancellationToken.None);
        return 2;

    case "measure":
        {
            if (!File.Exists(database))
            {
                var creation = BattleStorageSchema.CreateCandidate(database);
                if (creation.State != BattleStorageReadability.Readable)
                {
                    Console.Error.WriteLine(creation.Message);
                    return 3;
                }
            }
            long journalPeak = 0;
            var repository = new BattleCaptureRepository(
                database,
                "dev.guffawaffle.stfc-community-mod",
                null,
                stage =>
                {
                    if (stage == BattleStorageCommitStage.AfterWritesBeforeCommit
                        && File.Exists(database + "-journal"))
                    {
                        journalPeak = Math.Max(journalPeak, new FileInfo(database + "-journal").Length);
                    }
                });
            var events = Enumerable.Range(0, 64).Select(Capture).ToArray();
            var rawBytes = events.Sum(value => Encoding.UTF8.GetByteCount(value));
            var sourceSha = LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(events))));
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            var result = await repository.CommitAsync(Envelope("measurement-batch", events), CancellationToken.None);
            timer.Stop();
            var allocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            using var connection = BattleStorageSchema.Open(database, SqliteOpenMode.ReadOnly);
            var compressedBytes = Scalar(connection, "SELECT COALESCE(SUM(compressed_byte_count), 0) FROM event_blob;");
            var roundTrip = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var item in repository.ReadRecent(100).OrderBy(item => item.JournalId, StringComparer.Ordinal))
            {
                using var detail = repository.OpenCanonicalDetail(item.BattleRecordId);
                using var hashing = new HashingWriteStream(roundTrip);
                detail.CopyExactEventTo(hashing);
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "stfc.battle-storage-probe-result.v1",
                runtime = Environment.Version.ToString(),
                os = Environment.OSVersion.VersionString,
                events = result.AcceptedRecords,
                sourceSha256 = sourceSha,
                roundTripSha256 = LowerHex(roundTrip.GetHashAndReset()),
                rawBytes,
                compressedBytes,
                finalDatabaseBytes = new FileInfo(database).Length,
                journalPeakBytes = journalPeak,
                elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                allocatedBytes = allocations,
            }));
            return 0;
        }

    default:
        return 64;
}

static BattleIngestEnvelope Envelope(string batchId, params string[] events)
{
    var eventBytes = events.Select(Encoding.UTF8.GetBytes).Select(bytes => (ReadOnlyMemory<byte>)bytes).ToArray();
    var envelopeBytes = Encoding.UTF8.GetBytes(
        "probe-envelope:" + batchId + ":" + LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(events)))));
    return new(
        BattleIngestProtocol.Version,
        BattleIngestProtocol.BattleEventsKind,
        batchId,
        DateTimeOffset.Parse("2026-05-18T12:05:00Z", CultureInfo.InvariantCulture),
        "probe-session",
        "stfc-community-mod",
        "probe",
        BattleIngestProtocol.SidecarEventsVersion,
        envelopeBytes,
        eventBytes);
}

static string Capture(int index)
{
    var padding = new string((char)('a' + index % 26), 8 * 1024);
    return "{\"protocolVersion\":\"stfc.sidecar.events.v0\",\"type\":\"battle.capture\"," +
        "\"schemaVersion\":\"stfc.battle.capture.v1\",\"timestamp\":\"2026-05-18T12:05:00.000Z\"," +
        $"\"journalId\":\"probe-journal-{index:D3}\",\"battleId\":\"probe-battle-{index:D3}\",\"battleType\":1," +
        $"\"capture\":{{\"padding\":\"{padding}\",\"ordinal\":{index}}}}}";
}

static long Scalar(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return (long)command.ExecuteScalar()!;
}

static string LowerHex(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();

sealed class HashingWriteStream(IncrementalHash hash) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
