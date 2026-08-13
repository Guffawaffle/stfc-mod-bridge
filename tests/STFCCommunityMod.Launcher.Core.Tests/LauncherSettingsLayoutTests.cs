using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherSettingsLayoutTests
{
    [TestMethod]
    public void PrincipalLayoutUsesCatalogTaxonomy()
    {
        var setting = LoadSetting(
            path: "notifications.fleet_arrived_in_system",
            category: "notifications",
            group: "Fleet movement and mining");
        var provider = new PrincipalCatalogSettingsLayoutProvider();

        var placement = provider.Place(setting);

        Assert.AreEqual(
            LauncherSettingsSection.Notifications,
            placement.Section);
        Assert.AreEqual("Fleet movement and mining", placement.Group);
        Assert.AreEqual("Fleet arrived in system", placement.SortKey);
        Assert.IsFalse(placement.IsUncategorized);
        Assert.AreEqual(7, provider.Sections.Count);
    }

    [TestMethod]
    public void AlphabeticalFallbackDoesNotConsumePrincipalGroupingMetadata()
    {
        var setting = LoadSetting(
            path: "notifications.fleet_arrived_in_system",
            category: "notifications",
            group: "Fleet movement and mining");
        var provider = new AlphabeticalSettingsLayoutProvider();

        var placement = provider.Place(setting);

        Assert.AreEqual(LauncherSettingsSection.General, placement.Section);
        Assert.AreEqual(string.Empty, placement.Group);
        Assert.AreEqual("Fleet arrived in system", placement.SortKey);
        Assert.AreEqual(1, provider.Sections.Count);
        Assert.AreEqual("Settings", provider.Sections[0].Title);
    }

    [TestMethod]
    public void ComposerUsesRuntimeSelectionWhenCatalogHasNoReviewedLayout()
    {
        var activeProfile = new LauncherRuntimeProfile(
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId,
            new Version(2, 1),
            "test",
            new(1, "test"),
            [LauncherCapabilityIds.PrincipalSettingsTaxonomyV1],
            [new("test", "test")]);
        var activePlan = LauncherFeatureResolver.Resolve(
            activeProfile,
            LauncherFeatureCatalog.All);
        var fallbackPlan = LauncherFeatureResolver.Resolve(
            LauncherRuntimeProfile.Unknown("test", "missing"),
            LauncherFeatureCatalog.All);

        Assert.IsInstanceOfType<PrincipalCatalogSettingsLayoutProvider>(
            LauncherSettingsLayoutComposer.Select(activePlan));
        Assert.IsInstanceOfType<AlphabeticalSettingsLayoutProvider>(
            LauncherSettingsLayoutComposer.Select(fallbackPlan));
    }

    [TestMethod]
    public void ComposerUsesReviewedCatalogPresentationIndependentlyFromRuntimeActivation()
    {
        var fallbackPlan = LauncherFeatureResolver.Resolve(
            LauncherRuntimeProfile.Unknown("test", "missing"),
            LauncherFeatureCatalog.All);
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Configuration",
            "configuration-schema-set.netniv.v1.json");
        using var stream = File.OpenRead(fixture);
        var catalog = LauncherConfigurationSchemaSetLoader.Load(
            stream,
            new(
                "netniv",
                "stable",
                "1.1.4",
                "d912611fa1eca49fc54f363bdf8377dfebf8def0"));

        Assert.IsFalse(fallbackPlan.GetDecision(
            LauncherFeatureIds.SemanticSettingsGrouping).IsActive);
        Assert.IsInstanceOfType<PrincipalCatalogSettingsLayoutProvider>(
            LauncherSettingsLayoutComposer.Select(fallbackPlan, catalog));
    }

    private static LauncherConfigurationSetting LoadSetting(
        string path,
        string category,
        string group)
    {
        var json =
            $$"""
            {
              "schemaVersion": "1.0.0",
              "schemaId": "stfc-community-mod.config-schema",
              "source": {
                "id": "guffawaffle",
                "repository": "Guffawaffle/stfc-mod"
              },
              "settings": [
                {
                  "path": "{{path}}",
                  "title": "Fleet Arrived In System",
                  "description": "Choose system and audio delivery.",
                  "category": "{{category}}",
                  "control": "notification-policy",
                  "valueType": {
                    "kind": "union",
                    "variants": [
                      { "kind": "boolean" },
                      { "kind": "object" }
                    ]
                  },
                  "default": false,
                  "platforms": [ "windows" ],
                  "apply": "next-session",
                  "sensitivity": "public",
                  "stability": "stable",
                  "sourceSupport": [ "guffawaffle" ],
                  "presentation": {
                    "label": "Fleet arrived in system",
                    "help": "Choose system and audio delivery.",
                    "group": "{{group}}",
                    "searchTerms": [ "{{path}}" ],
                    "editorWidth": "standard",
                    "applyTiming": "Next launch",
                    "accessibleName": "Fleet arrived in system",
                    "accessibleHelp": "Choose system and audio delivery. Applies: Next launch."
                  }
                }
              ]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return LauncherConfigurationSchemaLoader.Load(stream).Settings.Single();
    }
}
