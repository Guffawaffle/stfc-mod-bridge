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

    private static BattleRuntimeLockRecord RunningRecord() => new(
        new string('1', 32),
        BattleRuntimeLockState.Running,
        Environment.ProcessId,
        new string('7', 32),
        Started,
        null);

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
