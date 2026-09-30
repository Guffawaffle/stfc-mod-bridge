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
    public async Task NewProfileGetsStableKeyAcrossEditAndRoundTrip()
    {
        using var root = new TemporaryDirectory();
        var first = MakeGame(root, "first");
        var store = new JsonLauncherProfilesStore(root.CreateDirectory("state"));
        var added = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Second", first, null);
        var profileId = added.Profiles.Single().Id;
        Assert.AreEqual(32, profileId.Length);

        var updated = LauncherProfiles.Select(LauncherProfiles.Edit(added, profileId, "Renamed", first, null), profileId);
        await store.SaveAsync(updated, store.Load().Revision!);
        var loaded = store.Load();

        Assert.AreEqual(LauncherProfilesLoadState.Loaded, loaded.State);
        Assert.IsNotNull(loaded.Snapshot);
        Assert.AreEqual(profileId, loaded.Snapshot.SelectedProfileId);
        Assert.AreEqual("Renamed", loaded.Snapshot.SelectedProfile!.Name);
        Assert.AreEqual(first, loaded.Snapshot.SelectedProfile.GameDirectory);
    }

    [TestMethod]
    public void ProfileIdIsStableWhenItsGameFolderChanges()
    {
        using var root = new TemporaryDirectory();
        var first = MakeGame(root, "first");
        var second = MakeGame(root, "second");
        var snapshot = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Secondary", first, null);

        var updated = LauncherProfiles.Edit(snapshot, snapshot.Profiles.Single().Id, "Renamed", second, null);
        Assert.AreEqual(snapshot.Profiles.Single().Id, updated.Profiles.Single().Id);
        Assert.AreEqual(second, updated.Profiles.Single().GameDirectory);
    }

    [TestMethod]
    public void DifferentProfilesCanRecordTheSameGameFolder()
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root, "game");
        var snapshot = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "One", game, null);

        var shared = LauncherProfiles.Add(snapshot, "Two", game, game);
        Assert.AreEqual(2, shared.Profiles.Count);
        Assert.AreNotEqual(shared.Profiles[0].Id, shared.Profiles[1].Id);
    }

    [TestMethod]
    public async Task StaleProfileRegistryRevisionCannotOverwriteAnotherWindow()
    {
        using var root = new TemporaryDirectory();
        var state = root.CreateDirectory("state");
        var first = MakeGame(root, "first");
        var second = MakeGame(root, "second");
        var store = new JsonLauncherProfilesStore(state);
        var windowA = store.Load();
        var windowB = store.Load();

        var savedA = LauncherProfiles.Add(windowA.Snapshot!, "First", first, null);
        await store.SaveAsync(savedA, windowA.Revision!);
        var savedB = LauncherProfiles.Add(windowB.Snapshot!, "Second", second, null);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.SaveAsync(savedB, windowB.Revision!));

        Assert.AreEqual("First", store.Load().Snapshot!.Profiles.Single().Name);
    }

    [TestMethod]
    public async Task NewProfileGetsARegistryKeyBeforeItCanBeSelected()
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root, "new-game");
        var store = new JsonLauncherProfilesStore(root.CreateDirectory("state"));

        var created = await store.CreateNewAsync("Secondary", game, null, store.Load().Revision!);
        var profile = created.Snapshot.Profiles.Single();

        var selected = await store.SelectAsync(profile.Id, null, created.Revision);
        Assert.AreEqual(profile.Id, selected.Snapshot.SelectedProfileId);
    }

    [TestMethod]
    public async Task StaleCreationDoesNotReplaceTheRegistry()
    {
        using var root = new TemporaryDirectory();
        var first = MakeGame(root, "first");
        var second = MakeGame(root, "second");
        var store = new JsonLauncherProfilesStore(root.CreateDirectory("state"));
        var staleRevision = store.Load().Revision!;
        await store.CreateNewAsync("First", first, null, staleRevision);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.CreateNewAsync("Second", second, null, staleRevision));
    }

    [TestMethod]
    public async Task ChangedDefaultSelectionCannotBecomeANewNamedProfile()
    {
        using var root = new TemporaryDirectory();
        var state = root.CreateDirectory("state");
        var previousDefault = MakeGame(root, "previous-default");
        var newDefault = MakeGame(root, "new-default");
        var store = new JsonLauncherProfilesStore(state);
        var defaultStore = new JsonGameInstallSelectionStore(state);
        defaultStore.Save(previousDefault);
        var capturedRevision = store.Load().Revision!;
        defaultStore.Save(newDefault);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.CreateNewAsync("Secondary", newDefault, previousDefault, capturedRevision));
        Assert.AreEqual(LauncherProfilesLoadState.Missing, store.Load().State);
    }

    [TestMethod]
    public async Task ChangedDefaultSelectionCannotSelectOverlappingNamedProfile()
    {
        using var root = new TemporaryDirectory();
        var state = root.CreateDirectory("state");
        var previousDefault = MakeGame(root, "previous-default");
        var newDefault = MakeGame(root, "new-default");
        var store = new JsonLauncherProfilesStore(state);
        var defaultStore = new JsonGameInstallSelectionStore(state);
        defaultStore.Save(previousDefault);
        var created = await store.CreateNewAsync("Secondary", newDefault, previousDefault, store.Load().Revision!);
        defaultStore.Save(newDefault);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.SelectAsync(created.Snapshot.Profiles.Single().Id, previousDefault, created.Revision));
        Assert.IsNull(store.Load().Snapshot!.SelectedProfileId);
    }

    [TestMethod]
    public void JunctionAliasCanBeRecordedWithoutChangingProfileIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var root = new TemporaryDirectory();
        var game = MakeGame(root, "game");
        var alias = Path.Combine(root.Path, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, game);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Windows could not create a directory alias for this test: {exception.Message}");
        }
        Assert.IsTrue(GameDirectoryIdentity.SameLocation(game, alias));
        var tracked = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Alias", alias, game, "dev");
        Assert.AreEqual("dev", tracked.Profiles.Single().Id);
    }

    [TestMethod]
    public void ExtendedPathAliasHasTheSamePhysicalIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var root = new TemporaryDirectory();
        var game = MakeGame(root, "game");
        var extended = @"\\?\" + game;

        Assert.IsTrue(GameDirectoryIdentity.SameLocation(game, extended));
    }

    [TestMethod]
    public void UppercaseAdoptedKeyIsRejected()
    {
        using var root = new TemporaryDirectory();
        var first = MakeGame(root, "first");
        var second = MakeGame(root, "second");
        var snapshot = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Josep", first, null, "josep");

        Assert.ThrowsException<ArgumentException>(() =>
            LauncherProfiles.Add(snapshot, "Another", second, null, "JOSEP"));
    }

    [TestMethod]
    public async Task InvalidRegistryDoesNotGetReplacedBySaveAttempt()
    {
        using var root = new TemporaryDirectory();
        var state = root.CreateDirectory("state");
        var path = Path.Combine(state, "launch-profiles.json");
        File.WriteAllText(path, "{ broken json");
        var store = new JsonLauncherProfilesStore(state);

        Assert.AreEqual(LauncherProfilesLoadState.Invalid, store.Load().State);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.SaveAsync(LauncherProfilesSnapshot.Empty, "missing"));
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
