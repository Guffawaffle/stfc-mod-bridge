using STFCCommunityMod.Launcher.Core;
using System.Text.Json.Nodes;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherConfigurationSchemaSetLoaderTests
{
    private const string LegacyStableCommit = "d912611fa1eca49fc54f363bdf8377dfebf8def0";
    private const string CurrentStableCommit = "e80a303a9949c89100b6e59b8a5e5cc2271e7144";
    private const string DevCommit = "238004460c4bb93aa717e47c41089fe8b71c4cf9";
    private static readonly string[] DisabledHotkeyAliases =
        ["shortcuts.set_hotkeys_disble", "shortcuts.set_hotkeys_disable"];
    private static readonly string[] ReviewedFamilyIds =
    [
        "netniv.hotkeys.save-zoom-positions",
        "netniv.hotkeys.ship-selection",
        "netniv.hotkeys.use-zoom-positions",
    ];

    [TestMethod]
    public void ExactReviewedApplicabilityPreservesReleaseDeltas()
    {
        var legacyStable = Load("stable", "1.1.4", LegacyStableCommit);
        var currentStable = Load("stable", "1.1.6.0", CurrentStableCommit);
        var dev = Load("dev", "1.1.5.1", DevCommit);

        Assert.AreEqual("netniv.configuration.stable-1.1.4", legacyStable.Identity.CatalogId);
        Assert.AreEqual(new Version(1, 1, 4, 3), legacyStable.Identity.CatalogVersion);
        Assert.AreEqual(LegacyStableCommit, legacyStable.Identity.SourceCommit);
        Assert.AreEqual(203, legacyStable.Settings.Count);
        Assert.AreEqual("netniv.configuration.stable-1.1.6.0", currentStable.Identity.CatalogId);
        Assert.AreEqual(new Version(1, 1, 6, 3), currentStable.Identity.CatalogVersion);
        Assert.AreEqual(CurrentStableCommit, currentStable.Identity.SourceCommit);
        Assert.AreEqual(204, currentStable.Settings.Count);
        Assert.AreEqual("netniv.configuration.dev-1.1.5.1", dev.Identity.CatalogId);
        Assert.AreEqual(new Version(1, 1, 5, 3), dev.Identity.CatalogVersion);
        Assert.AreEqual(DevCommit, dev.Identity.SourceCommit);
        Assert.AreEqual(206, dev.Settings.Count);

        var devOnly = new[]
        {
            "patches.cargoformathooks",
            "patches.officersorthooks",
            "ui.cargo_significant_decimals",
        };
        Assert.IsTrue(devOnly.All(path => dev.Settings.Any(setting => setting.Path == path)));
        Assert.IsTrue(devOnly.All(path => currentStable.Settings.Any(setting => setting.Path == path)));
        Assert.IsTrue(devOnly.All(path => legacyStable.Settings.All(setting => setting.Path != path)));
        var retiredInCurrentStable = new[]
        {
            "graphics.show_all_resolutions",
            "patches.resolutionlistfix",
        };
        Assert.IsTrue(retiredInCurrentStable.All(path =>
            currentStable.Settings.All(setting => setting.Path != path)));
        Assert.IsTrue(retiredInCurrentStable.All(path =>
            legacyStable.Settings.Any(setting => setting.Path == path)));
        Assert.IsTrue(retiredInCurrentStable.All(path =>
            dev.Settings.Any(setting => setting.Path == path)));
        StringAssert.Contains(
            legacyStable.Settings.Single(setting => setting.Path == "ui.extend_donation_max").Description,
            "ordinary donation cap");
        StringAssert.Contains(
            currentStable.Settings.Single(setting => setting.Path == "ui.extend_donation_max").Description,
            "zero or less");
        StringAssert.Contains(
            dev.Settings.Single(setting => setting.Path == "ui.extend_donation_max").Description,
            "set to 0 for unlimited");
    }

    [TestMethod]
    public void CurrentStableDefersNewControlsWithoutProjectingAdjacentMetadata()
    {
        var currentStable = Load("stable", "1.1.6.0", CurrentStableCommit);
        var deferredPaths = new[]
        {
            "graphics.ui_scale_ship",
            "shortcuts.toggle_instant_warp",
            "shortcuts.ui_scaleshipdown",
            "shortcuts.ui_scaleshipup",
            "ui.auto_confirm_ft_upgrade",
            "ui.auto_confirm_instant_warp",
            "ui.hud_daily_goals",
            "ui.hud_field_training",
            "ui.hud_missions",
            "ui.hud_outposts",
            "ui.hud_q_trials",
        };

        Assert.IsTrue(deferredPaths.All(path =>
            currentStable.Settings.All(setting => setting.Path != path)));
    }

    [TestMethod]
    public void ReviewedPresentationCoversEveryVisibleStableSetting()
    {
        var stable = Load("stable", "1.1.6.0", CurrentStableCommit);
        var layout = new PrincipalCatalogSettingsLayoutProvider();

        Assert.AreEqual(155, stable.VisibleSettings.Count);
        Assert.AreEqual(
            LauncherFeatureImplementations.PrincipalCatalogSettingsLayout,
            stable.ReviewedSettingsLayoutId);
        Assert.IsTrue(stable.VisibleSettings.All(setting =>
            !string.IsNullOrWhiteSpace(setting.Presentation.Label)
            && !string.IsNullOrWhiteSpace(setting.Presentation.Help)
            && !string.IsNullOrWhiteSpace(setting.Presentation.Group)
            && setting.Presentation.SearchTerms.Count >= 3
            && setting.Presentation.SearchTerms.Contains(
                setting.Path,
                StringComparer.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(setting.Presentation.AccessibleName)
            && !string.IsNullOrWhiteSpace(setting.Presentation.AccessibleHelp)));
        Assert.IsFalse(stable.VisibleSettings.Any(setting =>
            setting.Description.Contains(
                "runtime contract for",
                StringComparison.OrdinalIgnoreCase)));

        var sectionCounts = stable.VisibleSettings
            .GroupBy(setting => layout.Place(setting).Section)
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.AreEqual(6, sectionCounts[LauncherSettingsSection.General]);
        Assert.AreEqual(21, sectionCounts[LauncherSettingsSection.Interface]);
        Assert.AreEqual(22, sectionCounts[LauncherSettingsSection.Graphics]);
        Assert.AreEqual(89, sectionCounts[LauncherSettingsSection.Hotkeys]);
        Assert.AreEqual(17, sectionCounts[LauncherSettingsSection.DataSync]);

        var familyMembers = stable.VisibleSettings
            .Where(setting => setting.Presentation.Family is not null)
            .ToArray();
        Assert.AreEqual(20, familyMembers.Length);
        CollectionAssert.AreEquivalent(
            ReviewedFamilyIds,
            familyMembers
                .Select(setting => setting.Presentation.Family!.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        StringAssert.Contains(
            stable.Settings.Single(setting => setting.Path == "ui.extend_donation_max")
                .Presentation.Help!,
            "zero or less");
    }

    [TestMethod]
    public void ReviewedPresentationRejectsMissingDuplicateAndUnknownPaths()
    {
        AssertPresentationRejected(
            settings => settings.RemoveAt(0),
            "missing directly player-editable settings");
        AssertPresentationRejected(
            settings => settings.Add(settings[0]!.DeepClone()),
            "is duplicated");
        AssertPresentationRejected(
            settings => settings[0]!["path"] = "ui.not_a_reviewed_setting",
            "is not in the shared catalog");
    }

    [TestMethod]
    public void ReviewedPresentationRejectsStaleRevisionEntries()
    {
        AssertSchemaSetRejected(
            root => StableRevision(root)["presentationSettingRemovals"] = new JsonArray(),
            "not materialized as directly player-editable");
        AssertSchemaSetRejected(
            root => StableRevision(root)["settingOverrides"]!.AsArray().Add(
                new JsonObject
                {
                    ["path"] = "buffs.use_out_of_dock_power",
                    ["runtimeStatus"] = "ignored",
                }),
            "not materialized as directly player-editable");
    }

    [TestMethod]
    public void CatalogCapturesKnownDiscrepanciesAndRuntimeStatus()
    {
        var dev = Load("dev", "1.1.5.1", DevCommit);

        AssertSetting(dev, "sync.resolver_cache_ttl", LauncherConfigurationValueKind.Integer, 300L);
        AssertSetting(dev, "patches.game_version", LauncherConfigurationValueKind.Boolean, true);
        AssertSetting(dev, "patches.resolutionlistfix", LauncherConfigurationValueKind.Boolean, false);
        foreach (var path in new[]
                 {
                     "sync.battlelogs",
                     "sync.buffs",
                     "sync.buildings",
                     "sync.inventory",
                     "sync.jobs",
                     "sync.missions",
                     "sync.officer",
                     "sync.research",
                     "sync.resources",
                     "sync.ships",
                     "sync.slots",
                     "sync.tech",
                     "sync.traits",
                 })
        {
            AssertSetting(dev, path, LauncherConfigurationValueKind.Boolean, false);
        }

        Assert.AreEqual(
            LauncherConfigurationRuntimeStatus.ParsedUnused,
            dev.Settings.Single(setting => setting.Path == "graphics.transition_time").RuntimeStatus);
        Assert.AreEqual(
            LauncherConfigurationRuntimeStatus.ParsedUnused,
            dev.Settings.Single(setting => setting.Path == "graphics.system_pan_momentum").RuntimeStatus);
        foreach (var path in new[] { "ui.auto_confirm_discovery", "shortcuts.move_up", "shortcuts.move_down" })
        {
            Assert.AreEqual(
                LauncherConfigurationRuntimeStatus.Ignored,
                dev.Settings.Single(setting => setting.Path == path).RuntimeStatus);
        }
        CollectionAssert.Contains(
            dev.Settings.Single(setting => setting.Path == "shortcuts.show_lookup").FeatureGates.ToArray(),
            "control.enable_experimental=true");
    }

    [TestMethod]
    public void ShortcutAliasesAndNoneGrammarRemainCatalogBacked()
    {
        var dev = Load("dev", "1.1.5.1", DevCommit);
        var disabled = dev.Settings.Single(setting => setting.Path == "shortcuts.set_hotkeys_disabled");

        CollectionAssert.AreEqual(
            DisabledHotkeyAliases,
            disabled.Aliases.Select(alias => alias.Path).ToArray());
        Assert.IsTrue(LauncherKeybindingValue.Parse("NONE").IsValid);
        Assert.IsFalse(LauncherKeybindingValue.Parse("NOT-A-REAL-STFC-KEY").IsValid);
        Assert.AreEqual("R|MOUSE3", dev.Settings.Single(setting => setting.Path == "shortcuts.action_recall")
            .DefaultValue.GetString());
    }

    [DataTestMethod]
    [DataRow("stable", "1.1.6.0", LegacyStableCommit)]
    [DataRow("stable", "1.1.6", CurrentStableCommit)]
    [DataRow("dev", "1.1.5.1", CurrentStableCommit)]
    [DataRow("preview", "1.1.4", LegacyStableCommit)]
    public void UnreviewedApplicabilityFailsClosed(
        string trackId,
        string releaseVersion,
        string sourceCommit)
    {
        using var stream = File.OpenRead(FixturePath());

        var exception = Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LauncherConfigurationSchemaSetLoader.Load(
                stream,
                new("netniv", trackId, releaseVersion, sourceCommit)));

        StringAssert.Contains(exception.Message, "No unique reviewed configuration catalog applies");
    }

    [TestMethod]
    public void CrossProviderApplicabilityFailsClosed()
    {
        using var stream = File.OpenRead(FixturePath());

        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LauncherConfigurationSchemaSetLoader.Load(
                stream,
                new("guffawaffle", "stable", "1.1.6.0", CurrentStableCommit)));
    }

    private static LauncherConfigurationCatalog Load(
        string trackId,
        string releaseVersion,
        string sourceCommit)
    {
        using var stream = File.OpenRead(FixturePath());
        return LauncherConfigurationSchemaSetLoader.Load(
            stream,
            new("netniv", trackId, releaseVersion, sourceCommit));
    }

    private static void AssertPresentationRejected(
        Action<JsonArray> mutate,
        string expectedMessage)
    {
        var root = JsonNode.Parse(File.ReadAllText(FixturePath()))!.AsObject();
        var settings = root["presentation"]!["settings"]!.AsArray();
        mutate(settings);
        AssertSchemaSetRejected(root, expectedMessage);
    }

    private static void AssertSchemaSetRejected(
        Action<JsonObject> mutate,
        string expectedMessage)
    {
        var root = JsonNode.Parse(File.ReadAllText(FixturePath()))!.AsObject();
        mutate(root);
        AssertSchemaSetRejected(root, expectedMessage);
    }

    private static void AssertSchemaSetRejected(
        JsonObject root,
        string expectedMessage)
    {
        using var stream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));

        var exception = Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LauncherConfigurationSchemaSetLoader.Load(
                stream,
                new("netniv", "stable", "1.1.6.0", CurrentStableCommit)));

        StringAssert.Contains(exception.Message, expectedMessage);
    }

    private static JsonObject StableRevision(JsonObject root) =>
        root["revisions"]!.AsArray()
            .Select(revision => revision!.AsObject())
            .Single(revision =>
                revision["trackId"]!.GetValue<string>() == "stable"
                && revision["releaseVersion"]!.GetValue<string>() == "1.1.6.0");

    private static void AssertSetting(
        LauncherConfigurationCatalog catalog,
        string path,
        LauncherConfigurationValueKind kind,
        object expectedDefault)
    {
        var setting = catalog.Settings.Single(item => item.Path == path);
        Assert.AreEqual(kind, setting.ValueKind);
        object actual = kind switch
        {
            LauncherConfigurationValueKind.Boolean => (object)setting.DefaultValue.GetBoolean(),
            LauncherConfigurationValueKind.Integer => setting.DefaultValue.GetInt64(),
            _ => throw new AssertFailedException($"Unsupported test value kind {kind}."),
        };
        Assert.AreEqual(expectedDefault, actual);
    }

    private static string FixturePath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Configuration",
            "configuration-schema-set.netniv.v1.json");
}
