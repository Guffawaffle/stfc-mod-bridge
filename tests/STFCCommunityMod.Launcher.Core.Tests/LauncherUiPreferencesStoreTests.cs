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
