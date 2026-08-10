using System.Security.Cryptography;
using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleRuntimeLockTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 8, 9, 19, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void RuntimeRecordIsClosedCanonicalAndBounded()
    {
        var record = RunningRecord();
        var bytes = BattleRuntimeLockCodec.Encode(record);

        var decoded = BattleRuntimeLockCodec.Decode(bytes);

        Assert.AreEqual(record, decoded);
        Assert.IsTrue(bytes.Length <= BattleRuntimeLockCodec.MaximumBytes);
        var json = Encoding.UTF8.GetString(bytes);
        var duplicate = json.Replace(
            "\"ownerId\":",
            $"\"ownerId\":\"{record.OwnerId}\",\"\\u006fwnerId\":",
            StringComparison.Ordinal);
        var unknown = json[..^1] + ",\"extra\":true}";
        var caseDrift = json.Replace("\"state\":\"running\"", "\"state\":\"Running\"", StringComparison.Ordinal);

        Assert.ThrowsException<InvalidDataException>(() =>
            BattleRuntimeLockCodec.Decode(Encoding.UTF8.GetBytes(duplicate)));
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleRuntimeLockCodec.Decode(Encoding.UTF8.GetBytes(unknown)));
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleRuntimeLockCodec.Decode(Encoding.UTF8.GetBytes(caseDrift)));
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleRuntimeLockCodec.Decode(Encoding.UTF8.GetBytes(" " + json)));
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleRuntimeLockCodec.Decode(Encoding.UTF8.GetBytes("not-json")));
    }

    [TestMethod]
    public void RuntimeOwnerStoreConstructionIsPassive()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");

        _ = new BattleRuntimeLockStore(stateRoot);

        Assert.IsFalse(Directory.Exists(stateRoot));
    }

    [TestMethod]
    public async Task RuntimeOwnershipCannotPrecedeAnExactPreparedMarker()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            runtime.CreateBoundRunningAsync(operationLease, journal, RunningRecord()));

        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName)));
        Assert.IsFalse(Directory.Exists(Path.Combine(stateRoot, "battle")));
    }

    [TestMethod]
    public async Task PreparedMarkerBindsTheExactRunningBytesBeforeCreate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var protector = new RecordingMarkerProtector();
        var journal = new BattleLifecycleJournalStore(stateRoot, protector);
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var record = RunningRecord();
        var marker = PreparedMarker(record);
        await journal.CreatePreparedAsync(operationLease, marker);
        var runtimePath = Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName);

        Assert.IsTrue(File.Exists(journal.MarkerPath));
        Assert.IsFalse(File.Exists(runtimePath));
        await using var runtimeLease = await runtime.CreateBoundRunningAsync(
            operationLease,
            journal,
            record);

        Assert.AreEqual(BattleRuntimeLockState.Running, runtimeLease.Record.State);
        Assert.ThrowsException<IOException>(() => File.OpenRead(runtimePath).Dispose());
    }

    [TestMethod]
    public async Task MismatchedMarkerIdentityCreatesNoRuntimeLock()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var record = RunningRecord();
        var marker = PreparedMarker(record);
        var runtimeTransition = marker.Resources[0] with
        {
            After = new(marker.Resources[0].After!.ByteCount, new string('9', 64)),
        };
        await journal.CreatePreparedAsync(
            operationLease,
            marker with { Resources = [runtimeTransition] });

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            runtime.CreateBoundRunningAsync(operationLease, journal, record));

        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName)));
    }

    [TestMethod]
    public async Task RuntimeBootstrapRejectsARecordForAnotherProcess()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var record = RunningRecord() with { ProcessId = Environment.ProcessId + 1 };
        await journal.CreatePreparedAsync(operationLease, PreparedMarker(record));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            runtime.CreateBoundRunningAsync(operationLease, journal, record));

        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName)));
    }

    [TestMethod]
    public async Task AbsentStateBootstrapRejectsADeclaredPriorRuntimeIdentity()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var record = RunningRecord();
        var marker = PreparedMarker(record);
        var transition = marker.Resources[0] with
        {
            Before = new(128, new string('8', 64)),
        };
        await journal.CreatePreparedAsync(operationLease, marker with { Resources = [transition] });

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            runtime.CreateBoundRunningAsync(operationLease, journal, record));

        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName)));
    }

    [TestMethod]
    public async Task CleanCloseRewritesThroughTheSameExclusiveHandleThenReleasesOwnership()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var record = RunningRecord();
        await journal.CreatePreparedAsync(operationLease, PreparedMarker(record));
        var runtimeLease = await runtime.CreateBoundRunningAsync(operationLease, journal, record);
        var cleanAt = Started.AddMinutes(1);

        await runtimeLease.MarkCleanAsync(cleanAt);

        Assert.AreEqual(BattleRuntimeLockState.Clean, runtimeLease.Record.State);
        Assert.AreEqual(cleanAt, runtimeLease.Record.LastCleanCloseAtUtc);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            runtimeLease.MarkCleanAsync(cleanAt.AddSeconds(1)));
        await runtimeLease.DisposeAsync();
        var bytes = File.ReadAllBytes(Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName));
        var persisted = BattleRuntimeLockCodec.Decode(bytes);
        Assert.AreEqual(BattleRuntimeLockState.Clean, persisted.State);
        Assert.AreEqual(cleanAt, persisted.LastCleanCloseAtUtc);
    }

    [TestMethod]
    public async Task MissingRuntimeOwnerInAnExistingDirectoryRemainsAbsent()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var battleRoot = Path.Combine(stateRoot, "battle");
        Directory.CreateDirectory(battleRoot);
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);

        var result = await runtime.TryAcquireExistingAsync(
            operationLease,
            journal,
            RunningRecord());

        Assert.AreEqual(BattleRuntimeLockAcquisitionState.Absent, result.State);
        Assert.AreEqual("battle-runtime-owner-absent", result.Code);
        Assert.IsNull(result.PreviousRecord);
        Assert.IsNull(result.Lease);
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(battleRoot).Count());
    }

    [TestMethod]
    public async Task ExistingStateAcquisitionPreservesThePriorCleanReceipt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var runtimePath = CreateBattleRoot(stateRoot);
        var clean = RunningRecord() with
        {
            State = BattleRuntimeLockState.Clean,
            LastCleanCloseAtUtc = Started.AddMinutes(1),
        };
        File.WriteAllBytes(runtimePath, BattleRuntimeLockCodec.Encode(clean));
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var next = RunningRecord() with
        {
            OwnerId = new string('2', 32),
            ProcessStartNonce = new string('8', 32),
            StartedAtUtc = Started.AddMinutes(2),
        };

        var result = await runtime.TryAcquireExistingAsync(operationLease, journal, next);

        Assert.AreEqual(BattleRuntimeLockAcquisitionState.Acquired, result.State);
        Assert.AreEqual("battle-runtime-owner-acquired-after-clean", result.Code);
        Assert.AreEqual(clean, result.PreviousRecord);
        Assert.AreEqual(next, result.Lease!.Record);
        await result.Lease.MarkCleanAsync(Started.AddMinutes(3));
        await result.Lease.DisposeAsync();
        Assert.AreEqual(
            BattleRuntimeLockState.Clean,
            BattleRuntimeLockCodec.Decode(File.ReadAllBytes(runtimePath)).State);
    }

    [TestMethod]
    public async Task ExistingUncleanReceiptIsReturnedForStorageRecoveryBeforeComposition()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var runtimePath = CreateBattleRoot(stateRoot);
        var unclean = RunningRecord() with
        {
            OwnerId = new string('3', 32),
            ProcessStartNonce = new string('9', 32),
        };
        File.WriteAllBytes(runtimePath, BattleRuntimeLockCodec.Encode(unclean));
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);

        var result = await runtime.TryAcquireExistingAsync(
            operationLease,
            journal,
            RunningRecord() with { OwnerId = new string('4', 32) });

        Assert.AreEqual(BattleRuntimeLockAcquisitionState.Acquired, result.State);
        Assert.AreEqual("battle-runtime-owner-acquired-after-unclean", result.Code);
        Assert.AreEqual(unclean, result.PreviousRecord);
        await result.Lease!.DisposeAsync();
    }

    [TestMethod]
    public async Task CancellationBeforeOwnershipMutationPreservesThePriorReceipt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var runtimePath = CreateBattleRoot(stateRoot);
        var prior = BattleRuntimeLockCodec.Encode(RunningRecord());
        File.WriteAllBytes(runtimePath, prior);
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            runtime.TryAcquireExistingAsync(
                operationLease,
                journal,
                RunningRecord() with { OwnerId = new string('5', 32) },
                cancellation.Token));

        CollectionAssert.AreEqual(prior, File.ReadAllBytes(runtimePath));
    }

    [TestMethod]
    public async Task MissingBattleStateRemainsAbsentWithoutBootstrapWrites()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);

        var result = await runtime.TryAcquireExistingAsync(operationLease, journal, RunningRecord());

        Assert.AreEqual(BattleRuntimeLockAcquisitionState.Absent, result.State);
        Assert.AreEqual("battle-runtime-owner-absent", result.Code);
        Assert.IsFalse(Directory.Exists(Path.Combine(stateRoot, "battle")));
    }

    [TestMethod]
    public async Task RecoveryAndDeleteOwnersBlockRuntimeAcquisitionWithoutMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var runtimePath = CreateBattleRoot(stateRoot);
        var prior = BattleRuntimeLockCodec.Encode(RunningRecord());
        File.WriteAllBytes(runtimePath, prior);
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        File.WriteAllBytes(Path.Combine(stateRoot, "battle-delete-v1.dpapi"), [1]);

        var result = await runtime.TryAcquireExistingAsync(
            operationLease,
            journal,
            RunningRecord() with { OwnerId = new string('5', 32) });

        Assert.AreEqual(BattleRuntimeLockAcquisitionState.RecoveryRequired, result.State);
        Assert.AreEqual("battle-runtime-owner-delete-recovery-required", result.Code);
        CollectionAssert.AreEqual(prior, File.ReadAllBytes(runtimePath));
    }

    [TestMethod]
    public async Task ActiveLifecycleMarkerBlocksRuntimeAcquisitionWithoutTouchingTheLock()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var runtimePath = CreateBattleRoot(stateRoot);
        var priorRecord = RunningRecord();
        var prior = BattleRuntimeLockCodec.Encode(priorRecord);
        File.WriteAllBytes(runtimePath, prior);
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        await journal.CreatePreparedAsync(operationLease, PreparedMarker(priorRecord));

        var result = await runtime.TryAcquireExistingAsync(
            operationLease,
            journal,
            RunningRecord() with { OwnerId = new string('5', 32) });

        Assert.AreEqual(BattleRuntimeLockAcquisitionState.RecoveryRequired, result.State);
        Assert.AreEqual("battle-runtime-owner-recovery-required", result.Code);
        CollectionAssert.AreEqual(prior, File.ReadAllBytes(runtimePath));
    }

    [TestMethod]
    public async Task MalformedOrLiveRuntimeOwnerFailsClosedAndPreservesBytes()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var runtimePath = CreateBattleRoot(stateRoot);
        var malformed = Encoding.UTF8.GetBytes("not-a-runtime-record");
        File.WriteAllBytes(runtimePath, malformed);
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);

        var invalid = await runtime.TryAcquireExistingAsync(operationLease, journal, RunningRecord());

        Assert.AreEqual(BattleRuntimeLockAcquisitionState.Invalid, invalid.State);
        CollectionAssert.AreEqual(malformed, File.ReadAllBytes(runtimePath));

        File.WriteAllBytes(runtimePath, BattleRuntimeLockCodec.Encode(RunningRecord()));
        var heldResult = await runtime.TryAcquireExistingAsync(
            operationLease,
            journal,
            RunningRecord() with { OwnerId = new string('6', 32) });
        Assert.AreEqual(BattleRuntimeLockAcquisitionState.Acquired, heldResult.State);
        await using var held = heldResult.Lease!;
        var busy = await runtime.TryAcquireExistingAsync(
            operationLease,
            journal,
            RunningRecord() with { OwnerId = new string('7', 32) });

        Assert.AreEqual(BattleRuntimeLockAcquisitionState.Busy, busy.State);
        Assert.AreEqual("battle-runtime-owner-busy", busy.Code);
    }

    [TestMethod]
    public async Task RuntimeOwnerRefusesAFileReparsePointWithoutTouchingItsTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The authoritative no-follow runtime-lock contract is Windows-only.");
        }
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var runtimePath = CreateBattleRoot(stateRoot);
        var foreign = Path.Combine(temporaryDirectory.Path, "foreign-runtime.lock");
        var foreignBytes = BattleRuntimeLockCodec.Encode(RunningRecord());
        File.WriteAllBytes(foreign, foreignBytes);
        try
        {
            File.CreateSymbolicLink(runtimePath, foreign);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive($"File symlink creation is unavailable: {exception.Message}");
        }
        var journal = new BattleLifecycleJournalStore(stateRoot, new RecordingMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);

        var result = await runtime.TryAcquireExistingAsync(
            operationLease,
            journal,
            RunningRecord() with { OwnerId = new string('8', 32) });

        Assert.AreEqual(BattleRuntimeLockAcquisitionState.Invalid, result.State);
        CollectionAssert.AreEqual(foreignBytes, File.ReadAllBytes(foreign));
        Assert.IsTrue(File.GetAttributes(runtimePath).HasFlag(FileAttributes.ReparsePoint));
    }

    private static BattleRuntimeLockRecord RunningRecord() => new(
        new string('1', 32),
        BattleRuntimeLockState.Running,
        Environment.ProcessId,
        new string('7', 32),
        Started,
        null);

    private static string CreateBattleRoot(string stateRoot)
    {
        var battleRoot = Path.Combine(stateRoot, "battle");
        Directory.CreateDirectory(battleRoot);
        return Path.Combine(battleRoot, BattleRuntimeLockCodec.FileName);
    }

    private static BattleLifecycleMarker PreparedMarker(BattleRuntimeLockRecord runtime)
    {
        var bytes = BattleRuntimeLockCodec.Encode(runtime);
        var identity = new BattleLifecycleFileIdentity(
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return new(
            new string('a', 32),
            BattleLifecycleOperationKind.FeatureActivation,
            runtime.OwnerId,
            BattleLifecycleStage.Prepared,
            [LauncherFeatureIds.BattleCollection],
            [new("runtime-lock", "battle/runtime.lock", null, null, identity)],
            null,
            null,
            [
                new(
                    LauncherFeatureIds.BattleCollection,
                    LauncherPlayerFeaturePreference.Unset,
                    LauncherPlayerFeaturePreference.Enabled),
                new(
                    LauncherFeatureIds.FleetCollection,
                    LauncherPlayerFeaturePreference.Unset,
                    LauncherPlayerFeaturePreference.Unset),
            ],
            false,
            true,
            Started,
            Started,
            "battle-runtime-lock-foundation-v1",
            true,
            true);
    }

    private sealed class RecordingMarkerProtector : IBattleLifecycleMarkerProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
    }
}
