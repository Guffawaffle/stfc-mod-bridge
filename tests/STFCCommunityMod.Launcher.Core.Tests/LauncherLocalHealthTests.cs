namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherLocalHealthTests
{
    private const string InstalledSha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private static readonly DateTimeOffset ObservationTime = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void InstallationInspectorDistinguishesMissingManualManagedAndDamagedArtifacts()
    {
        var gameDirectory = Path.Combine(Path.GetTempPath(), "stfc-health-game");
        var stateReader = new FakeStateReader();
        var fileSystem = new FakeInstallationFileSystem();
        var inspector = new ModInstallationInspector(stateReader, fileSystem, new FakeGameTargetInspector(true));

        Assert.AreEqual(
            ModInstallationEvidenceState.NotInstalled,
            inspector.Capture(gameDirectory, isGameRunning: false).State);

        fileSystem.ArtifactExists = true;
        var manual = inspector.Capture(gameDirectory, isGameRunning: false);
        Assert.AreEqual(ModInstallationEvidenceState.ManualInstallation, manual.State);
        Assert.AreEqual(InstalledSha256, manual.InstalledSha256);
        Assert.AreEqual(ModBinaryProvenanceState.MetadataUnavailable, manual.BinaryProvenance?.State);

        stateReader.InstalledState = InstalledState(gameDirectory);
        fileSystem.Sha256 = InstalledSha256;
        var managed = inspector.Capture(gameDirectory, isGameRunning: true);
        Assert.AreEqual(ModInstallationEvidenceState.ManagedVerified, managed.State);
        Assert.IsTrue(managed.IsGameRunning);
        Assert.AreEqual("2.1.0.8", managed.InstalledVersion);
        Assert.AreEqual("guffawaffle", managed.InstalledProviderId);
        Assert.AreEqual("stable", managed.InstalledReleaseChannelId);

        fileSystem.Sha256 = new string('F', 64);
        Assert.AreEqual(
            ModInstallationEvidenceState.ManagedChanged,
            inspector.Capture(gameDirectory, isGameRunning: false).State);
    }

    [TestMethod]
    public void InstallationInspectorFailsClosedForTargetsRecoveryAndUnreadableState()
    {
        var gameDirectory = Path.Combine(Path.GetTempPath(), "stfc-health-game");
        var stateReader = new FakeStateReader();
        var inspector = new ModInstallationInspector(
            stateReader,
            new FakeInstallationFileSystem(),
            new FakeGameTargetInspector(false));

        Assert.AreEqual(ModInstallationEvidenceState.NoGameTarget, inspector.Capture(null, false).State);
        Assert.AreEqual(ModInstallationEvidenceState.InvalidGameTarget, inspector.Capture(gameDirectory, false).State);

        inspector = new(stateReader, new FakeInstallationFileSystem(), new FakeGameTargetInspector(true));
        stateReader.Journal = IncompleteJournal(gameDirectory);
        Assert.AreEqual(ModInstallationEvidenceState.RecoveryRequired, inspector.Capture(gameDirectory, false).State);

        stateReader.Journal = null;
        stateReader.ThrowOnRead = true;
        Assert.AreEqual(ModInstallationEvidenceState.Unavailable, inspector.Capture(gameDirectory, false).State);

        stateReader.ThrowOnRead = false;
        var oversizedFileSystem = new FakeInstallationFileSystem
        {
            ArtifactExists = true,
            FileLength = 128L * 1024L * 1024L + 1,
        };
        inspector = new(stateReader, oversizedFileSystem, new FakeGameTargetInspector(true));
        Assert.AreEqual(ModInstallationEvidenceState.Unavailable, inspector.Capture(gameDirectory, false).State);
    }

    [TestMethod]
    public void ProviderCompatibilityRequiresProviderChannelAndRuntimeIdentity()
    {
        var installation = ManagedInstallation();

        var matching = LauncherHealthResolver.Resolve(installation, Provider());
        var differentChannel = LauncherHealthResolver.Resolve(
            installation,
            Provider() with { ReleaseChannelId = "preview" });

        Assert.AreEqual(LauncherProviderCompatibilityState.MatchesSelectedProvider, matching.ProviderCompatibility);
        Assert.AreEqual(LauncherProviderCompatibilityState.DifferentProvider, differentChannel.ProviderCompatibility);
        Assert.AreEqual(LauncherHomeTone.Warning, differentChannel.ModManagement.Tone);
    }

    [TestMethod]
    public void ManualKnownAndSelfDeclaredArtifactsResolveWithoutGuessing()
    {
        var known = new KnownModArtifactIdentity(
            "netniv",
            "netniv.stfc-community-mod",
            "stable",
            "1.1.4",
            42,
            InstalledSha256,
            "github-release:v1.1.4",
            ObservationTime);
        var knownInstallation = new ModInstallationEvidence(
            ModInstallationEvidenceState.ManualInstallation,
            false,
            "1.1.4.0",
            InstalledSha256: InstalledSha256,
            BinaryProvenance: new(
                ModBinaryProvenanceState.KnownProviderArtifact,
                InstalledSha256,
                42,
                "1.1.4.0",
                "1.1.4.0",
                KnownArtifact: known));
        var netniv = Provider() with
        {
            ProviderId = "netniv",
            RuntimeDistributionId = "netniv.stfc-community-mod",
        };
        var matchingKnown = LauncherHealthResolver.Resolve(knownInstallation, netniv);
        var differentKnown = LauncherHealthResolver.Resolve(knownInstallation, Provider());

        Assert.AreEqual(
            LauncherProviderCompatibilityState.MatchesSelectedProvider,
            matchingKnown.ProviderCompatibility);
        Assert.AreEqual(
            LauncherProviderCompatibilityState.DifferentProvider,
            differentKnown.ProviderCompatibility);
        Assert.AreEqual(ModManagementActionKind.UpdateManualInstallation, matchingKnown.ModManagement.ActionKind);
        Assert.AreEqual(ModManagementActionKind.None, differentKnown.ModManagement.ActionKind);

        var identity = new ModBuildIdentity(
            1,
            "guffawaffle.windows",
            "git:abc",
            "abc",
            "ax:123",
            "release",
            "local");
        var selfDeclared = knownInstallation with
        {
            BinaryProvenance = knownInstallation.BinaryProvenance! with
            {
                State = ModBinaryProvenanceState.SelfDeclaredLineage,
                BuildIdentity = identity,
                KnownArtifact = null,
            },
        };

        Assert.AreEqual(
            LauncherProviderCompatibilityState.MatchesSelectedProvider,
            LauncherHealthResolver.Resolve(selfDeclared, Provider()).ProviderCompatibility);
    }

    [TestMethod]
    public void ResolverCoversProviderAndUpdateStateTaxonomy()
    {
        var notInstalled = LauncherHealthResolver.Resolve(
            new(ModInstallationEvidenceState.NotInstalled, false),
            Provider());
        var manual = LauncherHealthResolver.Resolve(
            new(ModInstallationEvidenceState.ManualInstallation, false),
            Provider());
        var changed = LauncherHealthResolver.Resolve(
            ManagedInstallation() with { State = ModInstallationEvidenceState.ManagedChanged },
            Provider());
        var unattributed = LauncherHealthResolver.Resolve(
            ManagedInstallation() with
            {
                InstalledProviderId = null,
                InstalledReleaseChannelId = null,
                InstalledRuntimeDistributionId = null,
            },
            Provider());
        var current = LauncherHealthResolver.Resolve(
            ManagedInstallation(),
            Provider(),
            UpdateEvidence(ModUpdateEvidenceState.UpToDate),
            nowUtc: ObservationTime.AddMinutes(5));

        Assert.AreEqual(LauncherProviderCompatibilityState.NotApplicable, notInstalled.ProviderCompatibility);
        Assert.AreEqual(ModUpdateEvidenceState.NotApplicable, notInstalled.UpdateAvailability);
        Assert.AreEqual(LauncherProviderCompatibilityState.Unattributed, manual.ProviderCompatibility);
        Assert.AreEqual(ModUpdateEvidenceState.Unknown, manual.UpdateAvailability);
        Assert.AreEqual(LauncherProviderCompatibilityState.Unknown, changed.ProviderCompatibility);
        Assert.AreEqual(LauncherProviderCompatibilityState.Unknown, unattributed.ProviderCompatibility);
        Assert.AreEqual(ModUpdateEvidenceState.Unknown, unattributed.UpdateAvailability);
        Assert.AreEqual(ModUpdateEvidenceState.UpToDate, current.UpdateAvailability);
    }

    [TestMethod]
    public void LocalInstallationStatesExposeOneSafePrimaryAction()
    {
        var missing = LauncherHealthResolver.Resolve(
            new(ModInstallationEvidenceState.NotInstalled, false),
            Provider());
        var manual = LauncherHealthResolver.Resolve(
            new(ModInstallationEvidenceState.ManualInstallation, false),
            Provider());
        var damaged = LauncherHealthResolver.Resolve(
            ManagedInstallation() with { State = ModInstallationEvidenceState.ManagedChanged },
            Provider());
        var healthy = LauncherHealthResolver.Resolve(ManagedInstallation(), Provider());

        Assert.AreEqual(ModManagementActionKind.Install, missing.ModManagement.ActionKind);
        Assert.AreEqual("Not installed", missing.ModManagement.Status);
        Assert.AreEqual(ModManagementActionKind.UpdateManualInstallation, manual.ModManagement.ActionKind);
        Assert.AreEqual("Manual installation detected", manual.ModManagement.Status);
        Assert.AreEqual("Check for updates", manual.ModManagement.ActionLabel);
        Assert.AreEqual(ModManagementActionKind.Repair, damaged.ModManagement.ActionKind);
        Assert.AreEqual("Repair required", damaged.ModManagement.Status);
        Assert.AreEqual(ModManagementActionKind.CheckForUpdate, healthy.ModManagement.ActionKind);
        Assert.AreEqual(LauncherHomeTone.Success, healthy.ModManagement.Tone);
    }

    [TestMethod]
    public void UpdateObservationMustMatchIdentityArtifactAndFreshness()
    {
        var installation = ManagedInstallation();
        var update = UpdateEvidence(ModUpdateEvidenceState.UpdateAvailable);

        var available = LauncherHealthResolver.Resolve(
            installation,
            Provider(),
            update,
            nowUtc: ObservationTime.AddMinutes(5));
        var stale = LauncherHealthResolver.Resolve(
            installation,
            Provider(),
            update,
            nowUtc: ObservationTime.AddHours(1));
        var wrongArtifact = LauncherHealthResolver.Resolve(
            installation,
            Provider(),
            update with { InstalledSha256 = new string('F', 64) },
            nowUtc: ObservationTime.AddMinutes(5));

        Assert.AreEqual(ModUpdateEvidenceState.UpdateAvailable, available.UpdateAvailability);
        Assert.AreEqual("Update available", available.ModManagement.Status);
        Assert.AreEqual("Update", available.ModManagement.ActionLabel);
        Assert.AreEqual(ModUpdateEvidenceState.Unknown, stale.UpdateAvailability);
        Assert.AreEqual(ModUpdateEvidenceState.Unknown, wrongArtifact.UpdateAvailability);
    }

    [TestMethod]
    public void VerifiedOfflineInstallationRemainsHealthyWithoutProviderDiscovery()
    {
        var snapshot = LauncherHealthResolver.Resolve(
            ManagedInstallation(),
            Provider() with
            {
                CanMutate = false,
                UnavailableReason = "The provider endpoint is offline.",
            });

        Assert.AreEqual(LauncherHomeTone.Success, snapshot.ModManagement.Tone);
        Assert.AreEqual("Installed 2.1.0.8", snapshot.ModManagement.Status);
        Assert.AreEqual(ModManagementActionKind.None, snapshot.ModManagement.ActionKind);
        StringAssert.Contains(snapshot.ModManagement.AutomationName, "offline");
        Assert.IsTrue(snapshot.Dimensions.Any(
            dimension => dimension.Category == LauncherHealthDimensionCategory.ProviderAvailability));
    }

    [TestMethod]
    public void MissingNativeContractDoesNotDowngradeVerifiedInstallationWhileGameRuns()
    {
        var snapshot = LauncherHealthResolver.Resolve(ManagedInstallation(isGameRunning: true), Provider());

        Assert.AreEqual(LauncherNativeEvidenceState.Unknown, snapshot.GameCompatibility);
        Assert.AreEqual(LauncherNativeEvidenceState.Unknown, snapshot.RuntimeActivation);
        Assert.AreEqual(LauncherNativeEvidenceState.Unknown, snapshot.NativeSupport);
        Assert.AreEqual("Installed 2.1.0.8", snapshot.ModManagement.Status);
        Assert.AreEqual(LauncherHomeTone.Success, snapshot.ModManagement.Tone);
        Assert.AreEqual(ModManagementActionKind.CheckForUpdate, snapshot.ModManagement.ActionKind);
        Assert.IsTrue(snapshot.ModManagement.CanExecute, "Read-only update discovery remains safe while the game runs.");
        StringAssert.Contains(snapshot.ModManagement.AutomationName, "close STFC only before installing");
    }

    [TestMethod]
    public void ClosedGameMakesLiveRuntimeEvidenceNotApplicable()
    {
        var snapshot = LauncherHealthResolver.Resolve(
            ManagedInstallation(),
            Provider(),
            nativeHealth: new(
                LauncherNativeEvidenceState.Healthy,
                LauncherNativeEvidenceState.Healthy,
                LauncherNativeEvidenceState.Degraded));

        Assert.AreEqual(LauncherNativeEvidenceState.Healthy, snapshot.GameCompatibility);
        Assert.AreEqual(LauncherNativeEvidenceState.NotApplicable, snapshot.RuntimeActivation);
        Assert.AreEqual(LauncherNativeEvidenceState.NotApplicable, snapshot.NativeSupport);
    }

    [TestMethod]
    public void AuthoritativeNativeEvidenceDistinguishesHealthyDegradedAndIncompatible()
    {
        var installation = ManagedInstallation(isGameRunning: true);
        var healthy = LauncherHealthResolver.Resolve(
            installation,
            Provider(),
            nativeHealth: new(
                LauncherNativeEvidenceState.Healthy,
                LauncherNativeEvidenceState.Healthy,
                LauncherNativeEvidenceState.Healthy));
        var degraded = LauncherHealthResolver.Resolve(
            installation,
            Provider(),
            nativeHealth: new(
                LauncherNativeEvidenceState.Healthy,
                LauncherNativeEvidenceState.Healthy,
                LauncherNativeEvidenceState.Degraded));
        var incompatible = LauncherHealthResolver.Resolve(
            installation,
            Provider(),
            nativeHealth: new(
                LauncherNativeEvidenceState.Incompatible,
                LauncherNativeEvidenceState.Unknown,
                LauncherNativeEvidenceState.Unknown));

        Assert.AreEqual("Installed 2.1.0.8", healthy.ModManagement.Status);
        Assert.AreEqual(LauncherHomeTone.Success, healthy.ModManagement.Tone);
        Assert.AreEqual("Running degraded", degraded.ModManagement.Status);
        Assert.AreEqual(LauncherHomeTone.Warning, degraded.ModManagement.Tone);
        Assert.AreEqual("Incompatible", incompatible.ModManagement.Status);
        Assert.AreEqual(LauncherHomeTone.Error, incompatible.ModManagement.Tone);
    }

    [TestMethod]
    public void KnownUpdateDisablesMutationButPreservesVerifiedIntegrityWhileRunning()
    {
        var installation = ManagedInstallation(isGameRunning: true);
        var snapshot = LauncherHealthResolver.Resolve(
            installation,
            Provider(),
            UpdateEvidence(ModUpdateEvidenceState.UpdateAvailable),
            nowUtc: ObservationTime.AddMinutes(5));

        Assert.AreEqual("Update available", snapshot.ModManagement.Status);
        Assert.AreEqual(ModManagementActionKind.CheckForUpdate, snapshot.ModManagement.ActionKind);
        Assert.IsFalse(snapshot.ModManagement.CanExecute);
        StringAssert.Contains(snapshot.ModManagement.AutomationName, "Close Star Trek Fleet Command");
        Assert.AreEqual(
            LauncherHealthSeverity.Healthy,
            snapshot.Dimensions.Single(
                dimension => dimension.Category == LauncherHealthDimensionCategory.ModInstallation).Severity);
        Assert.AreEqual(
            LauncherHealthSeverity.Healthy,
            snapshot.Dimensions.Single(
                dimension => dimension.Category == LauncherHealthDimensionCategory.ProviderCompatibility).Severity);
    }

    [TestMethod]
    public void HealthServiceUsesInjectedFilesystemProcessAndNativeEvidence()
    {
        var gameDirectory = Path.Combine(Path.GetTempPath(), "stfc-health-game");
        var stateReader = new FakeStateReader { InstalledState = InstalledState(gameDirectory) };
        var inspector = new ModInstallationInspector(
            stateReader,
            new FakeInstallationFileSystem { ArtifactExists = true, Sha256 = InstalledSha256 },
            new FakeGameTargetInspector(true));
        var service = new LauncherHealthService(
            inspector,
            Provider(),
            new FakeNativeHealthEvidenceSource(new(
                LauncherNativeEvidenceState.Healthy,
                LauncherNativeEvidenceState.Healthy,
                LauncherNativeEvidenceState.Degraded)));

        var snapshot = service.Capture(gameDirectory, isGameRunning: true);

        Assert.AreEqual(ModInstallationEvidenceState.ManagedVerified, snapshot.Installation.State);
        Assert.AreEqual(LauncherNativeEvidenceState.Degraded, snapshot.NativeSupport);
        Assert.AreEqual("Running degraded", snapshot.ModManagement.Status);
    }

    private static LauncherProviderHealthContext Provider() => new(
        "guffawaffle",
        "stable",
        "guffawaffle.windows",
        true,
        string.Empty);

    private static ModInstallationEvidence ManagedInstallation(bool isGameRunning = false) => new(
        ModInstallationEvidenceState.ManagedVerified,
        isGameRunning,
        "2.1.0.8",
        "guffawaffle",
        "stable",
        "guffawaffle.windows",
        InstalledSha256);

    private static ModUpdateEvidence UpdateEvidence(ModUpdateEvidenceState state) => new(
        state,
        ObservationTime,
        "guffawaffle",
        "stable",
        "guffawaffle.windows",
        InstalledSha256,
        "2.2.0.0");

    private static ModInstalledArtifactState InstalledState(string gameDirectory) => new(
        1,
        Path.GetFullPath(gameDirectory),
        "version.dll",
        "2.1.0.8",
        42,
        InstalledSha256,
        ObservationTime,
        null,
        "guffawaffle",
        "stable",
        "guffawaffle.windows");

    private static ModDeploymentJournal IncompleteJournal(string gameDirectory) => new(
        1,
        "transaction",
        ModDeploymentOperation.Deploy,
        ModDeploymentPhase.Committing,
        gameDirectory,
        new(new Uri("https://example.invalid/version.dll"), "version.dll", 42, InstalledSha256, "2.1.0.8"),
        Path.Combine(gameDirectory, "version.dll.stage"),
        Path.Combine(gameDirectory, "version.dll.rollback"),
        Path.Combine(gameDirectory, "version.dll.backup"),
        false,
        null,
        ObservationTime);

    private sealed class FakeStateReader : IModDeploymentStateReader
    {
        public ModDeploymentJournal? Journal { get; set; }

        public ModInstalledArtifactState? InstalledState { get; set; }

        public bool ThrowOnRead { get; set; }

        public ModDeploymentJournal? ReadJournal()
        {
            if (ThrowOnRead)
            {
                throw new IOException("Injected state read failure.");
            }
            return Journal;
        }

        public ModInstalledArtifactState? ReadInstalledState()
        {
            if (ThrowOnRead)
            {
                throw new IOException("Injected state read failure.");
            }
            return InstalledState;
        }
    }

    private sealed class FakeInstallationFileSystem : IModInstallationFileSystem
    {
        public bool ArtifactExists { get; set; }

        public string Sha256 { get; set; } = InstalledSha256;

        public long FileLength { get; set; } = 42;

        public bool FileExists(string path) => ArtifactExists;

        public long GetFileLength(string path) => FileLength;

        public string ComputeSha256(string path) => Sha256;
    }

    private sealed class FakeGameTargetInspector(bool isValid) : IGameTargetHealthInspector
    {
        public bool IsValid(string gameDirectory) => isValid;
    }

    private sealed class FakeNativeHealthEvidenceSource(LauncherNativeHealthEvidence evidence)
        : ILauncherNativeHealthEvidenceSource
    {
        public LauncherNativeHealthEvidence Capture(ModInstallationEvidence installation) => evidence;
    }
}
