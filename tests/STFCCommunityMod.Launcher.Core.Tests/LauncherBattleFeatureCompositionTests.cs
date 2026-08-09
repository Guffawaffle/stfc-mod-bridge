using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherBattleFeatureCompositionTests
{
    [TestMethod]
    public void EligibleFeaturesRequireIndependentPlayerIntent()
    {
        var plan = Plan(
            LauncherRuntimeManifestDetector.NetnivDistributionId,
            LauncherCapabilityIds.SidecarIngestV1,
            LauncherCapabilityIds.BattleCaptureV1,
            LauncherCapabilityIds.FleetRuntimeSnapshotV1);

        var unset = LauncherBattleFeatureComposer.Compose(plan);
        var selected = LauncherBattleFeatureComposer.Compose(
            plan,
            new(
                LauncherPlayerFeaturePreference.Enabled,
                LauncherPlayerFeaturePreference.Disabled));

        Assert.AreEqual(LauncherPlayerFeatureState.Available, unset.BattleCollection.State);
        Assert.AreEqual(LauncherPlayerFeatureState.Available, unset.FleetCollection.State);
        Assert.AreEqual(LauncherPlayerFeatureState.Enabled, selected.BattleCollection.State);
        Assert.AreEqual(LauncherPlayerFeatureState.Disabled, selected.FleetCollection.State);
    }

    [TestMethod]
    public void PlayerPreferenceCannotElevateMissingCapabilityOrPolicyDenial()
    {
        var missingCapability = Plan(
            LauncherRuntimeManifestDetector.NetnivDistributionId,
            LauncherCapabilityIds.SidecarIngestV1);
        var deniedPolicy = new LauncherFeaturePolicy(
            [new(LauncherFeatureIds.BattleCollection, false)],
            new("tests/disabled-battle-policy", "1"));
        var eligibleProfile = Profile(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            LauncherCapabilityIds.SidecarIngestV1,
            LauncherCapabilityIds.BattleCaptureV1);
        var denied = LauncherFeatureResolver.Resolve(
            eligibleProfile,
            LauncherFeatureCatalog.All,
            deniedPolicy);
        var requested = new LauncherBattlePreferences(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Enabled);

        var missingSnapshot = LauncherBattleFeatureComposer.Compose(missingCapability, requested);
        var deniedSnapshot = LauncherBattleFeatureComposer.Compose(denied, requested);

        Assert.AreEqual(LauncherPlayerFeatureState.Unavailable, missingSnapshot.BattleCollection.State);
        Assert.IsFalse(missingSnapshot.BattleCollection.IsEligible);
        Assert.IsTrue(missingSnapshot.BattleCollection.IsRequested);
        Assert.AreEqual(
            LauncherFeatureReasonCode.MissingCapability,
            missingSnapshot.BattleCollection.Decision.EligibilityEvidence.Code);
        Assert.AreEqual(LauncherPlayerFeaturePreference.Enabled, missingSnapshot.BattleCollection.Preference);
        Assert.AreEqual(LauncherPlayerFeatureState.Unavailable, deniedSnapshot.BattleCollection.State);
        Assert.AreEqual(
            LauncherFeatureReasonCode.PolicyDenied,
            deniedSnapshot.BattleCollection.Decision.EligibilityEvidence.Code);
    }

    [TestMethod]
    public void DistributionIdentityDoesNotChangeThePerFeatureProjection()
    {
        var preferences = new LauncherBattlePreferences(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Unset);
        var guff = LauncherBattleFeatureComposer.Compose(
            Plan(
                LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
                LauncherCapabilityIds.SidecarIngestV1,
                LauncherCapabilityIds.BattleCaptureV1),
            preferences);
        var netniv = LauncherBattleFeatureComposer.Compose(
            Plan(
                LauncherRuntimeManifestDetector.NetnivDistributionId,
                LauncherCapabilityIds.SidecarIngestV1,
                LauncherCapabilityIds.BattleCaptureV1),
            preferences);

        Assert.AreEqual(guff.BattleCollection.State, netniv.BattleCollection.State);
        Assert.AreEqual(
            guff.BattleCollection.Decision.SelectedImplementation,
            netniv.BattleCollection.Decision.SelectedImplementation);
        Assert.AreEqual(guff.FleetCollection.State, netniv.FleetCollection.State);
        Assert.AreEqual(
            guff.FleetCollection.Decision.EligibilityEvidence.Code,
            netniv.FleetCollection.Decision.EligibilityEvidence.Code);
    }

    [TestMethod]
    public void DiagnosticsAreBoundedAndExplicitlyKeepOperationalCollectionDormant()
    {
        var snapshot = LauncherBattleFeatureComposer.Compose(
            Plan(
                LauncherRuntimeManifestDetector.NetnivDistributionId,
                LauncherCapabilityIds.SidecarIngestV1,
                LauncherCapabilityIds.BattleCaptureV1),
            new(
                LauncherPlayerFeaturePreference.Enabled,
                LauncherPlayerFeaturePreference.Unset));

        var facts = snapshot.BuildDiagnosticFacts();
        var battle = facts.Single(fact => fact.Id == "feature.battle.collection");
        var fleet = facts.Single(fact => fact.Id == "feature.fleet.collection");

        StringAssert.Contains(battle.Summary, "remains dormant");
        StringAssert.Contains(battle.TechnicalDetail, "policyDisposition=catalog-default-enabled");
        StringAssert.Contains(battle.TechnicalDetail, "preference=Enabled");
        Assert.IsFalse(battle.TechnicalDetail!.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(LauncherDiagnosticLevel.Informational, fleet.Level);
        StringAssert.Contains(fleet.TechnicalDetail, "reason=MissingCapability");
    }

    [TestMethod]
    public void InvalidPlayerPreferenceFailsClosed()
    {
        var plan = Plan(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            LauncherCapabilityIds.SidecarIngestV1,
            LauncherCapabilityIds.BattleCaptureV1);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            LauncherBattleFeatureComposer.Compose(
                plan,
                new((LauncherPlayerFeaturePreference)99, LauncherPlayerFeaturePreference.Unset)));
    }

    [TestMethod]
    public void LocalIpcActivationConsumesOnlyEnabledReviewedFeatureProjections()
    {
        var eligiblePlan = Plan(
            LauncherRuntimeManifestDetector.NetnivDistributionId,
            LauncherCapabilityIds.SidecarIngestV1,
            LauncherCapabilityIds.BattleCaptureV1,
            LauncherCapabilityIds.FleetRuntimeSnapshotV1);

        var unset = BattleIngestActivation.Resolve(
            LauncherBattleFeatureComposer.Compose(eligiblePlan));
        var battleOnly = BattleIngestActivation.Resolve(
            LauncherBattleFeatureComposer.Compose(
                eligiblePlan,
                new(
                    LauncherPlayerFeaturePreference.Enabled,
                    LauncherPlayerFeaturePreference.Disabled)));
        var fleetOnly = BattleIngestActivation.Resolve(
            LauncherBattleFeatureComposer.Compose(
                eligiblePlan,
                new(
                    LauncherPlayerFeaturePreference.Disabled,
                    LauncherPlayerFeaturePreference.Enabled)));

        Assert.IsTrue(unset.IsReviewedFeatureComposition);
        Assert.IsFalse(unset.ShouldListen);
        CollectionAssert.AreEqual(
            new[] { BattleIngestProtocol.BattleEventsKind },
            battleOnly.AcceptedKinds.ToArray());
        CollectionAssert.AreEqual(
            new[] { BattleIngestProtocol.FleetRuntimeKind },
            fleetOnly.AcceptedKinds.ToArray());
    }

    private static LauncherActivationPlan Plan(string distributionId, params string[] capabilities) =>
        LauncherFeatureResolver.Resolve(
            Profile(distributionId, capabilities),
            LauncherFeatureCatalog.All);

    private static LauncherRuntimeProfile Profile(string distributionId, params string[] capabilities) =>
        new(
            distributionId,
            new Version(9, 0),
            "battle-feature-composition",
            new(1, "battle-feature-composition"),
            capabilities,
            [new("test", "battle feature composition")]);
}
