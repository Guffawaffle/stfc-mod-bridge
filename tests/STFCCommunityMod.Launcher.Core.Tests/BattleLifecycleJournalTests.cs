using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleLifecycleJournalTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 8, 9, 18, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ConstructorAndAbsentInspectionHaveZeroBattleSideEffects()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "missing-state");
        var protector = new RecordingProtector();

        var store = new BattleLifecycleJournalStore(stateRoot, protector);
        var result = store.Inspect();

        Assert.AreEqual(BattleLifecycleJournalState.Absent, result.State);
        Assert.AreEqual("battle-operation-absent", result.Code);
        Assert.IsNull(result.Marker);
        Assert.IsFalse(Directory.Exists(stateRoot));
        Assert.AreEqual(0, protector.ProtectCalls + protector.UnprotectCalls);
    }

    [TestMethod]
    public void CanonicalMarkerRoundTripsAndRejectsHostileJson()
    {
        var protector = new RecordingProtector();
        var marker = PreparedMarker();
        var protectedBytes = BattleLifecycleMarkerCodec.Protect(marker, protector);

        var decoded = BattleLifecycleMarkerCodec.Unprotect(protectedBytes, protector);

        Assert.IsTrue(BattleLifecycleMarkerCodec.AreEquivalent(marker, decoded));
        Assert.AreEqual(BattleLifecycleMarkerCodec.Schema, ReadString(protectedBytes, "schema"));

        var json = Encoding.UTF8.GetString(protectedBytes);
        var duplicate = json.Replace(
            "\"operationId\":",
            $"\"operationId\":\"{marker.OperationId}\",\"\\u006fperationId\":",
            StringComparison.Ordinal);
        var unknown = json[..^1] + ",\"extra\":false}";
        var noncanonical = " " + json;

        Assert.ThrowsException<InvalidDataException>(() =>
            BattleLifecycleMarkerCodec.Unprotect(Encoding.UTF8.GetBytes(duplicate), protector));
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleLifecycleMarkerCodec.Unprotect(Encoding.UTF8.GetBytes(unknown), protector));
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleLifecycleMarkerCodec.Unprotect(Encoding.UTF8.GetBytes(noncanonical), protector));
    }

    [TestMethod]
    public void ClosedModelRejectsUnsafePathsAndIncoherentFeatureState()
    {
        var protector = new RecordingProtector();
        var marker = PreparedMarker();
        var unsafeResource = marker.Resources[0] with
        {
            PrimaryRelativePath = "battle/../runtime.lock",
        };
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleLifecycleMarkerCodec.Protect(
                marker with { Resources = [unsafeResource] },
                protector));

        var incoherent = marker.FeatureTransitions
            .Select(feature => feature.FeatureId == LauncherFeatureIds.BattleCollection
                ? feature with { After = LauncherPlayerFeaturePreference.Disabled }
                : feature)
            .ToArray();
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleLifecycleMarkerCodec.Protect(
                marker with { FeatureTransitions = incoherent },
                protector));
    }

    [TestMethod]
    public async Task PreparedMarkerIsDirectAndPrecedesEveryBattleResource()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var store = new BattleLifecycleJournalStore(stateRoot, new RecordingProtector());
        await using var lease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(lease);

        await store.CreatePreparedAsync(lease, PreparedMarker());

        var inspection = store.Inspect();
        Assert.AreEqual(BattleLifecycleJournalState.Readable, inspection.State);
        Assert.AreEqual(BattleLifecycleStage.Prepared, inspection.Marker!.Stage);
        Assert.IsTrue(File.Exists(store.MarkerPath));
        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", "runtime.lock")));
        Assert.IsFalse(File.Exists(Path.Combine(
            stateRoot,
            "battle",
            BattleIngestCredentialCodec.FileName)));
        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", "battle-store-v1.sqlite3")));
    }

    [TestMethod]
    public async Task TornFirstMarkerIsPreservedAndBlocksRecoveryWithoutResourceCreation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var protector = new RecordingProtector();
        var store = new BattleLifecycleJournalStore(stateRoot, protector);
        var complete = BattleLifecycleMarkerCodec.Protect(PreparedMarker(), protector);
        Directory.CreateDirectory(Path.GetDirectoryName(store.MarkerPath)!);
        await File.WriteAllBytesAsync(store.MarkerPath, complete[..(complete.Length / 2)]);
        await using var lease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(lease);

        var inspection = store.Inspect();

        Assert.AreEqual(BattleLifecycleJournalState.RecoveryFailed, inspection.State);
        Assert.AreEqual("battle-operation-invalid", inspection.Code);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.RecoverAsync(lease));
        CollectionAssert.AreEqual(
            complete[..(complete.Length / 2)],
            await File.ReadAllBytesAsync(store.MarkerPath));
        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName)));
        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", BattleIngestCredentialCodec.FileName)));
        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", "battle-store-v1.sqlite3")));
    }

    [TestMethod]
    public async Task MarkerWritesRequireTheExactLiveRootOperationLease()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstRoot = Path.Combine(temporaryDirectory.Path, "first");
        var secondRoot = Path.Combine(temporaryDirectory.Path, "second");
        var store = new BattleLifecycleJournalStore(firstRoot, new RecordingProtector());
        var wrongLease = await new LauncherOperationLock(secondRoot).TryAcquireAsync();
        Assert.IsNotNull(wrongLease);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.CreatePreparedAsync(wrongLease, PreparedMarker()));
        await wrongLease.DisposeAsync();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() =>
            store.CreatePreparedAsync(wrongLease, PreparedMarker()));
        Assert.IsFalse(Directory.Exists(Path.Combine(firstRoot, "battle")));
    }

    [TestMethod]
    public async Task ExactMonotonicSuccessorReplacesTheFinalMarker()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var store = new BattleLifecycleJournalStore(stateRoot, new RecordingProtector());
        await using var lease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(lease);
        var prepared = PreparedMarker();
        await store.CreatePreparedAsync(lease, prepared);
        var quiesced = prepared with
        {
            Stage = BattleLifecycleStage.Quiesced,
            UpdatedAtUtc = Started.AddSeconds(1),
        };

        await store.AdvanceAsync(lease, quiesced);

        var inspection = store.Inspect();
        Assert.AreEqual(BattleLifecycleJournalState.Readable, inspection.State);
        Assert.AreEqual(BattleLifecycleStage.Quiesced, inspection.Marker!.Stage);
        Assert.IsFalse(Directory.Exists(Path.Combine(
            stateRoot,
            "battle",
            "recovery",
            prepared.OperationId)));
    }

    [TestMethod]
    public async Task CrashBeforeReplaceLeavesOnlyAValidatedRecoverableSuccessor()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var store = new BattleLifecycleJournalStore(
            stateRoot,
            new RecordingProtector(),
            beforeReplace: _ => throw new SimulatedCrashException());
        await using var lease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(lease);
        var prepared = PreparedMarker();
        await store.CreatePreparedAsync(lease, prepared);
        var quiesced = prepared with
        {
            Stage = BattleLifecycleStage.Quiesced,
            UpdatedAtUtc = Started.AddSeconds(1),
        };

        await Assert.ThrowsExceptionAsync<SimulatedCrashException>(() =>
            store.AdvanceAsync(lease, quiesced));

        var inspection = store.Inspect();
        Assert.AreEqual(BattleLifecycleJournalState.RecoverableSuccessor, inspection.State);
        Assert.AreEqual(BattleLifecycleStage.Prepared, inspection.Marker!.Stage);
        Assert.AreEqual(BattleLifecycleStage.Quiesced, inspection.Successor!.Stage);

        var recovered = await new BattleLifecycleJournalStore(stateRoot, new RecordingProtector())
            .RecoverAsync(lease);

        Assert.AreEqual(BattleLifecycleJournalState.Readable, recovered.State);
        Assert.AreEqual(BattleLifecycleStage.Quiesced, recovered.Marker!.Stage);
        Assert.IsFalse(Directory.Exists(Path.Combine(
            stateRoot,
            "battle",
            "recovery",
            prepared.OperationId)));
    }

    [TestMethod]
    public async Task ConcurrentLeaseDisposalWaitsForTheJournalOperationBoundary()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BattleLifecycleJournalStore(
            stateRoot,
            new RecordingProtector(),
            beforeReplace: async _ =>
            {
                started.TrySetResult();
                await release.Task;
            });
        var lease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(lease);
        var prepared = PreparedMarker();
        await store.CreatePreparedAsync(lease, prepared);
        var advance = store.AdvanceAsync(
            lease,
            prepared with
            {
                Stage = BattleLifecycleStage.Quiesced,
                UpdatedAtUtc = Started.AddSeconds(1),
            });
        await started.Task;

        var disposal = lease.DisposeAsync().AsTask();

        Assert.IsFalse(disposal.IsCompleted);
        Assert.IsNull(await new LauncherOperationLock(stateRoot).TryAcquireAsync());
        release.TrySetResult();
        await advance;
        await disposal;
        await using var replacement = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(replacement);
    }

    [TestMethod]
    public async Task EmptyExactOperationResidueIsRecoverableButUnknownSiblingsFailClosed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var store = new BattleLifecycleJournalStore(stateRoot, new RecordingProtector());
        await using var lease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(lease);
        var marker = PreparedMarker();
        await store.CreatePreparedAsync(lease, marker);
        var residue = Path.Combine(stateRoot, "battle", "recovery", marker.OperationId);
        Directory.CreateDirectory(Path.Combine(residue, "candidate"));

        var recoverable = store.Inspect();

        Assert.AreEqual(BattleLifecycleJournalState.RecoverableResidue, recoverable.State);
        var recovered = await store.RecoverAsync(lease);
        Assert.AreEqual(BattleLifecycleJournalState.Readable, recovered.State);
        Assert.IsFalse(Directory.Exists(residue));

        File.WriteAllText(Path.Combine(stateRoot, "battle", "recovery", "foreign.txt"), "preserve");
        var blocked = store.Inspect();
        Assert.AreEqual(BattleLifecycleJournalState.RecoveryFailed, blocked.State);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.RecoverAsync(lease));
        Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(
            stateRoot,
            "battle",
            "recovery",
            "foreign.txt")));
    }

    [TestMethod]
    public void SkippedRegressedOrRewrittenSuccessorsFailClosed()
    {
        var prepared = PreparedMarker();
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleLifecycleMarkerCodec.ValidateSuccessor(
                prepared,
                prepared with
                {
                    Stage = BattleLifecycleStage.BackupVerified,
                    UpdatedAtUtc = Started.AddSeconds(1),
                }));
        Assert.ThrowsException<InvalidDataException>(() =>
            BattleLifecycleMarkerCodec.ValidateSuccessor(
                prepared,
                prepared with
                {
                    Stage = BattleLifecycleStage.Quiesced,
                    OwnerId = new string('b', 32),
                    UpdatedAtUtc = Started.AddSeconds(1),
                }));
    }

    [TestMethod]
    public async Task EveryAcceptedStageIsExactAndMonotonic()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var store = new BattleLifecycleJournalStore(stateRoot, new RecordingProtector());
        await using var lease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(lease);
        var marker = PreparedMarker();
        await store.CreatePreparedAsync(lease, marker);

        var credentialHash = new string('3', 64);
        marker = marker with
        {
            Stage = BattleLifecycleStage.Quiesced,
            UpdatedAtUtc = Started.AddSeconds(1),
            Resources =
            [
                new(
                    "ingest-credential",
                    $"battle/{BattleIngestCredentialCodec.FileName}",
                    null,
                    $"battle/recovery/{marker.OperationId}/candidate/ingest-credential-v1.dpapi.next",
                    new(512, credentialHash)),
                marker.Resources[0],
            ],
            Credential = new(1, 512, credentialHash),
        };
        await store.AdvanceAsync(lease, marker);

        marker = marker with
        {
            Stage = BattleLifecycleStage.BackupVerified,
            UpdatedAtUtc = Started.AddSeconds(2),
            Configuration = new(
                new string('4', 64),
                1024,
                new string('4', 64),
                "battle-config-backup-v1",
                new string('5', 64),
                new string('6', 64)),
        };
        await store.AdvanceAsync(lease, marker);

        foreach (var stage in new[]
                 {
                     BattleLifecycleStage.CommitStarted,
                     BattleLifecycleStage.CommitVerified,
                     BattleLifecycleStage.CleanupPending,
                 })
        {
            marker = marker with
            {
                Stage = stage,
                UpdatedAtUtc = marker.UpdatedAtUtc.AddSeconds(1),
            };
            await store.AdvanceAsync(lease, marker);
        }

        var inspection = store.Inspect();
        Assert.AreEqual(BattleLifecycleJournalState.Readable, inspection.State);
        Assert.AreEqual(BattleLifecycleStage.CleanupPending, inspection.Marker!.Stage);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            store.AdvanceAsync(
                lease,
                marker with
                {
                    Stage = BattleLifecycleStage.Failed,
                    UpdatedAtUtc = marker.UpdatedAtUtc.AddSeconds(1),
                }));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void WindowsDpapiUsesTheExactLifecycleEntropy()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("DPAPI is Windows-only.");
        var marker = PreparedMarker();
        var protector = new WindowsDpapiBattleLifecycleMarkerProtector();
        var protectedBytes = BattleLifecycleMarkerCodec.Protect(marker, protector);

        var decoded = BattleLifecycleMarkerCodec.Unprotect(protectedBytes, protector);

        Assert.IsTrue(BattleLifecycleMarkerCodec.AreEquivalent(marker, decoded));
        var wrongEntropy = Encoding.UTF8.GetBytes("not the reviewed Battle lifecycle entropy");
        var wrong = ProtectedData.Protect(
            Encoding.UTF8.GetBytes("not a marker"),
            wrongEntropy,
            DataProtectionScope.CurrentUser);
        Assert.ThrowsException<CryptographicException>(() =>
            BattleLifecycleMarkerCodec.Unprotect(wrong, protector));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void MarkerReaderRefusesAFileReparsePointWithoutTouchingItsTarget()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Reparse-point proof is Windows-only.");
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var protector = new RecordingProtector();
        var store = new BattleLifecycleJournalStore(stateRoot, protector);
        Directory.CreateDirectory(Path.GetDirectoryName(store.MarkerPath)!);
        var foreign = Path.Combine(temporaryDirectory.Path, "foreign.dpapi");
        var protectedBytes = BattleLifecycleMarkerCodec.Protect(PreparedMarker(), protector);
        File.WriteAllBytes(foreign, protectedBytes);
        try
        {
            File.CreateSymbolicLink(store.MarkerPath, foreign);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating a file symlink is unavailable on this host.");
        }

        var inspection = store.Inspect();

        Assert.AreEqual(BattleLifecycleJournalState.RecoveryFailed, inspection.State);
        CollectionAssert.AreEqual(protectedBytes, File.ReadAllBytes(foreign));
    }

    [TestMethod]
    public void OversizedOrTamperedProtectedMarkerIsTypedAndBounded()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var protector = new RecordingProtector();
        var store = new BattleLifecycleJournalStore(stateRoot, protector);
        Directory.CreateDirectory(Path.GetDirectoryName(store.MarkerPath)!);
        File.WriteAllBytes(
            store.MarkerPath,
            new byte[BattleLifecycleMarkerCodec.MaximumProtectedBytes + 1]);

        var oversized = store.Inspect();

        Assert.AreEqual(BattleLifecycleJournalState.RecoveryFailed, oversized.State);
        Assert.AreEqual(0, protector.UnprotectCalls);
        File.WriteAllBytes(store.MarkerPath, [1, 2, 3]);
        protector.UnprotectFailure = new CryptographicException("private detail");

        var tampered = store.Inspect();

        Assert.AreEqual(BattleLifecycleJournalState.RecoveryFailed, tampered.State);
        Assert.AreEqual("battle-operation-invalid", tampered.Code);
        Assert.IsFalse(tampered.Code.Contains("private", StringComparison.Ordinal));
    }

    private static BattleLifecycleMarker PreparedMarker() => new(
        new string('a', 32),
        BattleLifecycleOperationKind.FeatureActivation,
        new string('1', 32),
        BattleLifecycleStage.Prepared,
        [LauncherFeatureIds.BattleCollection],
        [
            new(
                "runtime-lock",
                "battle/runtime.lock",
                null,
                null,
                new(256, new string('2', 64))),
        ],
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
        "battle-lifecycle-foundation-v1",
        true,
        true);

    private static string ReadString(byte[] json, string property)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).GetString()!;
    }

    private sealed class RecordingProtector : IBattleLifecycleMarkerProtector
    {
        public int ProtectCalls { get; private set; }

        public int UnprotectCalls { get; private set; }

        public Exception? UnprotectFailure { get; set; }

        public byte[] Protect(byte[] plaintext)
        {
            ProtectCalls++;
            return plaintext.ToArray();
        }

        public byte[] Unprotect(byte[] protectedBytes)
        {
            UnprotectCalls++;
            if (UnprotectFailure is not null)
            {
                throw UnprotectFailure;
            }
            return protectedBytes.ToArray();
        }
    }

    private sealed class SimulatedCrashException : Exception;
}
