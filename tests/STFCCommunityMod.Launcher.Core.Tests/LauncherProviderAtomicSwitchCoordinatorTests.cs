using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherProviderAtomicSwitchCoordinatorTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedManagedLegacyReceiptCommitsOrRollsBackWithoutPrematureMigration(
        bool failSelectionSave)
    {
        using var directory = new TemporaryDirectory();
        var selectionStore = new FailingSelectionStore();
        string? installedStatePath = null;
        byte[] expectedRegistry = [];
        var acquisitionCount = 0;
        var downloader = new CallbackDownloader(NetnivArtifact, () =>
        {
            acquisitionCount++;
            Assert.IsNotNull(installedStatePath);
            CollectionAssert.AreEqual(expectedRegistry, File.ReadAllBytes(installedStatePath!),
                "Backup receipt normalization must remain in memory before acquisition and commit.");
        });
        var fixture = await CreateFixtureAsync(directory, selectionStore,
            installSource: false, targetDownloader: downloader);
        var original = await SeedChangedManagedSourceAsync(fixture, hasOlderAdoptionBackup: true);
        var olderIdentity = ReplacementTestIdentity(original.PreviousArtifactBackupPath!);
        var legacy = WriteLegacyReplacementReceipt(fixture, original);
        var unrelated = SeedUnrelatedReplacementTestBackup(fixture);
        installedStatePath = fixture.SourceDeployment.InstalledStatePath;
        expectedRegistry = File.ReadAllBytes(installedStatePath);
        var dllPath = Path.Combine(fixture.GameDirectory, "version.dll");
        var liveIdentity = ReplacementTestIdentity(dllPath);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv", "stable", fixture.GameDirectory, isGameRunning: false, fixture.ConfigurationPath);
        Assert.IsTrue(preview.CanExecute, preview.BlockedMessage);
        Assert.IsTrue(preview.ReplacesChangedManagedArtifact);
        CollectionAssert.AreEqual(expectedRegistry, File.ReadAllBytes(installedStatePath),
            "Preview must not migrate the saved legacy receipt.");

        if (failSelectionSave)
        {
            selectionStore.FailNextSave = true;
            _ = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));
            CollectionAssert.AreEqual(expectedRegistry, File.ReadAllBytes(installedStatePath),
                "Compensation must restore the original raw receipt, including its null backup identity.");
            var restored = fixture.SourceDeployment.ReadInstalledState(fixture.GameDirectory)!;
            Assert.IsNull(restored.PreviousArtifactBackupIdentity);
            Assert.AreEqual(JsonSerializer.Serialize(legacy, JsonOptions),
                JsonSerializer.Serialize(restored, JsonOptions));
            CollectionAssert.AreEqual(ChangedManagedArtifact, File.ReadAllBytes(dllPath));
            Assert.AreEqual(liveIdentity, ReplacementTestIdentity(dllPath));
            CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration,
                File.ReadAllBytes(fixture.ConfigurationPath));
            Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), selectionStore.Load());
            Assert.AreEqual(unrelated, ReplacementTestRegistry(fixture).DetachedAdoptionBackups!.Single());
            Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RolledBack, fixture.Coordinator.ReadJournal()!.Phase);
            Assert.AreEqual(ModDeploymentPhase.RolledBack, fixture.TargetDeployment.ReadJournal()!.Phase);
            Assert.IsFalse(Directory.EnumerateFiles(fixture.GameDirectory, "*.rollback").Any());
        }
        else
        {
            var result = await fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);
            AssertLegacyReplacementJournal(fixture.TargetDeployment.ReadJournal()!, legacy, olderIdentity);
            var active = result.InstalledArtifact!;
            Assert.AreEqual("netniv", active.ProviderId);
            Assert.AreEqual(liveIdentity, active.PreviousArtifactBackupIdentity);
            Assert.AreNotEqual(legacy.PreviousArtifactBackupPath, active.PreviousArtifactBackupPath);
            CollectionAssert.AreEqual(ChangedManagedArtifact, File.ReadAllBytes(active.PreviousArtifactBackupPath!));
            CollectionAssert.AreEqual(NetnivArtifact, File.ReadAllBytes(dllPath));
            CollectionAssert.AreEqual(fixture.NetnivConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
            Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), selectionStore.Load());
            var detached = ReplacementTestRegistry(fixture).DetachedAdoptionBackups!;
            Assert.AreEqual(2, detached.Count);
            Assert.AreEqual(unrelated, detached.Single(backup => backup.DetachmentId == unrelated.DetachmentId));
            var older = detached.Single(backup => backup.DetachmentId == preview.Configuration.TransactionId);
            Assert.AreEqual(legacy.PreviousArtifactBackupPath, older.PreviousArtifactBackupPath);
            Assert.AreEqual(olderIdentity, older.PreviousArtifactBackupIdentity,
                "The non-owning detached receipt must carry the resolved identity of the older adoption.");
            var uninstall = await fixture.TargetDeployment.UninstallAsync(fixture.GameDirectory);
            Assert.AreEqual(ModDeploymentResultState.Succeeded, uninstall.State, uninstall.Message);
            CollectionAssert.AreEqual(ChangedManagedArtifact, File.ReadAllBytes(dllPath));
            Assert.AreEqual(liveIdentity, ReplacementTestIdentity(dllPath));
            Assert.IsNull(fixture.TargetDeployment.ReadInstalledState(fixture.GameDirectory));
            Assert.AreEqual(2, ReplacementTestRegistry(fixture).DetachedAdoptionBackups!.Count);
        }

        Assert.AreEqual(1, acquisitionCount);
        if (failSelectionSave)
            AssertLegacyReplacementJournal(fixture.TargetDeployment.ReadJournal()!, legacy, olderIdentity);
        CollectionAssert.AreEqual(OriginalAdoptedArtifact, File.ReadAllBytes(legacy.PreviousArtifactBackupPath!));
    }

    [TestMethod]
    public async Task ChangedManagedLegacyReceiptRejectsStaleReviewWithoutPersistingMigration()
    {
        using var directory = new TemporaryDirectory();
        var downloader = new CountingDownloader(NetnivArtifact);
        var fixture = await CreateFixtureAsync(directory, installSource: false, targetDownloader: downloader);
        var source = await SeedChangedManagedSourceAsync(fixture, hasOlderAdoptionBackup: true);
        var legacy = WriteLegacyReplacementReceipt(fixture, source);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv", "stable", fixture.GameDirectory, isGameRunning: false, fixture.ConfigurationPath);
        Assert.IsTrue(preview.CanExecute, preview.BlockedMessage);
        var registry = ReplacementTestRegistry(fixture);
        WriteJson(fixture.SourceDeployment.InstalledStatePath, registry with
        {
            Installations = [legacy with { InstalledAtUtc = legacy.InstalledAtUtc.AddMinutes(1) }],
        });
        var expectedRegistry = File.ReadAllBytes(fixture.SourceDeployment.InstalledStatePath);
        var dllPath = Path.Combine(fixture.GameDirectory, "version.dll");
        var liveIdentity = ReplacementTestIdentity(dllPath);

        _ = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        Assert.AreEqual(0, downloader.CallCount,
            "A full-receipt mismatch must fail before any target artifact acquisition.");
        CollectionAssert.AreEqual(expectedRegistry, File.ReadAllBytes(fixture.SourceDeployment.InstalledStatePath),
            "Rejecting a stale legacy review must not fill backup identities or rewrite its registry.");
        Assert.IsNull(fixture.SourceDeployment.ReadInstalledState(fixture.GameDirectory)!.PreviousArtifactBackupIdentity);
        CollectionAssert.AreEqual(ChangedManagedArtifact, File.ReadAllBytes(dllPath));
        Assert.AreEqual(liveIdentity, ReplacementTestIdentity(dllPath));
        CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), fixture.SelectionStore.Load());
        Assert.AreEqual(0, (ReplacementTestRegistry(fixture).DetachedAdoptionBackups ?? []).Count);
        CollectionAssert.AreEqual(OriginalAdoptedArtifact, File.ReadAllBytes(legacy.PreviousArtifactBackupPath!));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.GameDirectory, "*.rollback").Any());
    }

    private static ModInstalledArtifactState WriteLegacyReplacementReceipt(
        Fixture fixture, ModInstalledArtifactState source)
    {
        Assert.IsNotNull(source.PreviousArtifactBackupPath);
        var legacy = source with
        {
            PreviousArtifactBackupIdentity = null,
            PreviousRuntimeManifestBackupIdentity = null,
        };
        var registry = ReplacementTestRegistry(fixture);
        WriteJson(fixture.SourceDeployment.InstalledStatePath, registry with
        {
            Installations = registry.Installations.Select(state =>
                state.GameDirectory == legacy.GameDirectory ? legacy : state).ToArray(),
        });
        return legacy;
    }

    private static void AssertLegacyReplacementJournal(
        ModDeploymentJournal journal, ModInstalledArtifactState legacy,
        ModArtifactIdentityReceipt olderIdentity)
    {
        Assert.IsTrue(journal.AdoptChangedManagedArtifact);
        Assert.IsNotNull(journal.ReviewedPreviousInstalledState);
        Assert.IsNull(journal.ReviewedPreviousInstalledState.PreviousArtifactBackupIdentity);
        Assert.AreEqual(JsonSerializer.Serialize(legacy, JsonOptions),
            JsonSerializer.Serialize(journal.ReviewedPreviousInstalledState, JsonOptions));
        Assert.AreEqual(olderIdentity, journal.PreviousInstalledState!.PreviousArtifactBackupIdentity);
    }


    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StalePreferredSourceDoesNotBecomeInstalledArtifactAuthority(bool changedManaged)
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);
        if (changedManaged)
            await SeedChangedManagedSourceAsync(fixture, hasOlderAdoptionBackup: false);
        var before = File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll"));
        var installed = fixture.SourceDeployment.ReadInstalledState(fixture.GameDirectory)!;

        // Preference changed independently; actual receipt remains Guffawaffle.
        fixture.SelectionStore.Save(new("netniv", "stable"));
        var preview = await fixture.Coordinator.PreviewAsync(
            "guffawaffle", "stable", fixture.GameDirectory, isGameRunning: false,
            fixture.ConfigurationPath);

        Assert.AreEqual("netniv", preview.Configuration.Source.ProviderId);
        Assert.AreEqual(installed.ProviderId, preview.SourceInstallation.InstalledProviderId);
        Assert.AreEqual("guffawaffle", preview.Artifact!.ProviderId);
        Assert.AreEqual(changedManaged, preview.ReplacesChangedManagedArtifact);
        var result = await fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual("guffawaffle", result.InstalledArtifact!.ProviderId);
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), fixture.SelectionStore.Load());
        CollectionAssert.AreEqual(GuffawaffleArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        Assert.AreEqual("guffawaffle", fixture.SourceDeployment.ReadInstalledState(fixture.GameDirectory)!.ProviderId);
        if (changedManaged)
        {
            var current = fixture.SourceDeployment.ReadInstalledState(fixture.GameDirectory)!;
            Assert.IsNotNull(current.PreviousArtifactBackupPath);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(current.PreviousArtifactBackupPath));
        }
    }

    private static readonly byte[] ChangedManagedArtifact =
        Encoding.ASCII.GetBytes("current-custom-local-build");
    private static readonly byte[] OriginalAdoptedArtifact =
        Encoding.ASCII.GetBytes("original-pre-management-dll");

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedManagedDllSwitchPreservesCurrentBytesForUninstall(
        bool hasOlderAdoptionBackup)
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, installSource: !hasOlderAdoptionBackup);
        var sourceState = await SeedChangedManagedSourceAsync(fixture, hasOlderAdoptionBackup);
        var sourceIdentity = ReplacementTestIdentity(Path.Combine(fixture.GameDirectory, "version.dll"));
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv", "stable", fixture.GameDirectory, isGameRunning: false, fixture.ConfigurationPath);

        Assert.AreEqual(ModInstallationEvidenceState.ManagedChanged, preview.SourceInstallation.State);
        Assert.IsTrue(preview.ReplacesChangedManagedArtifact);
        Assert.IsNotNull(preview.Artifact);
        Assert.IsTrue(preview.CanExecute, preview.BlockedMessage);
        var result = await fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        CollectionAssert.AreEqual(NetnivArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(fixture.NetnivConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), fixture.SelectionStore.Load());
        Assert.IsNotNull(result.ConfigurationBackup, "The source TOML must have a protected backup.");
        Assert.AreEqual("netniv", result.InstalledArtifact!.ProviderId);
        Assert.IsNotNull(result.InstalledArtifact.PreviousArtifactBackupPath);
        Assert.AreNotEqual(sourceState.PreviousArtifactBackupPath,
            result.InstalledArtifact.PreviousArtifactBackupPath);
        CollectionAssert.AreEqual(ChangedManagedArtifact,
            File.ReadAllBytes(result.InstalledArtifact.PreviousArtifactBackupPath));
        Assert.AreEqual(sourceIdentity, result.InstalledArtifact.PreviousArtifactBackupIdentity);
        var detached = ReplacementTestRegistry(fixture).DetachedAdoptionBackups ?? [];
        Assert.AreEqual(hasOlderAdoptionBackup ? 1 : 0, detached.Count);
        if (hasOlderAdoptionBackup)
        {
            Assert.AreEqual(preview.Configuration.TransactionId, detached.Single().DetachmentId);
            Assert.AreEqual(sourceState.PreviousArtifactBackupPath,
                detached.Single().PreviousArtifactBackupPath);
            Assert.AreEqual(sourceState.PreviousArtifactBackupIdentity,
                detached.Single().PreviousArtifactBackupIdentity);
            CollectionAssert.AreEqual(OriginalAdoptedArtifact,
                File.ReadAllBytes(sourceState.PreviousArtifactBackupPath!));
        }

        var uninstall = await fixture.TargetDeployment.UninstallAsync(fixture.GameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, uninstall.State, uninstall.Message);
        CollectionAssert.AreEqual(ChangedManagedArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        Assert.AreEqual(sourceIdentity,
            ReplacementTestIdentity(Path.Combine(fixture.GameDirectory, "version.dll")));
        Assert.IsNull(fixture.TargetDeployment.ReadInstalledState(fixture.GameDirectory));
        // Removing the new mod does not turn an older adoption backup into the current restore target.
        if (hasOlderAdoptionBackup)
        {
            var retained = ReplacementTestRegistry(fixture).DetachedAdoptionBackups!.Single();
            Assert.AreEqual(preview.Configuration.TransactionId, retained.DetachmentId);
            CollectionAssert.AreEqual(OriginalAdoptedArtifact,
                File.ReadAllBytes(retained.PreviousArtifactBackupPath!));
        }
    }

    [DataTestMethod]
    [DataRow("dll-bytes")]
    [DataRow("dll-metadata")]
    [DataRow("receipt")]
    [DataRow("runtime-presence")]
    public async Task ChangedManagedSourceReviewRejectsStaleIdentityBeforeDownload(string changedMember)
    {
        using var directory = new TemporaryDirectory();
        var downloader = new CountingDownloader(NetnivArtifact);
        var fixture = await CreateFixtureAsync(directory, installSource: false, targetDownloader: downloader);
        var sourceState = await SeedChangedManagedSourceAsync(fixture, hasOlderAdoptionBackup: true);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv", "stable", fixture.GameDirectory, isGameRunning: false, fixture.ConfigurationPath);
        var dllPath = Path.Combine(fixture.GameDirectory, "version.dll");
        var runtimePath = Path.Combine(fixture.GameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName);
        switch (changedMember)
        {
            case "dll-bytes":
                File.WriteAllBytes(dllPath, Encoding.ASCII.GetBytes("another-local-build-after-review"));
                break;
            case "dll-metadata":
                File.SetLastWriteTimeUtc(dllPath, File.GetLastWriteTimeUtc(dllPath).AddMinutes(1));
                break;
            case "receipt":
                var registry = ReplacementTestRegistry(fixture);
                WriteJson(fixture.SourceDeployment.InstalledStatePath, registry with
                {
                    Installations = [sourceState with { InstalledAtUtc = sourceState.InstalledAtUtc.AddMinutes(1) }],
                });
                break;
            case "runtime-presence":
                File.WriteAllBytes(runtimePath, Encoding.UTF8.GetBytes("{\"changedAfterReview\":true}"));
                break;
            default:
                Assert.Fail($"Unknown changed member: {changedMember}");
                break;
        }
        var expectedDll = File.ReadAllBytes(dllPath);
        var expectedIdentity = ReplacementTestIdentity(dllPath);
        var expectedRegistry = File.ReadAllBytes(fixture.SourceDeployment.InstalledStatePath);
        var expectedRuntime = File.Exists(runtimePath) ? File.ReadAllBytes(runtimePath) : null;

        _ = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        Assert.AreEqual(0, downloader.CallCount, "A stale review must fail before artifact acquisition.");
        CollectionAssert.AreEqual(expectedDll, File.ReadAllBytes(dllPath));
        Assert.AreEqual(expectedIdentity, ReplacementTestIdentity(dllPath));
        CollectionAssert.AreEqual(expectedRegistry, File.ReadAllBytes(fixture.SourceDeployment.InstalledStatePath));
        CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), fixture.SelectionStore.Load());
        if (expectedRuntime is null)
            Assert.IsFalse(File.Exists(runtimePath));
        else
            CollectionAssert.AreEqual(expectedRuntime, File.ReadAllBytes(runtimePath));
        CollectionAssert.AreEqual(OriginalAdoptedArtifact,
            File.ReadAllBytes(sourceState.PreviousArtifactBackupPath!));
        Assert.AreEqual(0, (ReplacementTestRegistry(fixture).DetachedAdoptionBackups ?? []).Count);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.GameDirectory, "*.rollback").Any());
    }

    [TestMethod]
    public async Task ChangedManagedSourceSelectionFailureRestoresCustomDllAndPriorReceipt()
    {
        using var directory = new TemporaryDirectory();
        var selectionStore = new FailingSelectionStore();
        var fixture = await CreateFixtureAsync(directory, selectionStore, installSource: false);
        var sourceState = await SeedChangedManagedSourceAsync(fixture, hasOlderAdoptionBackup: true);
        var unrelated = SeedUnrelatedReplacementTestBackup(fixture);
        var expectedRegistry = File.ReadAllBytes(fixture.SourceDeployment.InstalledStatePath);
        var sourceIdentity = ReplacementTestIdentity(Path.Combine(fixture.GameDirectory, "version.dll"));
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv", "stable", fixture.GameDirectory, isGameRunning: false, fixture.ConfigurationPath);
        selectionStore.FailNextSave = true;

        _ = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        CollectionAssert.AreEqual(ChangedManagedArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        Assert.AreEqual(sourceIdentity,
            ReplacementTestIdentity(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(expectedRegistry, File.ReadAllBytes(fixture.SourceDeployment.InstalledStatePath));
        CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), selectionStore.Load());
        CollectionAssert.AreEqual(OriginalAdoptedArtifact,
            File.ReadAllBytes(sourceState.PreviousArtifactBackupPath!));
        Assert.AreEqual(unrelated,
            ReplacementTestRegistry(fixture).DetachedAdoptionBackups!.Single());
        Assert.IsFalse(ReplacementTestRegistry(fixture).DetachedAdoptionBackups!
            .Any(backup => backup.DetachmentId == preview.Configuration.TransactionId));
        Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RolledBack, fixture.Coordinator.ReadJournal()!.Phase);
        Assert.AreEqual(ModDeploymentPhase.RolledBack, fixture.TargetDeployment.ReadJournal()!.Phase);
        Assert.IsTrue(fixture.TargetDeployment.ReadJournal()!.AdoptChangedManagedArtifact);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.GameDirectory, "*.rollback").Any());
    }

    [TestMethod]
    public async Task ChangedManagedSourceFinalizationFailureRecoversDurableCustomBackupAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        ValueTask FailAtFinalization(LauncherProviderAtomicSwitchPhase phase, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (phase == LauncherProviderAtomicSwitchPhase.Completed)
                throw new IOException("Injected finalization failure after durable custom backup promotion.");
            return ValueTask.CompletedTask;
        }
        var fixture = await CreateFixtureAsync(directory.Path, installSource: false,
            checkpoint: FailAtFinalization);
        var sourceState = await SeedChangedManagedSourceAsync(fixture, hasOlderAdoptionBackup: true);
        var unrelated = SeedUnrelatedReplacementTestBackup(fixture);
        var expectedRegistry = File.ReadAllBytes(fixture.SourceDeployment.InstalledStatePath);
        var sourceIdentity = ReplacementTestIdentity(Path.Combine(fixture.GameDirectory, "version.dll"));
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv", "stable", fixture.GameDirectory, isGameRunning: false, fixture.ConfigurationPath);

        _ = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RecoveryRequired, fixture.Coordinator.ReadJournal()!.Phase);
        var innerJournal = fixture.TargetDeployment.ReadJournal()!;
        Assert.AreEqual(ModDeploymentPhase.CleanupPending, innerJournal.Phase);
        Assert.IsTrue(innerJournal.AdoptChangedManagedArtifact);
        Assert.IsFalse(File.Exists(innerJournal.SameVolumeBackupPath));
        CollectionAssert.AreEqual(ChangedManagedArtifact, File.ReadAllBytes(innerJournal.DurableBackupPath));
        CollectionAssert.AreEqual(NetnivArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(fixture.NetnivConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), fixture.SelectionStore.Load());
        var detachedBeforeRecovery = ReplacementTestRegistry(fixture).DetachedAdoptionBackups!;
        Assert.AreEqual(2, detachedBeforeRecovery.Count);
        Assert.IsTrue(detachedBeforeRecovery.Any(backup => backup.DetachmentId == preview.Configuration.TransactionId));

        // New coordinator/deployment objects must reconstruct recovery entirely from persisted state.
        var filesBeforeReconstruction = CaptureFiles(directory.Path);
        var restarted = await CreateFixtureAsync(directory.Path, initializeFixture: false);
        AssertFilesEqual(filesBeforeReconstruction, CaptureFiles(directory.Path));
        var recovery = await restarted.Coordinator.RecoverAsync();

        Assert.IsTrue(recovery.IsSuccess, recovery.Message);
        Assert.IsTrue(recovery.Changed, recovery.Message);
        CollectionAssert.AreEqual(ChangedManagedArtifact,
            File.ReadAllBytes(Path.Combine(restarted.GameDirectory, "version.dll")));
        Assert.AreEqual(sourceIdentity,
            ReplacementTestIdentity(Path.Combine(restarted.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(expectedRegistry, File.ReadAllBytes(restarted.SourceDeployment.InstalledStatePath));
        CollectionAssert.AreEqual(restarted.GuffawaffleConfiguration, File.ReadAllBytes(restarted.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), restarted.SelectionStore.Load());
        CollectionAssert.AreEqual(OriginalAdoptedArtifact,
            File.ReadAllBytes(sourceState.PreviousArtifactBackupPath!));
        Assert.AreEqual(unrelated, ReplacementTestRegistry(restarted).DetachedAdoptionBackups!.Single());
        Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RolledBack, restarted.Coordinator.ReadJournal()!.Phase);
        Assert.IsFalse(Directory.EnumerateFiles(restarted.GameDirectory, "*.rollback").Any());
        var secondRecovery = await restarted.Coordinator.RecoverAsync();
        Assert.IsTrue(secondRecovery.IsSuccess, secondRecovery.Message);
        Assert.IsFalse(secondRecovery.Changed, secondRecovery.Message);
    }

    private static async Task<ModInstalledArtifactState> SeedChangedManagedSourceAsync(
        Fixture fixture, bool hasOlderAdoptionBackup)
    {
        var dllPath = Path.Combine(fixture.GameDirectory, "version.dll");
        if (hasOlderAdoptionBackup)
        {
            File.WriteAllBytes(dllPath, OriginalAdoptedArtifact);
            var installation = await fixture.SourceDeployment.DeployAsync(
                fixture.GameDirectory, fixture.SourceArtifact, ExistingArtifactPolicy.AdoptAndPreserve);
            Assert.AreEqual(ModDeploymentResultState.Succeeded, installation.State, installation.Message);
        }
        var sourceState = fixture.SourceDeployment.ReadInstalledState(fixture.GameDirectory)!;
        Assert.IsNotNull(sourceState);
        File.WriteAllBytes(dllPath, ChangedManagedArtifact);
        File.SetLastWriteTimeUtc(dllPath, new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        return sourceState;
    }

    private static ModInstalledArtifactRegistry ReplacementTestRegistry(Fixture fixture) =>
        JsonSerializer.Deserialize<ModInstalledArtifactRegistry>(
            File.ReadAllBytes(fixture.SourceDeployment.InstalledStatePath), JsonOptions)!;

    private static ModArtifactIdentityReceipt ReplacementTestIdentity(string path) => new(
        new FileInfo(path).Length,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
        File.GetAttributes(path),
        File.GetLastWriteTimeUtc(path).Ticks);

    private static ModDetachedAdoptionBackupState SeedUnrelatedReplacementTestBackup(Fixture fixture)
    {
        var id = Guid.NewGuid().ToString("N");
        var backupPath = Path.Combine(fixture.StateDirectory, "rollback", id, "version.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.WriteAllBytes(backupPath, Encoding.ASCII.GetBytes("another-detached-backup"));
        var detached = new ModDetachedAdoptionBackupState(id, fixture.GameDirectory, DateTimeOffset.UtcNow,
            "netniv", "stable", "netniv.stfc-community-mod", backupPath,
            ReplacementTestIdentity(backupPath), null, null);
        var registry = ReplacementTestRegistry(fixture);
        WriteJson(fixture.SourceDeployment.InstalledStatePath, registry with
        {
            DetachedAdoptionBackups = [.. (registry.DetachedAdoptionBackups ?? []), detached],
        });
        return detached;
    }

    [TestMethod]
    public async Task MalformedProviderSwitchJournalsUseTheDedicatedReadFailure()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);
        var journalPath = Path.Combine(fixture.StateDirectory, "provider-switch-journal.json");
        var malformedDocuments = new (string Name, string Json)[]
        {
            ("invalid JSON", "{"),
            ("null document", "null"),
            ("missing document members", "{}"),
            ("unsupported schema", "{\"schemaVersion\":999,\"transactionId\":\"transaction\"}"),
            ("invalid identity", "{\"schemaVersion\":2,\"transactionId\":\"\"}"),
        };

        foreach (var malformed in malformedDocuments)
        {
            File.WriteAllText(journalPath, malformed.Json);
            _ = Assert.ThrowsException<LauncherProviderSwitchJournalException>(
                () => fixture.Coordinator.ReadJournal(),
                malformed.Name);
        }
    }

    [TestMethod]
    public async Task StructurallyInvalidProviderSwitchRecoveryCannotMutateState()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, installSource: false);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var sourceBackup = await fixture.BackupStore.CreateAsync(new(
            fixture.GameDirectory,
            "guffawaffle",
            fixture.ConfigurationPath,
            fixture.GuffawaffleConfiguration,
            "provider-switch",
            "netniv",
            "guffawaffle/stable"));
        var valid = new LauncherProviderAtomicSwitchJournal(
            2,
            preview.Configuration.TransactionId,
            LauncherProviderAtomicSwitchPhase.Prepared,
            preview.Configuration,
            sourceBackup,
            TargetArtifact: null,
            DateTimeOffset.UtcNow);
        var invalidJournals = new (string Name, LauncherProviderAtomicSwitchJournal Journal)[]
        {
            ("missing nested provider identity", valid with
            {
                Preview = valid.Preview with
                {
                    Source = valid.Preview.Source with { ProviderId = string.Empty },
                },
            }),
            ("mismatched transaction identity", valid with
            {
                TransactionId = Guid.NewGuid().ToString("N"),
            }),
            ("undefined phase", valid with
            {
                Phase = (LauncherProviderAtomicSwitchPhase)int.MaxValue,
            }),
            ("missing source backup", valid with
            {
                ConfigurationBackup = null,
            }),
            ("invalid artifact identity", valid with
            {
                TargetArtifact = fixture.TargetArtifact with { FileName = "not-version.dll" },
            }),
            ("incoherent target history", valid with
            {
                Preview = valid.Preview with
                {
                    ConfigurationKind = LauncherProviderSwitchConfigurationKind.RestoreProviderHistory,
                    TargetConfigurationBackupId = null,
                    TargetConfigurationSha256 = null,
                },
            }),
        };
        var journalPath = Path.Combine(fixture.StateDirectory, "provider-switch-journal.json");
        var originalSelection = fixture.SelectionStore.Load();
        var originalConfiguration = File.ReadAllBytes(fixture.ConfigurationPath);

        foreach (var invalid in invalidJournals)
        {
            WriteJson(journalPath, invalid.Journal);
            var originalJournal = File.ReadAllBytes(journalPath);

            _ = await Assert.ThrowsExceptionAsync<LauncherProviderSwitchJournalException>(
                () => fixture.Coordinator.RecoverAsync(),
                invalid.Name);

            CollectionAssert.AreEqual(originalJournal, File.ReadAllBytes(journalPath), invalid.Name);
            Assert.AreEqual(originalSelection, fixture.SelectionStore.Load(), invalid.Name);
            CollectionAssert.AreEqual(
                originalConfiguration,
                File.ReadAllBytes(fixture.ConfigurationPath),
                invalid.Name);
            Assert.IsFalse(
                File.Exists(Path.Combine(fixture.GameDirectory, "version.dll")),
                invalid.Name);
        }
    }

    [TestMethod]
    public async Task RecoveryDependencyPreflightRejectsPathBackupAndArtifactMismatchWithoutMutation()
    {
        await AssertOutOfScopeConfigurationRecoveryIsRejectedAsync();
        await AssertMissingSourceBackupRecoveryIsRejectedAsync();
        await AssertMismatchedArtifactRecoveryIsRejectedAsync();
    }

    private static async Task AssertOutOfScopeConfigurationRecoveryIsRejectedAsync()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, installSource: false);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var otherGameDirectory = Path.Combine(directory.Path, "other-game");
        Directory.CreateDirectory(otherGameDirectory);
        TemporaryDirectory.CreateFile(otherGameDirectory, "prime.exe");
        var otherConfigurationPath = Path.Combine(
            otherGameDirectory,
            "community_patch_settings.toml");
        File.WriteAllBytes(otherConfigurationPath, fixture.NetnivConfiguration);
        var targetSha256 = ConfigurationDocumentRevision
            .FromContents(fixture.NetnivConfiguration)
            .Sha256;
        var escapedPreview = preview.Configuration with
        {
            ConfigurationPath = otherConfigurationPath,
            ConfigurationExisted = false,
            ConfigurationSha256 = null,
            ConfigurationKind = LauncherProviderSwitchConfigurationKind.RestoreProviderHistory,
            TargetConfigurationBackupId = Guid.NewGuid().ToString("N"),
            TargetConfigurationSha256 = targetSha256,
        };
        var journal = new LauncherProviderAtomicSwitchJournal(
            2,
            escapedPreview.TransactionId,
            LauncherProviderAtomicSwitchPhase.Prepared,
            escapedPreview,
            ConfigurationBackup: null,
            TargetArtifact: null,
            DateTimeOffset.UtcNow);

        await AssertRecoveryPreflightPreservesTreeAsync(
            fixture,
            journal,
            directory.Path,
            "out-of-scope configuration path");
    }

    private static async Task AssertMissingSourceBackupRecoveryIsRejectedAsync()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, installSource: false);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var sourceBackup = await fixture.BackupStore.CreateAsync(new(
            fixture.GameDirectory,
            "guffawaffle",
            fixture.ConfigurationPath,
            fixture.GuffawaffleConfiguration,
            "provider-switch",
            "netniv",
            "guffawaffle/stable"));
        var sourceManifestPath = Path.Combine(
            fixture.StateDirectory,
            "configuration-backups",
            sourceBackup.InstallationId,
            sourceBackup.ProviderId,
            sourceBackup.BackupId,
            "manifest.json");
        File.Delete(sourceManifestPath);
        var journal = new LauncherProviderAtomicSwitchJournal(
            2,
            preview.Configuration.TransactionId,
            LauncherProviderAtomicSwitchPhase.Prepared,
            preview.Configuration,
            sourceBackup,
            TargetArtifact: null,
            DateTimeOffset.UtcNow);

        await AssertRecoveryPreflightPreservesTreeAsync(
            fixture,
            journal,
            directory.Path,
            "missing protected source receipt");
    }

    private static async Task AssertMismatchedArtifactRecoveryIsRejectedAsync()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var sourceBackup = await fixture.BackupStore.CreateAsync(new(
            fixture.GameDirectory,
            "guffawaffle",
            fixture.ConfigurationPath,
            fixture.GuffawaffleConfiguration,
            "provider-switch",
            "netniv",
            "guffawaffle/stable"));
        var journal = new LauncherProviderAtomicSwitchJournal(
            2,
            preview.Configuration.TransactionId,
            LauncherProviderAtomicSwitchPhase.ArtifactCommitting,
            preview.Configuration,
            sourceBackup,
            preview.Artifact!.Artifact,
            DateTimeOffset.UtcNow);

        await AssertRecoveryPreflightPreservesTreeAsync(
            fixture,
            journal,
            directory.Path,
            "mismatched artifact participant");
    }

    private static async Task AssertRecoveryPreflightPreservesTreeAsync(
        Fixture fixture,
        LauncherProviderAtomicSwitchJournal journal,
        string root,
        string scenario)
    {
        var journalPath = Path.Combine(fixture.StateDirectory, "provider-switch-journal.json");
        WriteJson(journalPath, journal);
        await using (var providerLease = await new LauncherOperationLock(
                         Path.Combine(fixture.StateDirectory, "provider-switch"))
                     .TryAcquireAsync())
        {
            Assert.IsNotNull(providerLease, scenario);
        }
        await using (var rootLease = await new LauncherOperationLock(fixture.StateDirectory)
                         .TryAcquireAsync())
        {
            Assert.IsNotNull(rootLease, scenario);
        }
        var before = CaptureFiles(root);

        _ = await Assert.ThrowsExceptionAsync<LauncherProviderSwitchJournalException>(
            () => fixture.Coordinator.RecoverAsync(),
            scenario);

        AssertFilesEqual(before, CaptureFiles(root));
    }

    private const string CrashModeEnvironment = "STFC_BRIDGE_PROVIDER_SWITCH_CRASH_MODE";
    private const string CrashStageEnvironment = "STFC_BRIDGE_PROVIDER_SWITCH_CRASH_STAGE";
    private const string CrashRootEnvironment = "STFC_BRIDGE_PROVIDER_SWITCH_CRASH_ROOT";
    private const string CrashReadyEnvironment = "STFC_BRIDGE_PROVIDER_SWITCH_CRASH_READY";
    private static readonly byte[] GuffawaffleArtifact = Encoding.ASCII.GetBytes("guffawaffle-artifact");
    private static readonly byte[] NetnivArtifact = Encoding.ASCII.GetBytes("netniv-artifact");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [TestMethod]
    public async Task SwitchCommitsDllSelectionAndTargetConfigurationTogether()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);

        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var result = await fixture.Coordinator.ExecuteAsync(
            preview,
            preview.ConfirmationText);

        CollectionAssert.AreEqual(
            NetnivArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(fixture.NetnivConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), fixture.SelectionStore.Load());
        Assert.AreEqual("netniv", result.InstalledArtifact!.ProviderId);
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.Completed,
            fixture.Coordinator.ReadJournal()!.Phase);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.GameDirectory, "*.rollback").Any());
    }

    [TestMethod]
    public async Task ExactCandidateCommitsThroughProviderTransactionWithoutSecondDownload()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, reviewedTarget: true);
        var candidateDownloader = new CountingDownloader(NetnivArtifact);
        var candidateAcquirer = new ReviewedModArtifactCandidateAcquirer(
            fixture.StateDirectory,
            candidateDownloader,
            new FakeVersionReader(fixture.TargetArtifact.ExpectedVersion),
            new FakeAuthenticityVerifier(),
            fixture.TargetAttribution,
            fixture.TargetCertification!);
        var candidate = await candidateAcquirer.AcquireAsync(fixture.TargetArtifact);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);

        var result = await fixture.Coordinator.ExecuteCandidateAsync(
            preview,
            candidate,
            preview.ConfirmationText);

        Assert.AreEqual(1, candidateDownloader.CallCount);
        Assert.AreEqual("netniv", result.InstalledArtifact!.ProviderId);
        CollectionAssert.AreEqual(
            NetnivArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(fixture.NetnivConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), fixture.SelectionStore.Load());
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.Completed,
            fixture.Coordinator.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task ExactCandidateFromAnotherReleaseChannelFailsBeforeProviderTransaction()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, reviewedTarget: true);
        var candidateDownloader = new CountingDownloader(NetnivArtifact);
        var candidateAcquirer = new ReviewedModArtifactCandidateAcquirer(
            fixture.StateDirectory,
            candidateDownloader,
            new FakeVersionReader(fixture.TargetArtifact.ExpectedVersion),
            new FakeAuthenticityVerifier(),
            fixture.TargetAttribution,
            fixture.TargetCertification!);
        await using var candidate = await candidateAcquirer.AcquireAsync(fixture.TargetArtifact);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var mismatchedPreview = preview with
        {
            Configuration = preview.Configuration with
            {
                Target = preview.Configuration.Target with { ReleaseChannelId = "preview" },
            },
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteCandidateAsync(
                mismatchedPreview,
                candidate,
                mismatchedPreview.ConfirmationText));

        Assert.AreEqual(1, candidateDownloader.CallCount);
        Assert.IsNull(fixture.Coordinator.ReadJournal());
        CollectionAssert.AreEqual(
            GuffawaffleArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), fixture.SelectionStore.Load());
    }

    [TestMethod]
    public async Task SelectionCommitFailureRestoresExactDllStateAndToml()
    {
        using var directory = new TemporaryDirectory();
        var selectionStore = new FailingSelectionStore();
        var fixture = await CreateFixtureAsync(directory, selectionStore);
        var originalState = fixture.SourceDeployment.ReadInstalledState();
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        selectionStore.FailNextSave = true;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        CollectionAssert.AreEqual(
            GuffawaffleArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), selectionStore.Load());
        Assert.AreEqual(originalState, fixture.TargetDeployment.ReadInstalledState());
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.RolledBack,
            fixture.Coordinator.ReadJournal()!.Phase);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.GameDirectory, "*.rollback").Any());
    }

    [TestMethod]
    public async Task StaleInstalledProviderEvidenceFailsBeforeArtifactReplacement()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var externallyChangedState = fixture.SourceDeployment.ReadInstalledState()! with
        {
            ProviderId = "future-provider",
            RuntimeDistributionId = "future-provider.windows",
        };
        WriteJson(fixture.SourceDeployment.InstalledStatePath, externallyChangedState);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        CollectionAssert.AreEqual(
            GuffawaffleArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), fixture.SelectionStore.Load());
        Assert.AreEqual(externallyChangedState, fixture.TargetDeployment.ReadInstalledState());
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.RolledBack,
            fixture.Coordinator.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task DifferentInstallationRunningDoesNotBlockAtomicSwitchPreview()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);

        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);

        Assert.AreEqual(fixture.GameDirectory, preview.Artifact!.GameDirectory);
    }

    [TestMethod]
    public async Task NonDefaultTargetChannelWithoutExactEndpointIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.PreviewAsync(
                "guffawaffle",
                "preview",
                fixture.GameDirectory,
                isGameRunning: false,
                fixture.ConfigurationPath));

        StringAssert.Contains(exception.Message, "guffawaffle/preview");
        CollectionAssert.AreEqual(
            GuffawaffleArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        Assert.AreEqual(
            new LauncherProviderSelection("guffawaffle", "stable"),
            fixture.SelectionStore.Load());
    }

    [TestMethod]
    public async Task InvalidTargetTomlStopsBeforeTargetReleaseDiscovery()
    {
        using var directory = new TemporaryDirectory();
        var discovery = new CountingReleaseDiscoveryClient(
            Artifact(NetnivArtifact, "1.1.5.1"));
        var fixture = await CreateFixtureAsync(
            directory,
            targetConfiguration: Encoding.UTF8.GetBytes(
                "[graphics]\nfree_resize = true\nfree_resize = false\n"),
            targetReleaseDiscovery: discovery);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => fixture.Coordinator.PreviewAsync(
                "netniv",
                "stable",
                fixture.GameDirectory,
                isGameRunning: false,
                fixture.ConfigurationPath));

        StringAssert.Contains(exception.Message, "verified shared TOML editor");
        Assert.AreEqual(0, discovery.CallCount);
        Assert.AreEqual(0, fixture.BackupStore.List(
            fixture.GameDirectory,
            "guffawaffle").Count);
        Assert.AreEqual(
            new LauncherProviderSelection("guffawaffle", "stable"),
            fixture.SelectionStore.Load());
    }

    [TestMethod]
    public async Task ConcurrentSwitchIsRejectedBeforeItCanOverwriteTransactionState()
    {
        using var directory = new TemporaryDirectory();
        var downloader = new BlockingDownloader(NetnivArtifact);
        var fixture = await CreateFixtureAsync(directory, targetDownloader: downloader);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);

        var firstSwitch = fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);
        await downloader.Started;
        try
        {
            var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));
            var recovery = await fixture.Coordinator.RecoverAsync();

            StringAssert.Contains(exception.Message, "already active");
            Assert.IsFalse(recovery.IsSuccess);
            StringAssert.Contains(recovery.Message, "already active");
        }
        finally
        {
            downloader.Release();
            await firstSwitch;
        }
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.Completed,
            fixture.Coordinator.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task NoInstalledDllKeepsSourceSelectionPreferenceOnly()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, installSource: false);

        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var result = await fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.IsNull(preview.Artifact);
        Assert.IsNull(result.InstalledArtifact);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.GameDirectory, "version.dll")));
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), fixture.SelectionStore.Load());
        CollectionAssert.AreEqual(fixture.NetnivConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        var journal = fixture.Coordinator.ReadJournal();
        Assert.IsNotNull(journal);
        Assert.AreEqual(2, journal.SchemaVersion);
        Assert.AreEqual(LauncherProviderAtomicSwitchPhase.Completed, journal.Phase);
        Assert.IsNull(journal.TargetArtifact);
        Assert.AreEqual(true, journal.Preview.ConfigurationExisted);
        Assert.IsNotNull(journal.Preview.TargetConfigurationAnalysis);
        Assert.IsTrue(journal.Preview.TargetConfigurationAnalysis.FindingCounts.Count > 0);
    }

    [TestMethod]
    public async Task ManualDllDoesNotBlockPreferenceAndConfigurationSourceChangeWhileGameIsRunning()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, installSource: false);
        var manualDll = GuffawaffleArtifact.ToArray();
        var dllPath = Path.Combine(fixture.GameDirectory, "version.dll");
        File.WriteAllBytes(dllPath, manualDll);

        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: true,
            fixture.ConfigurationPath);
        var result = await fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(
            ModInstallationEvidenceState.ManualInstallation,
            preview.SourceInstallation.State);
        Assert.IsTrue(preview.SourceInstallation.IsGameRunning);
        Assert.IsNull(preview.Artifact);
        Assert.IsNull(result.InstalledArtifact);
        CollectionAssert.AreEqual(manualDll, File.ReadAllBytes(dllPath));
        Assert.IsNull(fixture.TargetDeployment.ReadInstalledState());
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), fixture.SelectionStore.Load());
        CollectionAssert.AreEqual(fixture.NetnivConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.Completed,
            fixture.Coordinator.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task ExactCatalogAnalysisRoundTripsThroughConfigurationOnlyJournal()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(
            directory,
            installSource: false,
            configurationEvidenceResolver: ExactConfigurationEvidence());
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var expected = preview.Configuration.TargetConfigurationAnalysis!;

        await fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        var journal = fixture.Coordinator.ReadJournal()!;
        var actual = journal.Preview.TargetConfigurationAnalysis!;
        Assert.AreEqual(expected.Binding.Revision.Sha256, actual.Binding.Revision.Sha256);
        Assert.AreEqual(expected.Binding.ProviderId, actual.Binding.ProviderId);
        Assert.AreEqual(expected.Binding.ChannelId, actual.Binding.ChannelId);
        Assert.AreEqual(expected.Binding.CatalogId, actual.Binding.CatalogId);
        Assert.AreEqual(expected.Binding.CatalogVersion, actual.Binding.CatalogVersion);
        Assert.AreEqual(expected.Binding.EvidenceSource, actual.Binding.EvidenceSource);
        Assert.AreEqual(expected.CatalogIdentity!.CatalogId, actual.CatalogIdentity!.CatalogId);
        Assert.AreEqual(expected.CatalogIdentity.CatalogVersion, actual.CatalogIdentity.CatalogVersion);
        Assert.AreEqual(expected.CatalogIdentity.TrackId, actual.CatalogIdentity.TrackId);
        Assert.AreEqual(expected.CatalogIdentity.ReleaseVersion, actual.CatalogIdentity.ReleaseVersion);
        Assert.AreEqual(expected.CatalogIdentity.SourceCommit, actual.CatalogIdentity.SourceCommit);
        Assert.AreEqual(expected.CatalogStatus, actual.CatalogStatus);
        Assert.AreEqual(expected.AttentionFindingCount, actual.AttentionFindingCount);
        CollectionAssert.AreEqual(
            expected.BlockingFindingCodes.ToArray(),
            actual.BlockingFindingCodes.ToArray());
        CollectionAssert.AreEquivalent(
            expected.FindingCounts.Select(pair => $"{pair.Key}:{pair.Value}").ToArray(),
            actual.FindingCounts.Select(pair => $"{pair.Key}:{pair.Value}").ToArray());
    }

    [TestMethod]
    public async Task CatalogEvidenceChangeDuringParticipantCommitRollsBackAllState()
    {
        using var directory = new TemporaryDirectory();
        var exactEvidence = ExactConfigurationEvidence();
        var evidenceAvailable = true;
        LauncherConfigurationDiagnosisEvidence Resolve(LauncherProviderSelection selection) =>
            evidenceAvailable
                ? exactEvidence(selection)
                : LauncherConfigurationDiagnosisEvidence.Unavailable(
                    selection.ProviderId,
                    selection.ReleaseChannelId,
                    LauncherProviderCapabilityStatus.Unknown);
        var downloader = new CallbackDownloader(
            NetnivArtifact,
            () => evidenceAvailable = false);
        var fixture = await CreateFixtureAsync(
            directory,
            targetDownloader: downloader,
            configurationEvidenceResolver: Resolve);
        var sourceState = fixture.SourceDeployment.ReadInstalledState();
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        CollectionAssert.AreEqual(
            GuffawaffleArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            fixture.GuffawaffleConfiguration,
            File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(
            new LauncherProviderSelection("guffawaffle", "stable"),
            fixture.SelectionStore.Load());
        Assert.AreEqual(sourceState, fixture.TargetDeployment.ReadInstalledState());
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.RolledBack,
            fixture.Coordinator.ReadJournal()!.Phase);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.GameDirectory, "*.rollback").Any());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConfigurationOnlyRecoveryRestoresCrashAroundSelectionCommit(
        bool selectionWasCommitted)
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, installSource: false);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var sourceBackup = await fixture.BackupStore.CreateAsync(new(
            fixture.GameDirectory,
            "guffawaffle",
            fixture.ConfigurationPath,
            fixture.GuffawaffleConfiguration,
            "provider-switch",
            "netniv",
            "guffawaffle/stable"));
        File.WriteAllBytes(fixture.ConfigurationPath, fixture.NetnivConfiguration);
        if (selectionWasCommitted)
        {
            fixture.SelectionStore.Save(new("netniv", "stable"));
        }
        WriteJson(
            Path.Combine(fixture.StateDirectory, "provider-switch-journal.json"),
            new LauncherProviderAtomicSwitchJournal(
                2,
                preview.Configuration.TransactionId,
                LauncherProviderAtomicSwitchPhase.ConfigurationCommitting,
                preview.Configuration,
                sourceBackup,
                TargetArtifact: null,
                DateTimeOffset.UtcNow));

        var recovery = await fixture.Coordinator.RecoverAsync();

        Assert.IsTrue(recovery.IsSuccess);
        Assert.IsTrue(recovery.Changed);
        CollectionAssert.AreEqual(
            fixture.GuffawaffleConfiguration,
            File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(
            new LauncherProviderSelection("guffawaffle", "stable"),
            fixture.SelectionStore.Load());
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.RolledBack,
            fixture.Coordinator.ReadJournal()!.Phase);
    }

    private sealed class PhysicalSwitchTermination : Exception;

    [TestMethod]
    public async Task PhysicalConfigurationRecoveryRefusesReplacementAndPinsRestorationAcrossAwait()
    {
        using var directory = new TemporaryDirectory();
        var store = new NativeLauncherProfilesStore(Path.Combine(directory.Path, "ui"),
            NativeProfileCatalogIntegrationTests.Transport(), Path.Combine(directory.Path, "catalog"));
        var fixture = await CreateFixtureAsync(directory.Path, installSource: false, profilesStore: store,
            checkpoint: (phase, _) => phase == LauncherProviderAtomicSwitchPhase.ConfigurationCommitted
                ? throw new PhysicalSwitchTermination() : ValueTask.CompletedTask);
        var preview = await fixture.Coordinator.PreviewAsync("netniv", "stable", fixture.GameDirectory,
            false, fixture.ConfigurationPath);
        Assert.IsNull(preview.Artifact, "This fixture must exercise configuration-only recovery.");
        await Assert.ThrowsExceptionAsync<PhysicalSwitchTermination>(() =>
            fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));
        Assert.IsNotNull(fixture.Coordinator.ReadJournal()!.InstallationBinding);
        Directory.Move(fixture.GameDirectory, fixture.GameDirectory + "-original");
        Directory.CreateDirectory(fixture.GameDirectory);
        File.WriteAllBytes(Path.Combine(fixture.GameDirectory, "prime.exe"), [9]);
        File.WriteAllText(fixture.ConfigurationPath, "replacement setup\n");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = await CreateFixtureAsync(directory.Path, initializeFixture: false,
            installSource: false, profilesStore: store, checkpoint: (phase, _) =>
            {
                if (phase != LauncherProviderAtomicSwitchPhase.RollingBack) return ValueTask.CompletedTask;
                entered.TrySetResult(); return new ValueTask(resume.Task);
            });
        var before = CaptureFiles(directory.Path);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => restarted.Coordinator.RecoverAsync());
        AssertFilesEqual(before, CaptureFiles(directory.Path));
        Directory.Move(fixture.GameDirectory, fixture.GameDirectory + "-replacement");
        Directory.Move(fixture.GameDirectory + "-original", fixture.GameDirectory);
        var work = restarted.Coordinator.RecoverAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.ThrowsException<IOException>(() => Directory.Move(fixture.GameDirectory, fixture.GameDirectory + "-moved"));
        }
        finally { resume.TrySetResult(); await work; }
        Assert.IsTrue((await work).IsSuccess);
        CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Directory.Move(fixture.GameDirectory, fixture.GameDirectory + "-released");
    }

    [TestMethod]
    public async Task ConfigurationOnlyRecoveryRestoresExpectedFileAbsence()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(
            directory,
            installSource: false,
            sourceConfigurationExists: false);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        Assert.AreEqual(false, preview.Configuration.ConfigurationExisted);
        File.WriteAllBytes(fixture.ConfigurationPath, fixture.NetnivConfiguration);
        fixture.SelectionStore.Save(new("netniv", "stable"));
        WriteJson(
            Path.Combine(fixture.StateDirectory, "provider-switch-journal.json"),
            new LauncherProviderAtomicSwitchJournal(
                2,
                preview.Configuration.TransactionId,
                LauncherProviderAtomicSwitchPhase.ConfigurationCommitting,
                preview.Configuration,
                ConfigurationBackup: null,
                TargetArtifact: null,
                DateTimeOffset.UtcNow));

        var recovery = await fixture.Coordinator.RecoverAsync();

        Assert.IsTrue(recovery.IsSuccess);
        Assert.IsFalse(File.Exists(fixture.ConfigurationPath));
        Assert.AreEqual(
            new LauncherProviderSelection("guffawaffle", "stable"),
            fixture.SelectionStore.Load());
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.RolledBack,
            fixture.Coordinator.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task SelectionOnlySwitchIsRejectedWhileRootMutationLeaseIsHeld()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory, installSource: false);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        Assert.IsNull(preview.Artifact);

        await using var lease = await new LauncherOperationLock(fixture.StateDirectory).TryAcquireAsync();
        Assert.IsNotNull(lease);
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        StringAssert.Contains(exception.Message, "Another Mod Bridge mutation is already active");
        Assert.AreEqual(
            new LauncherProviderSelection("guffawaffle", "stable"),
            fixture.SelectionStore.Load());
        CollectionAssert.AreEqual(
            fixture.GuffawaffleConfiguration,
            await File.ReadAllBytesAsync(fixture.ConfigurationPath));
        Assert.IsNull(fixture.Coordinator.ReadJournal());
    }

    [TestMethod]
    public async Task ArtifactSwitchIsRejectedBeforePreparingBackupWhileRootMutationLeaseIsHeld()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        Assert.IsNotNull(preview.Artifact);
        var sourceBackupsBefore = fixture.BackupStore.List(
            fixture.GameDirectory,
            "guffawaffle").Count;

        await using var lease = await new LauncherOperationLock(fixture.StateDirectory).TryAcquireAsync();
        Assert.IsNotNull(lease);
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        StringAssert.Contains(exception.Message, "Another Mod Bridge mutation is already active");
        Assert.AreEqual(
            sourceBackupsBefore,
            fixture.BackupStore.List(fixture.GameDirectory, "guffawaffle").Count);
        CollectionAssert.AreEqual(
            GuffawaffleArtifact,
            await File.ReadAllBytesAsync(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            fixture.GuffawaffleConfiguration,
            await File.ReadAllBytesAsync(fixture.ConfigurationPath));
        Assert.AreEqual(
            new LauncherProviderSelection("guffawaffle", "stable"),
            fixture.SelectionStore.Load());
        Assert.IsNull(fixture.Coordinator.ReadJournal());
    }

    [TestMethod]
    public async Task BlockedArtifactPreviewCannotPrepareConfigurationOrCreateAJournal()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var blockedMessage = "The retained release floor blocks this provider switch.";
        var blocked = preview with
        {
            Artifact = preview.Artifact! with
            {
                State = ModOperationPreparationState.MutationBlocked,
                Message = blockedMessage,
            },
        };
        var sourceBackupsBefore = fixture.BackupStore.List(
            fixture.GameDirectory,
            "guffawaffle").Count;

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => fixture.Coordinator.ExecuteAsync(blocked, blocked.ConfirmationText));

        Assert.AreEqual(blockedMessage, exception.Message);
        Assert.IsFalse(blocked.CanExecute);
        Assert.AreEqual(
            sourceBackupsBefore,
            fixture.BackupStore.List(fixture.GameDirectory, "guffawaffle").Count);
        CollectionAssert.AreEqual(
            GuffawaffleArtifact,
            File.ReadAllBytes(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            fixture.GuffawaffleConfiguration,
            File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(
            new LauncherProviderSelection("guffawaffle", "stable"),
            fixture.SelectionStore.Load());
        Assert.IsNull(fixture.Coordinator.ReadJournal());
    }

    [TestMethod]
    public async Task RecoveryIsRejectedWhileRootMutationLeaseIsHeld()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var sourceBackup = await fixture.BackupStore.CreateAsync(new(
            fixture.GameDirectory,
            "guffawaffle",
            fixture.ConfigurationPath,
            fixture.GuffawaffleConfiguration,
            "provider-switch",
            "netniv",
            "guffawaffle/stable"));
        WriteJson(
            Path.Combine(fixture.StateDirectory, "provider-switch-journal.json"),
            new LauncherProviderAtomicSwitchJournal(
                1,
                preview.Configuration.TransactionId,
                LauncherProviderAtomicSwitchPhase.Prepared,
                preview.Configuration,
                ConfigurationBackup: sourceBackup,
                TargetArtifact: preview.Artifact!.Artifact,
                UpdatedAtUtc: DateTimeOffset.UtcNow));

        await using var lease = await new LauncherOperationLock(fixture.StateDirectory).TryAcquireAsync();
        Assert.IsNotNull(lease);
        var recovery = await fixture.Coordinator.RecoverAsync();

        Assert.IsFalse(recovery.IsSuccess);
        Assert.IsFalse(recovery.Changed);
        StringAssert.Contains(recovery.Message, "Another Mod Bridge mutation is already active");
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.Prepared,
            fixture.Coordinator.ReadJournal()!.Phase);
        CollectionAssert.AreEqual(
            GuffawaffleArtifact,
            await File.ReadAllBytesAsync(Path.Combine(fixture.GameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            fixture.GuffawaffleConfiguration,
            await File.ReadAllBytesAsync(fixture.ConfigurationPath));
    }

    [TestMethod]
    public async Task RecoveryRollsBackCrashAfterDllAndConfigurationCommit()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory);
        var sourceState = fixture.SourceDeployment.ReadInstalledState()!;
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        var targetArtifact = preview.Artifact!.Artifact;
        var sourceBackup = await fixture.BackupStore.CreateAsync(new(
            fixture.GameDirectory,
            "guffawaffle",
            fixture.ConfigurationPath,
            fixture.GuffawaffleConfiguration,
            "provider-switch",
            "netniv",
            "guffawaffle/stable"));
        var targetPath = Path.Combine(fixture.GameDirectory, "version.dll");
        var rollbackPath = Path.Combine(
            fixture.GameDirectory,
            $".version.dll.{preview.Configuration.TransactionId}.rollback");
        File.Move(targetPath, rollbackPath);
        File.WriteAllBytes(targetPath, NetnivArtifact);
        ModFileIdentityReceipt targetFileIdentity;
        using (var exactTarget = ExactFileMutation.Open(targetPath))
        {
            targetFileIdentity = new(
                exactTarget.Identity.VolumeSerialNumber,
                exactTarget.Identity.FileIndex);
        }
        File.WriteAllBytes(fixture.ConfigurationPath, fixture.NetnivConfiguration);
        fixture.SelectionStore.Save(new("netniv", "stable"));
        var targetState = sourceState with
        {
            Version = targetArtifact.ExpectedVersion,
            Size = NetnivArtifact.LongLength,
            Sha256 = targetArtifact.Sha256,
            ProviderId = "netniv",
            ReleaseChannelId = "stable",
            RuntimeDistributionId = "netniv.stfc-community-mod",
        };
        var deploymentJournal = new ModDeploymentJournal(
            2,
            preview.Configuration.TransactionId,
            ModDeploymentOperation.Deploy,
            ModDeploymentPhase.Committed,
            fixture.GameDirectory,
            targetArtifact,
            Path.Combine(fixture.GameDirectory, $".version.dll.{preview.Configuration.TransactionId}.stage"),
            rollbackPath,
            Path.Combine(
                fixture.StateDirectory,
                "rollback",
                preview.Configuration.TransactionId,
                "version.dll"),
            HadExistingArtifact: true,
            sourceState,
            DateTimeOffset.UtcNow,
            HasCommitParticipant: true,
            TargetInstallationAttribution: fixture.TargetAttribution,
            TargetArtifactFileIdentity: targetFileIdentity);
        WriteJson(fixture.TargetDeployment.InstalledStatePath, targetState);
        WriteJson(fixture.TargetDeployment.JournalPath, deploymentJournal);
        WriteJson(
            Path.Combine(fixture.StateDirectory, "provider-switch-journal.json"),
            new LauncherProviderAtomicSwitchJournal(
                1,
                preview.Configuration.TransactionId,
                LauncherProviderAtomicSwitchPhase.ConfigurationCommitted,
                preview.Configuration,
                sourceBackup,
                targetArtifact,
                DateTimeOffset.UtcNow));

        var recovery = await fixture.Coordinator.RecoverAsync();

        Assert.IsTrue(recovery.IsSuccess);
        Assert.IsTrue(recovery.Changed);
        CollectionAssert.AreEqual(GuffawaffleArtifact, File.ReadAllBytes(targetPath));
        CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), fixture.SelectionStore.Load());
        Assert.AreEqual(sourceState, fixture.TargetDeployment.ReadInstalledState());
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.RolledBack,
            fixture.Coordinator.ReadJournal()!.Phase);
    }

    [DataTestMethod]
    [DataRow("artifact", "Prepared")]
    [DataRow("artifact-inner-planned", "Prepared")]
    [DataRow("artifact-inner-planned-legacy", "Prepared")]
    [DataRow("artifact-inner-planned-legacy-upgraded", "Prepared")]
    [DataRow("artifact", "ArtifactCommitting")]
    [DataRow("artifact-partial", "ArtifactCommitting")]
    [DataRow("artifact", "ConfigurationCommitted")]
    [DataRow("artifact", "Completed")]
    [DataRow("configuration", "Prepared")]
    [DataRow("configuration", "ConfigurationCommitting")]
    [DataRow("configuration-partial", "ConfigurationCommitting")]
    [DataRow("configuration", "ConfigurationCommitted")]
    [DataRow("configuration", "Completed")]
    [DataRow("rollback", "RollingBack")]
    [DataRow("rollback-partial", "RollingBack")]
    [DataRow("rollback", "RolledBack")]
    [DataRow("recovery-required", "RecoveryRequired")]
    public async Task HardCrashAtEverySwitchBoundaryRecoversExactState(
        string crashMode,
        string crashStage)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new TemporaryDirectory();
        var readyPath = Path.Combine(directory.Path, "ready");
        using var child = StartCrashProbe(crashMode, crashStage, directory.Path, readyPath);
        try
        {
            var stateDirectory = Path.Combine(directory.Path, "state");
            await WaitForCrashProbeAsync(
                child,
                readyPath,
                stateDirectory,
                crashMode,
                crashStage);
            await using var competingProviderLease = await new LauncherOperationLock(
                    Path.Combine(stateDirectory, "provider-switch"))
                .TryAcquireAsync();
            await using var competingRootLease = await new LauncherOperationLock(stateDirectory)
                .TryAcquireAsync();
            Assert.IsNull(
                competingProviderLease,
                $"Switch stage '{crashMode}/{crashStage}' released its provider-switch lease early.");
            Assert.IsNull(
                competingRootLease,
                $"Switch stage '{crashMode}/{crashStage}' released its root mutation lease early.");
            var liveConfigurationPath = Path.Combine(
                directory.Path,
                "game",
                "community_patch_settings.toml");
            var liveDllPath = Path.Combine(directory.Path, "game", "version.dll");
            var liveSelectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
            if (crashMode == "rollback")
            {
                var rollbackStillPending = crashStage == "RollingBack";
                CollectionAssert.AreEqual(
                    rollbackStillPending
                        ? Encoding.UTF8.GetBytes("# netniv\n[graphics]\nfree_resize = false\n")
                        : Encoding.UTF8.GetBytes(
                            "# guffawaffle\r\n[graphics]\r\nfree_resize = true\r\n"),
                    await File.ReadAllBytesAsync(liveConfigurationPath));
                Assert.AreEqual(
                    rollbackStillPending
                        ? new LauncherProviderSelection("netniv", "stable")
                        : new LauncherProviderSelection("guffawaffle", "stable"),
                    liveSelectionStore.Load());
                CollectionAssert.AreEqual(
                    GuffawaffleArtifact,
                    await ReadAllBytesSharedAsync(liveDllPath));
            }
            else if (crashMode == "artifact-partial")
            {
                CollectionAssert.AreEqual(NetnivArtifact, await ReadAllBytesSharedAsync(liveDllPath));
                CollectionAssert.AreEqual(
                    Encoding.UTF8.GetBytes(
                        "# guffawaffle\r\n[graphics]\r\nfree_resize = true\r\n"),
                    await File.ReadAllBytesAsync(liveConfigurationPath));
                Assert.AreEqual(
                    new LauncherProviderSelection("guffawaffle", "stable"),
                    liveSelectionStore.Load());
            }
            else if (crashMode == "configuration-partial")
            {
                Assert.IsFalse(File.Exists(liveDllPath));
                CollectionAssert.AreEqual(
                    Encoding.UTF8.GetBytes("# netniv\n[graphics]\nfree_resize = false\n"),
                    await File.ReadAllBytesAsync(liveConfigurationPath));
                Assert.AreEqual(
                    new LauncherProviderSelection("guffawaffle", "stable"),
                    liveSelectionStore.Load());
            }
            else if (crashMode is "rollback-partial" or "recovery-required")
            {
                CollectionAssert.AreEqual(
                    Encoding.UTF8.GetBytes(
                        "# guffawaffle\r\n[graphics]\r\nfree_resize = true\r\n"),
                    await File.ReadAllBytesAsync(liveConfigurationPath));
                Assert.AreEqual(
                    new LauncherProviderSelection("netniv", "stable"),
                    liveSelectionStore.Load());
                CollectionAssert.AreEqual(
                    GuffawaffleArtifact,
                    await ReadAllBytesSharedAsync(liveDllPath));
            }
            await TerminateCrashProbeAsync(child, stateDirectory);
            if (crashMode.StartsWith(
                    "artifact-inner-planned-legacy",
                    StringComparison.Ordinal))
            {
                var outerJournalPath = Path.Combine(
                    stateDirectory,
                    "provider-switch-journal.json");
                var outerJournal = JsonSerializer.Deserialize<LauncherProviderAtomicSwitchJournal>(
                    File.ReadAllText(outerJournalPath),
                    JsonOptions)
                    ?? throw new AssertFailedException(
                        "The interrupted provider-switch journal was absent.");
                WriteJson(outerJournalPath, outerJournal with { SchemaVersion = 1 });
                var innerJournalPath = Path.Combine(stateDirectory, "deployment-journal.json");
                var innerJournal = JsonNode.Parse(File.ReadAllText(innerJournalPath))!.AsObject();
                innerJournal["schemaVersion"] = 1;
                innerJournal.Remove("hasCommitParticipant");
                innerJournal.Remove("commitParticipantCompleted");
                innerJournal.Remove("targetInstallationAttribution");
                File.WriteAllText(innerJournalPath, innerJournal.ToJsonString(JsonOptions));
            }

            var crashLeftFiles = CaptureFiles(directory.Path);
            var fixture = await CreateFixtureAsync(
                directory.Path,
                installSource: !crashMode.StartsWith("configuration", StringComparison.Ordinal),
                initializeFixture: false);
            AssertFilesEqual(crashLeftFiles, CaptureFiles(directory.Path));
            Assert.AreEqual(
                Enum.Parse<LauncherProviderAtomicSwitchPhase>(crashStage),
                fixture.Coordinator.ReadJournal()!.Phase);
            if (crashMode == "artifact-inner-planned-legacy-upgraded")
            {
                Assert.AreEqual(
                    CoordinatedRecoveryDependencyDisposition.RollBackParticipant,
                    fixture.TargetDeployment.PrepareCoordinatedRecoveryDependency(
                        fixture.Coordinator.ReadJournal()!.TransactionId,
                        fixture.GameDirectory,
                        fixture.TargetArtifact,
                        outerSchemaVersion: 1,
                        outerPrepared: true));
                Assert.AreEqual(
                    LauncherProviderAtomicSwitchPhase.Prepared,
                    fixture.Coordinator.ReadJournal()!.Phase);
                var upgradedFiles = CaptureFiles(directory.Path);
                fixture = await CreateFixtureAsync(
                    directory.Path,
                    installSource: true,
                    initializeFixture: false);
                AssertFilesEqual(upgradedFiles, CaptureFiles(directory.Path));
            }

            var recovery = await fixture.Coordinator.RecoverAsync();
            var completed = crashStage == "Completed";
            var terminalRollback = crashStage == "RolledBack";
            Assert.IsTrue(recovery.IsSuccess, recovery.Message);
            Assert.AreEqual(!completed && !terminalRollback, recovery.Changed, recovery.Message);
            Assert.AreEqual(
                completed
                    ? LauncherProviderAtomicSwitchPhase.Completed
                    : LauncherProviderAtomicSwitchPhase.RolledBack,
                fixture.Coordinator.ReadJournal()!.Phase);

            var targetCommitted = completed && crashMode != "rollback";
            CollectionAssert.AreEqual(
                targetCommitted ? fixture.NetnivConfiguration : fixture.GuffawaffleConfiguration,
                await File.ReadAllBytesAsync(fixture.ConfigurationPath));
            Assert.AreEqual(
                targetCommitted
                    ? new LauncherProviderSelection("netniv", "stable")
                    : new LauncherProviderSelection("guffawaffle", "stable"),
                fixture.SelectionStore.Load());
            var dllPath = Path.Combine(fixture.GameDirectory, "version.dll");
            if (crashMode.StartsWith("configuration", StringComparison.Ordinal))
            {
                Assert.IsFalse(File.Exists(dllPath));
                Assert.IsNull(fixture.TargetDeployment.ReadInstalledState());
            }
            else
            {
                CollectionAssert.AreEqual(
                    targetCommitted ? NetnivArtifact : GuffawaffleArtifact,
                    await ReadAllBytesSharedAsync(dllPath));
                var installedState = fixture.TargetDeployment.ReadInstalledState();
                Assert.IsNotNull(installedState);
                Assert.AreEqual(targetCommitted ? "netniv" : "guffawaffle", installedState.ProviderId);
                Assert.AreEqual(
                    targetCommitted ? fixture.TargetArtifact.Sha256 : fixture.SourceArtifact.Sha256,
                    installedState.Sha256);
                if (targetCommitted)
                {
                    Assert.AreEqual(
                        ModDeploymentPhase.CleanupPending,
                        fixture.TargetDeployment.ReadJournal()!.Phase);
                    var deploymentRecovery = await fixture.TargetDeployment.RecoverAsync();
                    Assert.IsTrue(deploymentRecovery.IsSuccess, deploymentRecovery.Message);
                    Assert.AreEqual(
                        ModDeploymentPhase.Committed,
                        fixture.TargetDeployment.ReadJournal()!.Phase);
                    CollectionAssert.AreEqual(
                        NetnivArtifact,
                        await ReadAllBytesSharedAsync(dllPath));
                    Assert.AreEqual(
                        new LauncherProviderSelection("netniv", "stable"),
                        fixture.SelectionStore.Load());
                }
                else
                {
                    Assert.IsFalse(
                        Directory.EnumerateFiles(fixture.GameDirectory, "*.rollback").Any());
                    if (crashMode.StartsWith("artifact-inner-planned", StringComparison.Ordinal))
                    {
                        var targetJournal = fixture.TargetDeployment.ReadJournal()!;
                        Assert.AreEqual(
                            ModDeploymentPhase.RolledBack,
                            targetJournal.Phase);
                        if (crashMode.Contains("legacy", StringComparison.Ordinal))
                        {
                            Assert.AreEqual(2, targetJournal.SchemaVersion);
                            Assert.IsTrue(targetJournal.HasCommitParticipant);
                            Assert.AreEqual(
                                fixture.TargetAttribution,
                                targetJournal.TargetInstallationAttribution);
                        }
                        var secondRecovery = await fixture.TargetDeployment.RecoverAsync();
                        Assert.IsTrue(secondRecovery.IsSuccess, secondRecovery.Message);
                        Assert.IsFalse(secondRecovery.Changed, secondRecovery.Message);
                    }
                }
            }
        }
        finally
        {
            await TerminateCrashProbeAsync(
                child,
                Path.Combine(directory.Path, "state"));
        }
    }

    [DataTestMethod]
    [DataRow("ArtifactCommitting", false)]
    [DataRow("ConfigurationCommitted", false)]
    [DataRow("Completed", false)]
    [DataRow("ConfigurationCommitted", true)]
    [DataRow("Completed", true)]
    public async Task ChangedManagedSourceHardCrashPreservesBothBackupGenerations(
        string crashStage, bool legacyAdoption)
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var directory = new TemporaryDirectory();
        var readyPath = Path.Combine(directory.Path, "ready");
        var stateDirectory = Path.Combine(directory.Path, "state");
        var crashMode = legacyAdoption ? "changed-adopted-legacy" : "changed-adopted";
        using var child = StartCrashProbe(crashMode, crashStage, directory.Path, readyPath);
        try
        {
            await WaitForCrashProbeAsync(child, readyPath, stateDirectory, crashMode, crashStage);
            await using (var competingProviderLease = await new LauncherOperationLock(
                Path.Combine(stateDirectory, "provider-switch")).TryAcquireAsync())
            await using (var competingRootLease = await new LauncherOperationLock(stateDirectory).TryAcquireAsync())
            {
                Assert.IsNull(competingProviderLease, "The live switch must retain its provider lease.");
                Assert.IsNull(competingRootLease, "The live switch must retain its root mutation lease.");
            }
            await TerminateCrashProbeAsync(child, stateDirectory);
            var crashLeftFiles = CaptureFiles(directory.Path);
            var restarted = await CreateFixtureAsync(directory.Path, initializeFixture: false);
            AssertFilesEqual(crashLeftFiles, CaptureFiles(directory.Path));
            var outer = restarted.Coordinator.ReadJournal()!;
            var inner = restarted.TargetDeployment.ReadJournal()!;
            Assert.AreEqual(Enum.Parse<LauncherProviderAtomicSwitchPhase>(crashStage), outer.Phase);
            Assert.IsTrue(inner.AdoptChangedManagedArtifact);
            var prior = inner.PreviousInstalledState!;
            var reviewedPrior = inner.ReviewedPreviousInstalledState ?? prior;
            Assert.IsNotNull(prior.PreviousArtifactBackupIdentity);
            if (legacyAdoption)
            {
                Assert.IsNotNull(inner.ReviewedPreviousInstalledState);
                Assert.IsNull(reviewedPrior.PreviousArtifactBackupIdentity);
                Assert.AreNotEqual(JsonSerializer.Serialize(prior, JsonOptions),
                    JsonSerializer.Serialize(reviewedPrior, JsonOptions));
            }
            Assert.IsNotNull(prior.PreviousArtifactBackupPath);
            CollectionAssert.AreEqual(OriginalAdoptedArtifact,
                File.ReadAllBytes(prior.PreviousArtifactBackupPath));
            if (crashStage == "ConfigurationCommitted")
            {
                // The selected preference/TOML committed, but receipt publication
                // has not happened. Recovery must handle this exact interval.
                Assert.AreEqual(JsonSerializer.Serialize(reviewedPrior, JsonOptions),
                    JsonSerializer.Serialize(restarted.TargetDeployment.ReadInstalledState(restarted.GameDirectory), JsonOptions));
                Assert.AreEqual(0, (ReplacementTestRegistry(restarted).DetachedAdoptionBackups ?? []).Count);
                if (legacyAdoption)
                {
                    CollectionAssert.AreEqual(
                        File.ReadAllBytes(Path.Combine(directory.Path, "legacy-reviewed-registry.json")),
                        File.ReadAllBytes(restarted.TargetDeployment.InstalledStatePath));
                }
            }
            var completed = crashStage == "Completed";
            var dllPath = Path.Combine(restarted.GameDirectory, "version.dll");
            CollectionAssert.AreEqual(crashStage == "ArtifactCommitting" ? ChangedManagedArtifact : NetnivArtifact,
                File.ReadAllBytes(dllPath));
            var recovery = await restarted.Coordinator.RecoverAsync();
            Assert.IsTrue(recovery.IsSuccess, recovery.Message);
            Assert.AreEqual(!completed, recovery.Changed, recovery.Message);
            if (completed)
            {
                Assert.AreEqual(ModDeploymentPhase.CleanupPending, restarted.TargetDeployment.ReadJournal()!.Phase);
                var cleanup = await restarted.TargetDeployment.RecoverAsync();
                Assert.IsTrue(cleanup.IsSuccess, cleanup.Message);
                Assert.AreEqual(ModDeploymentPhase.Committed, restarted.TargetDeployment.ReadJournal()!.Phase);
                CollectionAssert.AreEqual(NetnivArtifact, File.ReadAllBytes(dllPath));
                CollectionAssert.AreEqual(restarted.NetnivConfiguration, File.ReadAllBytes(restarted.ConfigurationPath));
                Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), restarted.SelectionStore.Load());
                var active = restarted.TargetDeployment.ReadInstalledState(restarted.GameDirectory)!;
                Assert.AreEqual(inner.ExistingArtifactIdentity, active.PreviousArtifactBackupIdentity);
                CollectionAssert.AreEqual(ChangedManagedArtifact,
                    File.ReadAllBytes(active.PreviousArtifactBackupPath!));
                var detached = ReplacementTestRegistry(restarted).DetachedAdoptionBackups!.Single();
                Assert.AreEqual(inner.TransactionId, detached.DetachmentId);
                Assert.AreEqual(prior.PreviousArtifactBackupPath, detached.PreviousArtifactBackupPath);
                Assert.AreEqual(prior.PreviousArtifactBackupIdentity, detached.PreviousArtifactBackupIdentity);
                var uninstall = await restarted.TargetDeployment.UninstallAsync(restarted.GameDirectory);
                Assert.AreEqual(ModDeploymentResultState.Succeeded, uninstall.State, uninstall.Message);
                CollectionAssert.AreEqual(ChangedManagedArtifact, File.ReadAllBytes(dllPath));
                Assert.AreEqual(inner.ExistingArtifactIdentity, ReplacementTestIdentity(dllPath));
                Assert.IsNull(restarted.TargetDeployment.ReadInstalledState(restarted.GameDirectory));
                Assert.AreEqual(inner.TransactionId,
                    ReplacementTestRegistry(restarted).DetachedAdoptionBackups!.Single().DetachmentId);
            }
            else
            {
                CollectionAssert.AreEqual(ChangedManagedArtifact, File.ReadAllBytes(dllPath));
                Assert.AreEqual(inner.ExistingArtifactIdentity, ReplacementTestIdentity(dllPath));
                CollectionAssert.AreEqual(restarted.GuffawaffleConfiguration,
                    File.ReadAllBytes(restarted.ConfigurationPath));
                Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), restarted.SelectionStore.Load());
                Assert.AreEqual(JsonSerializer.Serialize(reviewedPrior, JsonOptions),
                    JsonSerializer.Serialize(restarted.TargetDeployment.ReadInstalledState(restarted.GameDirectory), JsonOptions));
                if (legacyAdoption)
                {
                    Assert.IsNull(restarted.TargetDeployment.ReadInstalledState(
                        restarted.GameDirectory)!.PreviousArtifactBackupIdentity);
                    CollectionAssert.AreEqual(
                        File.ReadAllBytes(Path.Combine(directory.Path, "legacy-reviewed-registry.json")),
                        File.ReadAllBytes(restarted.TargetDeployment.InstalledStatePath));
                }
                Assert.AreEqual(0, (ReplacementTestRegistry(restarted).DetachedAdoptionBackups ?? []).Count);
                Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RolledBack, restarted.Coordinator.ReadJournal()!.Phase);
                Assert.IsFalse(Directory.EnumerateFiles(restarted.GameDirectory, "*.rollback").Any());
                var secondRecovery = await restarted.Coordinator.RecoverAsync();
                Assert.IsTrue(secondRecovery.IsSuccess, secondRecovery.Message);
                Assert.IsFalse(secondRecovery.Changed, secondRecovery.Message);
            }
            CollectionAssert.AreEqual(OriginalAdoptedArtifact,
                File.ReadAllBytes(prior.PreviousArtifactBackupPath));
        }
        finally
        {
            await TerminateCrashProbeAsync(child, stateDirectory);
        }
    }

    [TestMethod]
    public async Task LauncherProviderSwitchHardCrashProbe()
    {
        var configuredMode = Environment.GetEnvironmentVariable(CrashModeEnvironment);
        var configuredStage = Environment.GetEnvironmentVariable(CrashStageEnvironment);
        if (string.IsNullOrWhiteSpace(configuredMode)
            || string.IsNullOrWhiteSpace(configuredStage))
        {
            return;
        }
        var crashStage = Enum.Parse<LauncherProviderAtomicSwitchPhase>(configuredStage);
        var root = Environment.GetEnvironmentVariable(CrashRootEnvironment)
            ?? throw new InvalidOperationException("The provider-switch crash root is absent.");
        var readyPath = Environment.GetEnvironmentVariable(CrashReadyEnvironment)
            ?? throw new InvalidOperationException("The provider-switch crash ready path is absent.");
        async ValueTask Checkpoint(
            LauncherProviderAtomicSwitchPhase current,
            CancellationToken cancellationToken)
        {
            if (configuredMode.StartsWith("artifact-inner-planned", StringComparison.Ordinal)
                || configuredMode.EndsWith("-partial", StringComparison.Ordinal)
                || current != crashStage)
            {
                return;
            }
            await File.WriteAllTextAsync(
                readyPath,
                $"{configuredMode}/{current}",
                cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        ValueTask FailAfterConfigurationCommit(
            ModDeploymentPhase current,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current == ModDeploymentPhase.CleanupPending)
            {
                throw new IOException("Injected artifact finalization failure after configuration commit.");
            }
            return ValueTask.CompletedTask;
        }
        async ValueTask HoldAfterInnerPlanned(
            ModDeploymentPhase current,
            CancellationToken cancellationToken)
        {
            if (!configuredMode.StartsWith("artifact-inner-planned", StringComparison.Ordinal)
                || current != ModDeploymentPhase.Planned)
            {
                return;
            }
            await File.WriteAllTextAsync(
                readyPath,
                $"{configuredMode}/{crashStage}",
                cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        async ValueTask HoldAfterTargetDllInstall(
            ModDeploymentFileCheckpoint current,
            CancellationToken cancellationToken)
        {
            if (configuredMode != "artifact-partial"
                || current != ModDeploymentFileCheckpoint.TargetDllInstalled)
            {
                return;
            }
            await File.WriteAllTextAsync(
                readyPath,
                $"{configuredMode}/{crashStage}",
                cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        void HoldDuringSelectionCommit()
        {
            File.WriteAllText(readyPath, $"{configuredMode}/{crashStage}");
            Thread.Sleep(Timeout.Infinite);
        }
        var selectionInterruption = configuredMode switch
        {
            "configuration-partial" => SelectionInterruption.BeforeTargetSave,
            "rollback-partial" => SelectionInterruption.BeforeSourceRollbackSave,
            "recovery-required" => SelectionInterruption.FailBeforeSourceRollbackSave,
            _ => SelectionInterruption.None,
        };
        ILauncherProviderSelectionStore? selectionStore = selectionInterruption == SelectionInterruption.None
            ? null
            : new InterruptingSelectionStore(
                Path.Combine(root, "state"),
                selectionInterruption,
                HoldDuringSelectionCommit);
        var fixture = await CreateFixtureAsync(
            root,
            selectionStore,
            installSource: !configuredMode.StartsWith("configuration", StringComparison.Ordinal)
                && !configuredMode.StartsWith("changed-adopted", StringComparison.Ordinal),
            checkpoint: Checkpoint,
            targetPhaseCheckpoint: configuredMode.StartsWith("artifact-inner-planned", StringComparison.Ordinal)
                ? HoldAfterInnerPlanned
                : configuredMode.StartsWith("rollback", StringComparison.Ordinal)
                    || configuredMode == "recovery-required"
                    ? FailAfterConfigurationCommit
                    : null,
            targetFileCheckpoint: configuredMode == "artifact-partial"
                ? HoldAfterTargetDllInstall
                : null);
        if (configuredMode.StartsWith("changed-adopted", StringComparison.Ordinal))
        {
            var source = await SeedChangedManagedSourceAsync(fixture, hasOlderAdoptionBackup: true);
            if (configuredMode == "changed-adopted-legacy")
            {
                _ = WriteLegacyReplacementReceipt(fixture, source);
                File.WriteAllBytes(Path.Combine(root, "legacy-reviewed-registry.json"),
                    File.ReadAllBytes(fixture.SourceDeployment.InstalledStatePath));
            }
        }
        var preview = await fixture.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            fixture.GameDirectory,
            isGameRunning: false,
            fixture.ConfigurationPath);
        _ = await fixture.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);
        Assert.Fail($"Provider-switch crash probe passed stage '{configuredMode}/{configuredStage}'.");
    }

    private static async Task<Fixture> CreateFixtureAsync(
        TemporaryDirectory directory,
        ILauncherProviderSelectionStore? selectionStore = null,
        bool installSource = true,
        IModArtifactDownloader? targetDownloader = null,
        bool reviewedTarget = false,
        byte[]? targetConfiguration = null,
        IWindowsReleaseDiscoveryClient? targetReleaseDiscovery = null,
        bool sourceConfigurationExists = true,
        Func<LauncherProviderSelection, LauncherConfigurationDiagnosisEvidence>?
            configurationEvidenceResolver = null)
        => await CreateFixtureAsync(
            directory.Path,
            selectionStore,
            installSource,
            targetDownloader,
            reviewedTarget,
            targetConfiguration,
            targetReleaseDiscovery,
            sourceConfigurationExists,
            configurationEvidenceResolver).ConfigureAwait(false);

    private static async Task<Fixture> CreateFixtureAsync(
        string root,
        ILauncherProviderSelectionStore? selectionStore = null,
        bool installSource = true,
        IModArtifactDownloader? targetDownloader = null,
        bool reviewedTarget = false,
        byte[]? targetConfiguration = null,
        IWindowsReleaseDiscoveryClient? targetReleaseDiscovery = null,
        bool sourceConfigurationExists = true,
        Func<LauncherProviderSelection, LauncherConfigurationDiagnosisEvidence>?
            configurationEvidenceResolver = null,
        bool initializeFixture = true,
        Func<LauncherProviderAtomicSwitchPhase, CancellationToken, ValueTask>? checkpoint = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? targetPhaseCheckpoint = null,
        Func<ModDeploymentFileCheckpoint, CancellationToken, ValueTask>? targetFileCheckpoint = null,
        NativeLauncherProfilesStore? profilesStore = null)
    {
        var gameDirectory = Path.Combine(root, "game");
        var stateDirectory = Path.Combine(root, "state");
        if (initializeFixture)
        {
            Directory.CreateDirectory(gameDirectory);
            Directory.CreateDirectory(stateDirectory);
            TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
            if (profilesStore is not null)
            {
                File.WriteAllText(Path.Combine(gameDirectory, ".version"), "&game=221");
                File.WriteAllBytes(Path.Combine(gameDirectory, "GameAssembly.dll"), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(gameDirectory, "UnityPlayer.dll"), [1, 2, 3]);
                Directory.CreateDirectory(Path.Combine(gameDirectory, "prime_Data"));
            }
        }
        else if (!Directory.Exists(gameDirectory)
            || !Directory.Exists(stateDirectory)
            || !File.Exists(Path.Combine(gameDirectory, "prime.exe")))
        {
            throw new InvalidDataException(
                "The terminated provider-switch fixture lost its state or validated game installation.");
        }
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var guffawaffleConfiguration = Encoding.UTF8.GetBytes(
            "# guffawaffle\r\n[graphics]\r\nfree_resize = true\r\n");
        var netnivConfiguration = targetConfiguration
            ?? Encoding.UTF8.GetBytes(
                "# netniv\n[graphics]\nfree_resize = false\n");
        if (initializeFixture && sourceConfigurationExists)
        {
            File.WriteAllBytes(configurationPath, guffawaffleConfiguration);
        }
        selectionStore ??= new JsonLauncherProviderSelectionStore(stateDirectory);
        if (initializeFixture)
        {
            selectionStore.Save(new("guffawaffle", "stable"));
        }
        var backupStore = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity());
        if (initializeFixture)
        {
            await backupStore.CreateAsync(new(
                gameDirectory,
                "netniv",
                configurationPath,
                netnivConfiguration,
                "test-seed"));
        }

        var sourceArtifact = Artifact(GuffawaffleArtifact, "2.1.0.8");
        var sourceCertification = GuffawaffleCertification(
            GuffawaffleArtifact,
            sourceArtifact.ExpectedVersion);
        var targetCertification = reviewedTarget
            ? Certification(NetnivArtifact, "1.1.5.1")
            : null;
        var targetArtifact = reviewedTarget
            ? Artifact(
                NetnivArtifact,
                "1.1.5.1",
                targetCertification!.DownloadUri)
            : Artifact(NetnivArtifact, "1.1.5.1");
        var sourceDeployment = Deployment(
            stateDirectory,
            GuffawaffleArtifact,
            sourceArtifact.ExpectedVersion,
            new("guffawaffle", "stable", "guffawaffle.windows"),
            reviewedCertification: sourceCertification);
        var targetDeployment = Deployment(
            stateDirectory,
            NetnivArtifact,
            targetArtifact.ExpectedVersion,
            new("netniv", "stable", "netniv.stfc-community-mod"),
            targetDownloader ?? (reviewedTarget ? new ThrowingDownloader() : null),
            targetCertification,
            targetPhaseCheckpoint,
            targetFileCheckpoint);
        if (initializeFixture && installSource)
        {
            Assert.AreEqual(
                ModDeploymentResultState.Succeeded,
                (await sourceDeployment.DeployAsync(
                    gameDirectory,
                    sourceArtifact,
                    ExistingArtifactPolicy.Reject)).State);
        }

        var sourceCoordinator = Management(
            sourceDeployment,
            sourceArtifact,
            "guffawaffle",
            "guffawaffle.windows");
        var targetCoordinator = Management(
            targetDeployment,
            targetArtifact,
            "netniv",
            "netniv.stfc-community-mod",
            targetReleaseDiscovery);
        var configurationSwitch = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            selectionStore,
            backupStore,
            null,
            configurationEvidenceResolver);
        var coordinator = new LauncherProviderAtomicSwitchCoordinator(
            configurationSwitch,
            [
                new("guffawaffle", sourceCoordinator),
                new("netniv", targetCoordinator),
            ],
            stateDirectory,
            timeProvider: null,
            checkpoint, profilesStore);
        return new(
            gameDirectory,
            stateDirectory,
            configurationPath,
            guffawaffleConfiguration,
            netnivConfiguration,
            selectionStore,
            backupStore,
            sourceDeployment,
            targetDeployment,
            coordinator,
            sourceArtifact,
            targetArtifact,
            new("netniv", "stable", "netniv.stfc-community-mod"),
            targetCertification);
    }

    private static Process StartCrashProbe(
        string crashMode,
        string crashStage,
        string root,
        string readyPath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(LauncherProviderAtomicSwitchCoordinatorTests).Assembly.Location);
        start.ArgumentList.Add(
            "--Tests:STFCCommunityMod.Launcher.Core.Tests."
            + "LauncherProviderAtomicSwitchCoordinatorTests.LauncherProviderSwitchHardCrashProbe");
        start.Environment[CrashModeEnvironment] = crashMode;
        start.Environment[CrashStageEnvironment] = crashStage;
        start.Environment[CrashRootEnvironment] = root;
        start.Environment[CrashReadyEnvironment] = readyPath;
        return Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the provider-switch crash probe.");
    }

    private static async Task WaitForCrashProbeAsync(
        Process child,
        string readyPath,
        string stateDirectory,
        string crashMode,
        string crashStage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(readyPath))
            {
                if (child.HasExited)
                {
                    var output = await child.StandardOutput.ReadToEndAsync();
                    var error = await child.StandardError.ReadToEndAsync();
                    Assert.Fail(
                        $"Provider-switch crash probe {child.Id} exited before hold point "
                        + $"'{crashMode}/{crashStage}'. Output: {output} Error: {error}");
                }
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await TerminateCrashProbeAsync(child, stateDirectory);
            var output = await child.StandardOutput.ReadToEndAsync();
            var error = await child.StandardError.ReadToEndAsync();
            Assert.Fail(
                $"Timed out after 30 seconds waiting for provider-switch crash probe {child.Id} "
                + $"at '{crashMode}/{crashStage}' to publish '{readyPath}'. "
                + $"Output: {output} Error: {error}");
        }
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var destination = new MemoryStream(checked((int)source.Length));
        await source.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static async Task TerminateCrashProbeAsync(Process child, string stateDirectory)
    {
        if (!child.HasExited)
        {
            using var killer = Process.Start(new ProcessStartInfo("taskkill")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "/PID",
                    child.Id.ToString(CultureInfo.InvariantCulture),
                    "/T",
                    "/F",
                },
            }) ?? throw new InvalidOperationException("Could not start taskkill for the crash probe.");
            await killer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (killer.ExitCode != 0 && !child.HasExited)
            {
                var output = await killer.StandardOutput.ReadToEndAsync();
                var error = await killer.StandardError.ReadToEndAsync();
                Assert.Fail(
                    $"Could not terminate provider-switch crash probe {child.Id}. "
                    + $"Output: {output} Error: {error}");
            }
        }
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await WaitForCrashLocksReleasedAsync(stateDirectory);
    }

    private static async Task WaitForCrashLocksReleasedAsync(string stateDirectory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var unavailableLocks = "provider-switch and root mutation locks";
        try
        {
            while (true)
            {
                LauncherOperationLease? providerLease = null;
                LauncherOperationLease? rootLease = null;
                try
                {
                    providerLease = await new LauncherOperationLock(
                            Path.Combine(stateDirectory, "provider-switch"))
                        .TryAcquireAsync(timeout.Token);
                    rootLease = await new LauncherOperationLock(stateDirectory)
                        .TryAcquireAsync(timeout.Token);
                    unavailableLocks = (providerLease, rootLease) switch
                    {
                        (null, null) => "provider-switch and root mutation locks",
                        (null, not null) => "provider-switch lock",
                        (not null, null) => "root mutation lock",
                        _ => string.Empty,
                    };
                    if (providerLease is not null && rootLease is not null)
                    {
                        return;
                    }
                }
                finally
                {
                    if (rootLease is not null)
                    {
                        await rootLease.DisposeAsync();
                    }
                    if (providerLease is not null)
                    {
                        await providerLease.DisposeAsync();
                    }
                }
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail(
                $"Timed out after 15 seconds waiting for the terminated crash probe to release "
                + $"the {unavailableLocks} under '{stateDirectory}'.");
        }
    }

    private static Dictionary<string, byte[]> CaptureFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

    private static void AssertFilesEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var pair in expected)
        {
            CollectionAssert.AreEqual(pair.Value, actual[pair.Key], pair.Key);
        }
    }

    private static Func<LauncherProviderSelection, LauncherConfigurationDiagnosisEvidence>
        ExactConfigurationEvidence()
    {
        var guffawaffleCatalog = LauncherConfigurationSchemaLoader.LoadFile(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "Configuration",
                "config-schema.guffawaffle.v1.json"));
        using var netnivSchema = File.OpenRead(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "Configuration",
                "configuration-schema-set.netniv.v1.json"));
        var netnivCatalog = LauncherConfigurationSchemaSetLoader.Load(
            netnivSchema,
            new(
                "netniv",
                "stable",
                "1.1.4",
                "d912611fa1eca49fc54f363bdf8377dfebf8def0"));
        return selection => selection.ProviderId switch
        {
            "guffawaffle" => LauncherConfigurationDiagnosisEvidence.Supported(
                selection.ProviderId,
                selection.ReleaseChannelId,
                guffawaffleCatalog),
            "netniv" => LauncherConfigurationDiagnosisEvidence.Supported(
                selection.ProviderId,
                selection.ReleaseChannelId,
                netnivCatalog),
            _ => LauncherConfigurationDiagnosisEvidence.Unavailable(
                selection.ProviderId,
                selection.ReleaseChannelId,
                LauncherProviderCapabilityStatus.Unknown),
        };
    }

    private static ModManagementCoordinator Management(
        ModDeploymentService deployment,
        ModReleaseArtifact artifact,
        string providerId,
        string runtimeDistributionId,
        IWindowsReleaseDiscoveryClient? releaseDiscovery = null) =>
        new(
            deployment,
            releaseDiscovery ?? new FakeReleaseDiscoveryClient(artifact),
            new Version(0, 1, 0),
            healthService: new LauncherHealthService(
                new ModInstallationInspector(
                    deployment,
                    new SystemModInstallationFileSystem()),
                new(
                    providerId,
                    "stable",
                    runtimeDistributionId,
                    CanMutate: true,
                    UnavailableReason: string.Empty)));

    private static ModDeploymentService Deployment(
        string stateDirectory,
        byte[] contents,
        string version,
        ModInstallationAttribution attribution,
        IModArtifactDownloader? downloader = null,
        ReviewedReleaseCertification? reviewedCertification = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted = null,
        Func<ModDeploymentFileCheckpoint, CancellationToken, ValueTask>? afterFileCheckpoint = null) =>
        new(
            stateDirectory,
            downloader ?? new FakeDownloader(contents),
            new FakeVersionReader(version),
            new FakeAuthenticityVerifier(),
            _ => false,
            attribution,
            timeProvider: null,
            afterPhasePersisted: afterPhasePersisted,
            reviewedCertification: reviewedCertification,
            afterFileCheckpoint: afterFileCheckpoint);

    private static ModReleaseArtifact Artifact(byte[] contents, string version, Uri? uri = null) => new(
        uri ?? new Uri("https://example.invalid/version.dll"),
        "version.dll",
        contents.LongLength,
        Convert.ToHexString(SHA256.HashData(contents)),
        version);

    private static ReviewedReleaseCertification Certification(byte[] contents, string version)
    {
        var hash = Convert.ToHexString(SHA256.HashData(contents));
        return new(
            "netniv",
            "stable",
            "netniv.stfc-community-mod",
            "NetniV/stfc-mod",
            "v1.1.5.1",
            "1.1.5.1",
            new string('1', 40),
            "version.dll",
            contents.LongLength,
            hash,
            "version.dll",
            contents.LongLength,
            hash,
            version,
            DateTimeOffset.Parse("2026-08-09T00:00:00Z", CultureInfo.InvariantCulture));
    }

    private static ReviewedReleaseCertification GuffawaffleCertification(
        byte[] contents,
        string version)
    {
        var hash = Convert.ToHexString(SHA256.HashData(contents));
        return new(
            "guffawaffle",
            "stable",
            "guffawaffle.windows",
            "Guffawaffle/stfc-mod",
            "v2.1.0-guffa.8",
            "2.1.0-guffa.8",
            new string('2', 40),
            "version.dll",
            contents.LongLength,
            hash,
            "version.dll",
            contents.LongLength,
            hash,
            version,
            DateTimeOffset.Parse("2026-08-09T00:00:00Z", CultureInfo.InvariantCulture));
    }

    private sealed record Fixture(
        string GameDirectory,
        string StateDirectory,
        string ConfigurationPath,
        byte[] GuffawaffleConfiguration,
        byte[] NetnivConfiguration,
        ILauncherProviderSelectionStore SelectionStore,
        ProviderScopedConfigurationBackupStore BackupStore,
        ModDeploymentService SourceDeployment,
        ModDeploymentService TargetDeployment,
        LauncherProviderAtomicSwitchCoordinator Coordinator,
        ModReleaseArtifact SourceArtifact,
        ModReleaseArtifact TargetArtifact,
        ModInstallationAttribution TargetAttribution,
        ReviewedReleaseCertification? TargetCertification);

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, JsonOptions));
    }

    private sealed class FakeReleaseDiscoveryClient(ModReleaseArtifact artifact)
        : IWindowsReleaseDiscoveryClient
    {
        public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
            string channel,
            Version currentLauncherVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsReleaseDiscovery(
                new(
                    1,
                    artifact.ExpectedVersion,
                    $"v{artifact.ExpectedVersion}",
                    channel,
                    "active",
                    currentLauncherVersion,
                    new("example/repository", new string('0', 40)),
                    "none",
                    []),
                artifact));
    }

    private sealed class CountingReleaseDiscoveryClient(ModReleaseArtifact artifact)
        : IWindowsReleaseDiscoveryClient
    {
        public int CallCount { get; private set; }

        public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
            string channel,
            Version currentLauncherVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new WindowsReleaseDiscovery(
                new(
                    1,
                    artifact.ExpectedVersion,
                    $"v{artifact.ExpectedVersion}",
                    channel,
                    "active",
                    currentLauncherVersion,
                    new("example/repository", new string('0', 40)),
                    "none",
                    []),
                artifact));
        }
    }

    private sealed class FakeDownloader(byte[] contents) : IModArtifactDownloader
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromResult(new ModArtifactDownload(HttpStatusCode.OK, contents, contents.LongLength));
    }

    private sealed class CountingDownloader(byte[] contents) : IModArtifactDownloader
    {
        public int CallCount { get; private set; }

        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new ModArtifactDownload(HttpStatusCode.OK, contents, contents.LongLength));
        }
    }

    private sealed class CallbackDownloader(byte[] contents, Action callback) : IModArtifactDownloader
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            callback();
            return Task.FromResult(
                new ModArtifactDownload(HttpStatusCode.OK, contents, contents.LongLength));
        }
    }

    private sealed class ThrowingDownloader : IModArtifactDownloader
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Provider transaction attempted a second download.");
    }

    private sealed class BlockingDownloader(byte[] contents) : IModArtifactDownloader
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public void Release() => released.TrySetResult();

        public async Task<ModArtifactDownload> DownloadAsync(
            Uri uri,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await released.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK, contents, contents.LongLength);
        }
    }

    private sealed class FakeVersionReader(string version) : IModArtifactVersionReader
    {
        public string? ReadVersion(string artifactPath) => version;
    }

    private sealed class FakeAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(true, "trusted test artifact");
    }

    private sealed class ReversingProtector : IConfigurationBackupProtector
    {
        public string SchemeId => "test-reverse-v1";

        public byte[] Protect(byte[] contents) => [.. contents.Reverse()];

        public byte[] Unprotect(byte[] protectedContents) => [.. protectedContents.Reverse()];
    }

    private sealed class NoOpStorageSecurity : IConfigurationBackupStorageSecurity
    {
        public void SecureDirectory(string directory) => Directory.CreateDirectory(directory);
    }

    private sealed class FailingSelectionStore : ILauncherProviderSelectionStore
    {
        private LauncherProviderSelection? selection;

        public bool FailNextSave { get; set; }

        public LauncherProviderSelection? Load() => selection;

        public void Save(LauncherProviderSelection value)
        {
            selection = value;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("Injected selection failure.");
            }
        }

        public void Clear() => selection = null;
    }

    private enum SelectionInterruption
    {
        None,
        BeforeTargetSave,
        BeforeSourceRollbackSave,
        FailBeforeSourceRollbackSave,
    }

    private sealed class InterruptingSelectionStore(
        string stateDirectory,
        SelectionInterruption interruption,
        Action hold) : ILauncherProviderSelectionStore
    {
        private readonly JsonLauncherProviderSelectionStore inner = new(stateDirectory);
        private bool targetWasSaved;

        public LauncherProviderSelection? Load() => inner.Load();

        public void Save(LauncherProviderSelection value)
        {
            if (string.Equals(value.ProviderId, "netniv", StringComparison.Ordinal))
            {
                if (interruption == SelectionInterruption.BeforeTargetSave)
                {
                    hold();
                }
                inner.Save(value);
                targetWasSaved = true;
                return;
            }
            if (targetWasSaved
                && interruption is SelectionInterruption.BeforeSourceRollbackSave
                    or SelectionInterruption.FailBeforeSourceRollbackSave)
            {
                if (interruption == SelectionInterruption.FailBeforeSourceRollbackSave)
                {
                    throw new IOException("Injected source-selection rollback failure.");
                }
                hold();
            }
            inner.Save(value);
        }

        public void Clear() => inner.Clear();
    }

}
