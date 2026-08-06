using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherConfigurationSchemaSetLoaderTests
{
    private const string StableCommit = "d912611fa1eca49fc54f363bdf8377dfebf8def0";
    private const string DevCommit = "238004460c4bb93aa717e47c41089fe8b71c4cf9";
    private static readonly string[] DisabledHotkeyAliases =
        ["shortcuts.set_hotkeys_disble", "shortcuts.set_hotkeys_disable"];

    [TestMethod]
    public void ExactStableAndDevApplicabilityPreservesReviewedDelta()
    {
        var stable = Load("stable", "1.1.4", StableCommit);
        var dev = Load("dev", "1.1.5.1", DevCommit);

        Assert.AreEqual("netniv.configuration.stable-1.1.4", stable.Identity.CatalogId);
        Assert.AreEqual(new Version(1, 1, 4, 1), stable.Identity.CatalogVersion);
        Assert.AreEqual(StableCommit, stable.Identity.SourceCommit);
        Assert.AreEqual(203, stable.Settings.Count);
        Assert.AreEqual("netniv.configuration.dev-1.1.5.1", dev.Identity.CatalogId);
        Assert.AreEqual(new Version(1, 1, 5, 1), dev.Identity.CatalogVersion);
        Assert.AreEqual(DevCommit, dev.Identity.SourceCommit);
        Assert.AreEqual(206, dev.Settings.Count);

        var devOnly = new[]
        {
            "patches.cargoformathooks",
            "patches.officersorthooks",
            "ui.cargo_significant_decimals",
        };
        Assert.IsTrue(devOnly.All(path => dev.Settings.Any(setting => setting.Path == path)));
        Assert.IsTrue(devOnly.All(path => stable.Settings.All(setting => setting.Path != path)));
        StringAssert.Contains(
            stable.Settings.Single(setting => setting.Path == "ui.extend_donation_max").Description,
            "ordinary donation cap");
        StringAssert.Contains(
            dev.Settings.Single(setting => setting.Path == "ui.extend_donation_max").Description,
            "unlimited donation");
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
    [DataRow("stable", "1.1.4", "238004460c4bb93aa717e47c41089fe8b71c4cf9")]
    [DataRow("stable", "1.1.5", StableCommit)]
    [DataRow("dev", "1.1.5.1", StableCommit)]
    [DataRow("preview", "1.1.4", StableCommit)]
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
                new("guffawaffle", "stable", "1.1.4", StableCommit)));
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
