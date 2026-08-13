namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ProviderAwareModManagementCoordinatorTests
{
    private const string Sha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [TestMethod]
    public void ExactKnownHashRoutesHealthToDetectedProvider()
    {
        var known = new KnownModArtifactIdentity(
            "netniv",
            "netniv.stfc-community-mod",
            "stable",
            "1.1.4",
            42,
            Sha256,
            "github-release:v1.1.4",
            DateTimeOffset.UnixEpoch);
        var installation = Manual(new(
            ModBinaryProvenanceState.KnownProviderArtifact,
            Sha256,
            42,
            "1.1.4.0",
            "1.1.4.0",
            KnownArtifact: known));
        var selected = new FakeCoordinator("guffawaffle", Snapshot(installation, "selected"));
        var netniv = new FakeCoordinator("netniv", Snapshot(installation, "detected-netniv"));
        var router = Router(selected, netniv);

        var result = router.CaptureHealth("game", false);

        Assert.AreEqual("detected-netniv", result.ModManagement.Status);
        Assert.AreEqual(1, selected.CaptureCount);
        Assert.AreEqual(0, netniv.CaptureCount);
        Assert.AreEqual(1, netniv.ResolveCount);
    }

    [TestMethod]
    public async Task ExplicitCheckAndPreparedExecutionStayBoundToDetectedProvider()
    {
        var identity = new ModBuildIdentity(
            1,
            "netniv.stfc-community-mod",
            "git:abc",
            "abc",
            "github:1:1:windows",
            "release",
            "ci");
        var installation = Manual(new(
            ModBinaryProvenanceState.SelfDeclaredLineage,
            Sha256,
            42,
            "1.1.5.1",
            "1.1.5.1",
            BuildIdentity: identity));
        var selected = new FakeCoordinator("guffawaffle", Snapshot(installation, "selected"));
        var netniv = new FakeCoordinator("netniv", Snapshot(installation, "detected-netniv"))
        {
            Preparation = Preparation("netniv"),
        };
        var router = Router(selected, netniv);

        var preparation = await router.PrepareLatestAsync("game", false);
        var result = await router.ExecuteAsync(preparation);

        Assert.AreEqual("netniv", preparation.ProviderId);
        Assert.AreEqual(1, selected.CaptureCount);
        Assert.AreEqual(0, netniv.CaptureCount);
        Assert.AreEqual(1, netniv.PrepareCount);
        Assert.AreEqual(1, netniv.ExecuteCount);
        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public void UnrecognizedCustomBuildUsesExplicitlySelectedSource()
    {
        var installation = Manual(new(
            ModBinaryProvenanceState.CustomUnattributed,
            Sha256,
            42,
            "9.9.9.9",
            "custom"));
        var selected = new FakeCoordinator("guffawaffle", Snapshot(installation, "selected-guffawaffle"));
        var netniv = new FakeCoordinator("netniv", Snapshot(installation, "netniv"));
        var router = Router(selected, netniv);

        var result = router.CaptureHealth("game", false);

        Assert.AreEqual("selected-guffawaffle", result.ModManagement.Status);
        Assert.AreEqual(0, netniv.CaptureCount);
    }

    private static ProviderAwareModManagementCoordinator Router(
        FakeCoordinator guffawaffle,
        FakeCoordinator netniv) => new(
            "guffawaffle",
            [
                new("guffawaffle", "guffawaffle.stfc-community-mod", guffawaffle),
                new("netniv", "netniv.stfc-community-mod", netniv),
            ]);

    private static ModInstallationEvidence Manual(ModBinaryProvenance provenance) => new(
        ModInstallationEvidenceState.ManualInstallation,
        false,
        provenance.FileVersion,
        InstalledSha256: provenance.Sha256,
        BinaryProvenance: provenance);

    private static LauncherHealthSnapshot Snapshot(ModInstallationEvidence installation, string status) => new(
        installation,
        LauncherProviderCompatibilityState.Unattributed,
        ModUpdateEvidenceState.Unknown,
        LauncherNativeEvidenceState.Unknown,
        LauncherNativeEvidenceState.NotApplicable,
        LauncherNativeEvidenceState.NotApplicable,
        [],
        new(status, LauncherHomeTone.Neutral, "Check for updates", ModManagementActionKind.UpdateManualInstallation, true, status));

    private static ModOperationPreparation Preparation(string providerId) => new(
        ModOperationPreparationState.Ready,
        "Ready",
        "game",
        "1.2.3",
        new(new Uri("https://example.invalid/version.dll"), "version.dll", 42, Sha256, "1.2.3.0"),
        ExistingArtifactPolicy.AdoptAndPreserve,
        ModManagementActionKind.UpdateManualInstallation,
        providerId);

    private sealed class FakeCoordinator(string providerId, LauncherHealthSnapshot snapshot) : IModManagementCoordinator
    {
        public string ProviderId { get; } = providerId;

        public int CaptureCount { get; private set; }

        public int PrepareCount { get; private set; }

        public int ResolveCount { get; private set; }

        public int ExecuteCount { get; private set; }

        public ModOperationPreparation Preparation { get; set; } = Preparation("guffawaffle");

        public LauncherHealthSnapshot CaptureHealth(string? gameDirectory, bool isGameRunning)
        {
            CaptureCount++;
            return snapshot;
        }

        public LauncherHealthSnapshot ResolveHealth(ModInstallationEvidence installation)
        {
            ResolveCount++;
            return snapshot with { Installation = installation };
        }

        public ModManagementPresentation CapturePresentation(string? gameDirectory, bool isGameRunning) =>
            CaptureHealth(gameDirectory, isGameRunning).ModManagement;

        public Task<ModOperationPreparation> PrepareLatestAsync(
            string gameDirectory,
            bool isGameRunning,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            return Task.FromResult(Preparation);
        }

        public Task<ModOperationPreparation> PrepareLatestFromEvidenceAsync(
            string gameDirectory,
            bool isGameRunning,
            ModInstallationEvidence installation,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            return Task.FromResult(Preparation);
        }

        public Task<ModDeploymentResult> ExecuteAsync(
            ModOperationPreparation preparation,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return Task.FromResult(new ModDeploymentResult(
                ModDeploymentResultState.Succeeded,
                "Succeeded"));
        }

        public Task<ModDeploymentResult> RecoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModDeploymentResult(ModDeploymentResultState.Succeeded, "Recovered"));

        public Task<ModDeploymentResult> UninstallAsync(
            string gameDirectory,
            CancellationToken cancellationToken = default)
        {
            _ = gameDirectory;
            return Task.FromResult(new ModDeploymentResult(ModDeploymentResultState.Succeeded, "Removed"));
        }

        public Task<ModDeploymentResult> StopManagingAsync(
            string gameDirectory,
            CancellationToken cancellationToken = default)
        {
            _ = gameDirectory;
            return Task.FromResult(new ModDeploymentResult(ModDeploymentResultState.Succeeded, "Detached"));
        }
    }
}
