using System.IO.Compression;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherBootstrapInstallerTests
{
    [TestMethod]
    public async Task VerifiedPayloadReplacesExistingInstallationTransactionally()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var program = temporaryDirectory.CreateDirectory("program");
        File.WriteAllText(Path.Combine(program, "old.txt"), "old");
        var installer = new LauncherBootstrapInstaller(state, program, new FakeAuthenticityVerifier(true));

        var result = await installer.InstallAsync(CreateArchive());

        Assert.IsTrue(result.ReplacedExistingInstallation);
        Assert.IsTrue(File.Exists(result.LauncherPath));
        Assert.IsTrue(File.Exists(Path.Combine(program, "STFCCommunityMod.Launcher.Updater.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(program, "old.txt")));
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(state, "bootstrap")).Length);
    }

    [TestMethod]
    public async Task UntrustedPayloadLeavesExistingInstallationUntouched()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var program = temporaryDirectory.CreateDirectory("program");
        var sentinel = Path.Combine(program, "old.txt");
        File.WriteAllText(sentinel, "old");
        var installer = new LauncherBootstrapInstaller(state, program, new FakeAuthenticityVerifier(false));

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => installer.InstallAsync(CreateArchive()));

        Assert.AreEqual("old", File.ReadAllText(sentinel));
        Assert.IsFalse(File.Exists(Path.Combine(program, "STFCCommunityMod.Launcher.exe")));
    }

    [TestMethod]
    public async Task RunningLauncherPreventsAnyFilesystemMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = Path.Combine(temporaryDirectory.Path, "state");
        var program = Path.Combine(temporaryDirectory.Path, "program");
        var installer = new LauncherBootstrapInstaller(
            state,
            program,
            new FakeAuthenticityVerifier(true),
            () => true);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => installer.InstallAsync(CreateArchive()));

        Assert.IsFalse(Directory.Exists(state));
        Assert.IsFalse(Directory.Exists(program));
    }

    [TestMethod]
    public async Task SetupPreservesIndependentOperationJournalsInStateDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var program = Path.Combine(temporaryDirectory.Path, "program");
        var journalDirectory = Directory.CreateDirectory(Path.Combine(state, "mod-deployment", "transaction-1"));
        var journal = Path.Combine(journalDirectory.FullName, "journal.json");
        File.WriteAllText(journal, "preserve-me");
        var installer = new LauncherBootstrapInstaller(state, program, new FakeAuthenticityVerifier(true));

        await installer.InstallAsync(CreateArchive());

        Assert.AreEqual("preserve-me", File.ReadAllText(journal));
    }

    private static byte[] CreateArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "STFCCommunityMod.Launcher.exe", "STFCCommunityMod.Launcher.Updater.exe" })
            {
                var entry = archive.CreateEntry(name);
                using var target = entry.Open();
                target.Write([1, 2, 3]);
            }
        }
        return stream.ToArray();
    }

    private sealed class FakeAuthenticityVerifier(bool trusted) : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(trusted, trusted ? "trusted" : "untrusted");
    }
}
