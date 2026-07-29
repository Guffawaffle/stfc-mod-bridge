using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherUiPreferencesStoreTests
{
    [TestMethod]
    public void MissingPreferencesUseDefaults()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var store = new JsonLauncherUiPreferencesStore(
            temporaryDirectory.CreateDirectory("state"));

        var result = store.Load();

        Assert.IsFalse(result.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.System, result.ColorMode);
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
