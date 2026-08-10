using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class LauncherStartupCompositionTests
{
    [TestMethod]
    public void ResolvedNonDefaultReleaseChannelFlowsIntoSettingsDiagnostics()
    {
        var provider = BundledLauncherProviderCatalog.Load().GetProvider("guffawaffle");
        var preview = provider.ReleaseChannels["preview"];

        var composition = LauncherStartupComposition.Create(provider, preview);

        Assert.AreEqual("Preview", composition.SettingsDiagnostics.ReleaseChannelDisplayName);
        Assert.AreEqual("Guffawaffle/stfc-mod", composition.SettingsDiagnostics.ReleaseRepository);
    }

    [TestMethod]
    public void ReleaseChannelFromAnotherProviderIsRejected()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        var provider = catalog.GetProvider("guffawaffle");
        var foreignChannel = catalog.GetProvider("netniv").DefaultReleaseChannel;

        _ = Assert.ThrowsException<ArgumentException>(
            () => LauncherStartupComposition.Create(provider, foreignChannel));
    }

    [TestMethod]
    public void RuntimeCompositionSlotRevokesChangedReviewedEvidenceInProcess()
    {
        var provider = BundledLauncherProviderCatalog.Load().GetProvider("guffawaffle");
        var channel = provider.DefaultReleaseChannel;
        var profile = new LauncherRuntimeProfile(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            new Version(2, 1, 0, 8),
            "0123456789abcdef0123456789abcdef01234567",
            new(1, "reviewed-pair"),
            [LauncherCapabilityIds.PrincipalSettingsTaxonomyV1, "battle.capture.v1"],
            [new("managed-pair:sha256:test", "reviewed compatibility evidence")]);
        var plan = LauncherFeatureResolver.Resolve(profile, LauncherFeatureCatalog.All);
        var enabled = new LauncherBattlePreferences(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Unset);
        var reviewed = new LauncherStartupComposition(
            profile,
            plan,
            LauncherBattleFeatureComposer.Compose(plan, enabled),
            LauncherSettingsLayoutComposer.Select(plan),
            LauncherStartupComposition.Create(provider, channel).SettingsDiagnostics);
        var slot = new LauncherRuntimeCompositionSlot(
            provider,
            channel,
            reviewed,
            new string('a', 64));

        Assert.IsTrue(slot.Refresh(null, enabled));
        Assert.AreNotEqual(profile.SourceRevision, slot.Current.RuntimeProfile.SourceRevision);
        Assert.IsFalse(slot.Current.RuntimeProfile.HasCapability("battle.capture.v1"));
        Assert.AreEqual(
            LauncherPlayerFeatureState.Unavailable,
            slot.Current.BattleFeatures.BattleCollection.State);
        Assert.AreEqual(
            LauncherPlayerFeaturePreference.Enabled,
            slot.Current.BattleFeatures.BattleCollection.Preference);
        Assert.IsFalse(slot.Refresh(null, enabled));
    }

    [TestMethod]
    public void RuntimeCompositionSlotRefreshesPreferenceWithoutChangingReviewedEvidence()
    {
        var provider = BundledLauncherProviderCatalog.Load().GetProvider("guffawaffle");
        var channel = provider.DefaultReleaseChannel;
        var initial = LauncherStartupComposition.Create(provider, channel);
        var slot = new LauncherRuntimeCompositionSlot(
            provider,
            channel,
            initial,
            null);
        var enabled = new LauncherBattlePreferences(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Unset);

        Assert.IsTrue(slot.RefreshBattlePreferences(enabled));
        Assert.AreEqual(
            LauncherPlayerFeaturePreference.Enabled,
            slot.Current.BattleFeatures.BattleCollection.Preference);
        Assert.IsFalse(slot.RefreshBattlePreferences(enabled));
    }
}
