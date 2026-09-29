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
        var second = MakeGame(root, "second");
        var store = new JsonLauncherProfilesStore(root.CreateDirectory("state"));
        var added = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Second", first, null);
        var profileId = added.Profiles.Single().Id;
        Assert.AreEqual(32, profileId.Length);

        var updated = LauncherProfiles.Select(LauncherProfiles.Edit(added, profileId, "Renamed", second, null), profileId);
        await store.SaveAsync(updated, store.Load().Revision!);
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
    public async Task NewProfileCreatesAStableMarkerBeforeItCanBeSelected()
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root, "new-game");
        LauncherProfileLaunchContractTests.WriteProfileDll(Path.Combine(game, "version.dll"));
        var inspector = new FakeGameProcessInspector(GameProcessInspectionState.NotRunning);
        var store = new JsonLauncherProfilesStore(root.CreateDirectory("state"), inspector);

        var created = await store.CreateNewAsync("Secondary", game, null, store.Load().Revision!);
        var profile = created.Snapshot.Profiles.Single();

        Assert.AreEqual(game, inspector.InspectedGameDirectory);
        Assert.AreEqual($"v1:{profile.Id}\n", File.ReadAllText(Path.Combine(game, "stfc_community_mod.profile")));
        Assert.IsTrue(LauncherProfileLaunchContract.Inspect(game, profile.Id).IsValid);
        var selected = await store.SelectAsync(profile.Id, null, created.Revision);
        Assert.AreEqual(profile.Id, selected.Snapshot.SelectedProfileId);
    }

    [TestMethod]
    public async Task StaleCreationDoesNotWriteAMarker()
    {
        using var root = new TemporaryDirectory();
        var first = MakeGame(root, "first");
        var second = MakeGame(root, "second");
        LauncherProfileLaunchContractTests.WriteProfileDll(Path.Combine(first, "version.dll"));
        LauncherProfileLaunchContractTests.WriteProfileDll(Path.Combine(second, "version.dll"));
        var store = new JsonLauncherProfilesStore(root.CreateDirectory("state"),
            new FakeGameProcessInspector(GameProcessInspectionState.NotRunning));
        var staleRevision = store.Load().Revision!;
        await store.CreateNewAsync("First", first, null, staleRevision);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.CreateNewAsync("Second", second, null, staleRevision));
        Assert.IsFalse(File.Exists(Path.Combine(second, "stfc_community_mod.profile")));
    }

    [DataTestMethod]
    [DataRow(GameProcessInspectionState.RunningTarget)]
    [DataRow(GameProcessInspectionState.Unattributable)]
    public async Task RunningOrUnattributableGameCannotCreateProfileMarker(GameProcessInspectionState processState)
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root, "new-game");
        LauncherProfileLaunchContractTests.WriteProfileDll(Path.Combine(game, "version.dll"));
        var inspector = new FakeGameProcessInspector(processState);
        var store = new JsonLauncherProfilesStore(root.CreateDirectory("state"), inspector);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.CreateNewAsync("Secondary", game, null, store.Load().Revision!));

        Assert.AreEqual(game, inspector.InspectedGameDirectory);
        Assert.IsFalse(File.Exists(Path.Combine(game, "stfc_community_mod.profile")));
        Assert.AreEqual(0, Directory.GetFiles(game, ".stfc-profile.*.tmp").Length);
        Assert.AreEqual(LauncherProfilesLoadState.Missing, store.Load().State);
    }

    [TestMethod]
    public void JunctionAliasCannotRegisterTheDefaultInstallAsNamed()
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
        Assert.ThrowsException<InvalidOperationException>(() =>
            LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Alias", alias, game));
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

    private sealed class FakeGameProcessInspector(GameProcessInspectionState state) : IGameProcessInspector
    {
        public string? InspectedGameDirectory { get; private set; }

        public GameProcessInspectionState Inspect(string gameDirectory)
        {
            InspectedGameDirectory = gameDirectory;
            return state;
        }
    }
}
