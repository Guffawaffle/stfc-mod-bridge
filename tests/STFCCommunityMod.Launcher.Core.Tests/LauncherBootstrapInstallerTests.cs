using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherBootstrapInstallerTests
{
    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
        Assert.IsTrue(File.Exists(Path.Combine(program, "STFCModControl.Updater.exe")));
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
        Assert.IsFalse(File.Exists(Path.Combine(program, "STFCModControl.exe")));
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

    [TestMethod]
    public async Task ConcurrentUpdaterLeaseRejectsSetupBeforeRecoveryMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var program = temporaryDirectory.CreateDirectory("program");
        var sentinel = Path.Combine(program, "current.txt");
        File.WriteAllText(sentinel, "keep-current");
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(transactionRoot, "backup")).FullName;
        var oldPath = Path.Combine(backup, "old.txt");
        File.WriteAllText(oldPath, "would-restore");
        var oldFile = new LauncherUpdateFile(
            "old.txt",
            new FileInfo(oldPath).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oldPath))));
        var plan = new LauncherUpdatePlan(
            1,
            transactionId,
            123,
            state,
            Path.Combine(transactionRoot, "stage"),
            program,
            backup,
            Path.Combine(transactionRoot, "startup.ack"),
            "STFCModControl.exe",
            [],
            [oldFile]);
        File.WriteAllText(
            Path.Combine(transactionRoot, "plan.json"),
            JsonSerializer.Serialize(plan, PlanJsonOptions));
        await using var updaterLease = await new LauncherOperationLock(state).TryAcquireAsync();
        Assert.IsNotNull(updaterLease);
        var installer = new LauncherBootstrapInstaller(state, program, new FakeAuthenticityVerifier(true));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => installer.InstallAsync(CreateArchive()));

        Assert.AreEqual("keep-current", File.ReadAllText(sentinel));
        Assert.IsTrue(Directory.Exists(backup));
    }

    [TestMethod]
    public async Task ArchiveRejectsPortableExecutableWithDllOrRenamedIdentity()
    {
        var portableExecutable = await File.ReadAllBytesAsync(Environment.ProcessPath!);
        foreach (var unexpectedName in new[] { "payload.dll", "renamed-payload.bin" })
        {
            using var temporaryDirectory = new TemporaryDirectory();
            var state = temporaryDirectory.CreateDirectory("state");
            var program = Path.Combine(temporaryDirectory.Path, "program");
            var installer = new LauncherBootstrapInstaller(state, program, new FakeAuthenticityVerifier(true));

            var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
                () => installer.InstallAsync(CreateArchive((unexpectedName, portableExecutable))));

            StringAssert.Contains(exception.Message, "unexpected portable executable");
            Assert.IsFalse(Directory.Exists(program));
        }
    }

    private static byte[] CreateArchive(params (string Name, byte[] Contents)[] extraEntries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "STFCModControl.exe", "STFCModControl.Updater.exe" })
            {
                var entry = archive.CreateEntry(name);
                using var target = entry.Open();
                target.Write([1, 2, 3]);
            }
            foreach (var (name, contents) in extraEntries)
            {
                var entry = archive.CreateEntry(name);
                using var target = entry.Open();
                target.Write(contents);
            }
        }
        return stream.ToArray();
    }

    private sealed class FakeAuthenticityVerifier(bool trusted) : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(trusted, trusted ? "trusted" : "untrusted");
    }
}
