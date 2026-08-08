using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
            @"path=C:\Users\Private Player\AppData\Local game=D:\Games\STFC\game token=super-secret cookie=session-cookie Authorization: Bearer abc123 endpoint=https://private.example.test/hook?id=42";

        var output = redactor.Redact(input);

        Assert.IsFalse(output.Contains("Private Player", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("super-secret", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("abc123", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("session-cookie", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("private.example.test", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("%USERPROFILE%", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("%GAME_DIR%", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("<redacted-endpoint>", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow("{\"token\":\"json-token\",\"cookie\": \"json-cookie\"}", "json-token", "json-cookie")]
    [DataRow("{\"password\":\"json-password\",\"api-key\":\"json-api-key\"}", "json-password", "json-api-key")]
    [DataRow("{\"access_token\":\"json-access\",\"client-secret\":\"json-client\"}", "json-access", "json-client")]
    public void RedactorRemovesQuotedJsonShapedSensitiveAssignments(
        string input,
        string firstSecret,
        string secondSecret)
    {
        var output = new LauncherDiagnosticRedactor(null, null).Redact(input);

        Assert.IsFalse(output.Contains(firstSecret, StringComparison.Ordinal));
        Assert.IsFalse(output.Contains(secondSecret, StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("<redacted>", StringComparison.Ordinal));
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
            new FixedTimeProvider(),
            SupportedConfigurationEvidence());

        var localHealth = LauncherHealthResolver.Resolve(
            new ModInstallationEvidence(
                ModInstallationEvidenceState.ManagedVerified,
                false,
                "2.1.0.8",
                "guffawaffle",
                "stable",
                "guffawaffle",
                ReleaseArtifact().Sha256),
            new LauncherProviderHealthContext(
                "guffawaffle",
                "stable",
                "guffawaffle",
                true,
                string.Empty));

        var preview = diagnostics.BuildPreview(gameDirectory, localHealth);

        Assert.IsTrue(preview.Document.Health.Any(fact => fact.Name == "Managed artifact verification"));
        Assert.IsTrue(preview.Document.Health.Any(fact => fact.Id == "local-health.modinstallation"));
        Assert.IsTrue(preview.Document.Health.Any(fact => fact.Id == "configuration"));
        Assert.IsTrue(preview.Document.Health.All(fact => !string.IsNullOrWhiteSpace(fact.Id)));
        Assert.IsTrue(preview.Document.Health.Any(fact => fact.Id == "local-health.nativesupport"));
        Assert.IsTrue(preview.Document.RecentModLog.Count > 0);
        Assert.IsFalse(preview.RedactedJson.Contains(gameDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(preview.RedactedJson.Contains("runtime-secret", StringComparison.Ordinal));
        Assert.IsFalse(preview.RedactedJson.Contains("private.example.test", StringComparison.Ordinal));
        Assert.IsFalse(preview.RedactedJson.Contains("do-not-export", StringComparison.Ordinal));
        var structuredDocument = JsonSerializer.Serialize(preview.Document);
        Assert.IsFalse(structuredDocument.Contains(gameDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(structuredDocument.Contains("runtime-secret", StringComparison.Ordinal));
        Assert.IsFalse(structuredDocument.Contains("private.example.test", StringComparison.Ordinal));
        Assert.IsFalse(preview.RedactedSummary.Contains(gameDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(preview.RedactedSummary.Contains("runtime-secret", StringComparison.Ordinal));
        Assert.AreEqual(2, preview.Document.SchemaVersion);
    }

    [TestMethod]
    public void SerializedDocumentRedactsJsonShapedSecretsFromRecentLog()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = temporaryDirectory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        File.WriteAllText(
            Path.Combine(gameDirectory, "community_patch.log"),
            "{\"token\":\"document-token\",\"cookie\":\"document-cookie\","
            + "\"password\":\"document-password\",\"api-key\":\"document-api-key\"}\n");
        var diagnostics = new LauncherDiagnosticService(
            CreateDeploymentService(temporaryDirectory),
            new FakeOfficialLauncherService(),
            new FakeGameProcessInspector(),
            "0.1.0",
            new FixedTimeProvider());

        var preview = diagnostics.BuildPreview(gameDirectory);
        var serializedDocument = JsonSerializer.Serialize(preview.Document);

        foreach (var secret in new[]
                 {
                     "document-token",
                     "document-cookie",
                     "document-password",
                     "document-api-key",
                 })
        {
            Assert.IsFalse(serializedDocument.Contains(secret, StringComparison.Ordinal));
            Assert.IsFalse(preview.RedactedJson.Contains(secret, StringComparison.Ordinal));
        }
        Assert.IsTrue(preview.Document.RecentModLog.Single().Contains("<redacted>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ManualInstallationAndNotApplicableHealthRemainInformationalWithTechnicalFactsSeparate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = temporaryDirectory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        TemporaryDirectory.CreateFile(gameDirectory, "version.dll");
        var localHealth = LauncherHealthResolver.Resolve(
            new ModInstallationEvidence(ModInstallationEvidenceState.ManualInstallation, false),
            new LauncherProviderHealthContext(
                "guffawaffle",
                "stable",
                "guffawaffle",
                true,
                string.Empty));
        var diagnostics = new LauncherDiagnosticService(
            CreateDeploymentService(temporaryDirectory),
            new FakeOfficialLauncherService(),
            new FakeGameProcessInspector(),
            "0.1.0",
            new FixedTimeProvider());

        var preview = diagnostics.BuildPreview(gameDirectory, localHealth);
        var installation = preview.Document.Health.Single(fact => fact.Id == "local-health.modinstallation");
        var artifact = preview.Document.Health.Single(fact => fact.Id == "managed-artifact-verification");
        var transaction = preview.Document.Health.Single(fact => fact.Id == "deployment-transaction");

        Assert.AreEqual(LauncherDiagnosticLevel.Informational, installation.Level);
        StringAssert.Contains(installation.Summary, "Manual installation detected");
        Assert.AreEqual(LauncherDiagnosticLevel.Informational, artifact.Level);
        StringAssert.Contains(artifact.Summary, "no Mod Bridge-managed SHA-256 identity");
        Assert.AreEqual(LauncherDiagnosticLevel.Healthy, transaction.Level);
        Assert.IsTrue(
            preview.Document.Health
                .Where(fact => fact.Id.StartsWith("local-health.", StringComparison.Ordinal))
                .Where(fact => fact.Summary.Contains("not applicable", StringComparison.OrdinalIgnoreCase))
                .All(fact => fact.Level == LauncherDiagnosticLevel.Informational));
    }

    [TestMethod]
    public void SafelyAttributedRunningGameIsInformationalInsteadOfAttention()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = temporaryDirectory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        var diagnostics = new LauncherDiagnosticService(
            CreateDeploymentService(temporaryDirectory),
            new FakeOfficialLauncherService(),
            new FakeGameProcessInspector(GameProcessInspectionState.RunningTarget),
            "0.1.0");

        var process = diagnostics.BuildPreview(gameDirectory).Document.Health
            .Single(fact => fact.Id == "game-process");

        Assert.AreEqual(LauncherDiagnosticLevel.Informational, process.Level);
        StringAssert.Contains(process.Summary, "running normally");
        StringAssert.Contains(process.NextAction, "only before");
    }

    [TestMethod]
    public void UnattributablePrimeProcessRemainsDiagnosticAttention()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = temporaryDirectory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        var diagnostics = new LauncherDiagnosticService(
            CreateDeploymentService(temporaryDirectory),
            new FakeOfficialLauncherService(),
            new FakeGameProcessInspector(GameProcessInspectionState.Unattributable),
            "0.1.0");

        var process = diagnostics.BuildPreview(gameDirectory).Document.Health
            .Single(fact => fact.Id == "game-process");

        Assert.AreEqual(LauncherDiagnosticLevel.Attention, process.Level);
        StringAssert.Contains(process.Summary, "could not be attributed safely");
    }

    [TestMethod]
    public void ReportSurfacesUnknownProviderDiagnosisWithoutReadingValues()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = temporaryDirectory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        var secret = "never-project-this-token";
        File.WriteAllText(
            Path.Combine(gameDirectory, "community_patch_settings.toml"),
            $"[sync]\ntoken = \"{secret}\"\nurl = \"https://private.example.invalid/ingress\"\n",
            Encoding.UTF8);
        var diagnostics = new LauncherDiagnosticService(
            CreateDeploymentService(temporaryDirectory),
            new FakeOfficialLauncherService(),
            new FakeGameProcessInspector(),
            "0.1.0",
            new FixedTimeProvider(),
            LauncherConfigurationDiagnosisEvidence.Unavailable(
                "netniv",
                "main",
                LauncherProviderCapabilityStatus.Unsupported));

        var preview = diagnostics.BuildPreview(gameDirectory);
        var configuration = preview.Document.Health.Single(fact => fact.Id == "configuration");

        Assert.AreEqual(LauncherDiagnosticLevel.Unavailable, configuration.Level);
        Assert.IsTrue(configuration.Summary.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(preview.RedactedJson.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(preview.RedactedSummary.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(preview.RedactedJson.Contains("private.example.invalid", StringComparison.Ordinal));
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
        _ => false,
        new("guffawaffle", "stable", "guffawaffle.windows"));

    private static LauncherConfigurationDiagnosisEvidence SupportedConfigurationEvidence() =>
        LauncherConfigurationDiagnosisEvidence.Supported(
            "guffawaffle",
            "stable",
            LauncherConfigurationSchemaLoader.LoadFile(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Fixtures",
                    "Configuration",
                    "config-schema.guffawaffle.v1.json")));

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

        public Task<OfficialLauncherStartResult> StartAsync(CancellationToken cancellationToken) =>
            throw new AssertFailedException("Diagnostics must not start the official launcher.");
    }

    private sealed class FakeGameProcessInspector(
        GameProcessInspectionState state = GameProcessInspectionState.NotRunning) : IGameProcessInspector
    {
        public bool IsGameRunning(string gameDirectory) => state != GameProcessInspectionState.NotRunning;

        public GameProcessInspectionState Inspect(string gameDirectory) => state;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
