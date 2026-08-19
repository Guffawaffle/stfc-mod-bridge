using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ArtifactBoundRuntimeManifestTests
{
    private const string Repository = "Guffawaffle/stfc-mod";
    private const string Tag = "v2.1.0-guffa.8";
    private const string SourceRevision = "0123456789abcdef0123456789abcdef01234567";
    private const string DistributionId = "guffawaffle.stfc-community-mod";
    private static readonly byte[] DllBytes = [0x53, 0x54, 0x46, 0x43, 0x2d, 0x50, 0x41, 0x49, 0x52];
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void ExactReviewedPairCreatesActivationThroughExistingResolver()
    {
        var fixture = PairFixture.Create();

        var parsed = ArtifactBoundRuntimeManifestParser.Parse(
            fixture.ManifestBytes,
            fixture.Dll,
            fixture.Manifest,
            DistributionId);
        var activation = ArtifactBoundRuntimeManifestParser.AuthorizeActivation(
            parsed,
            fixture.Dll,
            fixture.Manifest,
            fixture.Certification);

        Assert.IsNotNull(activation);
        Assert.AreEqual(fixture.Manifest.Sha256.ToLowerInvariant(), activation.EvidenceSourceSha256);
        Assert.IsTrue(activation.RuntimeProfile.HasCapability("battle.capture.v1"));
        Assert.IsTrue(activation.ActivationPlan.IsActive(LauncherFeatureIds.SemanticSettingsGrouping));
    }

    [TestMethod]
    public void MissingReviewedCompanionWithholdsActivation()
    {
        var fixture = PairFixture.Create();
        var legacyCertification = fixture.Certification with { RuntimeManifest = null };
        var parsed = ArtifactBoundRuntimeManifestParser.Parse(
            fixture.ManifestBytes,
            fixture.Dll,
            fixture.Manifest,
            DistributionId);

        Assert.IsNull(ArtifactBoundRuntimeManifestParser.AuthorizeActivation(
            parsed,
            fixture.Dll,
            fixture.Manifest,
            legacyCertification));
    }

    [TestMethod]
    public void DuplicateEscapedPropertyFailsStrictParsing()
    {
        var fixture = PairFixture.Create();
        var raw = Encoding.UTF8.GetString(fixture.ManifestBytes)
            .Replace(
                "\"capabilities\":",
                "\"capabilities\": [], \"capabil\\u0069ties\":",
                StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(raw);
        var discovery = fixture.Manifest with
        {
            Size = bytes.LongLength,
            Sha256 = Sha256(bytes),
        };

        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            ArtifactBoundRuntimeManifestParser.Parse(bytes, fixture.Dll, discovery, DistributionId));

        StringAssert.Contains(exception.Message, "duplicate property 'capabilities'");
    }

    [TestMethod]
    public void ExtraTopLevelCapabilityCannotElevate()
    {
        var fixture = PairFixture.Create(extraCapability: "battle.unreviewed.v1");

        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            ArtifactBoundRuntimeManifestParser.Parse(
                fixture.ManifestBytes,
                fixture.Dll,
                fixture.Manifest,
                DistributionId));

        StringAssert.Contains(exception.Message, "does not exactly match");
    }

    [DataTestMethod]
    [DataRow("distribution")]
    [DataRow("source")]
    [DataRow("dll-hash")]
    [DataRow("dll-size")]
    [DataRow("schema")]
    [DataRow("version")]
    public void BoundIdentityAndSchemaMismatchesFailClosed(string mutation)
    {
        var fixture = PairFixture.Create();
        var json = Encoding.UTF8.GetString(fixture.ManifestBytes);
        json = mutation switch
        {
            "distribution" => json.Replace(DistributionId, "foreign.stfc-mod", StringComparison.Ordinal),
            "source" => json.Replace(SourceRevision, new string('b', 40), StringComparison.Ordinal),
            "dll-hash" => json.Replace(fixture.Dll.Sha256, new string('f', 64), StringComparison.Ordinal),
            "dll-size" => json.Replace(
                $"\"size\":{fixture.Dll.Size}",
                $"\"size\":{fixture.Dll.Size + 1}",
                StringComparison.Ordinal),
            "schema" => json.Replace("\"manifestSchema\":1", "\"manifestSchema\":2", StringComparison.Ordinal),
            "version" => json.Replace("\"runtimeVersion\":\"2.1.0.8\"", "\"runtimeVersion\":\"9.9.9.9\"", StringComparison.Ordinal),
            _ => throw new AssertFailedException("Unknown mutation."),
        };
        var bytes = Encoding.UTF8.GetBytes(json);
        var discovery = fixture.Manifest with { Size = bytes.LongLength, Sha256 = Sha256(bytes) };

        _ = Assert.ThrowsException<InvalidDataException>(() =>
            ArtifactBoundRuntimeManifestParser.Parse(bytes, fixture.Dll, discovery, DistributionId));
    }

    [TestMethod]
    public void OversizedRawManifestFailsBeforeJsonParsing()
    {
        var fixture = PairFixture.Create();
        var bytes = new byte[ArtifactBoundRuntimeManifestParser.MaximumManifestBytes + 1];
        var discovery = fixture.Manifest with { Size = bytes.LongLength, Sha256 = Sha256(bytes) };

        _ = Assert.ThrowsException<InvalidDataException>(() =>
            ArtifactBoundRuntimeManifestParser.Parse(bytes, fixture.Dll, discovery, DistributionId));
    }

    [TestMethod]
    public void ReviewedReleaseSelectionRequiresExactCompanionEntry()
    {
        var fixture = PairFixture.Create();
        var manifest = ReleaseManifest(fixture);

        var selected = WindowsReleaseSelectionPolicy.SelectReviewedRuntimeManifestArtifact(
            manifest,
            fixture.Dll,
            fixture.Certification);

        Assert.AreEqual(fixture.Manifest.FileName, selected.FileName);
        Assert.AreEqual(fixture.Manifest.Sha256, selected.Sha256);
        var changed = manifest with
        {
            Artifacts = manifest.Artifacts.Select(artifact =>
                artifact.Id == "windows-mod-runtime-manifest-x64"
                    ? artifact with { Sha256 = new string('f', 64) }
                    : artifact).ToArray(),
        };
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsReleaseSelectionPolicy.SelectReviewedRuntimeManifestArtifact(
                changed,
                fixture.Dll,
                fixture.Certification));
    }

    [TestMethod]
    public async Task ReviewedPairDeploysAndUninstallsAtomically()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var service = CreateService(temporaryDirectory, fixture);

        var installed = await service.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, installed.State, installed.Message);
        Assert.AreEqual("Community Mod installed successfully.", installed.Message);
        Assert.IsFalse(installed.Message.Contains("SHA-256", StringComparison.Ordinal));
        Assert.IsFalse(installed.Message.Contains("software safety", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(installed.RuntimeActivation);
        CollectionAssert.AreEqual(DllBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            fixture.ManifestBytes,
            File.ReadAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.IsNotNull(service.ReadInstalledState()!.RuntimeManifest);

        var removed = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, removed.State);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
    }

    [TestMethod]
    public async Task ReviewedPairReportsBothExactCommittedRevisionsFromLockedHandles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var committed = new Dictionary<string, ExactFileRevision>(StringComparer.OrdinalIgnoreCase);
        bool RecordCommitted(string path, ExactFileRevision revision)
        {
            Assert.ThrowsException<IOException>(
                () => File.Delete(path),
                "The commit callback must run while deployment still holds the exact file handle.");
            committed.Add(Path.GetFileName(path), revision);
            return true;
        }
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            afterArtifactCommitted: RecordCommitted,
            afterRuntimeManifestCommitted: RecordCommitted);

        var result = await service.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(2, committed.Count);
        using var exactDll = ExactFileMutation.Open(Path.Combine(gameDirectory, "version.dll"));
        using var exactRuntime = ExactFileMutation.Open(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName));
        Assert.IsTrue(committed["version.dll"].Matches(exactDll.CaptureRevision()));
        Assert.IsTrue(
            committed[ArtifactBoundRuntimeManifestParser.ManagedFileName]
                .Matches(exactRuntime.CaptureRevision()));
    }

    [TestMethod]
    public async Task RejectedRuntimeCommitOwnershipPreservesTheLivePairForRecovery()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var runtimeCallbackObserved = false;
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            afterArtifactCommitted: (_, _) => true,
            afterRuntimeManifestCommitted: (path, _) =>
            {
                runtimeCallbackObserved = true;
                Assert.ThrowsException<IOException>(
                    () => File.Delete(path),
                    "Rejected ownership must still be evaluated under the exact runtime handle.");
                return false;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        Assert.IsTrue(runtimeCallbackObserved);
        CollectionAssert.AreEqual(
            fixture.PayloadBytes,
            File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            fixture.ManifestBytes,
            File.ReadAllBytes(
                Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.IsNull(service.ReadInstalledState());
        var journal = service.ReadJournal();
        Assert.IsNotNull(journal);
        Assert.IsTrue(journal.PreserveLiveArtifactDuringRecovery);
    }

    [TestMethod]
    public async Task PairToLegacyUpdateRemovesManagedManifestButLeavesBaseHealthy()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var pairService = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await pairService.DeployAsync(gameDirectory, fixture.Dll, ExistingArtifactPolicy.Reject)).State);
        var legacyBytes = new byte[] { 9, 8, 7, 6 };
        var legacy = new ModReleaseArtifact(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            legacyBytes.LongLength,
            Sha256(legacyBytes),
            "2.1.0.9")
        {
            ExpectedProductVersion = "v2.1.0-guffa.9",
        };
        var legacyService = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [legacy.DownloadUri] = Download(legacyBytes),
            },
            expectedVersion: legacy.ExpectedVersion,
            expectedProductVersion: legacy.ExpectedProductVersion);

        var result = await legacyService.DeployAsync(
            gameDirectory,
            legacy,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        Assert.IsNull(legacyService.ReadInstalledState()!.RuntimeManifest);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
    }

    [TestMethod]
    public async Task ChangedManagedManifestBlocksUninstallWithoutMutatingDll()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var service = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(gameDirectory, fixture.Dll, ExistingArtifactPolicy.Reject)).State);
        File.WriteAllText(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName),
            "{}",
            Encoding.UTF8);

        var result = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.ManagedArtifactChanged, result.State);
        Assert.IsTrue(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual("{}", File.ReadAllText(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName),
            Encoding.UTF8));
    }

    [TestMethod]
    public async Task LocalHealthActivatesOnlyExactReviewedPairAndKeepsChangedJsonBaseOnly()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var service = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(gameDirectory, fixture.Dll, ExistingArtifactPolicy.Reject)).State);
        var inspector = new ModInstallationInspector(
            service,
            new SystemModInstallationFileSystem(),
            reviewedCertification: fixture.Certification);

        var exact = inspector.Capture(gameDirectory, isGameRunning: false);

        Assert.AreEqual(ModInstallationEvidenceState.ManagedVerified, exact.State);
        Assert.AreEqual(ManagedRuntimeManifestEvidenceState.ReviewedPairVerified, exact.RuntimeManifestState);
        Assert.IsNotNull(exact.RuntimeActivation);

        File.WriteAllText(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName),
            "{}",
            Encoding.UTF8);
        var changed = inspector.Capture(gameDirectory, isGameRunning: false);

        Assert.AreEqual(ModInstallationEvidenceState.ManagedVerified, changed.State);
        Assert.AreEqual(ManagedRuntimeManifestEvidenceState.MissingOrChanged, changed.RuntimeManifestState);
        Assert.IsNull(changed.RuntimeActivation);
        var health = LauncherHealthResolver.Resolve(
            changed,
            new(
                "guffawaffle",
                "stable",
                DistributionId,
                CanMutate: true,
                UnavailableReason: string.Empty));
        Assert.AreEqual(ModManagementActionKind.Repair, health.ModManagement.ActionKind);
        Assert.AreEqual("Repair", health.ModManagement.ActionLabel);

        var repaired = await service.RepairAsync(gameDirectory, fixture.Dll);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, repaired.State, repaired.Message);
        var restored = inspector.Capture(gameDirectory, isGameRunning: false);
        Assert.AreEqual(ManagedRuntimeManifestEvidenceState.ReviewedPairVerified, restored.RuntimeManifestState);
        Assert.IsNotNull(restored.RuntimeActivation);

        File.Delete(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName));
        var missing = inspector.Capture(gameDirectory, isGameRunning: false);
        Assert.AreEqual(ManagedRuntimeManifestEvidenceState.MissingOrChanged, missing.RuntimeManifestState);
        Assert.AreEqual(
            ModManagementActionKind.Repair,
            LauncherHealthResolver.Resolve(
                missing,
                new(
                    "guffawaffle",
                    "stable",
                    DistributionId,
                    CanMutate: true,
                    UnavailableReason: string.Empty)).ModManagement.ActionKind);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.RepairAsync(gameDirectory, fixture.Dll)).State);
    }

    [TestMethod]
    public async Task LooseManifestIsPreservedAcrossAdoptionAndUninstall()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var originalDll = new byte[] { 1, 2, 3 };
        var looseManifest = Encoding.UTF8.GetBytes("local user data");
        var dllPath = Path.Combine(gameDirectory, "version.dll");
        var runtimePath = Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName);
        File.WriteAllBytes(dllPath, originalDll);
        File.WriteAllBytes(runtimePath, looseManifest);
        var expectedTime = new DateTime(2026, 8, 18, 10, 20, 30, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(dllPath, expectedTime);
        File.SetLastWriteTimeUtc(runtimePath, expectedTime);
        var dllAttributes = File.GetAttributes(dllPath) | FileAttributes.ReadOnly;
        var runtimeAttributes = File.GetAttributes(runtimePath) | FileAttributes.ReadOnly;
        File.SetAttributes(dllPath, dllAttributes);
        File.SetAttributes(runtimePath, runtimeAttributes);
        var service = CreateService(temporaryDirectory, fixture);

        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                fixture.Dll,
                ExistingArtifactPolicy.AdoptAndPreserve)).State);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.UninstallAsync(gameDirectory)).State);

        CollectionAssert.AreEqual(originalDll, File.ReadAllBytes(dllPath));
        CollectionAssert.AreEqual(looseManifest, File.ReadAllBytes(runtimePath));
        Assert.AreEqual(expectedTime, File.GetLastWriteTimeUtc(dllPath));
        Assert.AreEqual(expectedTime, File.GetLastWriteTimeUtc(runtimePath));
        Assert.AreEqual(dllAttributes, File.GetAttributes(dllPath));
        Assert.AreEqual(runtimeAttributes, File.GetAttributes(runtimePath));
        File.SetAttributes(dllPath, dllAttributes & ~FileAttributes.ReadOnly);
        File.SetAttributes(runtimePath, runtimeAttributes & ~FileAttributes.ReadOnly);
    }

    [TestMethod]
    public async Task JsonOnlyLooseFileRequiresAdoptionAndIsRestoredExactly()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var looseManifest = Encoding.UTF8.GetBytes("user-owned runtime notes");
        var runtimePath = Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName);
        File.WriteAllBytes(runtimePath, looseManifest);
        var service = CreateService(temporaryDirectory, fixture);

        var rejected = await service.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.ExistingArtifactRequiresAdoption, rejected.State);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(looseManifest, File.ReadAllBytes(runtimePath));

        var installed = await service.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.AdoptAndPreserve);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, installed.State, installed.Message);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await service.UninstallAsync(gameDirectory)).State);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(looseManifest, File.ReadAllBytes(runtimePath));
    }

    [TestMethod]
    public async Task LegacyDllInstallAndUninstallIgnoreUnownedLooseJson()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var looseManifest = Encoding.UTF8.GetBytes("unowned legacy-side data");
        var runtimePath = Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName);
        File.WriteAllBytes(runtimePath, looseManifest);
        var legacyBytes = new byte[] { 4, 3, 2, 1 };
        var legacy = new ModReleaseArtifact(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            legacyBytes.LongLength,
            Sha256(legacyBytes),
            "2.1.0.9")
        {
            ExpectedProductVersion = "v2.1.0-guffa.9",
        };
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [legacy.DownloadUri] = Download(legacyBytes),
            },
            expectedVersion: legacy.ExpectedVersion,
            expectedProductVersion: legacy.ExpectedProductVersion);

        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(gameDirectory, legacy, ExistingArtifactPolicy.Reject)).State);
        CollectionAssert.AreEqual(looseManifest, File.ReadAllBytes(runtimePath));
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await service.UninstallAsync(gameDirectory)).State);
        CollectionAssert.AreEqual(looseManifest, File.ReadAllBytes(runtimePath));
    }

    [TestMethod]
    public async Task ManagedLegacyToPairRequiresExplicitLooseJsonAdoption()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var looseManifest = Encoding.UTF8.GetBytes("created after legacy install");
        var runtimePath = Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName);
        var legacyBytes = new byte[] { 2, 4, 6, 8 };
        var legacy = new ModReleaseArtifact(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            legacyBytes.LongLength,
            Sha256(legacyBytes),
            "2.1.0.9")
        {
            ExpectedProductVersion = "v2.1.0-guffa.7",
        };
        var legacyService = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload> { [legacy.DownloadUri] = Download(legacyBytes) },
            expectedVersion: legacy.ExpectedVersion,
            expectedProductVersion: legacy.ExpectedProductVersion);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await legacyService.DeployAsync(gameDirectory, legacy, ExistingArtifactPolicy.Reject)).State);
        File.WriteAllBytes(runtimePath, looseManifest);
        var fixture = PairFixture.Create();
        var pairService = CreateService(temporaryDirectory, fixture);

        var rejected = await pairService.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.ExistingArtifactRequiresAdoption, rejected.State);
        CollectionAssert.AreEqual(legacyBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(looseManifest, File.ReadAllBytes(runtimePath));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await pairService.DeployAsync(
                gameDirectory,
                fixture.Dll,
                ExistingArtifactPolicy.AdoptAndPreserve)).State);
    }

    [TestMethod]
    public async Task RecoveryCleansExactPairStageResidueAfterCommittedCrash()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var service = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(gameDirectory, fixture.Dll, ExistingArtifactPolicy.Reject)).State);
        var journal = service.ReadJournal()! with { Phase = ModDeploymentPhase.CleanupPending };
        File.WriteAllText(service.JournalPath, JsonSerializer.Serialize(journal, WebJsonOptions));
        File.WriteAllBytes(journal.StagePath, DllBytes);
        var runtimeStage = Path.Combine(
            gameDirectory,
            $".{ArtifactBoundRuntimeManifestParser.ManagedFileName}.{journal.TransactionId}.stage");
        File.WriteAllBytes(runtimeStage, fixture.ManifestBytes);

        var recovered = await service.RecoverAsync();

        Assert.IsTrue(recovered.Changed);
        Assert.IsFalse(File.Exists(journal.StagePath));
        Assert.IsFalse(File.Exists(runtimeStage));
        Assert.IsTrue(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
    }

    [TestMethod]
    public async Task RecoveryPreservesUnrecognizedCommittedResidue()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var service = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(gameDirectory, fixture.Dll, ExistingArtifactPolicy.Reject)).State);
        var journal = service.ReadJournal()! with { Phase = ModDeploymentPhase.CleanupPending };
        File.WriteAllText(service.JournalPath, JsonSerializer.Serialize(journal, WebJsonOptions));
        File.WriteAllBytes(journal.StagePath, [1, 2, 3]);

        var recovered = await service.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, recovered.State);
        Assert.IsTrue(File.Exists(journal.StagePath));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(journal.StagePath));
        Assert.IsTrue(File.Exists(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task RecoveryCleansPairUninstallResidueAfterCommittedCrash()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var service = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(gameDirectory, fixture.Dll, ExistingArtifactPolicy.Reject)).State);
        var installed = service.ReadInstalledState()!;
        var transactionId = Guid.NewGuid().ToString("N");
        var dllBackup = Path.Combine(gameDirectory, $".version.dll.{transactionId}.rollback");
        var runtimeBackup = RuntimeBackupPath(gameDirectory, transactionId);
        File.Move(Path.Combine(gameDirectory, "version.dll"), dllBackup);
        File.Move(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName),
            runtimeBackup);
        File.Delete(service.InstalledStatePath);
        var journal = new ModDeploymentJournal(
            1,
            transactionId,
            ModDeploymentOperation.Uninstall,
            ModDeploymentPhase.CleanupPending,
            gameDirectory,
            fixture.Dll with { RuntimeManifest = null },
            Path.Combine(gameDirectory, $".version.dll.{transactionId}.stage"),
            dllBackup,
            Path.Combine(
                Path.GetDirectoryName(service.JournalPath)!,
                "rollback",
                transactionId,
                "version.dll"),
            HadExistingArtifact: true,
            PreviousInstalledState: installed,
            DateTimeOffset.UtcNow,
            HadExistingRuntimeManifest: true,
            CommitParticipantCompleted: true,
            ExistingArtifactIdentity: new(installed.Size, installed.Sha256),
            ExistingRuntimeManifestIdentity: new(
                installed.RuntimeManifest!.Size,
                installed.RuntimeManifest.Sha256));
        File.WriteAllText(
            service.JournalPath,
            JsonSerializer.Serialize(journal, WebJsonOptions));

        var recovered = await service.RecoverAsync();

        Assert.IsTrue(recovered.Changed);
        Assert.IsFalse(File.Exists(dllBackup));
        Assert.IsFalse(File.Exists(runtimeBackup));
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorDllBackedUp)]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorRuntimeManifestBackedUp)]
    [DataRow((int)ModDeploymentFileCheckpoint.TargetDllInstalled)]
    [DataRow((int)ModDeploymentFileCheckpoint.TargetRuntimeManifestInstalled)]
    [DataRow((int)ModDeploymentFileCheckpoint.DurableDllBackupCopyStarted)]
    [DataRow((int)ModDeploymentFileCheckpoint.DurableRuntimeManifestBackupCopyStarted)]
    [DataRow((int)ModDeploymentFileCheckpoint.DurableDllBackupPromoted)]
    [DataRow((int)ModDeploymentFileCheckpoint.DurableDllSourceRemoved)]
    [DataRow((int)ModDeploymentFileCheckpoint.DurableRuntimeManifestBackupPromoted)]
    [DataRow((int)ModDeploymentFileCheckpoint.DurableRuntimeManifestSourceRemoved)]
    public async Task PairAdoptionRecoversAtEveryLiveMove(int checkpointValue)
    {
        var checkpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var priorDll = new byte[] { 1, 7, 1, 7 };
        var priorRuntime = Encoding.UTF8.GetBytes("prior user runtime file");
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), priorDll);
        File.WriteAllBytes(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName),
            priorRuntime);
        var fixture = PairFixture.Create();
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            (observed, _) => observed == checkpoint
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() => crashing.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.AdoptAndPreserve));
        var recovery = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await recovery.RecoverAsync()).State);
        CollectionAssert.AreEqual(priorDll, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            priorRuntime,
            File.ReadAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.IsNull(recovery.ReadInstalledState());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PairAdoptionRecoversFromMidCopyInterruption(bool interruptRuntime)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var priorDll = Enumerable.Range(0, 200_000)
            .Select(index => checked((byte)(index % 251)))
            .ToArray();
        var priorRuntime = Enumerable.Range(0, 180_000)
            .Select(index => checked((byte)(index % 239)))
            .ToArray();
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), priorDll);
        File.WriteAllBytes(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName),
            priorRuntime);
        var fixture = PairFixture.Create();
        var interrupted = false;
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            afterDurableCopyChunkWritten: (source, _, written, _) =>
            {
                var isRuntime = Path.GetFileName(source).Contains(
                    ArtifactBoundRuntimeManifestParser.ManagedFileName,
                    StringComparison.OrdinalIgnoreCase);
                if (!interrupted && isRuntime == interruptRuntime)
                {
                    interrupted = true;
                    Assert.IsTrue(written < (isRuntime ? priorRuntime.LongLength : priorDll.LongLength));
                    throw new SimulatedProcessTerminationException(
                        isRuntime
                            ? ModDeploymentFileCheckpoint.DurableRuntimeManifestBackupCopyStarted
                            : ModDeploymentFileCheckpoint.DurableDllBackupCopyStarted);
                }
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() => crashing.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.AdoptAndPreserve));
        var recovery = CreateService(temporaryDirectory, fixture);
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(priorDll, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            priorRuntime,
            File.ReadAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.IsNull(recovery.ReadInstalledState());
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.ManagedDllRemoved)]
    [DataRow((int)ModDeploymentFileCheckpoint.ManagedRuntimeManifestRemoved)]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedDllRestoreCopyStarted)]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestoreCopyStarted)]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedDllRestoreStaged)]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestoreStaged)]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedDllRestored)]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestored)]
    public async Task PairUninstallRecoversAtEveryLiveMove(int checkpointValue)
    {
        var checkpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var priorDll = new byte[] { 9, 1, 1 };
        var priorRuntime = Encoding.UTF8.GetBytes("prior runtime companion");
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), priorDll);
        File.WriteAllBytes(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName),
            priorRuntime);
        var fixture = PairFixture.Create();
        var installer = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(
                gameDirectory,
                fixture.Dll,
                ExistingArtifactPolicy.AdoptAndPreserve)).State);
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            (observed, _) => observed == checkpoint
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(
            () => crashing.UninstallAsync(gameDirectory));
        var recovery = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await recovery.RecoverAsync()).State);
        CollectionAssert.AreEqual(DllBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            fixture.ManifestBytes,
            File.ReadAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        var state = recovery.ReadInstalledState()!;
        CollectionAssert.AreEqual(priorDll, File.ReadAllBytes(state.PreviousArtifactBackupPath!));
        CollectionAssert.AreEqual(priorRuntime, File.ReadAllBytes(state.PreviousRuntimeManifestBackupPath!));
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedDllRestored)]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestored)]
    public async Task ReadOnlyAdoptedPairUninstallRecoveryIsIdempotentAfterRestore(int checkpointValue)
    {
        var checkpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var dllPath = Path.Combine(gameDirectory, "version.dll");
        var runtimePath = Path.Combine(
            gameDirectory,
            ArtifactBoundRuntimeManifestParser.ManagedFileName);
        var priorDll = new byte[] { 9, 2, 2 };
        var priorRuntime = Encoding.UTF8.GetBytes("read-only prior runtime companion");
        File.WriteAllBytes(dllPath, priorDll);
        File.WriteAllBytes(runtimePath, priorRuntime);
        File.SetAttributes(dllPath, File.GetAttributes(dllPath) | FileAttributes.ReadOnly);
        File.SetAttributes(runtimePath, File.GetAttributes(runtimePath) | FileAttributes.ReadOnly);
        var fixture = PairFixture.Create();
        var installer = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(
                gameDirectory,
                fixture.Dll,
                ExistingArtifactPolicy.AdoptAndPreserve)).State);
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            (observed, _) => observed == checkpoint
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(
            () => crashing.UninstallAsync(gameDirectory));
        var recovery = CreateService(temporaryDirectory, fixture);
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(fixture.PayloadBytes, File.ReadAllBytes(dllPath));
        CollectionAssert.AreEqual(fixture.ManifestBytes, File.ReadAllBytes(runtimePath));
        var state = recovery.ReadInstalledState()!;
        CollectionAssert.AreEqual(priorDll, File.ReadAllBytes(state.PreviousArtifactBackupPath!));
        CollectionAssert.AreEqual(priorRuntime, File.ReadAllBytes(state.PreviousRuntimeManifestBackupPath!));
        Assert.IsTrue(File.GetAttributes(state.PreviousArtifactBackupPath!).HasFlag(FileAttributes.ReadOnly));
        Assert.IsTrue(File.GetAttributes(state.PreviousRuntimeManifestBackupPath!).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(state.PreviousArtifactBackupPath!, FileAttributes.Normal);
        File.SetAttributes(state.PreviousRuntimeManifestBackupPath!, FileAttributes.Normal);
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedDllRestored)]
    [DataRow((int)ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestored)]
    public async Task FreshRecoveryPreservesSameByteReplacementOfRestoredAdoptedMember(int checkpointValue)
    {
        var checkpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var dllPath = Path.Combine(gameDirectory, "version.dll");
        var runtimePath = Path.Combine(
            gameDirectory,
            ArtifactBoundRuntimeManifestParser.ManagedFileName);
        var priorDll = new byte[] { 9, 3, 3 };
        var priorRuntime = Encoding.UTF8.GetBytes("same-byte adopted runtime companion");
        File.WriteAllBytes(dllPath, priorDll);
        File.WriteAllBytes(runtimePath, priorRuntime);
        var fixture = PairFixture.Create();
        var installer = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(
                gameDirectory,
                fixture.Dll,
                ExistingArtifactPolicy.AdoptAndPreserve)).State);
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            (observed, _) => observed == checkpoint
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(
            () => crashing.UninstallAsync(gameDirectory));
        var replacedPath = checkpoint == ModDeploymentFileCheckpoint.AdoptedDllRestored
            ? dllPath
            : runtimePath;
        var journal = crashing.ReadJournal()!;
        var persistedIdentity = checkpoint == ModDeploymentFileCheckpoint.AdoptedDllRestored
            ? journal.RestoredAdoptedArtifactFileIdentity
            : journal.RestoredAdoptedRuntimeManifestFileIdentity;
        Assert.IsNotNull(persistedIdentity);
        var contents = File.ReadAllBytes(replacedPath);
        var attributes = File.GetAttributes(replacedPath);
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(replacedPath);
        File.Delete(replacedPath);
        File.WriteAllBytes(replacedPath, contents);
        File.SetLastWriteTimeUtc(replacedPath, lastWriteTimeUtc);
        File.SetAttributes(replacedPath, attributes);
        CandidateFileIdentity externalIdentity;
        using (var exact = ExactFileMutation.Open(replacedPath))
        {
            externalIdentity = exact.Identity;
            Assert.IsFalse(
                string.Equals(
                    persistedIdentity.VolumeSerialNumber,
                    exact.Identity.VolumeSerialNumber,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    persistedIdentity.FileIndex,
                    exact.Identity.FileIndex,
                    StringComparison.OrdinalIgnoreCase));
        }

        var recovery = CreateService(temporaryDirectory, fixture);
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        CollectionAssert.AreEqual(contents, File.ReadAllBytes(replacedPath));
        using (var exact = ExactFileMutation.Open(replacedPath))
        {
            Assert.AreEqual(externalIdentity, exact.Identity);
        }
        Assert.AreEqual(ModDeploymentPhase.RollingBack, recovery.ReadJournal()!.Phase);
        var retainedRollbackPath = checkpoint == ModDeploymentFileCheckpoint.AdoptedDllRestored
            ? journal.SameVolumeBackupPath
            : RuntimeBackupPath(gameDirectory, journal.TransactionId);
        Assert.IsTrue(File.Exists(retainedRollbackPath));
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorDllBackedUp)]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorRuntimeManifestBackedUp)]
    [DataRow((int)ModDeploymentFileCheckpoint.TargetDllInstalled)]
    [DataRow((int)ModDeploymentFileCheckpoint.TargetRuntimeManifestInstalled)]
    public async Task ManagedPairUpdateRecoversPriorPairAtEveryLiveMove(int checkpointValue)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var prior = PairFixture.Create();
        var installer = CreateService(temporaryDirectory, prior);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(gameDirectory, prior.Dll, ExistingArtifactPolicy.Reject)).State);
        var target = PairFixture.Create(
            payloadBytes: [0x54, 0x41, 0x52, 0x47, 0x45, 0x54],
            runtimeVersion: "2.1.0.9");
        var checkpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [target.Dll.DownloadUri] = Download(target.PayloadBytes),
                [target.Manifest.DownloadUri] = Download(target.ManifestBytes),
            },
            target.Certification,
            target.Dll.ExpectedVersion,
            (observed, _) => observed == checkpoint
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() => crashing.DeployAsync(
            gameDirectory,
            target.Dll,
            ExistingArtifactPolicy.Reject));
        var recovery = CreateService(temporaryDirectory, target);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await recovery.RecoverAsync()).State);
        CollectionAssert.AreEqual(prior.PayloadBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            prior.ManifestBytes,
            File.ReadAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.AreEqual(
            prior.Dll.Sha256,
            recovery.ReadInstalledState()!.Sha256,
            ignoreCase: true,
            CultureInfo.InvariantCulture);
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorDllBackedUp)]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorRuntimeManifestBackedUp)]
    [DataRow((int)ModDeploymentFileCheckpoint.TargetDllInstalled)]
    [DataRow((int)ModDeploymentFileCheckpoint.TargetRuntimeManifestInstalled)]
    public async Task CoordinatedPairUpdateCanRollBackAtEveryLiveMove(int checkpointValue)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var prior = PairFixture.Create();
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await CreateService(temporaryDirectory, prior).DeployAsync(
                gameDirectory,
                prior.Dll,
                ExistingArtifactPolicy.Reject)).State);
        var target = PairFixture.Create(
            payloadBytes: [0x43, 0x4f, 0x4f, 0x52, 0x44],
            runtimeVersion: "2.1.0.9");
        var checkpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [target.Dll.DownloadUri] = Download(target.PayloadBytes),
                [target.Manifest.DownloadUri] = Download(target.ManifestBytes),
            },
            target.Certification,
            target.Dll.ExpectedVersion,
            (observed, _) => observed == checkpoint
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);
        var transactionId = Guid.NewGuid().ToString("N");
        var participant = new NoOpCommitParticipant();

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() =>
            crashing.DeployCoordinatedAsync(
                gameDirectory,
                target.Dll,
                ExistingArtifactPolicy.Reject,
                transactionId,
                participant));
        var recovery = CreateService(temporaryDirectory, target);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await recovery.RollBackCoordinatedAsync(transactionId)).State);
        CollectionAssert.AreEqual(prior.PayloadBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            prior.ManifestBytes,
            File.ReadAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.AreEqual(1, participant.BeginCount);
        Assert.AreEqual(0, participant.CommitCount);
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.RollbackDllRestoreCopyStarted)]
    [DataRow((int)ModDeploymentFileCheckpoint.RollbackRuntimeManifestRestoreCopyStarted)]
    [DataRow((int)ModDeploymentFileCheckpoint.RollbackDllRestoreStaged)]
    [DataRow((int)ModDeploymentFileCheckpoint.RollbackRuntimeManifestRestoreStaged)]
    [DataRow((int)ModDeploymentFileCheckpoint.RollbackDllRestored)]
    [DataRow((int)ModDeploymentFileCheckpoint.RollbackRuntimeManifestRestored)]
    public async Task DurablePairRollbackCanResumeAtEveryCopyAndMoveBoundary(int checkpointValue)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var priorDll = new byte[] { 0x4f, 0x4c, 0x44 };
        var priorRuntime = Encoding.UTF8.GetBytes("old-loose-runtime");
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), priorDll);
        File.WriteAllBytes(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName),
            priorRuntime);
        var target = PairFixture.Create();
        var forwardCrash = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [target.Dll.DownloadUri] = Download(target.PayloadBytes),
                [target.Manifest.DownloadUri] = Download(target.ManifestBytes),
            },
            target.Certification,
            target.Dll.ExpectedVersion,
            (observed, _) => observed == ModDeploymentFileCheckpoint.DurableRuntimeManifestSourceRemoved
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);
        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() => forwardCrash.DeployAsync(
            gameDirectory,
            target.Dll,
            ExistingArtifactPolicy.AdoptAndPreserve));
        var rollbackCheckpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        var rollbackCrash = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>(),
            target.Certification,
            target.Dll.ExpectedVersion,
            (observed, _) => observed == rollbackCheckpoint
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() => rollbackCrash.RecoverAsync());
        var recovery = CreateService(temporaryDirectory, target);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await recovery.RecoverAsync()).State);
        CollectionAssert.AreEqual(priorDll, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            priorRuntime,
            File.ReadAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.IsNull(recovery.ReadInstalledState());
    }

    [TestMethod]
    public async Task DurableReadOnlyPairRollbackCanResumeAfterBothFilesWereRestored()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var dllPath = Path.Combine(gameDirectory, "version.dll");
        var runtimePath = Path.Combine(
            gameDirectory,
            ArtifactBoundRuntimeManifestParser.ManagedFileName);
        var priorDll = new byte[] { 0x52, 0x4f, 0x2d, 0x44, 0x4c, 0x4c };
        var priorRuntime = Encoding.UTF8.GetBytes("read-only-runtime");
        File.WriteAllBytes(dllPath, priorDll);
        File.WriteAllBytes(runtimePath, priorRuntime);
        var expectedTime = new DateTime(2026, 8, 18, 9, 8, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(dllPath, expectedTime);
        File.SetLastWriteTimeUtc(runtimePath, expectedTime);
        var dllAttributes = File.GetAttributes(dllPath) | FileAttributes.ReadOnly;
        var runtimeAttributes = File.GetAttributes(runtimePath) | FileAttributes.ReadOnly;
        File.SetAttributes(dllPath, dllAttributes);
        File.SetAttributes(runtimePath, runtimeAttributes);
        var target = PairFixture.Create();
        try
        {
            var forwardCrash = CreateService(
                temporaryDirectory,
                new Dictionary<Uri, ModArtifactDownload>
                {
                    [target.Dll.DownloadUri] = Download(target.PayloadBytes),
                    [target.Manifest.DownloadUri] = Download(target.ManifestBytes),
                },
                target.Certification,
                target.Dll.ExpectedVersion,
                (observed, _) => observed
                        == ModDeploymentFileCheckpoint.DurableRuntimeManifestSourceRemoved
                    ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                    : ValueTask.CompletedTask);
            await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() =>
                forwardCrash.DeployAsync(
                    gameDirectory,
                    target.Dll,
                    ExistingArtifactPolicy.AdoptAndPreserve));
            var rollbackCrash = CreateService(
                temporaryDirectory,
                new Dictionary<Uri, ModArtifactDownload>(),
                target.Certification,
                target.Dll.ExpectedVersion,
                (observed, _) => observed
                        == ModDeploymentFileCheckpoint.RollbackRuntimeManifestRestored
                    ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                    : ValueTask.CompletedTask);
            await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(
                () => rollbackCrash.RecoverAsync());

            var recovery = CreateService(temporaryDirectory, target);
            var result = await recovery.RecoverAsync();

            Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
            CollectionAssert.AreEqual(priorDll, File.ReadAllBytes(dllPath));
            CollectionAssert.AreEqual(priorRuntime, File.ReadAllBytes(runtimePath));
            Assert.AreEqual(expectedTime, File.GetLastWriteTimeUtc(dllPath));
            Assert.AreEqual(expectedTime, File.GetLastWriteTimeUtc(runtimePath));
            Assert.AreEqual(dllAttributes, File.GetAttributes(dllPath));
            Assert.AreEqual(runtimeAttributes, File.GetAttributes(runtimePath));
            Assert.IsNull(recovery.ReadInstalledState());
        }
        finally
        {
            if (File.Exists(dllPath))
            {
                File.SetAttributes(dllPath, File.GetAttributes(dllPath) & ~FileAttributes.ReadOnly);
            }
            if (File.Exists(runtimePath))
            {
                File.SetAttributes(
                    runtimePath,
                    File.GetAttributes(runtimePath) & ~FileAttributes.ReadOnly);
            }
        }
    }

    [TestMethod]
    public async Task SameProcessDurableCopyFailureRollsBackExactPair()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var priorDll = new byte[] { 0x43, 0x4f, 0x50, 0x59 };
        var priorRuntime = Encoding.UTF8.GetBytes("copy-failure-runtime");
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), priorDll);
        File.WriteAllBytes(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName),
            priorRuntime);
        var fixture = PairFixture.Create();
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            (observed, _) => observed == ModDeploymentFileCheckpoint.DurableDllBackupCopyStarted
                ? ValueTask.FromException(new IOException("Injected copy failure."))
                : ValueTask.CompletedTask);

        var result = await service.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.AdoptAndPreserve);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State);
        CollectionAssert.AreEqual(priorDll, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            priorRuntime,
            File.ReadAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.IsNull(service.ReadInstalledState());
    }

    [TestMethod]
    public async Task UnreceiptedDurablePromotionStageIsPreservedEvenWithExactSource()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var priorDll = new byte[] { 7, 7, 1 };
        var priorRuntime = Encoding.UTF8.GetBytes("loose-runtime");
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), priorDll);
        File.WriteAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName), priorRuntime);
        var fixture = PairFixture.Create();
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            (observed, _) => observed == ModDeploymentFileCheckpoint.TargetRuntimeManifestInstalled
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);
        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() => crashing.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.AdoptAndPreserve));
        var journal = crashing.ReadJournal()!;
        Directory.CreateDirectory(Path.GetDirectoryName(journal.DurableBackupPath)!);
        File.WriteAllBytes(journal.DurableBackupPath + ".stage", [0xde, 0xad]);

        var recovery = CreateService(temporaryDirectory, fixture);
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        CollectionAssert.AreEqual(
            new byte[] { 0xde, 0xad },
            File.ReadAllBytes(journal.DurableBackupPath + ".stage"));
    }

    [TestMethod]
    public async Task UnreceiptedGameVolumeRestoreStageIsPreservedEvenWithExactDurableSource()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var priorDll = new byte[] { 8, 8, 2 };
        var priorRuntime = Encoding.UTF8.GetBytes("adopted-runtime");
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), priorDll);
        File.WriteAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName), priorRuntime);
        var fixture = PairFixture.Create();
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await CreateService(temporaryDirectory, fixture).DeployAsync(
                gameDirectory,
                fixture.Dll,
                ExistingArtifactPolicy.AdoptAndPreserve)).State);
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>(),
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            (observed, _) => observed == ModDeploymentFileCheckpoint.ManagedRuntimeManifestRemoved
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);
        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(
            () => crashing.UninstallAsync(gameDirectory));
        var journal = crashing.ReadJournal()!;
        File.WriteAllBytes(journal.StagePath, [0xba, 0xd0]);

        var recovery = CreateService(temporaryDirectory, fixture);
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        CollectionAssert.AreEqual(new byte[] { 0xba, 0xd0 }, File.ReadAllBytes(journal.StagePath));
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorDllBackedUp)]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorRuntimeManifestBackedUp)]
    [DataRow((int)ModDeploymentFileCheckpoint.TargetDllInstalled)]
    public async Task PairToLegacyRecoversExactPriorPairAtEveryMove(int checkpointValue)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var prior = PairFixture.Create();
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await CreateService(temporaryDirectory, prior).DeployAsync(
                gameDirectory,
                prior.Dll,
                ExistingArtifactPolicy.Reject)).State);
        var legacyBytes = new byte[] { 0x4c, 0x45, 0x47 };
        var legacy = new ModReleaseArtifact(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            legacyBytes.LongLength,
            Sha256(legacyBytes),
            "2.1.0.9")
        {
            ExpectedProductVersion = "v2.1.0-guffa.9",
        };
        var checkpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload> { [legacy.DownloadUri] = Download(legacyBytes) },
            expectedVersion: legacy.ExpectedVersion,
            expectedProductVersion: legacy.ExpectedProductVersion,
            afterFileCheckpoint: (observed, _) => observed == checkpoint
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() => crashing.DeployAsync(
            gameDirectory,
            legacy,
            ExistingArtifactPolicy.Reject));
        var recovery = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>(),
            expectedVersion: legacy.ExpectedVersion,
            expectedProductVersion: legacy.ExpectedProductVersion);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await recovery.RecoverAsync()).State);
        CollectionAssert.AreEqual(prior.PayloadBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            prior.ManifestBytes,
            File.ReadAllBytes(Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.IsNotNull(recovery.ReadInstalledState()!.RuntimeManifest);
    }

    [TestMethod]
    public async Task TamperedRuntimeRollbackCounterpartPreventsAnyRecoveryMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var prior = PairFixture.Create();
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await CreateService(temporaryDirectory, prior).DeployAsync(
                gameDirectory,
                prior.Dll,
                ExistingArtifactPolicy.Reject)).State);
        var target = PairFixture.Create(payloadBytes: [0x4e, 0x45, 0x57], runtimeVersion: "2.1.0.9");
        var crashing = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [target.Dll.DownloadUri] = Download(target.PayloadBytes),
                [target.Manifest.DownloadUri] = Download(target.ManifestBytes),
            },
            target.Certification,
            target.Dll.ExpectedVersion,
            (observed, _) => observed == ModDeploymentFileCheckpoint.TargetDllInstalled
                ? ValueTask.FromException(new SimulatedProcessTerminationException(observed))
                : ValueTask.CompletedTask);
        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() => crashing.DeployAsync(
            gameDirectory,
            target.Dll,
            ExistingArtifactPolicy.Reject));
        var journal = crashing.ReadJournal()!;
        var runtimeBackup = RuntimeBackupPath(gameDirectory, journal.TransactionId);
        File.WriteAllBytes(runtimeBackup, [0xff]);
        var paths = new[]
        {
            Path.Combine(gameDirectory, "version.dll"),
            journal.SameVolumeBackupPath,
            runtimeBackup,
            Path.Combine(
                gameDirectory,
                $".{ArtifactBoundRuntimeManifestParser.ManagedFileName}.{journal.TransactionId}.stage"),
        };
        var before = paths.ToDictionary(path => path, File.ReadAllBytes);

        var result = await CreateService(temporaryDirectory, target).RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State);
        foreach (var path in paths)
        {
            CollectionAssert.AreEqual(before[path], File.ReadAllBytes(path));
        }
        Assert.IsFalse(File.Exists(
            Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName)));
    }

    [TestMethod]
    public async Task PairUpdateRollbackReplacesExactReadOnlyTransactionTargets()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var prior = PairFixture.Create();
        var installer = CreateService(temporaryDirectory, prior);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(
                gameDirectory,
                prior.Dll,
                ExistingArtifactPolicy.Reject)).State);
        var target = PairFixture.Create(payloadBytes: [0x4e, 0x45, 0x57], runtimeVersion: "2.1.0.9");
        var dllPath = Path.Combine(gameDirectory, "version.dll");
        var runtimePath = Path.Combine(
            gameDirectory,
            ArtifactBoundRuntimeManifestParser.ManagedFileName);
        var injected = false;
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [target.Dll.DownloadUri] = Download(target.PayloadBytes),
                [target.Manifest.DownloadUri] = Download(target.ManifestBytes),
            },
            target.Certification,
            target.Dll.ExpectedVersion,
            afterPhasePersisted: (phase, _) =>
            {
                if (!injected && phase == ModDeploymentPhase.CleanupPending)
                {
                    injected = true;
                    File.SetAttributes(dllPath, File.GetAttributes(dllPath) | FileAttributes.ReadOnly);
                    File.SetAttributes(
                        runtimePath,
                        File.GetAttributes(runtimePath) | FileAttributes.ReadOnly);
                    return ValueTask.FromException(new IOException("Injected pair cleanup-boundary failure."));
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            target.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State, result.Message);
        Assert.IsTrue(injected);
        CollectionAssert.AreEqual(prior.PayloadBytes, File.ReadAllBytes(dllPath));
        CollectionAssert.AreEqual(prior.ManifestBytes, File.ReadAllBytes(runtimePath));
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await service.RecoverAsync()).State);
    }

    [TestMethod]
    public async Task VerifiedPairStagesCannotBeReplacedBeforeExactPromotion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var blockedReplacementCount = 0;
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            afterPhasePersisted: (phase, _) =>
            {
                if (phase != ModDeploymentPhase.Staged)
                {
                    return ValueTask.CompletedTask;
                }
                foreach (var stagePath in Directory.GetFiles(gameDirectory, "*.stage"))
                {
                    try
                    {
                        File.Delete(stagePath);
                    }
                    catch (IOException)
                    {
                        blockedReplacementCount++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        blockedReplacementCount++;
                    }
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(2, blockedReplacementCount);
        CollectionAssert.AreEqual(
            fixture.PayloadBytes,
            File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            fixture.ManifestBytes,
            File.ReadAllBytes(Path.Combine(
                gameDirectory,
                ArtifactBoundRuntimeManifestParser.ManagedFileName)));
    }

    [TestMethod]
    public async Task PairUpdateLocksExistingMembersAcrossTheCommittingBackupBoundary()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var prior = PairFixture.Create();
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await CreateService(temporaryDirectory, prior).DeployAsync(
                gameDirectory,
                prior.Dll,
                ExistingArtifactPolicy.Reject)).State);
        var target = PairFixture.Create(payloadBytes: [0x55, 0x50, 0x44], runtimeVersion: "2.1.0.9");
        var dllPath = Path.Combine(gameDirectory, "version.dll");
        var runtimePath = Path.Combine(
            gameDirectory,
            ArtifactBoundRuntimeManifestParser.ManagedFileName);
        var blockedMutations = 0;
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [target.Dll.DownloadUri] = Download(target.PayloadBytes),
                [target.Manifest.DownloadUri] = Download(target.ManifestBytes),
            },
            target.Certification,
            target.Dll.ExpectedVersion,
            afterPhasePersisted: (phase, _) =>
            {
                if (phase == ModDeploymentPhase.Committing)
                {
                    blockedMutations += CountBlockedLiveMutations(
                        dllPath,
                        prior.PayloadBytes);
                    blockedMutations += CountBlockedLiveMutations(
                        runtimePath,
                        prior.ManifestBytes);
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            target.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(4, blockedMutations);
        CollectionAssert.AreEqual(target.PayloadBytes, File.ReadAllBytes(dllPath));
        CollectionAssert.AreEqual(target.ManifestBytes, File.ReadAllBytes(runtimePath));
    }

    [TestMethod]
    public async Task PairUninstallLocksManagedMembersAcrossTheCommittingBackupBoundary()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await CreateService(temporaryDirectory, fixture).DeployAsync(
                gameDirectory,
                fixture.Dll,
                ExistingArtifactPolicy.Reject)).State);
        var dllPath = Path.Combine(gameDirectory, "version.dll");
        var runtimePath = Path.Combine(
            gameDirectory,
            ArtifactBoundRuntimeManifestParser.ManagedFileName);
        var blockedMutations = 0;
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>(),
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            afterPhasePersisted: (phase, _) =>
            {
                if (phase == ModDeploymentPhase.Committing)
                {
                    blockedMutations += CountBlockedLiveMutations(
                        dllPath,
                        fixture.PayloadBytes);
                    blockedMutations += CountBlockedLiveMutations(
                        runtimePath,
                        fixture.ManifestBytes);
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(4, blockedMutations);
        Assert.IsFalse(File.Exists(dllPath));
        Assert.IsFalse(File.Exists(runtimePath));
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorDllBackedUp)]
    [DataRow((int)ModDeploymentFileCheckpoint.PriorRuntimeManifestBackedUp)]
    public async Task PairUpdateCheckpointFailureAfterBackupRestoresTheExactPriorPair(int checkpointValue)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var prior = PairFixture.Create();
        var installer = CreateService(temporaryDirectory, prior);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(
                gameDirectory,
                prior.Dll,
                ExistingArtifactPolicy.Reject)).State);
        var priorState = installer.ReadInstalledState(gameDirectory);
        var target = PairFixture.Create(payloadBytes: [0x55, 0x50, 0x44], runtimeVersion: "2.1.0.9");
        var checkpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [target.Dll.DownloadUri] = Download(target.PayloadBytes),
                [target.Manifest.DownloadUri] = Download(target.ManifestBytes),
            },
            target.Certification,
            target.Dll.ExpectedVersion,
            afterFileCheckpoint: (observed, _) => observed == checkpoint
                ? ValueTask.FromException(new InvalidDataException("Injected exact-backup checkpoint failure."))
                : ValueTask.CompletedTask);

        var result = await service.DeployAsync(
            gameDirectory,
            target.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State, result.Message);
        CollectionAssert.AreEqual(
            prior.PayloadBytes,
            File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            prior.ManifestBytes,
            File.ReadAllBytes(Path.Combine(
                gameDirectory,
                ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.AreEqual(priorState, service.ReadInstalledState(gameDirectory));
        Assert.AreEqual(ModDeploymentPhase.RolledBack, service.ReadJournal()!.Phase);
    }

    [DataTestMethod]
    [DataRow((int)ModDeploymentFileCheckpoint.ManagedDllRemoved)]
    [DataRow((int)ModDeploymentFileCheckpoint.ManagedRuntimeManifestRemoved)]
    public async Task PairUninstallCheckpointFailureAfterRemovalRestoresTheExactManagedPair(int checkpointValue)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var installer = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(
                gameDirectory,
                fixture.Dll,
                ExistingArtifactPolicy.Reject)).State);
        var installedState = installer.ReadInstalledState(gameDirectory);
        var checkpoint = (ModDeploymentFileCheckpoint)checkpointValue;
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>(),
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            afterFileCheckpoint: (observed, _) => observed == checkpoint
                ? ValueTask.FromException(new InvalidDataException("Injected exact-removal checkpoint failure."))
                : ValueTask.CompletedTask);

        var result = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State, result.Message);
        CollectionAssert.AreEqual(
            fixture.PayloadBytes,
            File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(
            fixture.ManifestBytes,
            File.ReadAllBytes(Path.Combine(
                gameDirectory,
                ArtifactBoundRuntimeManifestParser.ManagedFileName)));
        Assert.AreEqual(installedState, service.ReadInstalledState(gameDirectory));
        Assert.AreEqual(ModDeploymentPhase.RolledBack, service.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task SameByteRuntimeAppearingAfterDllPromotionIsPreservedDuringRollback()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var dllPath = Path.Combine(gameDirectory, "version.dll");
        var runtimePath = Path.Combine(
            gameDirectory,
            ArtifactBoundRuntimeManifestParser.ManagedFileName);
        CandidateFileIdentity? externalIdentity = null;
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            afterFileCheckpoint: (checkpoint, _) =>
            {
                if (checkpoint == ModDeploymentFileCheckpoint.TargetDllInstalled)
                {
                    File.WriteAllBytes(runtimePath, fixture.ManifestBytes);
                    using var exact = ExactFileMutation.Open(runtimePath);
                    externalIdentity = exact.Identity;
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            fixture.Dll,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        Assert.IsFalse(File.Exists(dllPath));
        Assert.IsNotNull(externalIdentity);
        CollectionAssert.AreEqual(fixture.ManifestBytes, File.ReadAllBytes(runtimePath));
        using (var exact = ExactFileMutation.Open(runtimePath))
        {
            Assert.AreEqual(externalIdentity, exact.Identity);
        }
        Assert.AreEqual(ModDeploymentPhase.RollingBack, service.ReadJournal()!.Phase);
    }

    private static int CountBlockedLiveMutations(string path, byte[] sameBytes)
    {
        var blocked = 0;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            blocked++;
        }
        catch (UnauthorizedAccessException)
        {
            blocked++;
        }
        try
        {
            File.WriteAllBytes(path, sameBytes);
        }
        catch (IOException)
        {
            blocked++;
        }
        catch (UnauthorizedAccessException)
        {
            blocked++;
        }
        return blocked;
    }

    [DataTestMethod]
    [DataRow("dll")]
    [DataRow("runtime")]
    public async Task UninstallRechecksManagedPairAfterPlannedBeforeAnyMove(string member)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = PairFixture.Create();
        var installer = CreateService(temporaryDirectory, fixture);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(gameDirectory, fixture.Dll, ExistingArtifactPolicy.Reject)).State);
        var priorState = installer.ReadInstalledState()!;
        var changedPath = Path.Combine(
            gameDirectory,
            member == "dll" ? "version.dll" : ArtifactBoundRuntimeManifestParser.ManagedFileName);
        var changedBytes = Encoding.UTF8.GetBytes($"external-{member}-change");
        var service = CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>(),
            fixture.Certification,
            fixture.Dll.ExpectedVersion,
            afterPhasePersisted: (phase, _) =>
            {
                if (phase == ModDeploymentPhase.Planned)
                {
                    File.WriteAllBytes(changedPath, changedBytes);
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.ManagedArtifactChanged, result.State);
        CollectionAssert.AreEqual(changedBytes, File.ReadAllBytes(changedPath));
        Assert.AreEqual(priorState, service.ReadInstalledState());
        var journal = service.ReadJournal()!;
        Assert.AreEqual(ModDeploymentPhase.Failed, journal.Phase);
        Assert.IsFalse(File.Exists(journal.SameVolumeBackupPath));
        Assert.IsFalse(File.Exists(RuntimeBackupPath(gameDirectory, journal.TransactionId)));
    }

    private static ModDeploymentService CreateService(TemporaryDirectory temporaryDirectory, PairFixture fixture) =>
        CreateService(
            temporaryDirectory,
            new Dictionary<Uri, ModArtifactDownload>
            {
                [fixture.Dll.DownloadUri] = Download(fixture.PayloadBytes),
                [fixture.Manifest.DownloadUri] = Download(fixture.ManifestBytes),
            },
            fixture.Certification,
            fixture.Dll.ExpectedVersion);

    private static ModDeploymentService CreateService(
        TemporaryDirectory temporaryDirectory,
        IReadOnlyDictionary<Uri, ModArtifactDownload> downloads,
        ReviewedReleaseCertification? certification = null,
        string expectedVersion = "2.1.0.8",
        Func<ModDeploymentFileCheckpoint, CancellationToken, ValueTask>? afterFileCheckpoint = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted = null,
        string? expectedProductVersion = null,
        Func<string, string, long, CancellationToken, ValueTask>? afterDurableCopyChunkWritten = null,
        Func<string, ExactFileRevision, bool>? afterArtifactCommitted = null,
        Func<string, ExactFileRevision, bool>? afterRuntimeManifestCommitted = null) =>
        new(
            temporaryDirectory.CreateDirectory("state"),
            new RouteDownloader(downloads),
            new StaticVersionReader(expectedVersion, expectedProductVersion),
            new TrustedVerifier(),
            _ => false,
            new("guffawaffle", "stable", DistributionId),
            timeProvider: null,
            afterPhasePersisted: afterPhasePersisted,
            reviewedCertification: certification,
            afterFileCheckpoint: afterFileCheckpoint,
            afterArtifactCommitted: afterArtifactCommitted,
            afterRuntimeManifestCommitted: afterRuntimeManifestCommitted,
            afterDurableCopyChunkWritten: afterDurableCopyChunkWritten);

    private static WindowsReleaseManifest ReleaseManifest(PairFixture fixture) => new(
        1,
        "2.1.0-guffa.8",
        Tag,
        "stable",
        "active",
        new Version(0, 1, 0),
        new(Repository, SourceRevision),
        "none",
        [
            new(
                "windows-mod-dll-x64",
                "windows-mod",
                "windows",
                "x64",
                "version.dll",
                "application/vnd.microsoft.portable-executable",
                fixture.Dll.Size,
                fixture.Dll.Sha256,
                new("authenticode", "artifact", [])),
            new(
                "windows-mod-runtime-manifest-x64",
                "windows-mod-runtime-manifest",
                "windows",
                "x64",
                fixture.Manifest.FileName,
                "application/json",
                fixture.Manifest.Size,
                fixture.Manifest.Sha256,
                new("none", "none", [])),
        ]);

    private static string CreateGameDirectory(TemporaryDirectory temporaryDirectory)
    {
        var gameDirectory = temporaryDirectory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        return gameDirectory;
    }

    private static ModArtifactDownload Download(byte[] bytes) =>
        new(HttpStatusCode.OK, bytes, bytes.LongLength);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string RuntimeBackupPath(string gameDirectory, string transactionId) =>
        Path.Combine(
            gameDirectory,
            $".{ArtifactBoundRuntimeManifestParser.ManagedFileName}.{transactionId}.rollback");

    private sealed record PairFixture(
        ModReleaseArtifact Dll,
        ModRuntimeManifestArtifact Manifest,
        byte[] ManifestBytes,
        ReviewedReleaseCertification Certification,
        byte[] PayloadBytes)
    {
        public static PairFixture Create(
            string? extraCapability = null,
            byte[]? payloadBytes = null,
            string runtimeVersion = "2.1.0.8")
        {
            payloadBytes ??= DllBytes;
            var releaseTag = runtimeVersion == "2.1.0.8"
                ? Tag
                : $"v2.1.0-guffa.{runtimeVersion.Split('.')[^1]}";
            var dllSha256 = Sha256(payloadBytes);
            var capabilities = new List<string>
            {
                LauncherCapabilityIds.PrincipalSettingsTaxonomyV1,
                "ingest.stfc-sidecar.v1",
                "battle.capture.v1",
                "fleet.runtime-snapshot.v1",
            };
            if (extraCapability is not null)
            {
                capabilities.Add(extraCapability);
            }
            var manifestObject = new
            {
                manifestSchema = 1,
                distributionId = DistributionId,
                runtimeVersion,
                sourceRevision = SourceRevision,
                capabilities,
                settingsCatalog = new { schemaVersion = 1, revision = "guffawaffle-taxonomy-2026-07-29" },
                producerContract = new
                {
                    schema = "stfc.battle-bridge.producer-capabilities.v1",
                    capabilityEvidencePin = new
                    {
                        schema = "stfc.battle-bridge.capability-evidence-pin.v1",
                        sha256 = new string('a', 64),
                    },
                    runtimeCapabilities = new object[]
                    {
                        new
                        {
                            id = "ingest.stfc-sidecar.v1",
                            schema = "stfc.sidecar.ingest.v1",
                            evidenceStatus = "payload-fixture-only",
                            payloadKinds = new[] { "battle.events", "fleet.runtime", "transport.chunk" },
                        },
                        new
                        {
                            id = "battle.capture.v1",
                            schema = "stfc.battle.capture.v1",
                            evidenceStatus = "payload-fixture-only",
                            envelopeKind = "battle.events",
                        },
                        new
                        {
                            id = "fleet.runtime-snapshot.v1",
                            schema = "stfc.fleet.runtime_snapshot.v1",
                            evidenceStatus = "payload-fixture-only",
                            envelopeKind = "fleet.runtime",
                        },
                    },
                    artifact = new { fileName = "version.dll", size = payloadBytes.LongLength, sha256 = dllSha256 },
                    compatibilityEvidenceOnly = true,
                    operationalActivation = "requires-bridge-transactional-binding",
                },
            };
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifestObject);
            var manifestSha256 = Sha256(manifestBytes);
            var manifest = new ModRuntimeManifestArtifact(
                new Uri($"https://github.com/{Repository}/releases/download/{releaseTag}/stfc-runtime-manifest.json"),
                "stfc-runtime-manifest.json",
                manifestBytes.LongLength,
                manifestSha256,
                SourceRevision,
                Repository,
                releaseTag);
            var dll = new ModReleaseArtifact(
                new Uri($"https://github.com/{Repository}/releases/download/{releaseTag}/version.dll"),
                "version.dll",
                payloadBytes.LongLength,
                dllSha256,
                runtimeVersion,
                manifest);
            var certification = new ReviewedReleaseCertification(
                "guffawaffle",
                "stable",
                DistributionId,
                Repository,
                releaseTag,
                releaseTag.TrimStart('v'),
                SourceRevision,
                "version.dll",
                payloadBytes.LongLength,
                dllSha256,
                "version.dll",
                payloadBytes.LongLength,
                dllSha256,
                runtimeVersion,
                DateTimeOffset.Parse(
                    "2026-08-09T00:00:00.0000000+00:00",
                    CultureInfo.InvariantCulture),
                new("stfc-runtime-manifest.json", manifestBytes.LongLength, manifestSha256));
            return new(dll, manifest, manifestBytes, certification, payloadBytes);
        }
    }

    private sealed class RouteDownloader(IReadOnlyDictionary<Uri, ModArtifactDownload> downloads)
        : IModArtifactDownloader
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(downloads[uri]);
        }
    }

    private sealed class StaticVersionReader(string version, string? productVersion = null)
        : IModArtifactProductVersionReader
    {
        public string? ReadVersion(string artifactPath) => version;

        public string? ReadProductVersion(string artifactPath) => productVersion;
    }

    private sealed class TrustedVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(true, "trusted test DLL");
    }

    private sealed class NoOpCommitParticipant : IModDeploymentCommitParticipant
    {
        public int BeginCount { get; private set; }

        public int CommitCount { get; private set; }

        public Task BeginAsync(ModDeploymentCommitContext context, CancellationToken cancellationToken)
        {
            BeginCount++;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RollBackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
