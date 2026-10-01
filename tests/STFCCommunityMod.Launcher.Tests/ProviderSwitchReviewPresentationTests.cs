using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProviderSwitchReviewPresentationTests
{
    [TestMethod]
    public void RememberedIntroductionCannotSuppressChangedManagedReplacementReview()
    {
        var preview = ChangedManagedPreview("2.1.0.42");
        var presentation = ProviderSwitchReviewPresentation.From(
            preview, "Stable", introductoryReviewAcknowledged: true);

        Assert.IsTrue(presentation.RequiresReview);
        Assert.IsTrue(presentation.HasFocusedWarning);
        Assert.IsFalse(presentation.IsIntroductoryReview);
        Assert.IsFalse(presentation.IsBlocked);
        StringAssert.Contains(presentation.Summary, "current custom DLL (2.1.0.42)");
        StringAssert.Contains(presentation.Summary, "release 1.1.4");
        StringAssert.Contains(presentation.Summary, "exact bytes will be preserved for restoration");
        StringAssert.Contains(presentation.Summary, "STFC must remain closed");
        Assert.IsFalse(presentation.Summary.Contains("saved-receipt-version", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ChangedManagedReplacementWithUnavailableMetadataStillRequiresReview()
    {
        var presentation = ProviderSwitchReviewPresentation.From(
            ChangedManagedPreview(liveVersion: null), "Stable", introductoryReviewAcknowledged: true);

        Assert.IsTrue(presentation.RequiresReview);
        Assert.IsTrue(presentation.HasFocusedWarning);
        StringAssert.Contains(presentation.Summary, "current custom DLL (version unknown)");
        StringAssert.Contains(presentation.Summary, "exact bytes will be preserved for restoration");
    }

    [TestMethod]
    public void StalePreferenceDoesNotReplaceActualCustomVersionWithPreferenceOrReceiptEvidence()
    {
        var preview = ChangedManagedPreview("2.1.0.42");
        // Preferred source is NetniV, while the installed receipt and current local
        // DLL remain attributed to Guffawaffle. The target returns to Guffawaffle.
        preview = preview with
        {
            Configuration = preview.Configuration with
            {
                Source = new("netniv", "stable"),
                Target = new("guffawaffle", "stable"),
                SourceDisplayName = "NetniV",
                TargetDisplayName = "Guffawaffle",
                ConfirmationText = "guffawaffle",
            },
            Artifact = preview.Artifact! with { ProviderId = "guffawaffle" },
        };
        var presentation = ProviderSwitchReviewPresentation.From(
            preview, "Stable", introductoryReviewAcknowledged: true);

        Assert.IsTrue(presentation.RequiresReview);
        Assert.IsTrue(presentation.HasFocusedWarning);
        StringAssert.Contains(presentation.Summary, "NetniV → Guffawaffle · Stable");
        StringAssert.Contains(presentation.Summary, "current custom DLL (2.1.0.42)");
        Assert.IsFalse(presentation.Summary.Contains("saved-receipt-version", StringComparison.Ordinal));
        Assert.AreEqual("guffawaffle", preview.SourceInstallation.InstalledProviderId);
    }

    [TestMethod]
    public void BlockedChangedManagedReplacementDoesNotInviteConfirmationOrClaimReplacement()
    {
        var preview = ChangedManagedPreview("2.1.0.42");
        preview = preview with
        {
            Artifact = preview.Artifact! with
            {
                State = ModOperationPreparationState.MutationBlocked,
                Message = "The target release is unavailable.",
            },
        };
        var presentation = ProviderSwitchReviewPresentation.From(
            preview, "Stable", introductoryReviewAcknowledged: true);

        Assert.IsTrue(presentation.IsBlocked);
        Assert.IsFalse(presentation.RequiresReview);
        StringAssert.Contains(presentation.Summary, "target release is unavailable");
        Assert.IsFalse(presentation.Summary.Contains("will be replaced", StringComparison.Ordinal));
    }

    private static LauncherProviderAtomicSwitchPreview ChangedManagedPreview(string? liveVersion)
    {
        var liveIdentity = new ModArtifactIdentityReceipt(64, new('B', 64));
        var previous = new ModInstalledArtifactState(
            1, "game", "version.dll", "saved-receipt-version", 64, new('A', 64),
            DateTimeOffset.UnixEpoch, null, "guffawaffle", "stable", "guffawaffle.windows");
        return Preview(LauncherProviderCompatibilityKind.Compatible, "compatible") with
        {
            SourceInstallation = new(
                ModInstallationEvidenceState.ManagedChanged, IsGameRunning: false,
                InstalledVersion: previous.Version, InstalledProviderId: previous.ProviderId,
                InstalledReleaseChannelId: previous.ReleaseChannelId,
                InstalledRuntimeDistributionId: previous.RuntimeDistributionId,
                InstalledSha256: liveIdentity.Sha256,
                BinaryProvenance: new(ModBinaryProvenanceState.SelfDeclaredLineage,
                    liveIdentity.Sha256, liveIdentity.Size, liveVersion, ProductVersion: null)),
            Artifact = new(ModOperationPreparationState.Ready, "Ready", "game", "1.1.4",
                new(new Uri("https://example.invalid/version.dll"), "version.dll", 42,
                    new('C', 64), "1.1.4.0"), ExistingArtifactPolicy.AdoptAndPreserve,
                ModManagementActionKind.UpdateManualInstallation, "netniv"),
            ReplacementSource = new(previous, liveIdentity, LiveRuntimeManifestIdentity: null),
        };
    }

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
