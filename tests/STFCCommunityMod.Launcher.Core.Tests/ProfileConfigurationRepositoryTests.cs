namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ProfileConfigurationRepositoryTests
{
    [TestMethod]
    public async Task MissingProfileConfigurationIsStagedAndCreatedOnlyOnCommit()
    {
        using var directory = new TemporaryDirectory();
        var profile = Profile(directory.Path);
        var transport = new LeaseCatalog(profile);
        var store = new NativeLauncherProfilesStore(directory.Path, transport);
        var repository = new ProfileConfigurationRepository(profile, store, () => profile.Id,
            new TomlConfigurationRepository());
        var read = repository.Read(profile.ConfigPath);
        Assert.IsTrue(read.IsSuccess);
        Assert.IsFalse(read.Snapshot!.Existed);
        Assert.IsFalse(File.Exists(profile.ConfigPath));
        var desired = System.Text.Encoding.UTF8.GetBytes("[ui]\nscale = 1.1\n");
        var saved = await repository.CommitDocumentAsync(new(profile.ConfigPath, read.Snapshot.Revision,
            read.Snapshot.Contents, desired, baselineExisted: false));
        Assert.IsTrue(saved.IsSuccess);
        CollectionAssert.AreEqual(desired, File.ReadAllBytes(profile.ConfigPath));
        Assert.AreEqual(0, transport.HeldLeases);
        Assert.AreEqual(2, transport.Acquisitions);
    }

    [TestMethod]
    public async Task ArchivedOrChangedSelectionCannotCreateConfigurationAtTheOldPath()
    {
        using var directory = new TemporaryDirectory();
        var profile = Profile(directory.Path);
        var transport = new LeaseCatalog(profile);
        var store = new NativeLauncherProfilesStore(directory.Path, transport);
        string? selected = profile.Id;
        var repository = new ProfileConfigurationRepository(profile, store, () => selected,
            new TomlConfigurationRepository());
        var baseline = repository.Read(profile.ConfigPath).Snapshot!;
        selected = null;
        var result = await repository.CommitDocumentAsync(new(profile.ConfigPath, baseline.Revision,
            baseline.Contents, System.Text.Encoding.UTF8.GetBytes("value = true\n"), baselineExisted: false));
        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.IsFalse(File.Exists(profile.ConfigPath));
        selected = profile.Id;
        transport.IsArchived = true;
        result = await repository.CommitDocumentAsync(new(profile.ConfigPath, baseline.Revision,
            baseline.Contents, System.Text.Encoding.UTF8.GetBytes("value = true\n"), baselineExisted: false));
        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.IsFalse(File.Exists(profile.ConfigPath));
    }

    [TestMethod]
    public async Task CommitRetainsNativeDirectoryLeaseUntilTheAtomicSaveCompletes()
    {
        using var directory = new TemporaryDirectory();
        var profile = Profile(directory.Path);
        File.WriteAllText(profile.ConfigPath, "value = false\n");
        var transport = new LeaseCatalog(profile);
        var saveObservedLease = false;
        var atomic = new AtomicTomlStore(beforeReplace: (_, _, _) =>
        {
            saveObservedLease = transport.HeldLeases == 1;
            return ValueTask.CompletedTask;
        });
        var repository = new ProfileConfigurationRepository(profile,
            new NativeLauncherProfilesStore(directory.Path, transport), () => profile.Id,
            new TomlConfigurationRepository(atomic));
        var baseline = repository.Read(profile.ConfigPath).Snapshot!;
        var result = await repository.CommitDocumentAsync(new(profile.ConfigPath, baseline.Revision,
            baseline.Contents, System.Text.Encoding.UTF8.GetBytes("value = true\n")));
        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(saveObservedLease);
        Assert.AreEqual(0, transport.HeldLeases);
    }

    [TestMethod]
    public async Task VerifiedProfileBackupKeepsEncryptedBytesAndReceiptInsideItsOwnDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var profile = Profile(directory.Path);
        var contents = System.Text.Encoding.UTF8.GetBytes("[sync]\napi_token = 'fixture-private-token'\n");
        File.WriteAllBytes(profile.ConfigPath, contents);
        var receipt = await new ProfileConfigurationMutationBackup(profile, "fixture-provider")
            .BeforeReplaceAsync(profile.ConfigPath, contents, CancellationToken.None);
        var backup = Path.Combine(profile.Directory, "backups", "configuration", receipt.BackupId);
        var encrypted = File.ReadAllBytes(Path.Combine(backup, "config.bin"));
        Assert.IsFalse(encrypted.AsSpan().SequenceEqual(contents));
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(encrypted).Contains("fixture-private-token", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(Path.Combine(backup, "receipt.json")));
        Assert.AreEqual(ConfigurationDocumentRevision.FromContents(contents).Sha256, receipt.ContentSha256);
        Assert.AreEqual(profile.Id, receipt.InstallationId);
        Assert.AreEqual(0, System.IO.Directory.GetFiles(profile.Directory).Count(path => path.EndsWith(".bak", StringComparison.Ordinal)));
    }

    private static LauncherProfile Profile(string path) => new("0123456789abcdef0123456789abcdef", "Science", "",
        path, Path.Combine(path, "config.toml"), Path.Combine(path, "logs", "Player.log"), "revision");

    private sealed class LeaseCatalog(LauncherProfile profile) : IProfileCatalogTransport, IProfileCatalogLeaseTransport
    {
        public int HeldLeases { get; private set; }
        public int Acquisitions { get; private set; }
        public bool IsArchived { get; set; }
        public ProfileCatalogResponse Request(ProfileCatalogRequest request) => new(true, Profile: profile);
        public ProfileCatalogLease AcquireDataLease(ProfileCatalogRequest request)
        {
            if (IsArchived) throw new InvalidOperationException("Profile was archived");
            HeldLeases++;
            Acquisitions++;
            return new(profile, new Release(() => HeldLeases--));
        }
        private sealed class Release(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }
}
