using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherSelfUpdateTests
{
    private const string TargetCommit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [TestMethod]
    public async Task VerifiedArchiveStagesPlanWithoutTouchingProgramDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateArchive(("STFCCommunityMod.Launcher.exe", [1, 2, 3]), ("STFCCommunityMod.Launcher.Updater.exe", [4, 5, 6]));
        var artifact = Artifact(archive);
        var service = CreateService(temporaryDirectory, archive);

        var result = await service.PrepareAsync(Discovery(artifact), new string('a', 40), 123);

        Assert.AreEqual(LauncherUpdatePreparationState.Ready, result.State);
        Assert.IsTrue(File.Exists(result.PlanPath));
        Assert.IsTrue(File.Exists(result.UpdaterPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(temporaryDirectory.Path, "program")));
    }

    [TestMethod]
    public async Task CurrentSourceCommitRequiresNoDownloadOrMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateArchive(("STFCCommunityMod.Launcher.exe", [1]), ("STFCCommunityMod.Launcher.Updater.exe", [2]));
        var downloader = new FakeDownloader(archive);
        var service = CreateService(temporaryDirectory, archive, downloader);

        var result = await service.PrepareAsync(Discovery(Artifact(archive)), TargetCommit, 123);

        Assert.AreEqual(LauncherUpdatePreparationState.UpToDate, result.State);
        Assert.AreEqual(0, downloader.CallCount);
    }

    [TestMethod]
    public async Task ArchiveTraversalFailsBeforeExecutableVerification()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateArchive(("../escape.exe", [1]), ("STFCCommunityMod.Launcher.exe", [2]), ("STFCCommunityMod.Launcher.Updater.exe", [3]));
        var service = CreateService(temporaryDirectory, archive);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => service.PrepareAsync(Discovery(Artifact(archive)), new string('a', 40), 123));
        Assert.IsFalse(File.Exists(Path.Combine(temporaryDirectory.Path, "escape.exe")));
    }

    [TestMethod]
    public async Task ArchiveAlternateDataStreamFailsBeforeExecutableVerification()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateArchive(
            ("payload.txt:stream", [1]),
            ("STFCCommunityMod.Launcher.exe", [2]),
            ("STFCCommunityMod.Launcher.Updater.exe", [3]));
        var service = CreateService(temporaryDirectory, archive);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => service.PrepareAsync(Discovery(Artifact(archive)), new string('a', 40), 123));
    }

    [TestMethod]
    public void LauncherSelectionRequiresSignedContentsContract()
    {
        var archive = new byte[] { 1, 2, 3 };
        var selected = WindowsReleaseSelectionPolicy.SelectLauncherArtifact(
            Discovery(Artifact(archive)).Manifest,
            "stable",
            new Version(0, 1, 0),
            "Guffawaffle/stfc-mod-launcher");

        Assert.AreEqual(TargetCommit, selected.TargetCommit);
        Assert.AreEqual("stfc-community-mod-launcher-win-x64.zip", selected.FileName);
    }

    [TestMethod]
    public async Task PreparationPreservesIndependentOperationJournalsInStateDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var journalDirectory = Directory.CreateDirectory(Path.Combine(state, "mod-deployment", "transaction-1"));
        var journal = Path.Combine(journalDirectory.FullName, "journal.json");
        File.WriteAllText(journal, "preserve-me");
        var archive = CreateArchive(
            ("STFCCommunityMod.Launcher.exe", [1, 2, 3]),
            ("STFCCommunityMod.Launcher.Updater.exe", [4, 5, 6]));
        var service = CreateService(temporaryDirectory, archive);

        await service.PrepareAsync(Discovery(Artifact(archive)), new string('a', 40), 123);

        Assert.AreEqual("preserve-me", File.ReadAllText(journal));
    }

    [TestMethod]
    public void SetupRecoveryRestoresVerifiedBackupFromAbandonedSelfUpdate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        File.WriteAllText(Path.Combine(target, "new.txt"), "unacknowledged");
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(transactionRoot, "backup")).FullName;
        var previousPath = Path.Combine(backup, "old.txt");
        File.WriteAllText(previousPath, "trusted previous payload");
        WritePlan(state, target, transactionId, transactionRoot, [FileRecord(backup, previousPath)]);

        var result = LauncherUpdateRecovery.RecoverBeforeSetup(state, target);

        Assert.AreEqual(1, result.ExaminedTransactions);
        Assert.AreEqual(1, result.RestoredBackups);
        Assert.AreEqual("trusted previous payload", File.ReadAllText(Path.Combine(target, "old.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "new.txt")));
        Assert.IsFalse(Directory.Exists(transactionRoot));
    }

    [TestMethod]
    public void SetupRecoveryVerifiesBackupBeforeRemovingCurrentPayload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var currentPath = Path.Combine(target, "current.txt");
        File.WriteAllText(currentPath, "keep-current");
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(transactionRoot, "backup")).FullName;
        var previousPath = Path.Combine(backup, "old.txt");
        File.WriteAllText(previousPath, "original");
        var previous = FileRecord(backup, previousPath);
        WritePlan(state, target, transactionId, transactionRoot, [previous]);
        File.WriteAllText(previousPath, "tampered");

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherUpdateRecovery.RecoverBeforeSetup(state, target));

        Assert.AreEqual("keep-current", File.ReadAllText(currentPath));
        Assert.IsTrue(Directory.Exists(transactionRoot));
    }

    [TestMethod]
    public void SetupRecoveryRejectsInvalidPlanPathsBeforeRemovingCurrentPayload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var currentPath = Path.Combine(target, "current.txt");
        File.WriteAllText(currentPath, "keep-current");
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(transactionRoot, "backup")).FullName;
        var previousPath = Path.Combine(backup, "old.txt");
        File.WriteAllText(previousPath, "trusted previous payload");
        WritePlan(
            state,
            Path.Combine(temporaryDirectory.Path, "wrong-program"),
            transactionId,
            transactionRoot,
            [FileRecord(backup, previousPath)]);

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherUpdateRecovery.RecoverBeforeSetup(state, target));

        Assert.AreEqual("keep-current", File.ReadAllText(currentPath));
        Assert.IsTrue(Directory.Exists(backup));
    }

    [TestMethod]
    public void SetupRecoveryRejectsMultipleBackupsBeforeChoosingAnOrder()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var currentPath = Path.Combine(target, "current.txt");
        File.WriteAllText(currentPath, "keep-current");
        CreateRecoveryTransaction(state, target, "old-one");
        CreateRecoveryTransaction(state, target, "old-two");

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherUpdateRecovery.RecoverBeforeSetup(state, target));

        Assert.AreEqual("keep-current", File.ReadAllText(currentPath));
        Assert.AreEqual(2, Directory.GetDirectories(Path.Combine(state, "self-update")).Length);
    }

    [TestMethod]
    public void SetupRecoveryCleansVerifiedPlanThatNeverMovedTheOldPayload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var currentPath = Path.Combine(target, "current.txt");
        File.WriteAllText(currentPath, "keep-current");
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        WritePlan(state, target, transactionId, transactionRoot, []);

        var result = LauncherUpdateRecovery.RecoverBeforeSetup(state, target);

        Assert.AreEqual(1, result.ExaminedTransactions);
        Assert.AreEqual(0, result.RestoredBackups);
        Assert.AreEqual("keep-current", File.ReadAllText(currentPath));
        Assert.IsFalse(Directory.Exists(transactionRoot));
    }

    [TestMethod]
    public void SetupRecoveryRefusesReparsePointsBeforeRemovingCurrentPayload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var currentPath = Path.Combine(target, "current.txt");
        File.WriteAllText(currentPath, "keep-current");
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        WritePlan(state, target, transactionId, transactionRoot, []);
        var linkTarget = temporaryDirectory.CreateDirectory("link-target");
        var linkPath = Path.Combine(transactionRoot, "link");
        CreateDirectoryLink(linkPath, linkTarget);
        try
        {
            Assert.ThrowsException<InvalidDataException>(
                () => LauncherUpdateRecovery.RecoverBeforeSetup(state, target));

            Assert.AreEqual("keep-current", File.ReadAllText(currentPath));
            Assert.IsTrue(Directory.Exists(transactionRoot));
        }
        finally
        {
            Directory.Delete(linkPath);
        }
    }

    [TestMethod]
    public void SetupRecoveryMoveFailureRestoresCurrentPayloadAndLeavesBackupForRetry()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var currentPath = Path.Combine(target, "current.txt");
        File.WriteAllText(currentPath, "keep-current");
        var transactionRoot = CreateRecoveryTransaction(state, target, "trusted previous payload");
        Assert.ThrowsException<IOException>(
            () => LauncherUpdateRecovery.RecoverBeforeSetup(
                state,
                target,
                (source, destination) =>
                {
                    if (Path.GetFileName(source) == "backup")
                    {
                        throw new IOException("simulated backup move failure");
                    }
                    Directory.Move(source, destination);
                }));

        Assert.AreEqual("keep-current", File.ReadAllText(currentPath));
        Assert.IsTrue(Directory.Exists(Path.Combine(transactionRoot, "backup")));
        Assert.IsTrue(Directory.Exists(transactionRoot));
    }

    private static LauncherSelfUpdateService CreateService(
        TemporaryDirectory temporaryDirectory,
        byte[] archive,
        FakeDownloader? downloader = null) => new(
            temporaryDirectory.CreateDirectory("state"),
            Path.Combine(temporaryDirectory.Path, "program"),
            downloader ?? new FakeDownloader(archive),
            new FakeAuthenticityVerifier(),
            new FakeIdentityReader());

    private static LauncherReleaseDiscovery Discovery(LauncherReleaseArtifact artifact)
    {
        var manifest = new WindowsReleaseManifest(
            1,
            "2.1.0-guffa.8",
            "v2.1.0-guffa.8",
            "stable",
            "active",
            new Version(0, 1, 0),
            new("Guffawaffle/stfc-mod-launcher", TargetCommit),
            "none",
            [
                new(
                    "windows-launcher-archive-x64",
                    "windows-launcher",
                    "windows",
                    "x64",
                    artifact.FileName,
                    "application/zip",
                    artifact.Size,
                    artifact.Sha256,
                    new(
                        "authenticode",
                        "contents",
                        ["STFCCommunityMod.Launcher.exe", "STFCCommunityMod.Launcher.Updater.exe"])),
            ]);
        return new(manifest, artifact);
    }

    private static LauncherReleaseArtifact Artifact(byte[] archive) => new(
        new Uri("https://example.invalid/launcher.zip"),
        "stfc-community-mod-launcher-win-x64.zip",
        archive.LongLength,
        Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),
        "2.1.0-guffa.8",
        TargetCommit);

    private static byte[] CreateArchive(params (string Name, byte[] Contents)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var target = entry.Open();
                target.Write(contents);
            }
        }
        return stream.ToArray();
    }

    private static LauncherUpdateFile FileRecord(string root, string path) => new(
        Path.GetRelativePath(root, path),
        new FileInfo(path).Length,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static string CreateRecoveryTransaction(string state, string target, string contents)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(transactionRoot, "backup")).FullName;
        var previousPath = Path.Combine(backup, "old.txt");
        File.WriteAllText(previousPath, contents);
        WritePlan(state, target, transactionId, transactionRoot, [FileRecord(backup, previousPath)]);
        return transactionRoot;
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.AreEqual(
            0,
            process.ExitCode,
            process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
    }

    private static void WritePlan(
        string state,
        string target,
        string transactionId,
        string transactionRoot,
        IReadOnlyList<LauncherUpdateFile> previousFiles)
    {
        var plan = new LauncherUpdatePlan(
            1,
            transactionId,
            123,
            state,
            Path.Combine(transactionRoot, "stage"),
            target,
            Path.Combine(transactionRoot, "backup"),
            Path.Combine(transactionRoot, "startup.ack"),
            "STFCCommunityMod.Launcher.exe",
            [],
            previousFiles);
        File.WriteAllText(
            Path.Combine(transactionRoot, "plan.json"),
            JsonSerializer.Serialize(plan, PlanJsonOptions));
    }

    private sealed class FakeDownloader(byte[] contents) : ILauncherArchiveDownloader
    {
        public int CallCount { get; private set; }

        public Task<ModArtifactDownload> DownloadAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new ModArtifactDownload(HttpStatusCode.OK, contents, contents.LongLength));
        }
    }

    private sealed class FakeAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(true, "trusted");
    }

    private sealed class FakeIdentityReader : ILauncherArtifactIdentityReader
    {
        public string? ReadSourceCommit(string executablePath) => TargetCommit;
    }
}
