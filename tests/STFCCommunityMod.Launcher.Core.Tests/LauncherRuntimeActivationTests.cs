using System.Text;
using System.Text.Json;
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
        Assert.AreEqual(
            "Required capability settings.principal-taxonomy.v1 is unavailable. "
            + "Detected distribution: Unknown. Fallback: alphabetical-settings-layout.",
            decision.Reason);
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
        ],
        new("tests/product-policy-disabled", "1"));

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
        Assert.AreEqual(policy.Source, plan.PolicySource);
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
            [dependentFeature, baseFeature],
            catalogSource: new("tests/dependency-order", "1"));

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
                [first, second],
                catalogSource: new("tests/dependency-cycle", "1")));

        StringAssert.Contains(exception.Message, "cycle");
    }

    [TestMethod]
    public void TypedEvidenceSerializationEqualityAndSourceIdentityAreStable()
    {
        using var manifest = JsonStream(Manifest(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            settingsCatalogSchema: 1));
        var profile = LauncherRuntimeManifestDetector.Detect(manifest, "typed evidence test");

        var first = LauncherFeatureResolver.Resolve(profile, LauncherFeatureCatalog.All);
        var second = LauncherFeatureResolver.Resolve(profile, LauncherFeatureCatalog.All);
        var firstDecision = first.GetDecision(LauncherFeatureIds.SemanticSettingsGrouping);
        var secondDecision = second.GetDecision(LauncherFeatureIds.SemanticSettingsGrouping);
        var json = JsonSerializer.Serialize(firstDecision);

        Assert.AreEqual(firstDecision, secondDecision);
        Assert.AreEqual(LauncherFeatureCatalog.Source, first.CatalogSource);
        Assert.AreEqual(LauncherFeaturePolicy.DefaultSource, first.PolicySource);
        StringAssert.Contains(json, "\"Code\":\"active\"");
        StringAssert.Contains(json, "\"Reason\":\"Runtime provides settings.principal-taxonomy.v1.\"");
        Assert.AreEqual(firstDecision, JsonSerializer.Deserialize<LauncherFeatureDecision>(json));
        var planJson = JsonSerializer.Serialize(first);
        StringAssert.Contains(planJson, LauncherFeatureCatalog.Source.Id);
        StringAssert.Contains(planJson, LauncherFeaturePolicy.DefaultSource.Id);
        StringAssert.Contains(planJson, "\"Version\":\"1\"");
    }

    [TestMethod]
    public void EveryReasonCodeHasAStableNonNumericWireValue()
    {
        var expected = new Dictionary<LauncherFeatureReasonCode, string>
        {
            [LauncherFeatureReasonCode.Active] = "active",
            [LauncherFeatureReasonCode.MissingCapability] = "missing-capability",
            [LauncherFeatureReasonCode.PolicyDenied] = "policy-denied",
            [LauncherFeatureReasonCode.MissingDependency] = "missing-dependency",
            [LauncherFeatureReasonCode.UnavailableImplementation] = "unavailable-implementation",
            [LauncherFeatureReasonCode.Fallback] = "fallback",
        };
        foreach (var (code, wireValue) in expected)
        {
            var json = JsonSerializer.Serialize(code);
            Assert.AreEqual($"\"{wireValue}\"", json);
            Assert.AreEqual(code, JsonSerializer.Deserialize<LauncherFeatureReasonCode>(json));
        }
        Assert.ThrowsException<JsonException>(() =>
            JsonSerializer.Deserialize<LauncherFeatureReasonCode>("1"));
        foreach (var hostile in new[] { "\"ACTIVE\"", "\"Active\"", "\"unknown\"", "\"missing_capability\"" })
        {
            Assert.ThrowsException<JsonException>(() =>
                JsonSerializer.Deserialize<LauncherFeatureReasonCode>(hostile));
        }
    }

    [TestMethod]
    public void PublicEvidenceDecisionAndSourceConstructorsRejectContradictions()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new LauncherFeatureDecisionEvidence((LauncherFeatureReasonCode)999, ["test"]));
        Assert.ThrowsException<ArgumentException>(() =>
            new LauncherFeatureDecisionEvidence(
                LauncherFeatureReasonCode.MissingCapability,
                []));
        Assert.ThrowsException<ArgumentException>(() =>
            new LauncherFeatureDecisionEvidence(
                LauncherFeatureReasonCode.MissingCapability,
                ["z-capability", "a-capability"],
                "Unknown"));
        Assert.ThrowsException<ArgumentException>(() =>
            new LauncherFeatureDecisionEvidence(
                LauncherFeatureReasonCode.Fallback,
                ["unsafe fallback"]));
        Assert.ThrowsException<ArgumentException>(() =>
            new LauncherFeatureSourceIdentity("unsafe source", "1"));
        Assert.ThrowsException<ArgumentException>(() =>
            new LauncherFeatureSourceIdentity("test/source", new string('x', 65)));

        var activeEligibility = new LauncherFeatureDecisionEvidence(
            LauncherFeatureReasonCode.Active,
            ["test.capability"]);
        var activeSelection = new LauncherFeatureDecisionEvidence(
            LauncherFeatureReasonCode.Active,
            ["active-implementation"]);
        var fallbackSelection = new LauncherFeatureDecisionEvidence(
            LauncherFeatureReasonCode.Fallback,
            ["fallback-implementation"]);
        Assert.ThrowsException<ArgumentException>(() =>
            new LauncherFeatureDecision(
                "test.feature",
                LauncherFeatureActivationState.Inactive,
                activeEligibility,
                fallbackSelection,
                "fallback-implementation"));
        Assert.ThrowsException<ArgumentException>(() =>
            new LauncherFeatureDecision(
                "test.feature",
                LauncherFeatureActivationState.Active,
                activeEligibility,
                activeSelection,
                "different-implementation"));
    }

    [TestMethod]
    public void MalformedJsonCannotForgeDecisionStateOrPresentation()
    {
        var contradictory =
            """
            {
              "Id": "test.feature",
              "State": 0,
              "EligibilityEvidence": {
                "Code": "missing-capability",
                "Subjects": ["test.capability"],
                "Context": "Unknown"
              },
              "SelectionEvidence": {
                "Code": "fallback",
                "Subjects": ["fallback-implementation"],
                "Context": ""
              },
              "SelectedImplementation": "fallback-implementation"
            }
            """;
        Assert.ThrowsException<ArgumentException>(() =>
            JsonSerializer.Deserialize<LauncherFeatureDecision>(contradictory));

        var validWithForgedReason = contradictory
            .Replace("\"State\": 0", "\"State\": 1", StringComparison.Ordinal)
            .Replace(
                "\"SelectedImplementation\": \"fallback-implementation\"",
                "\"SelectedImplementation\": \"fallback-implementation\", \"Reason\": \"forged\"",
                StringComparison.Ordinal);
        var decision = JsonSerializer.Deserialize<LauncherFeatureDecision>(validWithForgedReason)!;
        Assert.AreEqual(
            "Required capability test.capability is unavailable. "
            + "Detected distribution: Unknown. Fallback: fallback-implementation.",
            decision.Reason);
    }

    [TestMethod]
    public void ResolverEmitsDistinctPolicyDependencyImplementationAndFallbackEvidence()
    {
        var noRequirements = new HashSet<string>(StringComparer.Ordinal);
        var baseFeature = new LauncherFeatureDefinition(
            "test.base",
            LauncherFeatureKind.CompatibilityGate,
            LauncherFeatureActivationMode.StartupLatched,
            noRequirements,
            noRequirements,
            LauncherFeatureDefault.Disabled,
            "base-active",
            "base-fallback");
        var dependent = new LauncherFeatureDefinition(
            "test.dependent",
            LauncherFeatureKind.CompatibilityGate,
            LauncherFeatureActivationMode.StartupLatched,
            noRequirements,
            new HashSet<string>([baseFeature.Id], StringComparer.Ordinal),
            LauncherFeatureDefault.EnabledWhenEligible,
            "dependent-active",
            "dependent-fallback");
        var unavailable = new LauncherFeatureDefinition(
            "test.unavailable",
            LauncherFeatureKind.CompatibilityGate,
            LauncherFeatureActivationMode.StartupLatched,
            noRequirements,
            noRequirements,
            LauncherFeatureDefault.EnabledWhenEligible,
            "unavailable-active",
            "unavailable-fallback",
            ActiveImplementationAvailable: false);

        var plan = LauncherFeatureResolver.Resolve(
            LauncherRuntimeProfile.Unknown("typed evidence test", "unknown"),
            [dependent, unavailable, baseFeature],
            catalogSource: new("tests/typed-feature-evidence", "1"));

        AssertDecision(
            plan.GetDecision(baseFeature.Id),
            LauncherFeatureReasonCode.PolicyDenied,
            "Product policy disabled this feature. Fallback: base-fallback.");
        AssertDecision(
            plan.GetDecision(dependent.Id),
            LauncherFeatureReasonCode.MissingDependency,
            "Required feature test.base is inactive. Fallback: dependent-fallback.");
        AssertDecision(
            plan.GetDecision(unavailable.Id),
            LauncherFeatureReasonCode.UnavailableImplementation,
            "Required implementation unavailable-active is unavailable. Fallback: unavailable-fallback.");
    }

    [TestMethod]
    public void ProviderAndEvidenceClassesUseOneNeutralResolverPath()
    {
        var cases = new[]
        {
            ("current-guff", LauncherRuntimeManifestDetector.GuffawaffleDistributionId, true, true),
            ("current-netniv", LauncherRuntimeManifestDetector.NetnivDistributionId, false, false),
            ("future-netniv", LauncherRuntimeManifestDetector.NetnivDistributionId, true, true),
            ("partial-custom", "custom.partial-runtime", false, false),
            ("legacy-guff", LauncherRuntimeManifestDetector.GuffawaffleDistributionId, false, false),
            ("unknown", LauncherRuntimeManifestDetector.UnknownDistributionId, false, false),
        };

        foreach (var (id, distribution, hasCapability, expectedActive) in cases)
        {
            var profile = new LauncherRuntimeProfile(
                distribution,
                hasCapability ? new Version(9, 0) : null,
                id,
                hasCapability ? new(1, id) : null,
                hasCapability ? [LauncherCapabilityIds.PrincipalSettingsTaxonomyV1] : [],
                [new("typed evidence matrix", id)]);

            var decision = LauncherFeatureResolver.Resolve(profile, LauncherFeatureCatalog.All)
                .GetDecision(LauncherFeatureIds.SemanticSettingsGrouping);

            Assert.AreEqual(expectedActive, decision.IsActive, id);
            Assert.AreEqual(
                expectedActive
                    ? LauncherFeatureReasonCode.Active
                    : LauncherFeatureReasonCode.MissingCapability,
                decision.EligibilityEvidence.Code,
                id);
            Assert.AreEqual(
                expectedActive
                    ? LauncherFeatureReasonCode.Active
                    : LauncherFeatureReasonCode.Fallback,
                decision.SelectionEvidence.Code,
                id);
        }
    }

    [TestMethod]
    public void PlayerPreferenceIsNotAnEligibilityOrPolicyInput()
    {
        var parameters = typeof(LauncherFeatureResolver)
            .GetMethod(nameof(LauncherFeatureResolver.Resolve))!
            .GetParameters();

        Assert.IsFalse(parameters.Any(parameter =>
            parameter.Name!.Contains("preference", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CustomCatalogAndPolicyCannotMasqueradeAsCheckedInSources()
    {
        var definition = LauncherFeatureCatalog.All.Single();
        Assert.ThrowsException<ArgumentException>(() =>
            LauncherFeatureResolver.Resolve(
                LauncherRuntimeProfile.Unknown("test", "test"),
                [definition]));
        Assert.ThrowsException<ArgumentException>(() =>
            new LauncherFeaturePolicy(
                [new KeyValuePair<string, bool>(definition.Id, false)]));
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

    private static void AssertDecision(
        LauncherFeatureDecision decision,
        LauncherFeatureReasonCode reasonCode,
        string expectedPresentation)
    {
        Assert.AreEqual(reasonCode, decision.EligibilityEvidence.Code);
        Assert.AreEqual(LauncherFeatureReasonCode.Fallback, decision.SelectionEvidence.Code);
        Assert.AreEqual(expectedPresentation, decision.Reason);
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
