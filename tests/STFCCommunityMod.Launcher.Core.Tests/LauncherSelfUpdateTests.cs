using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherSelfUpdateTests
{
    private const string TargetCommit = "0123456789abcdef0123456789abcdef01234567";

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
            "Guffawaffle/stfc-mod");

        Assert.AreEqual(TargetCommit, selected.TargetCommit);
        Assert.AreEqual("stfc-community-mod-launcher-win-x64.zip", selected.FileName);
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

    private static WindowsReleaseDiscovery Discovery(LauncherReleaseArtifact artifact)
    {
        var manifest = new WindowsReleaseManifest(
            1,
            "2.1.0-guffa.8",
            "v2.1.0-guffa.8",
            "stable",
            "active",
            new Version(0, 1, 0),
            new("Guffawaffle/stfc-mod", TargetCommit),
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
        return new(manifest, null!, artifact);
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
