using System.Globalization;
using System.Net;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherFeatureRemediationTests
{
    private const string FeatureId = "battle.test";
    private const string DllSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ManifestSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SourceRevision = "cccccccccccccccccccccccccccccccccccccccc";

    [TestMethod]
    public async Task ExactCandidateProducesTypedProviderNeutralReviewAndCancelRemovesCandidate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var candidateDirectory = temporaryDirectory.CreateDirectory("candidate");
        var currentPlan = Resolve([]);
        var targetPlan = Resolve([LauncherCapabilityIds.BattleCaptureV1]);
        var lease = Lease(candidateDirectory, targetPlan);

        var review = LauncherFeatureRemediationReviewer.Review(FeatureId, currentPlan, lease);

        Assert.IsFalse(review.CurrentDecision.IsActive);
        Assert.IsTrue(review.TargetDecision.IsActive);
        Assert.IsTrue(review.WouldActivate);
        Assert.AreEqual("future-provider", review.ProviderId);
        Assert.AreEqual("stable", review.ReleaseChannelId);
        Assert.AreEqual("future.runtime", review.RuntimeDistributionId);
        Assert.AreEqual("example/mod", review.SourceRepository);
        Assert.AreEqual("v2.0.0", review.SourceTag);
        Assert.AreEqual(SourceRevision, review.SourceRevision);
        Assert.AreEqual(DllSha, review.DllSha256);
        Assert.AreEqual(ManifestSha, review.RuntimeManifestSha256);
        Assert.AreEqual(LauncherFeatureReasonCode.MissingCapability, review.CurrentDecision.EligibilityEvidence.Code);
        Assert.AreEqual(LauncherFeatureReasonCode.Active, review.TargetDecision.EligibilityEvidence.Code);
        Assert.AreEqual("tests/feature-remediation", review.CurrentCatalogSource.Id);
        Assert.AreEqual(LauncherFeaturePolicy.DefaultSource, review.CurrentPolicySource);
        Assert.AreEqual("tests/feature-remediation", review.TargetCatalogSource.Id);
        Assert.AreEqual(LauncherFeaturePolicy.DefaultSource, review.TargetPolicySource);
        StringAssert.Contains(review.ConfirmationText, SourceRevision);
        StringAssert.Contains(review.ConfirmationText, DllSha);
        StringAssert.Contains(review.ConfirmationText, ManifestSha);
        StringAssert.Contains(review.ConfirmationText, "eligibility=MissingCapability");
        StringAssert.Contains(review.ConfirmationText, "eligibility=Active");
        StringAssert.Contains(review.ConfirmationText, "Current catalog source:");
        StringAssert.Contains(review.ConfirmationText, "Target product-policy source:");
        StringAssert.Contains(review.ConfirmationText, "Player preference is applied only after");

        await review.DisposeAsync();

        Assert.IsFalse(Directory.Exists(candidateDirectory));
    }

    [TestMethod]
    public async Task ExactCandidateCanTruthfullyRemainIneligibleWithoutProviderBranching()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var candidateDirectory = temporaryDirectory.CreateDirectory("candidate");
        var currentPlan = Resolve([]);
        var targetPlan = Resolve([]);
        await using var review = LauncherFeatureRemediationReviewer.Review(
            FeatureId,
            currentPlan,
            Lease(candidateDirectory, targetPlan));

        Assert.IsFalse(review.WouldActivate);
        Assert.AreEqual(
            LauncherFeatureReasonCode.MissingCapability,
            review.TargetDecision.EligibilityEvidence.Code);
        Assert.AreEqual(
            LauncherFeatureReasonCode.Fallback,
            review.TargetDecision.SelectionEvidence.Code);
    }

    [TestMethod]
    public async Task ExactCandidateRetainsCheckedInProductPolicyDenialAndFallbackEvidence()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var candidateDirectory = temporaryDirectory.CreateDirectory("candidate");
        var currentPlan = Resolve([]);
        var policySource = new LauncherFeatureSourceIdentity("tests/remediation-policy", "7");
        var targetPlan = Resolve(
            [LauncherCapabilityIds.BattleCaptureV1],
            new LauncherFeaturePolicy(
                [new KeyValuePair<string, bool>(FeatureId, false)],
                policySource));
        await using var review = LauncherFeatureRemediationReviewer.Review(
            FeatureId,
            currentPlan,
            Lease(candidateDirectory, targetPlan));

        Assert.AreEqual(
            LauncherFeatureReasonCode.PolicyDenied,
            review.TargetDecision.EligibilityEvidence.Code);
        Assert.AreEqual(
            LauncherFeatureReasonCode.Fallback,
            review.TargetDecision.SelectionEvidence.Code);
        Assert.AreEqual(policySource, review.TargetPolicySource);
        StringAssert.Contains(review.ConfirmationText, "eligibility=PolicyDenied");
        StringAssert.Contains(review.ConfirmationText, "tests/remediation-policy@7");
    }

    [TestMethod]
    public async Task ReceiptAndActivationMismatchFailsClosed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var candidateDirectory = temporaryDirectory.CreateDirectory("candidate");
        var currentPlan = Resolve([]);
        var targetPlan = Resolve([LauncherCapabilityIds.BattleCaptureV1]);
        var lease = Lease(candidateDirectory, targetPlan, activationManifestSha: DllSha);

        Assert.ThrowsException<InvalidDataException>(() =>
            LauncherFeatureRemediationReviewer.Review(FeatureId, currentPlan, lease));

        await lease.DisposeAsync();
        Assert.IsFalse(Directory.Exists(candidateDirectory));
    }

    [TestMethod]
    public async Task InvalidReceiptDigestFailsClosedAsCandidateEvidence()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var candidateDirectory = temporaryDirectory.CreateDirectory("candidate");
        var targetPlan = Resolve([LauncherCapabilityIds.BattleCaptureV1]);
        var lease = Lease(
            candidateDirectory,
            targetPlan,
            activationManifestSha: new string('z', 64));

        Assert.ThrowsException<InvalidDataException>(() =>
            LauncherFeatureRemediationReviewer.Review(FeatureId, Resolve([]), lease));

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task ActivationPlanCannotBeDetachedFromItsReviewedRuntimeProfile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var candidateDirectory = temporaryDirectory.CreateDirectory("candidate");
        var targetPlan = Resolve([LauncherCapabilityIds.BattleCaptureV1]);
        var detachedProfile = new LauncherRuntimeProfile(
            targetPlan.Runtime.DistributionId,
            targetPlan.Runtime.RuntimeVersion,
            targetPlan.Runtime.SourceRevision,
            targetPlan.Runtime.SettingsCatalog,
            targetPlan.Runtime.Capabilities,
            targetPlan.Runtime.Evidence);
        var lease = Lease(candidateDirectory, targetPlan, activationProfile: detachedProfile);

        Assert.ThrowsException<InvalidDataException>(() =>
            LauncherFeatureRemediationReviewer.Review(FeatureId, Resolve([]), lease));

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task CandidateReleaseChannelMustMatchTheReviewedProviderTransition()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var candidateDirectory = temporaryDirectory.CreateDirectory("candidate");
        var review = LauncherFeatureRemediationReviewer.Review(
            FeatureId,
            Resolve([]),
            Lease(candidateDirectory, Resolve([LauncherCapabilityIds.BattleCaptureV1])));
        var switchConfiguration = new LauncherProviderSwitchPreview(
            Guid.NewGuid().ToString("N"),
            LauncherProviderSelectionResolutionState.Selected,
            new("current-provider", "stable"),
            new("future-provider", "preview"),
            "Current",
            "Future",
            [],
            ConfigurationPath: null,
            ConfigurationSha256: null,
            ConfigurationKind: LauncherProviderSwitchConfigurationKind.None,
            TargetConfigurationBackupId: null,
            TargetConfigurationSha256: null,
            ConfirmationText: "Confirm exact provider switch");
        var providerPreview = new LauncherProviderAtomicSwitchPreview(
            switchConfiguration,
            Artifact: null,
            new(ModInstallationEvidenceState.NotInstalled, IsGameRunning: false));

        Assert.ThrowsException<InvalidDataException>(() =>
            new LauncherFeatureRemediationPreview(providerPreview, review));

        await review.DisposeAsync();
    }

    [TestMethod]
    public async Task AlreadyActiveFeatureCannotStartRemediation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var candidateDirectory = temporaryDirectory.CreateDirectory("candidate");
        var activePlan = Resolve([LauncherCapabilityIds.BattleCaptureV1]);
        var lease = Lease(candidateDirectory, activePlan);

        Assert.ThrowsException<InvalidOperationException>(() =>
            LauncherFeatureRemediationReviewer.Review(FeatureId, activePlan, lease));

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task ExplicitCandidateRecoveryUsesTypedLocalResultWithoutNetworkOrPassiveScan()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var downloader = new RejectingDownloader();
        var certification = Certification();
        var acquirer = new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            downloader,
            new StaticVersionReader(),
            new TrustedVerifier(),
            new("future-provider", "stable", "future.runtime"),
            certification);
        await using var candidates = new LauncherFeatureRemediationCandidates(
            [new("future-provider", acquirer)]);
        var candidateRoot = Path.Combine(stateDirectory, "artifact-candidates");

        Assert.IsFalse(Directory.Exists(candidateRoot));

        var result = await candidates.RecoverAsync();

        Assert.AreEqual(ReviewedCandidateRecoveryState.Ready, result.State);
        Assert.IsTrue(result.CanAcquire);
        Assert.AreEqual(0, downloader.CallCount);
        Assert.IsFalse(Directory.Exists(candidateRoot));
    }

    private static LauncherActivationPlan Resolve(
        IEnumerable<string> capabilities,
        LauncherFeaturePolicy? policy = null)
    {
        var runtime = new LauncherRuntimeProfile(
            "future.runtime",
            new Version(2, 0, 0),
            SourceRevision,
            null,
            capabilities,
            [new("test", "exact fixture")]);
        var feature = new LauncherFeatureDefinition(
            FeatureId,
            LauncherFeatureKind.ExperimentalFeature,
            LauncherFeatureActivationMode.StartupLatched,
            new HashSet<string>([LauncherCapabilityIds.BattleCaptureV1], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            LauncherFeatureDefault.EnabledWhenEligible,
            "battle.test.active",
            "battle.test.fallback");
        return LauncherFeatureResolver.Resolve(
            runtime,
            [feature],
            policy,
            catalogSource: new("tests/feature-remediation", "1"));
    }

    private static ReviewedModArtifactCandidateLease Lease(
        string candidateDirectory,
        LauncherActivationPlan targetPlan,
        string activationManifestSha = ManifestSha,
        LauncherRuntimeProfile? activationProfile = null)
    {
        var runtimeManifest = new ModRuntimeManifestArtifact(
            new Uri("https://github.com/example/mod/releases/download/v2.0.0/stfc-runtime-manifest.json"),
            "stfc-runtime-manifest.json",
            20,
            ManifestSha,
            SourceRevision,
            "example/mod",
            "v2.0.0");
        var artifact = new ModReleaseArtifact(
            new Uri("https://github.com/example/mod/releases/download/v2.0.0/version.dll"),
            "version.dll",
            10,
            DllSha,
            "2.0.0.0",
            runtimeManifest);
        var activation = new ReviewedRuntimeActivation(
            activationManifestSha,
            activationProfile ?? targetPlan.Runtime,
            targetPlan);
        var receipt = new ReviewedModArtifactCandidateReceipt(
            Guid.NewGuid().ToString("N"),
            artifact,
            new string('d', 64),
            new(artifact.Size, artifact.Sha256),
            new(runtimeManifest.Size, runtimeManifest.Sha256),
            activation,
            new("future-provider", "stable", "future.runtime"));
        return new(
            candidateDirectory,
            Path.Combine(candidateDirectory, "version.dll"),
            Path.Combine(candidateDirectory, "stfc-runtime-manifest.json"),
            dllStream: null,
            runtimeManifestStream: null,
            ownershipStream: null,
            receipt,
            _ => true,
            afterDeploymentClaimed: null,
            candidateLifetime: null,
            afterReleased: null);
    }

    private static ReviewedReleaseCertification Certification() =>
        new(
            "future-provider",
            "stable",
            "future.runtime",
            "example/mod",
            "v2.0.0",
            "2.0.0",
            SourceRevision,
            "version.dll",
            10,
            DllSha,
            "version.dll",
            10,
            DllSha,
            "2.0.0.0",
            DateTimeOffset.Parse("2026-08-09T00:00:00Z", CultureInfo.InvariantCulture));

    private sealed class RejectingDownloader : IModArtifactDownloader
    {
        public int CallCount { get; private set; }

        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new AssertFailedException("Candidate recovery attempted a network download.");
        }
    }

    private sealed class StaticVersionReader : IModArtifactVersionReader
    {
        public string? ReadVersion(string artifactPath) => "2.0.0.0";
    }

    private sealed class TrustedVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(true, "trusted fixture");
    }
}
