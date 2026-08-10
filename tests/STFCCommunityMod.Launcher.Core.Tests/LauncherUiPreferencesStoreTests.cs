using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherUiPreferencesStoreTests
{
    [TestMethod]
    public void BattleCompareAndSwapRejectsInvalidExistingDocumentWithoutWriting()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "ui-preferences.json");
        var invalid = "{\"schemaVersion\":5,\"schemaVersion\":5}";
        File.WriteAllText(path, invalid);
        var store = new JsonLauncherUiPreferencesStore(temporaryDirectory.Path);

        var saved = store.TrySaveBattlePreferences(
            LauncherBattlePreferences.Default,
            new(
                LauncherPlayerFeaturePreference.Enabled,
                LauncherPlayerFeaturePreference.Unset));

        Assert.IsFalse(saved);
        Assert.AreEqual(invalid, File.ReadAllText(path));
    }

    [TestMethod]
    public void BattleCompareAndSwapRejectsNoncanonicalPreferenceWithoutWriting()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "ui-preferences.json");
        var invalid = """
            {
              "schemaVersion": 5,
              "settingsSearchVisible": false,
              "colorMode": "System",
              "launchTarget": "ScopelyLauncher",
              "providerSwitchReviewAcknowledged": false,
              "battleCollectionPreference": "enabled",
              "fleetCollectionPreference": "Unset"
            }
            """;
        File.WriteAllText(path, invalid);
        var store = new JsonLauncherUiPreferencesStore(temporaryDirectory.Path);

        var saved = store.TrySaveBattlePreferences(
            LauncherBattlePreferences.Default,
            new(
                LauncherPlayerFeaturePreference.Enabled,
                LauncherPlayerFeaturePreference.Unset));

        Assert.IsFalse(saved);
        Assert.AreEqual(invalid, File.ReadAllText(path));
    }

    [TestMethod]
    public void MissingPreferencesUseDefaults()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var store = new JsonLauncherUiPreferencesStore(
            temporaryDirectory.CreateDirectory("state"));

        var result = store.Load();

        Assert.IsFalse(result.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.System, result.ColorMode);
        Assert.AreEqual(LauncherLaunchTarget.ScopelyLauncher, result.LaunchTarget);
        Assert.IsFalse(result.ProviderSwitchReviewAcknowledged);
        Assert.AreEqual(LauncherBattlePreferences.Default, result.EffectiveBattlePreferences);
    }

    [TestMethod]
    public void SearchVisibilityRoundTripsAcrossStoreInstances()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");

        new JsonLauncherUiPreferencesStore(stateDirectory)
            .Save(new LauncherUiPreferences(SettingsSearchVisible: true));
        var result = new JsonLauncherUiPreferencesStore(stateDirectory).Load();

        Assert.IsTrue(result.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.System, result.ColorMode);
        Assert.AreEqual(LauncherLaunchTarget.ScopelyLauncher, result.LaunchTarget);
    }

    [TestMethod]
    public void ColorModeRoundTripsAcrossStoreInstances()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");

        new JsonLauncherUiPreferencesStore(stateDirectory)
            .Save(
                new LauncherUiPreferences(
                    SettingsSearchVisible: true,
                    ColorMode: LauncherColorMode.Dark));
        var result = new JsonLauncherUiPreferencesStore(stateDirectory).Load();

        Assert.IsTrue(result.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.Dark, result.ColorMode);
    }

    [TestMethod]
    public void LaunchTargetRoundTripsAcrossStoreInstances()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");

        new JsonLauncherUiPreferencesStore(stateDirectory)
            .Save(
                new LauncherUiPreferences(
                    SettingsSearchVisible: false,
                    LaunchTarget: LauncherLaunchTarget.PrimeExecutable));

        var result = new JsonLauncherUiPreferencesStore(stateDirectory).Load();

        Assert.AreEqual(LauncherLaunchTarget.PrimeExecutable, result.LaunchTarget);
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(stateDirectory, "ui-preferences.json")),
            "PrimeExecutable");
    }

    [TestMethod]
    public void ProviderSwitchReviewAcknowledgementRoundTripsAsOneGlobalPreference()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");

        new JsonLauncherUiPreferencesStore(stateDirectory)
            .Save(
                new LauncherUiPreferences(
                    SettingsSearchVisible: false,
                    ProviderSwitchReviewAcknowledged: true));

        var result = new JsonLauncherUiPreferencesStore(stateDirectory).Load();

        Assert.IsTrue(result.ProviderSwitchReviewAcknowledged);
        var contents = File.ReadAllText(Path.Combine(stateDirectory, "ui-preferences.json"));
        StringAssert.Contains(contents, "providerSwitchReviewAcknowledged");
        Assert.IsFalse(contents.Contains("providerPair", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(contents.Contains("providerVersion", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BattleAndFleetPreferencesRoundTripIndependently()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");

        new JsonLauncherUiPreferencesStore(stateDirectory).Save(
            new(
                SettingsSearchVisible: false,
                BattlePreferences: new(
                    LauncherPlayerFeaturePreference.Enabled,
                    LauncherPlayerFeaturePreference.Disabled)));
        var result = new JsonLauncherUiPreferencesStore(stateDirectory).Load();

        Assert.AreEqual(
            LauncherPlayerFeaturePreference.Enabled,
            result.EffectiveBattlePreferences.BattleCollection);
        Assert.AreEqual(
            LauncherPlayerFeaturePreference.Disabled,
            result.EffectiveBattlePreferences.FleetCollection);
        var contents = File.ReadAllText(Path.Combine(stateDirectory, "ui-preferences.json"));
        StringAssert.Contains(contents, "battleCollectionPreference");
        StringAssert.Contains(contents, "fleetCollectionPreference");
    }

    [TestMethod]
    public void BattleCompareAndSwapPreservesEveryUnrelatedPreference()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var store = new JsonLauncherUiPreferencesStore(stateDirectory);
        store.Save(new(
            SettingsSearchVisible: true,
            ColorMode: LauncherColorMode.Dark,
            LaunchTarget: LauncherLaunchTarget.PrimeExecutable,
            ProviderSwitchReviewAcknowledged: true,
            BattlePreferences: LauncherBattlePreferences.Default));

        var saved = store.TrySaveBattlePreferences(
            LauncherBattlePreferences.Default,
            new(
                LauncherPlayerFeaturePreference.Enabled,
                LauncherPlayerFeaturePreference.Unset));
        var result = store.Load();

        Assert.IsTrue(saved);
        Assert.IsTrue(result.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.Dark, result.ColorMode);
        Assert.AreEqual(LauncherLaunchTarget.PrimeExecutable, result.LaunchTarget);
        Assert.IsTrue(result.ProviderSwitchReviewAcknowledged);
        Assert.AreEqual(
            LauncherPlayerFeaturePreference.Enabled,
            result.EffectiveBattlePreferences.BattleCollection);
        Assert.AreEqual(
            LauncherPlayerFeaturePreference.Unset,
            result.EffectiveBattlePreferences.FleetCollection);
    }

    [TestMethod]
    public void StaleBattleCompareAndSwapIsAByteExactNoOp()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var path = Path.Combine(stateDirectory, "ui-preferences.json");
        var store = new JsonLauncherUiPreferencesStore(stateDirectory);
        store.Save(new(
            SettingsSearchVisible: true,
            BattlePreferences: new(
                LauncherPlayerFeaturePreference.Disabled,
                LauncherPlayerFeaturePreference.Unset)));
        var before = File.ReadAllBytes(path);

        var saved = store.TrySaveBattlePreferences(
            LauncherBattlePreferences.Default,
            new(
                LauncherPlayerFeaturePreference.Enabled,
                LauncherPlayerFeaturePreference.Unset));

        Assert.IsFalse(saved);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task ConcurrentBattleCompareAndSwapHasExactlyOneWinner()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var first = new JsonLauncherUiPreferencesStore(stateDirectory);
        var second = new JsonLauncherUiPreferencesStore(stateDirectory);
        var ready = new CountdownEvent(2);
        var start = new ManualResetEventSlim();
        Task<bool> Attempt(JsonLauncherUiPreferencesStore store, LauncherPlayerFeaturePreference value) =>
            Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                return store.TrySaveBattlePreferences(
                    LauncherBattlePreferences.Default,
                    new(value, LauncherPlayerFeaturePreference.Unset));
            });
        var firstAttempt = Attempt(first, LauncherPlayerFeaturePreference.Enabled);
        var secondAttempt = Attempt(second, LauncherPlayerFeaturePreference.Disabled);
        Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(5)));
        start.Set();

        var results = await Task.WhenAll(firstAttempt, secondAttempt);

        Assert.AreEqual(1, results.Count(value => value));
        Assert.AreNotEqual(
            LauncherPlayerFeaturePreference.Unset,
            first.Load().EffectiveBattlePreferences.BattleCollection);
    }

    [TestMethod]
    public void OlderAndInvalidFeaturePreferencesFailClosedToUnset()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var path = Path.Combine(stateDirectory, "ui-preferences.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 4,
              "settingsSearchVisible": true,
              "providerSwitchReviewAcknowledged": true,
              "battleCollectionPreference": "Enabled",
              "fleetCollectionPreference": "Disabled"
            }
            """);

        var old = new JsonLauncherUiPreferencesStore(stateDirectory).Load();
        Assert.AreEqual(LauncherBattlePreferences.Default, old.EffectiveBattlePreferences);
        Assert.IsTrue(old.ProviderSwitchReviewAcknowledged);

        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 5,
              "settingsSearchVisible": true,
              "battleCollectionPreference": "enabled",
              "fleetCollectionPreference": "RemoteOverride"
            }
            """);
        var invalid = new JsonLauncherUiPreferencesStore(stateDirectory).Load();
        Assert.AreEqual(LauncherBattlePreferences.Default, invalid.EffectiveBattlePreferences);
    }

    [TestMethod]
    public void InvalidInMemoryFeaturePreferenceIsRejectedBeforePersistence()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var store = new JsonLauncherUiPreferencesStore(stateDirectory);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            store.Save(
                new(
                    SettingsSearchVisible: false,
                    BattlePreferences: new(
                        (LauncherPlayerFeaturePreference)99,
                        LauncherPlayerFeaturePreference.Unset))));
        Assert.IsFalse(File.Exists(Path.Combine(stateDirectory, "ui-preferences.json")));
    }

    [TestMethod]
    public void VersionOnePreferencesKeepSearchAndDefaultToSystemColorMode()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        File.WriteAllText(
            Path.Combine(stateDirectory, "ui-preferences.json"),
            """
            {
              "schemaVersion": 1,
              "settingsSearchVisible": true
            }
            """);

        var result = new JsonLauncherUiPreferencesStore(stateDirectory).Load();

        Assert.IsTrue(result.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.System, result.ColorMode);
        Assert.AreEqual(LauncherLaunchTarget.ScopelyLauncher, result.LaunchTarget);
    }

    [TestMethod]
    public void InvalidColorModeFallsBackToSystemWithoutDroppingSearch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        File.WriteAllText(
            Path.Combine(stateDirectory, "ui-preferences.json"),
            """
            {
              "schemaVersion": 2,
              "settingsSearchVisible": true,
              "colorMode": "lcars"
            }
            """);

        var result = new JsonLauncherUiPreferencesStore(stateDirectory).Load();

        Assert.IsTrue(result.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.System, result.ColorMode);
    }

    [TestMethod]
    public void VersionTwoPreferencesDefaultLaunchTargetWithoutDroppingExistingValues()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        File.WriteAllText(
            Path.Combine(stateDirectory, "ui-preferences.json"),
            """
            {
              "schemaVersion": 2,
              "settingsSearchVisible": true,
              "colorMode": "Dark"
            }
            """);

        var result = new JsonLauncherUiPreferencesStore(stateDirectory).Load();

        Assert.IsTrue(result.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.Dark, result.ColorMode);
        Assert.AreEqual(LauncherLaunchTarget.ScopelyLauncher, result.LaunchTarget);
        Assert.IsFalse(result.ProviderSwitchReviewAcknowledged);
    }

    [TestMethod]
    public void UnknownLaunchTargetFallsBackWithoutDroppingExistingValues()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        File.WriteAllText(
            Path.Combine(stateDirectory, "ui-preferences.json"),
            """
            {
              "schemaVersion": 3,
              "settingsSearchVisible": true,
              "colorMode": "Light",
              "launchTarget": "FerengiShuttle"
            }
            """);

        var result = new JsonLauncherUiPreferencesStore(stateDirectory).Load();

        Assert.IsTrue(result.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.Light, result.ColorMode);
        Assert.AreEqual(LauncherLaunchTarget.ScopelyLauncher, result.LaunchTarget);
        Assert.IsFalse(result.ProviderSwitchReviewAcknowledged);
    }

    [TestMethod]
    public void InvalidPreferencesFallBackToDefaults()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        File.WriteAllText(Path.Combine(stateDirectory, "ui-preferences.json"), "{ definitely not json");
        var store = new JsonLauncherUiPreferencesStore(stateDirectory);

        var result = store.Load();

        Assert.AreEqual(LauncherUiPreferences.Default, result);
    }
}
