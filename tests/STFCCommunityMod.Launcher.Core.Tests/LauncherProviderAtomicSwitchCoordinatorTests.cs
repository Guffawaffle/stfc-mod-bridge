using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherProviderAtomicSwitchCoordinatorTests
{
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

        StringAssert.Contains(exception.Message, "conservative TOML parser");
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
        WriteJson(
            Path.Combine(fixture.StateDirectory, "provider-switch-journal.json"),
            new LauncherProviderAtomicSwitchJournal(
                1,
                preview.Configuration.TransactionId,
                LauncherProviderAtomicSwitchPhase.Prepared,
                preview.Configuration,
                ConfigurationBackup: null,
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
            1,
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
            DateTimeOffset.UtcNow);
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
    {
        var gameDirectory = directory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        var stateDirectory = directory.CreateDirectory("state");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var guffawaffleConfiguration = Encoding.UTF8.GetBytes(
            "# guffawaffle\r\n[graphics]\r\nfree_resize = true\r\n");
        var netnivConfiguration = targetConfiguration
            ?? Encoding.UTF8.GetBytes(
                "# netniv\n[graphics]\nfree_resize = false\n");
        if (sourceConfigurationExists)
        {
            File.WriteAllBytes(configurationPath, guffawaffleConfiguration);
        }
        selectionStore ??= new JsonLauncherProviderSelectionStore(stateDirectory);
        selectionStore.Save(new("guffawaffle", "stable"));
        var backupStore = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity());
        await backupStore.CreateAsync(new(
            gameDirectory,
            "netniv",
            configurationPath,
            netnivConfiguration,
            "test-seed"));

        var sourceArtifact = Artifact(GuffawaffleArtifact, "2.1.0.8");
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
            new("guffawaffle", "stable", "guffawaffle.windows"));
        var targetDeployment = Deployment(
            stateDirectory,
            NetnivArtifact,
            targetArtifact.ExpectedVersion,
            new("netniv", "stable", "netniv.stfc-community-mod"),
            targetDownloader ?? (reviewedTarget ? new ThrowingDownloader() : null),
            targetCertification);
        if (installSource)
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
            stateDirectory);
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
            targetArtifact,
            new("netniv", "stable", "netniv.stfc-community-mod"),
            targetCertification);
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
        ReviewedReleaseCertification? reviewedCertification = null) =>
        new(
            stateDirectory,
            downloader ?? new FakeDownloader(contents),
            new FakeVersionReader(version),
            new FakeAuthenticityVerifier(),
            _ => false,
            attribution,
            reviewedCertification: reviewedCertification);

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
}
