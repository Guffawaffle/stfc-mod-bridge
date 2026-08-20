using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProviderSwitchReviewPresentationTests
{
    [TestMethod]
    public void FirstRoutineSwitchShowsCompactIntroductionWithoutSpeculativeConcerns()
    {
        var presentation = ProviderSwitchReviewPresentation.From(
            Preview(LauncherProviderCompatibilityKind.Unknown, "internal.capability is unknown"),
            "Stable",
            introductoryReviewAcknowledged: false);

        Assert.IsTrue(presentation.RequiresReview);
        Assert.IsTrue(presentation.IsIntroductoryReview);
        Assert.IsFalse(presentation.HasFocusedWarning);
        StringAssert.Contains(presentation.Summary, "Guffawaffle → NetniV · Stable");
        StringAssert.Contains(presentation.Summary, "TOML is preserved");
        Assert.IsFalse(presentation.Summary.Contains("internal.capability", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RememberedRoutineSwitchBypassesOnlyTheIntroduction()
    {
        var presentation = ProviderSwitchReviewPresentation.From(
            Preview(LauncherProviderCompatibilityKind.Unknown, "speculative detail"),
            "Stable",
            introductoryReviewAcknowledged: true);

        Assert.IsFalse(presentation.RequiresReview);
        Assert.IsFalse(presentation.IsIntroductoryReview);
        Assert.IsFalse(presentation.HasFocusedWarning);
    }

    [TestMethod]
    public void RememberedAcknowledgementNeverSuppressesConcreteCompatibilityLoss()
    {
        var presentation = ProviderSwitchReviewPresentation.From(
            Preview(
                LauncherProviderCompatibilityKind.Loss,
                "NetniV does not support signed withdrawal evidence."),
            "Stable",
            introductoryReviewAcknowledged: true);

        Assert.IsTrue(presentation.RequiresReview);
        Assert.IsFalse(presentation.IsIntroductoryReview);
        Assert.IsTrue(presentation.HasFocusedWarning);
        StringAssert.Contains(presentation.Summary, "Warning:");
        StringAssert.Contains(presentation.Summary, "signed withdrawal evidence");
    }

    [TestMethod]
    public void PreservedUnknownTomlIsShownAsWarningWithoutClaimingLoss()
    {
        var presentation = ProviderSwitchReviewPresentation.From(
            Preview(
                LauncherProviderCompatibilityKind.Warning,
                "Two unrecognized TOML items will be preserved exactly."),
            "Stable",
            introductoryReviewAcknowledged: true);

        Assert.IsTrue(presentation.RequiresReview);
        Assert.IsFalse(presentation.IsIntroductoryReview);
        Assert.IsTrue(presentation.HasFocusedWarning);
        StringAssert.Contains(presentation.Summary, "preserved exactly");
    }

    [TestMethod]
    public void IgnoredInvalidValueIsAdvisoryAndDoesNotRequireASecondConfirmation()
    {
        var presentation = ProviderSwitchReviewPresentation.From(
            Preview(
                LauncherProviderCompatibilityKind.Compatible,
                "Supported mod runtimes ignore invalid overrides; exact bytes are preserved."),
            "Stable",
            introductoryReviewAcknowledged: true);

        Assert.IsFalse(presentation.RequiresReview);
        Assert.IsFalse(presentation.IsIntroductoryReview);
        Assert.IsFalse(presentation.HasFocusedWarning);
        Assert.IsFalse(presentation.Summary.Contains("Warning:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ManualDllSourceChangeExplainsThatTheDllWillRemainUnchanged()
    {
        var preview = Preview(LauncherProviderCompatibilityKind.Compatible, "compatible") with
        {
            SourceInstallation = new(
                ModInstallationEvidenceState.ManualInstallation,
                IsGameRunning: false),
        };

        var presentation = ProviderSwitchReviewPresentation.From(
            preview,
            "Stable",
            introductoryReviewAcknowledged: true);

        Assert.IsFalse(presentation.RequiresReview);
        StringAssert.Contains(presentation.Summary, "manual DLL will remain unchanged");
        StringAssert.Contains(presentation.Summary, "preferred source");
    }

    [TestMethod]
    public void ManagedDllReviewStatesReleaseAndGameClosedBoundary()
    {
        var preview = Preview(LauncherProviderCompatibilityKind.Compatible, "compatible") with
        {
            Artifact = new(
                ModOperationPreparationState.Ready,
                "Ready",
                "game",
                "1.1.4",
                new(
                    new Uri("https://example.invalid/version.dll"),
                    "version.dll",
                    42,
                    new('A', 64),
                    "1.1.4.0"),
                ExistingArtifactPolicy.Reject,
                ModManagementActionKind.CheckForUpdate,
                "netniv"),
        };

        var presentation = ProviderSwitchReviewPresentation.From(
            preview,
            "Stable",
            introductoryReviewAcknowledged: false);

        StringAssert.Contains(presentation.Summary, "release 1.1.4");
        StringAssert.Contains(presentation.Summary, "STFC must remain closed");
    }

    [TestMethod]
    public void BlockedArtifactSwitchPresentsTheAdmissionFailureWithoutInvitingReview()
    {
        const string blockedMessage =
            "The selected signed release is older than this installation's retained release floor.";
        var preview = Preview(LauncherProviderCompatibilityKind.Compatible, "compatible") with
        {
            Artifact = new(
                ModOperationPreparationState.MutationBlocked,
                blockedMessage,
                "game",
                "2.1.0-guffa.9",
                new(
                    new Uri("https://example.invalid/version.dll"),
                    "version.dll",
                    42,
                    new('A', 64),
                    "2.1.0.0",
                    ExpectedProductVersion: "v2.1.0-guffa.9"),
                ExistingArtifactPolicy.Reject,
                ModManagementActionKind.CheckForUpdate,
                "guffawaffle"),
        };

        var presentation = ProviderSwitchReviewPresentation.From(
            preview,
            "Stable",
            introductoryReviewAcknowledged: false);

        Assert.IsTrue(presentation.IsBlocked);
        Assert.IsFalse(presentation.RequiresReview);
        StringAssert.Contains(presentation.Summary, blockedMessage);
        Assert.IsFalse(presentation.Summary.Contains("will change", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExpectedMissingTomlExplainsAbsenceRecheck()
    {
        var preview = Preview(LauncherProviderCompatibilityKind.Compatible, "compatible") with
        {
            Configuration = Preview(
                LauncherProviderCompatibilityKind.Compatible,
                "compatible").Configuration with
            {
                ConfigurationKind = LauncherProviderSwitchConfigurationKind.None,
                ConfigurationSha256 = null,
                ConfigurationExisted = false,
            },
        };

        var presentation = ProviderSwitchReviewPresentation.From(
            preview,
            "Stable",
            introductoryReviewAcknowledged: false);

        StringAssert.Contains(presentation.Summary, "No TOML exists now");
        StringAssert.Contains(presentation.Summary, "community_patch_settings.toml");
        StringAssert.Contains(presentation.Summary, "recheck that exact path");
    }

    private static LauncherProviderAtomicSwitchPreview Preview(
        LauncherProviderCompatibilityKind concernKind,
        string concernMessage) =>
        new(
            new LauncherProviderSwitchPreview(
                "transaction",
                LauncherProviderSelectionResolutionState.Selected,
                new("guffawaffle", "stable"),
                new("netniv", "stable"),
                "Guffawaffle",
                "NetniV",
                [new("internal.capability", concernKind, concernMessage)],
                "community_patch_settings.toml",
                new('A', 64),
                LauncherProviderSwitchConfigurationKind.PreserveCurrent,
                TargetConfigurationBackupId: null,
                TargetConfigurationSha256: null,
                ConfirmationText: "netniv"),
            Artifact: null,
            new(ModInstallationEvidenceState.NotInstalled, IsGameRunning: false));
}
