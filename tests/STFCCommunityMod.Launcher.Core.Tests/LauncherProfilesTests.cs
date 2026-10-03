using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherProfilesTests
{
    private static readonly string[] SelectionOperations = ["paths", "ensure-default", "list"];
    private const string ProfileId = "0123456789abcdef0123456789abcdef";
    private const string DefaultId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static LauncherProfile DefaultProfile() => new(DefaultId, "Default", "", Revision: "default-revision",
        Kind: "windows-user", OwnerUserId: "S-1-5-21-test", BuiltIn: true);

    [TestMethod]
    public async Task SelectionStoresOnlyUiIdentityAndNeverCopiesCatalogMetadata()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingCatalog(Profile());
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        await store.SelectAsync(ProfileId);
        var snapshot = store.Load().Snapshot!;
        Assert.AreEqual(ProfileId, snapshot.SelectedProfileId);
        Assert.AreEqual("Science", snapshot.SelectedProfile!.Name);
        var files = System.IO.Directory.GetFiles(temporary.Path);
        Assert.AreEqual(1, files.Count(path => path.EndsWith(".json", StringComparison.Ordinal)));
        var selection = File.ReadAllText(Path.Combine(temporary.Path, "profile-ui-selection.json"));
        StringAssert.Contains(selection, ProfileId);
        Assert.IsFalse(selection.Contains("Science", StringComparison.Ordinal));
        Assert.IsFalse(selection.Contains("gameDirectory", StringComparison.Ordinal));
        CollectionAssert.AreEqual(SelectionOperations, transport.Requests.Select(request => request.Operation).ToArray());
    }

    [TestMethod]
    public async Task CatalogRenameIsImmediatelyVisibleThroughImmutableSelection()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingCatalog(Profile());
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        await store.SelectAsync(ProfileId);
        transport.Profile = transport.Profile with { Name = "Renamed", Revision = "second" };
        Assert.AreEqual("Renamed", store.Load().Snapshot!.SelectedProfile!.Name);
        Assert.AreEqual(ProfileId, store.LoadSelectedId());
    }

    [TestMethod]
    public async Task ArchivedSelectionNeverBecomesDefaultWithoutExplicitSelection()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingCatalog(Profile());
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        await store.SelectAsync(ProfileId);
        transport.Profile = transport.Profile with { State = "archived" };
        var active = store.Load();
        Assert.AreEqual(ProfileId, active.Snapshot!.SelectedProfileId);
        Assert.IsNull(active.Snapshot.SelectedProfile);
        StringAssert.Contains(active.Error!, "Restore");
        Assert.AreEqual(ProfileId, store.Load(archived: true).Snapshot!.Profiles.Single().Id);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.SelectAsync(ProfileId));
        await store.SelectAsync(null);
        Assert.AreEqual(DefaultId, store.LoadSelectedId());
    }

    [TestMethod]
    public void CatalogReportsInvalidDirectoriesAlongsideValidProfiles()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingCatalog(Profile())
        {
            Issues = [new("invalid", "profiles/invalid", "invalid_metadata", "Unsupported metadata")],
        };
        var loaded = new NativeLauncherProfilesStore(temporary.Path, transport).Load();
        Assert.AreEqual(LauncherProfilesLoadState.Loaded, loaded.State);
        Assert.AreEqual(2, loaded.Snapshot!.Profiles.Count);
        Assert.AreEqual("invalid_metadata", loaded.Snapshot.Issues!.Single().Code);
    }

    [TestMethod]
    public async Task ArchiveAndRestoreUseSharedOperationsWithExpectedRevision()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingCatalog(Profile());
        var store = new NativeLauncherProfilesStore(temporary.Path, transport, temporary.Path);
        await store.ArchiveAsync(transport.Profile);
        var request = transport.Requests.Single();
        Assert.AreEqual("archive", request.Operation);
        Assert.AreEqual(ProfileId, request.Id);
        Assert.AreEqual("first", request.ExpectedRevision);
        Assert.AreEqual(temporary.Path, request.Root);
        await store.RestoreAsync(transport.Profile);
        Assert.AreEqual("restore", transport.Requests.Last().Operation);
        Assert.IsFalse(System.IO.Directory.Exists(Path.Combine(temporary.Path, "profiles")));
    }

    [TestMethod]
    public async Task ClearingThePreferredInstallationIsAnExplicitEmptyNativeField()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingCatalog(Profile());
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        await store.EditAsync(transport.Profile, "Science", "");
        Assert.AreEqual(string.Empty, transport.Requests.Single().GameDirectory);
    }

    [TestMethod]
    public async Task StaleSharedMutationFailsAndDoesNotWriteBridgeMetadata()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingCatalog(Profile()) { Failure = new("stale_revision", "Profile changed") };
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => store.EditAsync(transport.Profile, "New name", ""));
        StringAssert.Contains(exception.Message, "stale_revision");
        Assert.AreEqual(0, System.IO.Directory.GetFiles(temporary.Path).Length);
    }

    [TestMethod]
    public async Task CatalogFailurePreservesNamedSelectionAndCannotInventDefaultIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingCatalog(Profile());
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        await store.SelectAsync(ProfileId);
        transport.Failure = new("unavailable", "Catalog is unavailable");
        var loaded = store.Load();
        Assert.AreEqual(LauncherProfilesLoadState.Invalid, loaded.State);
        Assert.AreEqual(ProfileId, loaded.Snapshot!.SelectedProfileId);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.SelectAsync(null));
        Assert.AreEqual(ProfileId, store.LoadSelectedId());
    }

    [TestMethod]
    public void CorruptUiSelectionIsNotInterpretedAsDefault()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "profile-ui-selection.json"), "{broken");
        var loaded = new NativeLauncherProfilesStore(temporary.Path, new RecordingCatalog(Profile())).Load();
        Assert.AreEqual(LauncherProfilesLoadState.Invalid, loaded.State);
        Assert.IsNull(loaded.Snapshot);
        var repair = new NativeLauncherProfilesStore(temporary.Path, new RecordingCatalog(Profile()))
            .Load(allowSelectionRepair: true);
        Assert.AreEqual(LauncherProfilesLoadState.Loaded, repair.State);
        Assert.AreEqual(2, repair.Snapshot!.Profiles.Count);
        StringAssert.Contains(repair.Error!, "needs repair");
        Assert.AreEqual("{broken", File.ReadAllText(Path.Combine(temporary.Path, "profile-ui-selection.json")));
    }

    [TestMethod]
    public void NativeComponentRequiresExactCompiledBytePairingBeforeLoading()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, NativeProfileCatalogTransport.LibraryName);
        File.WriteAllBytes(path, [1, 2, 3]);
        var transport = new NativeProfileCatalogTransport(path, new string('a', 64));
        Assert.ThrowsException<InvalidDataException>(() => transport.Request(new("list")));
        var unqualified = new NativeProfileCatalogTransport(path, new string('0', 64));
        Assert.ThrowsException<InvalidOperationException>(() => unqualified.Request(new("list")));
    }

    private static LauncherProfile Profile() => new(ProfileId, "Science", "", "catalog/profiles/" + ProfileId,
        "catalog/profiles/" + ProfileId + "/config.toml", "catalog/profiles/" + ProfileId + "/logs/Player.log", "first");

    [TestMethod]
    public async Task DefaultHasExplicitSharedIdentityAndDoesNotLaunchAsIsolated()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingCatalog(Profile());
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        var loaded = store.Load();
        Assert.IsTrue(loaded.Snapshot!.SelectedProfile!.IsDefault);
        Assert.AreEqual(DefaultId, loaded.Snapshot.SelectedProfileId);
        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "profile-ui-selection.json")));
        await store.SelectAsync(null);
        Assert.AreEqual(DefaultId, store.LoadSelectedId());
        await store.LaunchAsync(loaded.Snapshot.SelectedProfile);
        Assert.AreEqual("launch-ordinary", transport.Requests.Last().Operation);
        Assert.IsTrue(transport.Requests.All(request => request.ApiVersion == 2));
        Assert.AreEqual("", loaded.Snapshot.SelectedProfile.ConfigPath);
        Assert.AreEqual("", loaded.Snapshot.SelectedProfile.Directory);
    }

    private sealed class RecordingCatalog(LauncherProfile profile) : IProfileCatalogTransport
    {
        public LauncherProfile Profile { get; set; } = profile;
        public ProfileCatalogError? Failure { get; set; }
        public IReadOnlyList<ProfileCatalogIssue> Issues { get; set; } = [];
        public List<ProfileCatalogRequest> Requests { get; } = [];
        public ProfileCatalogResponse Request(ProfileCatalogRequest request)
        {
            Requests.Add(request);
            if (Failure is not null) return new(false, Error: Failure);
            if (request.Operation is "ensure-default" or "resolve-default") return new(true, Profile: DefaultProfile());
            if (request.Operation == "paths" && request.Id == DefaultId) return new(true, Profile: DefaultProfile());
            IReadOnlyList<LauncherProfile> profiles = request.Archived
                ? Profile.State == "archived" ? [Profile] : []
                : Profile.State == "active" ? [DefaultProfile(), Profile] : [DefaultProfile()];
            return new(true, Profiles: profiles,
                Profile: Profile, Issues: Issues, Revision: "catalog-first");
        }
    }
}
