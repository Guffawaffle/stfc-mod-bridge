using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ObservedAtomicConfigurationSwitchTests
{
    private const string ReviewedCommit = "e80a303a9949c89100b6e59b8a5e5cc2271e7144";

    [TestMethod]
    public async Task ActualAtomicSwitchKeepsSourceCatalogUntilCommitAndCanSwitchBackFromUnknownRelease()
    {
        using var fixture = await Fixture.CreateAsync();
        var guffawaffle = new LauncherProviderSelection("guffawaffle", "stable");
        var netniv = new LauncherProviderSelection("netniv", "stable");
        var coordinator = fixture.CreateCoordinator(guffawaffle);
        var preview = await coordinator.PreviewAsync(
            "netniv", "stable", fixture.Game, isGameRunning: false, fixture.Config);
        Assert.IsTrue(preview.CanExecute, preview.BlockedMessage);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Supported,
            preview.Configuration.SourceConfigurationAnalysis!.CatalogStatus);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown,
            preview.Configuration.TargetConfigurationAnalysis!.CatalogStatus);

        var forward = await coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(netniv, forward.Selection);
        Assert.AreEqual(netniv, fixture.SelectionStore.Load());
        CollectionAssert.AreEqual(fixture.NetnivBytes, File.ReadAllBytes(fixture.Dll));
        CollectionAssert.AreEqual(fixture.NetnivConfiguration, File.ReadAllBytes(fixture.Config));
        var observed = fixture.NetnivDeployment.ReadInstalledState(fixture.Game);
        Assert.IsNotNull(observed);
        Assert.AreEqual(fixture.NetnivArtifact.RepositoryRelease, observed.RepositoryRelease);
        Assert.AreEqual("netniv", observed.ProviderId);
        Assert.AreEqual("v1.1.8.0", observed.ReleaseProductVersion);
        Assert.AreEqual(ModDeploymentPhase.Committed, fixture.NetnivDeployment.ReadJournal()!.Phase);
        Assert.AreEqual(LauncherProviderAtomicSwitchPhase.Completed, coordinator.ReadJournal()!.Phase);
        Assert.AreEqual(1, fixture.NetnivDownloader.CallCount);

        // A new resolver/session must read the actual persisted observation. The
        // installed unknown release cannot borrow the historical typed catalog.
        var restartedResolver = fixture.CreateResolver();
        Assert.IsFalse(restartedResolver.ResolveCatalog(netniv, fixture.Game).IsQualified);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown,
            restartedResolver.ResolveEvidence(netniv, fixture.Game).CapabilityStatus);
        coordinator = fixture.CreateCoordinator(netniv);
        var reversePreview = await coordinator.PreviewAsync(
            "guffawaffle", "stable", fixture.Game, isGameRunning: false, fixture.Config);
        Assert.IsTrue(reversePreview.CanExecute, reversePreview.BlockedMessage);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown,
            reversePreview.Configuration.SourceConfigurationAnalysis!.CatalogStatus);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Supported,
            reversePreview.Configuration.TargetConfigurationAnalysis!.CatalogStatus);

        var reverse = await coordinator.ExecuteAsync(reversePreview, reversePreview.ConfirmationText);

        Assert.AreEqual(guffawaffle, reverse.Selection);
        Assert.AreEqual(guffawaffle, fixture.SelectionStore.Load());
        CollectionAssert.AreEqual(fixture.GuffawaffleBytes, File.ReadAllBytes(fixture.Dll));
        CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.Config));
        var restored = fixture.GuffawaffleDeployment.ReadInstalledState(fixture.Game);
        Assert.IsNotNull(restored);
        Assert.AreEqual("guffawaffle", restored.ProviderId);
        Assert.IsNull(restored.RepositoryRelease);
        Assert.AreEqual(ModDeploymentPhase.Committed, fixture.GuffawaffleDeployment.ReadJournal()!.Phase);
        Assert.AreEqual(LauncherProviderAtomicSwitchPhase.Completed, coordinator.ReadJournal()!.Phase);
        Assert.AreEqual(2, fixture.GuffawaffleDownloader.CallCount,
            "The initial deployment and reverse switch must each acquire the exact fixture artifact.");
        Assert.IsTrue(fixture.CreateResolver().ResolveCatalog(guffawaffle, fixture.Game).IsQualified);
    }

    [TestMethod]
    public async Task ConfigurationCommittedBeforePublicationCannotBorrowTargetCatalogAndCompensates()
    {
        using var fixture = await Fixture.CreateAsync();
        var guffawaffle = new LauncherProviderSelection("guffawaffle", "stable");
        var netniv = new LauncherProviderSelection("netniv", "stable");
        var registryBefore = File.ReadAllBytes(fixture.GuffawaffleDeployment.InstalledStatePath);
        var boundaryObserved = false;
        var coordinator = fixture.CreateCoordinator(guffawaffle, (phase, _) =>
        {
            if (phase != LauncherProviderAtomicSwitchPhase.ConfigurationCommitted)
                return ValueTask.CompletedTask;

            // Observe the mixed state before publication or compensation, rather
            // than inferring it from the transaction's final rollback result.
            Assert.AreEqual(netniv, fixture.SelectionStore.Load());
            CollectionAssert.AreEqual(fixture.NetnivBytes, File.ReadAllBytes(fixture.Dll));
            CollectionAssert.AreEqual(fixture.NetnivConfiguration, File.ReadAllBytes(fixture.Config));
            var sourceReceipt = fixture.GuffawaffleDeployment.ReadInstalledState(fixture.Game);
            Assert.IsNotNull(sourceReceipt);
            Assert.AreEqual("guffawaffle", sourceReceipt.ProviderId);
            Assert.IsNull(sourceReceipt.RepositoryRelease);
            CollectionAssert.AreEqual(registryBefore,
                File.ReadAllBytes(fixture.GuffawaffleDeployment.InstalledStatePath));
            var resolver = fixture.CreateResolver();
            Assert.IsTrue(resolver.ResolveCatalog(guffawaffle, fixture.Game).IsQualified,
                "Fresh source analysis must retain its matching source catalog through commit.");
            Assert.IsFalse(resolver.ResolveCatalog(netniv, fixture.Game).IsQualified);
            var evidence = resolver.ResolveEvidence(netniv, fixture.Game);
            Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown, evidence.CapabilityStatus);
            Assert.AreEqual(ConfigurationEffectiveExportState.Unavailable,
                ConfigurationEffectiveExportService.Build(new ConfigurationDocumentSnapshot(
                    fixture.Config, File.ReadAllBytes(fixture.Config)), evidence).State);
            var cleanup = MainWindow.CapturePersistedConfigurationMigrationAuthority(
                fixture.State, BundledLauncherProviderCatalog.Load(), fixture.SelectionStore);
            Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown,
                cleanup.DiagnosisEvidence.CapabilityStatus);
            boundaryObserved = true;
            throw new IOException("Injected interruption before receipt publication.");
        });
        var preview = await coordinator.PreviewAsync(
            "netniv", "stable", fixture.Game, isGameRunning: false, fixture.Config);
        Assert.IsTrue(preview.CanExecute, preview.BlockedMessage);

        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        Assert.IsTrue(boundaryObserved,
            "All publication-interval assertions must pass before injecting the failure.");
        StringAssert.Contains(failure.Message, "Injected interruption before receipt publication.");
        Assert.AreEqual(guffawaffle, fixture.SelectionStore.Load());
        CollectionAssert.AreEqual(fixture.GuffawaffleBytes, File.ReadAllBytes(fixture.Dll));
        CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.Config));
        CollectionAssert.AreEqual(registryBefore,
            File.ReadAllBytes(fixture.GuffawaffleDeployment.InstalledStatePath));
        Assert.AreEqual(ModDeploymentPhase.RolledBack, fixture.NetnivDeployment.ReadJournal()!.Phase);
        Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RolledBack, coordinator.ReadJournal()!.Phase);
        Assert.AreEqual(1, fixture.NetnivDownloader.CallCount);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Game, "*.rollback").Any());
    }

    [TestMethod]
    public async Task ChangedObservedSourceCommitRejectsStaleCatalogBeforeTargetAcquisition()
    {
        using var fixture = await Fixture.CreateAsync(initialNetniv: true, reviewedNetniv: true);
        var netniv = new LauncherProviderSelection("netniv", "stable");
        var coordinator = fixture.CreateCoordinator(netniv);
        var preview = await coordinator.PreviewAsync(
            "guffawaffle", "stable", fixture.Game, isGameRunning: false, fixture.Config);
        Assert.IsTrue(preview.CanExecute, preview.BlockedMessage);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Supported,
            preview.Configuration.SourceConfigurationAnalysis!.CatalogStatus);
        var source = fixture.NetnivDeployment.ReadInstalledState(fixture.Game)!;
        var changed = source with
        {
            RepositoryRelease = source.RepositoryRelease! with { SourceCommit = new string('b', 40) },
        };
        File.WriteAllText(fixture.NetnivDeployment.InstalledStatePath,
            JsonSerializer.Serialize(new ModInstalledArtifactRegistry(2, [changed]), Fixture.JsonOptions));
        var registryBefore = File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath);
        var deploymentJournalBefore = File.ReadAllBytes(fixture.NetnivDeployment.JournalPath);
        var configBefore = File.ReadAllBytes(fixture.Config);
        var dllBefore = File.ReadAllBytes(fixture.Dll);

        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        StringAssert.Contains(failure.Message, "source configuration catalog");
        Assert.AreEqual(0, fixture.GuffawaffleDownloader.CallCount,
            "Locked canonical preparation must reject a changed source catalog before artifact acquisition.");
        CollectionAssert.AreEqual(registryBefore, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
        CollectionAssert.AreEqual(deploymentJournalBefore, File.ReadAllBytes(fixture.NetnivDeployment.JournalPath));
        CollectionAssert.AreEqual(configBefore, File.ReadAllBytes(fixture.Config));
        CollectionAssert.AreEqual(dllBefore, File.ReadAllBytes(fixture.Dll));
        Assert.AreEqual(netniv, fixture.SelectionStore.Load());
        Assert.IsNull(coordinator.ReadJournal());
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Game, "*.rollback").Any());
    }

    [TestMethod]
    public async Task ReceiptPublicationFailureKeepsCoordinatedRecoveryPendingUntilRegistryIsWritable()
    {
        using var fixture = await Fixture.CreateAsync();
        var source = new LauncherProviderSelection("guffawaffle", "stable");
        var registryBefore = File.ReadAllBytes(fixture.GuffawaffleDeployment.InstalledStatePath);
        var receiptBefore = JsonSerializer.Serialize(
            fixture.GuffawaffleDeployment.ReadInstalledState(fixture.Game), Fixture.JsonOptions);
        FileStream? blockedRegistry = null;
        var checkpointObserved = false;
        ValueTask Checkpoint(LauncherProviderAtomicSwitchPhase phase, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (phase == LauncherProviderAtomicSwitchPhase.ConfigurationCommitted)
            {
                checkpointObserved = true;
                // Reads remain possible, but Windows cannot replace the open registry.
                // Retain this handle through both publication and compensation attempts.
                blockedRegistry = new FileStream(fixture.NetnivDeployment.InstalledStatePath,
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            return ValueTask.CompletedTask;
        }

        try
        {
            var coordinator = fixture.CreateCoordinator(source, checkpoint: Checkpoint);
            var preview = await coordinator.PreviewAsync(
                "netniv", "stable", fixture.Game, isGameRunning: false, fixture.Config);
            Assert.IsTrue(preview.CanExecute, preview.BlockedMessage);

            var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => coordinator.ExecuteAsync(preview, preview.ConfirmationText));

            Assert.IsTrue(checkpointObserved, "The test must reach configuration commit before blocking publication.");
            StringAssert.Contains(failure.Message, "requires recovery");
            Assert.AreEqual(ModDeploymentPhase.RollingBack, fixture.NetnivDeployment.ReadJournal()!.Phase);
            Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RecoveryRequired, coordinator.ReadJournal()!.Phase);
            CollectionAssert.AreEqual(registryBefore, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
            CollectionAssert.AreEqual(fixture.GuffawaffleBytes, File.ReadAllBytes(fixture.Dll));
            CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.Config));
            Assert.AreEqual(source, fixture.SelectionStore.Load());
            Assert.AreEqual(1, fixture.NetnivDownloader.CallCount);

            blockedRegistry!.Dispose();
            blockedRegistry = null;
            // Reconstruction must use the nonterminal outer and exact inner journals.
            coordinator = fixture.CreateCoordinator(source);
            var recovery = await coordinator.RecoverAsync();

            Assert.IsTrue(recovery.IsSuccess, recovery.Message);
            Assert.IsTrue(recovery.Changed, recovery.Message);
            Assert.AreEqual(ModDeploymentPhase.RolledBack, fixture.NetnivDeployment.ReadJournal()!.Phase);
            Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RolledBack, coordinator.ReadJournal()!.Phase);
            CollectionAssert.AreEqual(registryBefore, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
            Assert.AreEqual(receiptBefore, JsonSerializer.Serialize(
                fixture.NetnivDeployment.ReadInstalledState(fixture.Game), Fixture.JsonOptions));
            CollectionAssert.AreEqual(fixture.GuffawaffleBytes, File.ReadAllBytes(fixture.Dll));
            CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.Config));
            Assert.AreEqual(source, fixture.SelectionStore.Load());
            Assert.IsFalse(Directory.EnumerateFiles(fixture.Game, "*.rollback").Any());

            var registryRecovered = File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath);
            var innerRecovered = File.ReadAllBytes(fixture.NetnivDeployment.JournalPath);
            var outerRecovered = JsonSerializer.Serialize(coordinator.ReadJournal(), Fixture.JsonOptions);
            var secondRecovery = await coordinator.RecoverAsync();
            Assert.IsTrue(secondRecovery.IsSuccess, secondRecovery.Message);
            Assert.IsFalse(secondRecovery.Changed, secondRecovery.Message);
            CollectionAssert.AreEqual(registryRecovered, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
            CollectionAssert.AreEqual(innerRecovered, File.ReadAllBytes(fixture.NetnivDeployment.JournalPath));
            Assert.AreEqual(outerRecovered, JsonSerializer.Serialize(coordinator.ReadJournal(), Fixture.JsonOptions));
        }
        finally
        {
            blockedRegistry?.Dispose();
        }
    }

    [TestMethod]
    public async Task CancellationBeforeReceiptPublicationKeepsCoordinatedRecoveryPendingWhenRegistryIsBlocked()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var source = new LauncherProviderSelection("guffawaffle", "stable");
        var registryBefore = File.ReadAllBytes(fixture.GuffawaffleDeployment.InstalledStatePath);
        var receiptBefore = JsonSerializer.Serialize(
            fixture.GuffawaffleDeployment.ReadInstalledState(fixture.Game), Fixture.JsonOptions);
        FileStream? blockedRegistry = null;
        var checkpointObserved = false;
        ValueTask Checkpoint(LauncherProviderAtomicSwitchPhase phase, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (phase == LauncherProviderAtomicSwitchPhase.ConfigurationCommitted)
            {
                checkpointObserved = true;
                // Cancellation interrupts before publication. The compensating registry
                // rewrite must fail while source receipt reads still remain possible.
                blockedRegistry = new FileStream(fixture.NetnivDeployment.InstalledStatePath,
                    FileMode.Open, FileAccess.Read, FileShare.Read);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.CompletedTask;
        }

        try
        {
            var coordinator = fixture.CreateCoordinator(source, checkpoint: Checkpoint);
            var preview = await coordinator.PreviewAsync(
                "netniv", "stable", fixture.Game, isGameRunning: false, fixture.Config);
            Assert.IsTrue(preview.CanExecute, preview.BlockedMessage);

            var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => coordinator.ExecuteAsync(preview, preview.ConfirmationText, cancellation.Token));

            Assert.IsTrue(checkpointObserved, "The test must cancel after configuration commit and before publication.");
            Assert.IsTrue(cancellation.IsCancellationRequested);
            StringAssert.Contains(failure.Message, "requires recovery");
            Assert.AreEqual(ModDeploymentPhase.RollingBack, fixture.NetnivDeployment.ReadJournal()!.Phase);
            Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RecoveryRequired, coordinator.ReadJournal()!.Phase);
            CollectionAssert.AreEqual(registryBefore, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
            CollectionAssert.AreEqual(fixture.GuffawaffleBytes, File.ReadAllBytes(fixture.Dll));
            CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.Config));
            Assert.AreEqual(source, fixture.SelectionStore.Load());
            Assert.AreEqual(1, fixture.NetnivDownloader.CallCount);

            blockedRegistry!.Dispose();
            blockedRegistry = null;
            coordinator = fixture.CreateCoordinator(source);
            var recovery = await coordinator.RecoverAsync();

            Assert.IsTrue(recovery.IsSuccess, recovery.Message);
            Assert.IsTrue(recovery.Changed, recovery.Message);
            Assert.AreEqual(ModDeploymentPhase.RolledBack, fixture.NetnivDeployment.ReadJournal()!.Phase);
            Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RolledBack, coordinator.ReadJournal()!.Phase);
            CollectionAssert.AreEqual(registryBefore, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
            Assert.AreEqual(receiptBefore, JsonSerializer.Serialize(
                fixture.NetnivDeployment.ReadInstalledState(fixture.Game), Fixture.JsonOptions));
            CollectionAssert.AreEqual(fixture.GuffawaffleBytes, File.ReadAllBytes(fixture.Dll));
            CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.Config));
            Assert.AreEqual(source, fixture.SelectionStore.Load());
            Assert.IsFalse(Directory.EnumerateFiles(fixture.Game, "*.rollback").Any());

            var registryRecovered = File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath);
            var innerRecovered = File.ReadAllBytes(fixture.NetnivDeployment.JournalPath);
            var outerRecovered = JsonSerializer.Serialize(coordinator.ReadJournal(), Fixture.JsonOptions);
            var secondRecovery = await coordinator.RecoverAsync();
            Assert.IsTrue(secondRecovery.IsSuccess, secondRecovery.Message);
            Assert.IsFalse(secondRecovery.Changed, secondRecovery.Message);
            CollectionAssert.AreEqual(registryRecovered, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
            CollectionAssert.AreEqual(innerRecovered, File.ReadAllBytes(fixture.NetnivDeployment.JournalPath));
            Assert.AreEqual(outerRecovered, JsonSerializer.Serialize(coordinator.ReadJournal(), Fixture.JsonOptions));
        }
        finally
        {
            blockedRegistry?.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreparedOuterJournalRecoversMatchingRollbackAfterPublicationIsBlocked(bool blockRegistry)
    {
        using var fixture = await Fixture.CreateAsync();
        var source = new LauncherProviderSelection("guffawaffle", "stable");
        var registryBefore = File.ReadAllBytes(fixture.GuffawaffleDeployment.InstalledStatePath);
        FileStream? blockedOuterJournal = null;
        FileStream? blockedRegistry = null;
        var checkpointObserved = false;
        ValueTask Checkpoint(LauncherProviderAtomicSwitchPhase phase, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (phase == LauncherProviderAtomicSwitchPhase.Prepared)
            {
                checkpointObserved = true;
                blockedOuterJournal = new FileStream(Path.Combine(fixture.State, "provider-switch-journal.json"),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
                if (blockRegistry)
                    blockedRegistry = new FileStream(fixture.NetnivDeployment.InstalledStatePath,
                        FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            return ValueTask.CompletedTask;
        }

        try
        {
            var coordinator = fixture.CreateCoordinator(source, checkpoint: Checkpoint);
            var preview = await coordinator.PreviewAsync(
                "netniv", "stable", fixture.Game, isGameRunning: false, fixture.Config);
            Assert.IsTrue(preview.CanExecute, preview.BlockedMessage);

            await Assert.ThrowsExceptionAsync<IOException>(
                () => coordinator.ExecuteAsync(preview, preview.ConfirmationText));

            Assert.IsTrue(checkpointObserved);
            Assert.AreEqual(LauncherProviderAtomicSwitchPhase.Prepared, coordinator.ReadJournal()!.Phase);
            Assert.AreEqual(blockRegistry ? ModDeploymentPhase.RollingBack : ModDeploymentPhase.RolledBack,
                fixture.NetnivDeployment.ReadJournal()!.Phase);
            Assert.AreEqual(coordinator.ReadJournal()!.TransactionId,
                fixture.NetnivDeployment.ReadJournal()!.TransactionId);
            Assert.AreEqual(0, fixture.NetnivDownloader.CallCount,
                "Artifact acquisition cannot begin before its outer journal advances.");
            CollectionAssert.AreEqual(registryBefore, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
            CollectionAssert.AreEqual(fixture.GuffawaffleBytes, File.ReadAllBytes(fixture.Dll));
            CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.Config));
            Assert.AreEqual(source, fixture.SelectionStore.Load());

            blockedRegistry?.Dispose();
            blockedRegistry = null;
            blockedOuterJournal!.Dispose();
            blockedOuterJournal = null;
            coordinator = fixture.CreateCoordinator(source);
            var recovery = await coordinator.RecoverAsync();

            Assert.IsTrue(recovery.IsSuccess, recovery.Message);
            Assert.IsTrue(recovery.Changed, recovery.Message);
            Assert.AreEqual(LauncherProviderAtomicSwitchPhase.RolledBack, coordinator.ReadJournal()!.Phase);
            Assert.AreEqual(ModDeploymentPhase.RolledBack, fixture.NetnivDeployment.ReadJournal()!.Phase);
            CollectionAssert.AreEqual(registryBefore, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
            CollectionAssert.AreEqual(fixture.GuffawaffleBytes, File.ReadAllBytes(fixture.Dll));
            CollectionAssert.AreEqual(fixture.GuffawaffleConfiguration, File.ReadAllBytes(fixture.Config));
            Assert.AreEqual(source, fixture.SelectionStore.Load());
            Assert.IsFalse(Directory.EnumerateFiles(fixture.Game, "*.rollback").Any());

            var recoveredRegistry = File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath);
            var recoveredInner = File.ReadAllBytes(fixture.NetnivDeployment.JournalPath);
            var recoveredOuter = File.ReadAllBytes(Path.Combine(fixture.State, "provider-switch-journal.json"));
            var secondRecovery = await coordinator.RecoverAsync();
            Assert.IsTrue(secondRecovery.IsSuccess, secondRecovery.Message);
            Assert.IsFalse(secondRecovery.Changed, secondRecovery.Message);
            CollectionAssert.AreEqual(recoveredRegistry, File.ReadAllBytes(fixture.NetnivDeployment.InstalledStatePath));
            CollectionAssert.AreEqual(recoveredInner, File.ReadAllBytes(fixture.NetnivDeployment.JournalPath));
            CollectionAssert.AreEqual(recoveredOuter, File.ReadAllBytes(Path.Combine(fixture.State, "provider-switch-journal.json")));
        }
        finally
        {
            blockedRegistry?.Dispose();
            blockedOuterJournal?.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly string root = Path.Combine(Path.GetTempPath(), "observed-atomic-switch-" + Guid.NewGuid().ToString("N"));
        private readonly ProviderScopedConfigurationBackupStore backupStore;
        private readonly LauncherDistributionProviderCatalog providers = BundledLauncherProviderCatalog.Load();
        private readonly ReviewedReleaseCertificationCatalog reviewedReleases;
        public string State { get; }
        public string Game { get; }
        public string Config { get; }
        public string Dll => Path.Combine(Game, "version.dll");
        public byte[] GuffawaffleBytes { get; } = Encoding.UTF8.GetBytes("2.1.0.8");
        public byte[] NetnivBytes { get; }
        public byte[] GuffawaffleConfiguration { get; } = Encoding.UTF8.GetBytes(
            "# Guffawaffle original\r\n[graphics]\r\nfree_resize = true\r\n");
        public byte[] NetnivConfiguration { get; } = Encoding.UTF8.GetBytes(
            "# NetniV protected history\n[graphics]\nfree_resize = false\n");
        public JsonLauncherProviderSelectionStore SelectionStore { get; }
        public ModReleaseArtifact GuffawaffleArtifact { get; }
        public ModReleaseArtifact NetnivArtifact { get; }
        public CountingDownloader GuffawaffleDownloader { get; }
        public CountingDownloader NetnivDownloader { get; }
        public ModDeploymentService GuffawaffleDeployment { get; }
        public ModDeploymentService NetnivDeployment { get; }

        private Fixture(bool reviewedNetniv)
        {
            State = Path.Combine(root, "state");
            Game = Path.Combine(root, "game");
            Config = Path.Combine(Game, "community_patch_settings.toml");
            Directory.CreateDirectory(State);
            Directory.CreateDirectory(Game);
            File.WriteAllBytes(Path.Combine(Game, "prime.exe"), [1]);
            File.WriteAllBytes(Config, GuffawaffleConfiguration);
            reviewedReleases = BundledLauncherProviderCatalog.LoadReviewedWindowsReleases(providers);
            SelectionStore = new(State);
            new JsonGameInstallSelectionStore(State).Save(Game);
            backupStore = new(State, new FixtureProtector(), new FixtureStorageSecurity());
            var netnivVersion = reviewedNetniv ? "1.1.6.0" : "1.1.8.0";
            NetnivBytes = Encoding.UTF8.GetBytes(netnivVersion);
            var observation = new NetnivRepositoryReleaseObservation(
                1, 2, "v" + netnivVersion, reviewedNetniv ? ReviewedCommit : new string('a', 40),
                new("https://github.com/netniV/stfc-mod/releases/download/v" + netnivVersion + "/stfc-community-mod.zip"),
                200, new string('c', 64), NetnivBytes.LongLength, Hash(NetnivBytes), netnivVersion, DateTimeOffset.UtcNow);
            GuffawaffleArtifact = new(new("https://example.invalid/guffawaffle/version.dll"),
                "version.dll", GuffawaffleBytes.LongLength, Hash(GuffawaffleBytes), "2.1.0.8",
                ExpectedProductVersion: "v2.1.0-guffa.8");
            NetnivArtifact = new(observation.DownloadUri, "version.dll", NetnivBytes.LongLength,
                observation.PayloadSha256, netnivVersion, RepositoryRelease: observation);
            GuffawaffleDownloader = new(GuffawaffleBytes);
            NetnivDownloader = new(NetnivBytes);
            // These exact-byte fixture dependencies do not assert real release authenticity.
            GuffawaffleDeployment = new(State, GuffawaffleDownloader,
                new FixtureVersionReader("2.1.0.8", "v2.1.0-guffa.8"), new FixtureAuthenticityVerifier(),
                _ => false, new("guffawaffle", "stable", "guffawaffle.stfc-community-mod"));
            NetnivDeployment = new(State, NetnivDownloader,
                new FixtureVersionReader(netnivVersion), new FixtureAuthenticityVerifier(),
                _ => false, new("netniv", "stable", "netniv.stfc-community-mod"));
        }

        public static async Task<Fixture> CreateAsync(bool initialNetniv = false, bool reviewedNetniv = false)
        {
            var fixture = new Fixture(reviewedNetniv);
            try
            {
                var initial = initialNetniv ? fixture.NetnivDeployment : fixture.GuffawaffleDeployment;
                var artifact = initialNetniv ? fixture.NetnivArtifact : fixture.GuffawaffleArtifact;
                var installed = await initial.DeployAsync(fixture.Game, artifact, ExistingArtifactPolicy.Reject);
                Assert.AreEqual(ModDeploymentResultState.Succeeded, installed.State, installed.Message);
                fixture.SelectionStore.Save(new(initialNetniv ? "netniv" : "guffawaffle", "stable"));
                await fixture.backupStore.CreateAsync(new(fixture.Game, "netniv", fixture.Config,
                    fixture.NetnivConfiguration, "test-seed"));
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public LauncherInstalledConfigurationResolver CreateResolver() => new(providers, reviewedReleases,
            LauncherInstalledConfigurationResolver.CreateReadOnlyStateReader(State));

        public LauncherProviderAtomicSwitchCoordinator CreateCoordinator(
            LauncherProviderSelection activeSelection,
            Func<LauncherProviderAtomicSwitchPhase, CancellationToken, ValueTask>? checkpoint = null)
        {
            var resolver = CreateResolver();
            var configurationSwitch = new LauncherProviderSourceSwitchService(providers, SelectionStore,
                backupStore, backupCompleted: null,
                configurationEvidenceResolver: selection => resolver.ResolveSwitchEvidence(
                    selection, activeSelection, Game));
            return new(configurationSwitch,
                [new("guffawaffle", Management(GuffawaffleDeployment, GuffawaffleArtifact, "guffawaffle", "guffawaffle.stfc-community-mod")),
                 new("netniv", Management(NetnivDeployment, NetnivArtifact, "netniv", "netniv.stfc-community-mod"))],
                State, timeProvider: null, checkpoint: checkpoint);
        }

        private static ModManagementCoordinator Management(ModDeploymentService deployment,
            ModReleaseArtifact artifact, string providerId, string runtimeDistributionId) => new(
                deployment, new FixtureReleaseDiscovery(artifact), new Version(0, 1, 0),
                healthService: new LauncherHealthService(new ModInstallationInspector(deployment,
                    new SystemModInstallationFileSystem()), new(providerId, "stable", runtimeDistributionId,
                        CanMutate: true, UnavailableReason: string.Empty)));

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        public void Dispose() => Directory.Delete(root, recursive: true);
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

    private sealed class FixtureVersionReader(string version, string? productVersion = null) : IModArtifactProductVersionReader
    {
        public string? ReadVersion(string artifactPath) => version;
        public string? ReadProductVersion(string artifactPath) => productVersion;
    }

    private sealed class FixtureAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(true, "Synthetic exact-byte fixture only.");
    }

    private sealed class FixtureReleaseDiscovery(ModReleaseArtifact artifact) : IWindowsReleaseDiscoveryClient
    {
        public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(string channel, Version currentLauncherVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = artifact.ExpectedProductVersion is { } productVersion
                ? productVersion[1..] : artifact.ExpectedVersion;
            return Task.FromResult(new WindowsReleaseDiscovery(new(1, version, "v" + version,
                channel, "active", currentLauncherVersion, new("example/fixture", new string('0', 40)),
                "fixture", []), artifact));
        }
    }

    private sealed class FixtureProtector : IConfigurationBackupProtector
    {
        public string SchemeId => "fixture-reverse";
        public byte[] Protect(byte[] contents) => contents.Reverse().ToArray();
        public byte[] Unprotect(byte[] protectedContents) => protectedContents.Reverse().ToArray();
    }

    private sealed class FixtureStorageSecurity : IConfigurationBackupStorageSecurity
    {
        public void SecureDirectory(string directory) => Directory.CreateDirectory(directory);
    }
}
