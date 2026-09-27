using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherProfilesTests
{
    [TestMethod]
    public void AdoptExistingProfilePreservesKeyAndDerivesSeparatePaths()
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root, "josep");
        var snapshot = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Josep", game, null, "josep");
        var profile = snapshot.Profiles.Single();

        Assert.AreEqual("josep", profile.Id);
        Assert.AreEqual(Path.Combine(game, "stfc-mod", "josep", "josep.toml"), LauncherProfiles.GameConfigPath(profile));
        Assert.AreEqual(Path.Combine(root.Path, "STFC Community Mod", "Profiles", "josep", "Player.log"),
            LauncherProfiles.UnityLogPath(profile, root.Path));
    }

    [TestMethod]
    public void NewProfileGetsStableKeyAcrossEditAndRoundTrip()
    {
        using var root = new TemporaryDirectory();
        var first = MakeGame(root, "first");
        var second = MakeGame(root, "second");
        var store = new JsonLauncherProfilesStore(root.CreateDirectory("state"));
        var added = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Second", first, null);
        var profileId = added.Profiles.Single().Id;
        Assert.AreEqual(32, profileId.Length);

        var updated = LauncherProfiles.Select(LauncherProfiles.Edit(added, profileId, "Renamed", second, null), profileId);
        store.Save(updated);
        var loaded = store.Load();

        Assert.AreEqual(LauncherProfilesLoadState.Loaded, loaded.State);
        Assert.IsNotNull(loaded.Snapshot);
        Assert.AreEqual(profileId, loaded.Snapshot.SelectedProfileId);
        Assert.AreEqual("Renamed", loaded.Snapshot.SelectedProfile!.Name);
        Assert.AreEqual(second, loaded.Snapshot.SelectedProfile.GameDirectory);
    }

    [TestMethod]
    public void DuplicateGameFolderAndDefaultFolderAreRejected()
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root, "game");
        var snapshot = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "One", game, null);

        Assert.ThrowsException<InvalidOperationException>(() =>
            LauncherProfiles.Add(snapshot, "Two", game, null));
        Assert.ThrowsException<InvalidOperationException>(() =>
            LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "One", game, game));
    }

    [TestMethod]
    public void DuplicateProfileKeyIsRejectedIgnoringWindowsCase()
    {
        using var root = new TemporaryDirectory();
        var first = MakeGame(root, "first");
        var second = MakeGame(root, "second");
        var snapshot = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Josep", first, null, "josep");

        Assert.ThrowsException<InvalidOperationException>(() =>
            LauncherProfiles.Add(snapshot, "Another", second, null, "JOSEP"));
    }

    [TestMethod]
    public void InvalidRegistryDoesNotGetReplacedBySaveAttempt()
    {
        using var root = new TemporaryDirectory();
        var state = root.CreateDirectory("state");
        var path = Path.Combine(state, "launch-profiles.json");
        File.WriteAllText(path, "{ broken json");
        var store = new JsonLauncherProfilesStore(state);

        Assert.AreEqual(LauncherProfilesLoadState.Invalid, store.Load().State);
        Assert.ThrowsException<InvalidOperationException>(() => store.Save(LauncherProfilesSnapshot.Empty));
        Assert.AreEqual("{ broken json", File.ReadAllText(path));
    }

    [TestMethod]
    public void RemovingProfileOnlyRemovesRegistryEntry()
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root, "game");
        var config = Path.Combine(game, "stfc-mod", "josep", "josep.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "kept = true");
        var snapshot = LauncherProfiles.Select(
            LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Josep", game, null, "josep"), "josep");

        var removed = LauncherProfiles.Remove(snapshot, "josep");

        Assert.IsNull(removed.SelectedProfileId);
        Assert.AreEqual(0, removed.Profiles.Count);
        Assert.IsTrue(File.Exists(config));
    }

    private static string MakeGame(TemporaryDirectory root, string name)
    {
        var directory = root.CreateDirectory(name);
        TemporaryDirectory.CreateFile(directory, "prime.exe");
        return directory;
    }
}
