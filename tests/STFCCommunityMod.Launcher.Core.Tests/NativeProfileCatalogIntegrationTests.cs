using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class NativeProfileCatalogIntegrationTests
{
    [TestMethod]
    public void BundledInstallationAbiReportsNumericVersionAndReaderLeaseExcludesUpdater()
    {
        var transport = Transport();
        using var temporary = new TemporaryDirectory();
        var game = temporary.CreateDirectory("game");
        var root = temporary.CreateDirectory("catalog");
        File.WriteAllText(Path.Combine(game, ".version"), "&game=221");
        File.WriteAllBytes(Path.Combine(game, "prime.exe"), [1, 2, 3]);
        var observed = transport.Request(new("installation-status", Root: root, GameDirectory: game));
        Assert.IsTrue(observed.Ok, observed.Error?.Message);
        Assert.AreEqual(221, observed.Installation!.InstalledVersion);
        Assert.AreEqual(game, observed.Installation.GameDirectory);
        Assert.AreEqual("ready", observed.Installation.State);
        using (transport.AcquireInstallationLease(new("installation-status", Root: root, GameDirectory: game)))
        {
            var refused = transport.Request(new("update-game", Root: root, GameDirectory: game, ExpectedVersion: 267));
            Assert.IsFalse(refused.Ok);
            Assert.AreEqual("busy", refused.Error!.Code);
        }
        Assert.IsTrue(transport.Request(new("installation-status", Root: root, GameDirectory: game)).Ok);
    }

    [TestMethod]
    public async Task BundledAbiRoundTripsUtf8CatalogAndExcludesArchiveDuringConfigLease()
    {
        var transport = Transport();
        using var temporary = new TemporaryDirectory();
        var root = temporary.CreateDirectory("catalog");
        var store = new NativeLauncherProfilesStore(temporary.CreateDirectory("bridge"), transport, root);
        var created = await store.CreateNewAsync("Science Ω", "");
        Assert.IsTrue(LauncherProfiles.ValidId(created.Id));
        Assert.AreEqual("Science Ω", created.Name);
        Assert.AreEqual(Path.Combine(root, "profiles", created.Id, "config.toml"), created.ConfigPath);
        Assert.AreEqual(Path.Combine(root, "profiles", created.Id, "logs", "Player.log"), created.LogPath);
        await store.SelectAsync(created.Id);
        Assert.AreEqual(created.Id, store.Load().Snapshot!.SelectedProfile!.Id);
        var edited = await store.EditAsync(created, "Renamed Ω", "");
        Assert.AreEqual(created.Id, edited.Id);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.EditAsync(created, "Stale", ""));
        File.WriteAllText(Path.Combine(edited.Directory, "owned-data.txt"), "retained account-adjacent data");
        using (store.AcquireDataLease(edited.Id))
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.ArchiveAsync(edited));
        var archived = await store.ArchiveAsync(edited);
        Assert.AreEqual("archived", archived.State);
        Assert.IsTrue(File.Exists(Path.Combine(archived.Directory, "owned-data.txt")));
        Assert.IsNull(store.Load().Snapshot!.SelectedProfile);
        Assert.AreEqual(edited.Id, store.LoadSelectedId());
        var restored = await store.RestoreAsync(archived);
        Assert.AreEqual(edited.Id, restored.Id);
        Assert.AreEqual("active", restored.State);
        Assert.AreEqual("Renamed Ω", store.Load().Snapshot!.SelectedProfile!.Name);
    }

    [TestMethod]
    public void BundledAbiCatalogLocationUsesUnredirectedOsUserFolderWithoutCatalogMutation()
    {
        var transport = Transport();
        var location = transport.Request(new("catalog-location"));
        Assert.IsTrue(location.Ok, location.Error?.Message);
        var folderId = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        var status = SHGetKnownFolderPath(ref folderId, 0x00010000, IntPtr.Zero, out var value);
        try
        {
            Assert.AreEqual(0, status);
            var expected = Path.Combine(Marshal.PtrToStringUni(value)!, "STFC Profiles");
            Assert.IsTrue(string.Equals(expected, location.CatalogRoot, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"Native catalog location: {location.CatalogRoot}; process: {Environment.ProcessPath}");
        }
        finally { Marshal.FreeCoTaskMem(value); }
        using var temporary = new TemporaryDirectory();
        var rejectedRoot = temporary.CreateDirectory("caller-root");
        var rejected = transport.Request(new("catalog-location", Root: rejectedRoot));
        Assert.IsFalse(rejected.Ok);
        Assert.AreEqual("invalid_request", rejected.Error!.Code);
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(rejectedRoot).Count());
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private static NativeProfileCatalogTransport Transport()
    {
        var native = Environment.GetEnvironmentVariable("STFC_PROFILES_NATIVE_TEST_DLL");
        if (string.IsNullOrWhiteSpace(native))
            Assert.Inconclusive("Set STFC_PROFILES_NATIVE_TEST_DLL to the built native component for isolated ABI qualification.");
        return new NativeProfileCatalogTransport(native,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native!))).ToLowerInvariant());
    }
}
