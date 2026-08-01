using System.Net;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherDiagnosticsTests
{
    private static readonly byte[] ArtifactContents = [5, 8, 13, 21];

    [TestMethod]
    public void RedactorRemovesTokensPrivatePathsAndEndpoints()
    {
        var redactor = new LauncherDiagnosticRedactor(
            @"C:\Users\Private Player",
            @"D:\Games\STFC\game");
        const string input =
            @"path=C:\Users\Private Player\AppData\Local game=D:\Games\STFC\game token=super-secret Authorization: Bearer abc123 endpoint=https://private.example.test/hook?id=42";

        var output = redactor.Redact(input);

        Assert.IsFalse(output.Contains("Private Player", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("super-secret", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("abc123", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("private.example.test", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("%USERPROFILE%", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("%GAME_DIR%", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("<redacted-endpoint>", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PreviewContainsBoundedHealthAndRedactedRecentLogWithoutConfigValues()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = temporaryDirectory.CreateDirectory("Private Player", "game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        File.WriteAllText(
            Path.Combine(gameDirectory, "community_patch_settings.toml"),
            "[sync]\nendpoint = \"https://private.example.test/ingest\"\ntoken = \"do-not-export\"\n");
        File.WriteAllText(
            Path.Combine(gameDirectory, "community_patch.log"),
            $"loading {gameDirectory} token=runtime-secret https://private.example.test/log\nready\n");
        var service = CreateDeploymentService(temporaryDirectory);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var diagnostics = new LauncherDiagnosticService(
            service,
            new FakeOfficialLauncherService(),
            new FakeGameProcessInspector(),
            "0.1.0",
            new FixedTimeProvider());

        var preview = diagnostics.BuildPreview(gameDirectory);

        Assert.IsTrue(preview.Document.Health.Any(fact => fact.Name == "Community mod"));
        Assert.IsTrue(preview.Document.Health.Any(fact => fact.Name == "Configuration"));
        Assert.IsTrue(preview.Document.RecentModLog.Count > 0);
        Assert.IsFalse(preview.RedactedJson.Contains(gameDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(preview.RedactedJson.Contains("runtime-secret", StringComparison.Ordinal));
        Assert.IsFalse(preview.RedactedJson.Contains("private.example.test", StringComparison.Ordinal));
        Assert.IsFalse(preview.RedactedJson.Contains("do-not-export", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExportWritesOnlyThePreviewedRedactedDocument()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var preview = new LauncherDiagnosticPreview(
            new LauncherDiagnosticDocument(
                1,
                DateTimeOffset.UnixEpoch,
                "0.1.0",
                "test",
                [],
                []),
            "{\"safe\":true}");
        var outputPath = Path.Combine(temporaryDirectory.Path, "diagnostics.json");

        await LauncherDiagnosticService.ExportAsync(preview, outputPath);

        Assert.AreEqual(preview.RedactedJson, File.ReadAllText(outputPath));
        Assert.AreEqual(1, Directory.EnumerateFiles(temporaryDirectory.Path).Count());
    }

    private static ModDeploymentService CreateDeploymentService(TemporaryDirectory temporaryDirectory) => new(
        temporaryDirectory.CreateDirectory("state"),
        new FakeDownloader(),
        new FakeVersionReader(),
        new FakeAuthenticityVerifier(),
        () => false);

    private static ModReleaseArtifact ReleaseArtifact() => new(
        new Uri("https://example.invalid/version.dll"),
        "version.dll",
        ArtifactContents.LongLength,
        Convert.ToHexString(SHA256.HashData(ArtifactContents)),
        "2.1.0.8");

    private sealed class FakeDownloader : IModArtifactDownloader
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromResult(new ModArtifactDownload(HttpStatusCode.OK, ArtifactContents, ArtifactContents.LongLength));
    }

    private sealed class FakeVersionReader : IModArtifactVersionReader
    {
        public string? ReadVersion(string artifactPath) => "2.1.0.8";
    }

    private sealed class FakeAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(true, "trusted");
    }

    private sealed class FakeOfficialLauncherService : IOfficialLauncherService
    {
        public bool IsAvailable => true;

        public Task<IOfficialLauncherProcess> StartAsync(CancellationToken cancellationToken) =>
            throw new AssertFailedException("Diagnostics must not start the official launcher.");
    }

    private sealed class FakeGameProcessInspector : IGameProcessInspector
    {
        public bool IsGameRunning() => false;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
