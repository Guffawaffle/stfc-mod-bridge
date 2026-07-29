using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherRuntimeActivationTests
{
    [TestMethod]
    public void CompatibleManifestPositivelyIdentifiesRuntimeAndActivatesSemanticGrouping()
    {
        using var manifest = JsonStream(Manifest(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            settingsCatalogSchema: 1));

        var profile = LauncherRuntimeManifestDetector.Detect(
            manifest,
            "test manifest");
        var plan = LauncherFeatureResolver.Resolve(
            profile,
            LauncherFeatureCatalog.All);
        var decision = plan.GetDecision(
            LauncherFeatureIds.SemanticSettingsGrouping);

        Assert.AreEqual(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            profile.DistributionId);
        Assert.AreEqual(new Version(2, 1, 0, 0), profile.RuntimeVersion);
        Assert.IsTrue(
            profile.HasCapability(
                LauncherCapabilityIds.PrincipalSettingsTaxonomyV1));
        Assert.IsTrue(decision.IsActive);
        Assert.AreEqual(
            LauncherFeatureImplementations.PrincipalCatalogSettingsLayout,
            decision.SelectedImplementation);
        StringAssert.Contains(
            profile.Evidence.Single().Detail,
            "Positively identified");
    }

    [TestMethod]
    public void MissingManifestFailsClosedToUnknownAndAlphabeticalLayout()
    {
        var profile = LauncherRuntimeManifestDetector.Detect(
            null,
            "missing test manifest");
        var plan = LauncherFeatureResolver.Resolve(
            profile,
            LauncherFeatureCatalog.All);
        var decision = plan.GetDecision(
            LauncherFeatureIds.SemanticSettingsGrouping);

        Assert.AreEqual(
            LauncherRuntimeManifestDetector.UnknownDistributionId,
            profile.DistributionId);
        Assert.AreEqual(0, profile.Capabilities.Count);
        Assert.IsFalse(decision.IsActive);
        Assert.AreEqual(
            LauncherFeatureImplementations.AlphabeticalSettingsLayout,
            decision.SelectedImplementation);
        StringAssert.Contains(
            decision.Reason,
            LauncherCapabilityIds.PrincipalSettingsTaxonomyV1);
    }

    [TestMethod]
    public void IncompatibleCatalogWithholdsTaxonomyCapabilityButRetainsDistributionEvidence()
    {
        using var manifest = JsonStream(Manifest(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            settingsCatalogSchema: 2));

        var profile = LauncherRuntimeManifestDetector.Detect(
            manifest,
            "test manifest");
        var plan = LauncherFeatureResolver.Resolve(
            profile,
            LauncherFeatureCatalog.All);

        Assert.AreEqual(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            profile.DistributionId);
        Assert.IsFalse(
            profile.HasCapability(
                LauncherCapabilityIds.PrincipalSettingsTaxonomyV1));
        Assert.IsFalse(
            plan.IsActive(LauncherFeatureIds.SemanticSettingsGrouping));
        Assert.IsTrue(
            profile.Evidence.Any(
                item => item.Detail.Contains(
                    "was withheld",
                    StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CapabilityRatherThanOwnerIdentityDeterminesFeatureEligibility()
    {
        var profile = new LauncherRuntimeProfile(
            LauncherRuntimeManifestDetector.NetnivDistributionId,
            new Version(9, 0),
            "netniv-taxonomy",
            new(1, "netniv-taxonomy-v1"),
            [LauncherCapabilityIds.PrincipalSettingsTaxonomyV1],
            [new("test", "NetniV test manifest")]);

        var plan = LauncherFeatureResolver.Resolve(
            profile,
            LauncherFeatureCatalog.All);

        Assert.IsTrue(
            plan.IsActive(LauncherFeatureIds.SemanticSettingsGrouping));
        Assert.AreEqual(
            LauncherFeatureImplementations.PrincipalCatalogSettingsLayout,
            plan.GetDecision(LauncherFeatureIds.SemanticSettingsGrouping)
                .SelectedImplementation);
    }

    [TestMethod]
    public void ProductPolicyCanDisableAnEligibleFeatureWithoutChangingRuntimeFacts()
    {
        using var manifest = JsonStream(Manifest(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            settingsCatalogSchema: 1));
        var profile = LauncherRuntimeManifestDetector.Detect(
            manifest,
            "test manifest");
        var policy = new LauncherFeaturePolicy(
        [
            new KeyValuePair<string, bool>(
                LauncherFeatureIds.SemanticSettingsGrouping,
                false),
        ]);

        var plan = LauncherFeatureResolver.Resolve(
            profile,
            LauncherFeatureCatalog.All,
            policy);
        var decision = plan.GetDecision(
            LauncherFeatureIds.SemanticSettingsGrouping);

        Assert.IsTrue(
            profile.HasCapability(
                LauncherCapabilityIds.PrincipalSettingsTaxonomyV1));
        Assert.IsFalse(decision.IsActive);
        StringAssert.Contains(decision.Reason, "Product policy disabled");
    }

    [TestMethod]
    public void FeatureDependenciesResolveOnceEvenWhenDefinitionsAreOutOfOrder()
    {
        var capability = new HashSet<string>(
            [LauncherCapabilityIds.PrincipalSettingsTaxonomyV1],
            StringComparer.Ordinal);
        var noCapabilities = new HashSet<string>(StringComparer.Ordinal);
        var noDependencies = new HashSet<string>(StringComparer.Ordinal);
        var baseFeature = new LauncherFeatureDefinition(
            "settings.base",
            LauncherFeatureKind.CompatibilityGate,
            LauncherFeatureActivationMode.StartupLatched,
            capability,
            noDependencies,
            LauncherFeatureDefault.EnabledWhenEligible,
            "base-active",
            "base-fallback");
        var dependentFeature = new LauncherFeatureDefinition(
            "settings.dependent",
            LauncherFeatureKind.CompatibilityGate,
            LauncherFeatureActivationMode.StartupLatched,
            noCapabilities,
            new HashSet<string>(["settings.base"], StringComparer.Ordinal),
            LauncherFeatureDefault.EnabledWhenEligible,
            "dependent-active",
            "dependent-fallback");
        var profile = new LauncherRuntimeProfile(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            new Version(2, 1),
            "test",
            new(1, "test"),
            capability,
            [new("test", "test")]);

        var plan = LauncherFeatureResolver.Resolve(
            profile,
            [dependentFeature, baseFeature]);

        Assert.IsTrue(plan.IsActive("settings.base"));
        Assert.IsTrue(plan.IsActive("settings.dependent"));
    }

    [TestMethod]
    public void FeatureDependencyCyclesFailBeforeComposition()
    {
        var noCapabilities = new HashSet<string>(StringComparer.Ordinal);
        var first = new LauncherFeatureDefinition(
            "cycle.first",
            LauncherFeatureKind.CompatibilityGate,
            LauncherFeatureActivationMode.StartupLatched,
            noCapabilities,
            new HashSet<string>(["cycle.second"], StringComparer.Ordinal),
            LauncherFeatureDefault.EnabledWhenEligible,
            "first-active",
            "first-fallback");
        var second = new LauncherFeatureDefinition(
            "cycle.second",
            LauncherFeatureKind.CompatibilityGate,
            LauncherFeatureActivationMode.StartupLatched,
            noCapabilities,
            new HashSet<string>(["cycle.first"], StringComparer.Ordinal),
            LauncherFeatureDefault.EnabledWhenEligible,
            "second-active",
            "second-fallback");

        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => LauncherFeatureResolver.Resolve(
                LauncherRuntimeProfile.Unknown("test", "test"),
                [first, second]));

        StringAssert.Contains(exception.Message, "cycle");
    }

    [TestMethod]
    public void InvalidManifestDoesNotElevateAnUnknownRuntime()
    {
        using var manifest = JsonStream(
            """
            {
              "manifestSchema": 1,
              "distributionId": "guffawaffle.stfc-community-mod",
              "runtimeVersion": "not-a-version",
              "sourceRevision": "test",
              "capabilities": [ "settings.principal-taxonomy.v1" ],
              "settingsCatalog": {
                "schemaVersion": 1,
                "revision": "test"
              }
            }
            """);

        var profile = LauncherRuntimeManifestDetector.Detect(
            manifest,
            "invalid test manifest");

        Assert.AreEqual(
            LauncherRuntimeManifestDetector.UnknownDistributionId,
            profile.DistributionId);
        Assert.AreEqual(0, profile.Capabilities.Count);
        StringAssert.Contains(profile.Evidence.Single().Detail, "invalid");
    }

    private static string Manifest(
        string distributionId,
        int settingsCatalogSchema) =>
        $$"""
        {
          "manifestSchema": 1,
          "distributionId": "{{distributionId}}",
          "runtimeVersion": "2.1.0.0",
          "sourceRevision": "test-revision",
          "capabilities": [
            "settings.principal-taxonomy.v1"
          ],
          "settingsCatalog": {
            "schemaVersion": {{settingsCatalogSchema}},
            "revision": "test-taxonomy"
          }
        }
        """;

    private static MemoryStream JsonStream(string json) =>
        new(Encoding.UTF8.GetBytes(json));
}
