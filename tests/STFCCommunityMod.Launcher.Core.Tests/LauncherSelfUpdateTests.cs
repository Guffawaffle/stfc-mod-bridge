using System.IO.Compression;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherSelfUpdateTests
{
    private const string TargetCommit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly ILauncherUpdateRecoveryJournalProtector JournalProtector =
        new ReversingRecoveryJournalProtector();
    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [TestMethod]
    public async Task VerifiedArchiveStagesPlanWithoutTouchingProgramDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateArchive(
            ("STFCModBridge.exe", [1, 2, 3]),
            ("STFCModBridge.ReleaseVerifier.exe", [7, 8, 9]),
            ("STFCModBridge.Updater.exe", [4, 5, 6]));
        var artifact = Artifact(archive);
        var service = CreateService(temporaryDirectory, archive);

        var result = await service.PrepareAsync(Discovery(artifact, temporaryDirectory), new string('a', 40), 123);

        Assert.AreEqual(LauncherUpdatePreparationState.Ready, result.State);
        Assert.IsTrue(File.Exists(result.PlanPath));
        Assert.IsTrue(File.Exists(result.UpdaterPath));
        StringAssert.Matches(result.PlanSha256, new System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$"));
        var serializedPlan = JsonSerializer.Deserialize<LauncherUpdatePlan>(
            await File.ReadAllBytesAsync(result.PlanPath),
            PlanJsonOptions)!;
        var retained = LauncherUpdateTransactionSecurity.LoadAndRetain(
            result.PlanPath,
            result.PlanSha256,
            serializedPlan.StateRoot,
            serializedPlan.TargetDirectory);
        Assert.AreEqual(2, retained.Plan.SchemaVersion);
        Assert.IsTrue(retained.Plan.Files.All(file => string.Equals(
            file.Sha256,
            file.Sha256.ToLowerInvariant(),
            StringComparison.Ordinal)));
        Assert.AreEqual(
            "keep-current",
            File.ReadAllText(Path.Combine(temporaryDirectory.Path, "program", "sentinel.txt")));
        StringAssert.Contains(result.Message, "Integrity:");
        StringAssert.Contains(result.Message, "Producer origin:");
        StringAssert.Contains(result.Message, "Freshness:");
        StringAssert.Contains(result.Message, "Runtime lock:");
        StringAssert.Contains(result.Message, "Action outcome:");
        StringAssert.Contains(result.Message, "not software safety");
    }

    [TestMethod]
    public async Task CurrentSourceCommitRequiresNoDownloadOrMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateArchive(("STFCModBridge.exe", [1]), ("STFCModBridge.Updater.exe", [2]));
        var downloader = new FakeDownloader(archive);
        var service = CreateService(temporaryDirectory, archive, downloader);

        var result = await service.PrepareAsync(Discovery(Artifact(archive)), TargetCommit, 123);

        Assert.AreEqual(LauncherUpdatePreparationState.UpToDate, result.State);
        Assert.AreEqual(0, downloader.CallCount);
    }

    [TestMethod]
    public async Task MissingAuthenticatedSelectionEvidenceFailsBeforeArchiveDownload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateArchive(("STFCModBridge.exe", [1]), ("STFCModBridge.Updater.exe", [2]));
        var downloader = new FakeDownloader(archive);
        var service = CreateService(temporaryDirectory, archive, downloader);
        var discovery = Discovery(Artifact(archive)) with { Authentication = null };

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => service.PrepareAsync(discovery, new string('a', 40), 123));

        Assert.AreEqual(0, downloader.CallCount);
    }

    [TestMethod]
    public async Task AuthenticatedEvidenceCannotBeReboundToAnotherArchiveSelection()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateArchive(("STFCModBridge.exe", [1]), ("STFCModBridge.Updater.exe", [2]));
        var downloader = new FakeDownloader(archive);
        var service = CreateService(temporaryDirectory, archive, downloader);
        var discovery = Discovery(Artifact(archive));
        discovery = discovery with
        {
            LauncherArtifact = discovery.LauncherArtifact with
            {
                Sha256 = new string('f', 64),
            },
        };

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => service.PrepareAsync(discovery, new string('a', 40), 123));

        Assert.AreEqual(0, downloader.CallCount);
    }

    [TestMethod]
    public async Task ArchiveTraversalFailsBeforeExecutableVerification()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateArchive(("../escape.exe", [1]), ("STFCModBridge.exe", [2]), ("STFCModBridge.Updater.exe", [3]));
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
            ("STFCModBridge.exe", [2]),
            ("STFCModBridge.Updater.exe", [3]));
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
            "Guffawaffle/stfc-mod-bridge");

        Assert.AreEqual(TargetCommit, selected.TargetCommit);
        Assert.AreEqual("stfc-mod-bridge-win-x64.zip", selected.FileName);
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
            ("STFCModBridge.exe", [1, 2, 3]),
            ("STFCModBridge.ReleaseVerifier.exe", [7, 8, 9]),
            ("STFCModBridge.Updater.exe", [4, 5, 6]));
        var service = CreateService(temporaryDirectory, archive);

        await service.PrepareAsync(Discovery(Artifact(archive), temporaryDirectory), new string('a', 40), 123);

        Assert.AreEqual("preserve-me", File.ReadAllText(journal));
    }

    [TestMethod]
    public void RecoveryInspectionFindsProtectedVerifiedBackupWithoutMutatingTarget()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        File.WriteAllText(Path.Combine(target, "current.txt"), "keep-current");
        var recovery = CreateRecoveryTransaction(state, target, "trusted previous payload");

        var preparation = InspectRecovery(state, target);

        Assert.IsNotNull(preparation);
        Assert.AreEqual(recovery.TransactionId, preparation.TransactionId);
        Assert.AreEqual("keep-current", File.ReadAllText(Path.Combine(target, "current.txt")));
        Assert.IsTrue(Directory.Exists(recovery.Backup));
    }

    [TestMethod]
    public void RecoveryRestoreUsesProtectedJournalAndRevalidatesAuthority()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        File.WriteAllText(Path.Combine(target, "new.txt"), "unacknowledged");
        var recovery = CreateRecoveryTransaction(state, target, "trusted previous payload");
        var preparation = InspectRecovery(state, target)!;

        var launcherPath = LauncherUpdateRecovery.RestoreFromJournal(
            preparation.JournalPath,
            preparation.JournalSha256,
            state,
            target,
            JournalProtector,
            new FakeAuthenticityVerifier(),
            new FakeIdentityReader());

        Assert.AreEqual(Path.Combine(target, ModBridgeProductIdentity.ExecutableName), launcherPath);
        Assert.AreEqual("trusted previous payload", File.ReadAllText(Path.Combine(target, "old.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "new.txt")));
        Assert.IsFalse(Directory.Exists(recovery.Backup));
    }

    [TestMethod]
    public void RecoveryRejectsPlanAndBackupSubstitution()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var recovery = CreateRecoveryTransaction(state, target, "original");
        var oldPath = Path.Combine(recovery.Backup, "old.txt");
        File.WriteAllText(oldPath, "substituted");
        WritePlan(
            state,
            target,
            recovery.TransactionId,
            recovery.TransactionRoot,
            Directory.EnumerateFiles(recovery.Backup, "*", SearchOption.AllDirectories)
                .Select(path => FileRecord(recovery.Backup, path))
                .ToArray(),
            schemaVersion: 2);

        Assert.ThrowsException<InvalidDataException>(() => InspectRecovery(state, target));
    }

    [TestMethod]
    public void RecoveryRejectsBackupWithoutProtectedJournal()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        Directory.CreateDirectory(Path.Combine(transactionRoot, "backup"));
        WritePlan(state, target, transactionId, transactionRoot, [], schemaVersion: 1);

        Assert.ThrowsException<InvalidDataException>(() => InspectRecovery(state, target));
        Assert.IsTrue(Directory.Exists(transactionRoot));
    }

    [TestMethod]
    public void RecoveryRejectsUntrustedBackupAuthority()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        _ = CreateRecoveryTransaction(state, target, "trusted previous payload");

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherUpdateRecovery.InspectBeforeStartup(
                state,
                target,
                JournalProtector,
                new FakeAuthenticityVerifier(isTrusted: false),
                new FakeIdentityReader()));
    }

    [TestMethod]
    public void RecoveryRejectsMultipleProtectedBackupsBeforeChoosingAnOrder()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        _ = CreateRecoveryTransaction(state, target, "old-one");
        _ = CreateRecoveryTransaction(state, target, "old-two");

        Assert.ThrowsException<InvalidDataException>(() => InspectRecovery(state, target));
        Assert.AreEqual(2, Directory.GetDirectories(Path.Combine(state, "self-update")).Length);
    }

    [TestMethod]
    public void RecoveryCleansValidatedTransactionThatNeverCreatedBackup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        WritePlan(state, target, transactionId, transactionRoot, [], schemaVersion: 2);

        Assert.IsNull(InspectRecovery(state, target));
        Assert.IsFalse(Directory.Exists(transactionRoot));
    }

    [TestMethod]
    public void RecoveryCleansAcknowledgedTransactionAfterBackupWasRemoved()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        var target = temporaryDirectory.CreateDirectory("program");
        var recovery = CreateRecoveryTransaction(state, target, "old");
        Directory.Delete(recovery.Backup, recursive: true);

        Assert.IsNull(InspectRecovery(state, target));
        Assert.IsFalse(Directory.Exists(recovery.TransactionRoot));
    }

    [TestMethod]
    public void PayloadReplacementKeepsLauncherPresentAcrossInterruptedNonLauncherWrite()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stage = temporaryDirectory.CreateDirectory("stage");
        var target = temporaryDirectory.CreateDirectory("program");
        var launcher = Path.Combine(target, ModBridgeProductIdentity.ExecutableName);
        File.WriteAllText(launcher, "old-launcher");
        WritePayload(stage, "new");
        var expected = Directory.EnumerateFiles(stage)
            .Select(path => FileRecord(stage, path))
            .ToArray();

        Assert.ThrowsException<IOException>(
            () => LauncherUpdatePayloadTransaction.InstallPreservingLauncher(
                stage,
                target,
                expected,
                ModBridgeProductIdentity.ExecutableName,
                relativePath =>
                {
                    if (relativePath != ModBridgeProductIdentity.ExecutableName)
                    {
                        throw new IOException("simulated power-loss boundary");
                    }
                }));

        Assert.IsTrue(File.Exists(launcher));
        Assert.AreEqual("old-launcher", File.ReadAllText(launcher));
    }

    [TestMethod]
    public void PayloadReplacementRejectsChangedCandidateBeforeReplacingLauncher()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stage = temporaryDirectory.CreateDirectory("stage");
        var target = temporaryDirectory.CreateDirectory("program");
        var launcher = Path.Combine(target, ModBridgeProductIdentity.ExecutableName);
        File.WriteAllText(launcher, "old-launcher");
        WritePayload(stage, "new");
        var expected = Directory.EnumerateFiles(stage)
            .Select(path => FileRecord(stage, path))
            .ToArray();
        File.WriteAllText(Path.Combine(stage, "old.txt"), "changed-after-verification");

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherUpdatePayloadTransaction.InstallPreservingLauncher(
                stage,
                target,
                expected,
                ModBridgeProductIdentity.ExecutableName));

        Assert.AreEqual("old-launcher", File.ReadAllText(launcher));
    }

    [TestMethod]
    public void WindowsRecoveryJournalProtectionRejectsTampering()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows DPAPI is required for this recovery-journal check.");
            return;
        }
        AssertWindowsRecoveryJournalProtectionRejectsTampering();
    }

    [TestMethod]
    public async Task SharedOperationLeaseRejectsConcurrentRecoveryInspection()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = temporaryDirectory.CreateDirectory("state");
        await using var first = await new LauncherOperationLock(state).TryAcquireAsync();

        await using var second = await new LauncherOperationLock(state).TryAcquireAsync();

        Assert.IsNotNull(first);
        Assert.IsNull(second);
    }

    private static LauncherSelfUpdateService CreateService(
        TemporaryDirectory temporaryDirectory,
        byte[] archive,
        FakeDownloader? downloader = null)
    {
        var program = temporaryDirectory.CreateDirectory("program");
        File.WriteAllBytes(Path.Combine(program, ModBridgeProductIdentity.ExecutableName), [10, 11, 12]);
        File.WriteAllBytes(Path.Combine(program, ModBridgeProductIdentity.ReleaseVerifierExecutableName), [13, 14, 15]);
        File.WriteAllText(Path.Combine(program, "sentinel.txt"), "keep-current");
        return new(
            temporaryDirectory.CreateDirectory("state"),
            program,
            downloader ?? new FakeDownloader(archive),
            new FakeAuthenticityVerifier(),
            new FakeIdentityReader());
    }

    private static LauncherReleaseDiscovery Discovery(
        LauncherReleaseArtifact artifact,
        TemporaryDirectory? temporaryDirectory = null)
    {
        var manifest = new WindowsReleaseManifest(
            2,
            artifact.ReleaseVersion,
            $"v{artifact.ReleaseVersion}",
            "stable",
            "active",
            new Version(0, 1, 0),
            new("Guffawaffle/stfc-mod-bridge", TargetCommit),
            AuthenticatedReleaseManifestPolicy.AuthenticityScheme,
            [
                new(
                    "windows-mod-bridge-archive-x64",
                    "windows-mod-bridge",
                    "windows",
                    "x64",
                    artifact.FileName,
                    "application/zip",
                    artifact.Size,
                    artifact.Sha256,
                    new(
                        "authenticode",
                        "contents",
                        [
                            "STFCModBridge.exe",
                            "STFCModBridge.ReleaseVerifier.exe",
                            "STFCModBridge.Updater.exe",
                        ])),
                new(
                    "windows-mod-bridge-msix-x64",
                    "windows-mod-bridge-package",
                    "windows",
                    "x64",
                    "STFCModBridge.msix",
                    "application/msix",
                    123,
                    new string('e', 64),
                    new("authenticode", "artifact", [])),
            ]);
        var issuedAt = new DateTimeOffset(
            DateTimeOffset.UtcNow.AddMinutes(-35).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
            TimeSpan.Zero);
        var observedAt = issuedAt.AddMinutes(35);
        var authenticatedManifest = new AuthenticatedWindowsReleaseManifest(
            2,
            42,
            issuedAt,
            issuedAt.AddDays(45),
            manifest.ReleaseVersion,
            manifest.Tag,
            manifest.Channel,
            manifest.ReleaseState,
            manifest.MinimumLauncherVersion,
            manifest.Source,
            AuthenticatedReleaseManifestPolicy.AuthenticityScheme,
            manifest.Artifacts,
            []);
        var evidenceDirectory = temporaryDirectory is null
            ? "unused"
            : temporaryDirectory.CreateDirectory("evidence-" + Guid.NewGuid().ToString("N"));
        var manifestPath = Path.Combine(evidenceDirectory, ReleaseSelectionAttestationPolicy.ManifestName);
        var bundlePath = Path.Combine(evidenceDirectory, ReleaseSelectionAttestationPolicy.BundleName);
        var manifestBytes = "test manifest"u8.ToArray();
        var bundleBytes = "test bundle"u8.ToArray();
        if (temporaryDirectory is not null)
        {
            File.WriteAllBytes(manifestPath, manifestBytes);
            File.WriteAllBytes(bundlePath, bundleBytes);
        }
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var bundleSha256 = Convert.ToHexString(SHA256.HashData(bundleBytes)).ToLowerInvariant();
        var receipt = new ReleaseSelectionVerificationReceipt(
            1,
            true,
            "offline",
            "Guffawaffle/stfc-mod-bridge",
            "1320037274",
            "105761663",
            ".github/workflows/release.yml",
            $"refs/tags/{manifest.Tag}",
            TargetCommit,
            "push",
            "github-hosted",
            "https://in-toto.io/Statement/v1",
            "https://slsa.dev/provenance/v1",
            "https://actions.github.io/buildtypes/workflow/v1",
            "stfc-mod-bridge-release-manifest.json",
            manifestSha256,
            bundleSha256,
            1,
            "844a1c6de3986c9f02070266b25e0d1a2fa99ceccc89f6b9ad90aae47b62a16e",
            "https://token.actions.githubusercontent.com",
            $"https://github.com/Guffawaffle/stfc-mod-bridge/.github/workflows/release.yml@refs/tags/{manifest.Tag}",
            [new("c0d23d6ad406973f9559f3ba2d1ca01f84147d8ffc5b8445c224f98b9591801d", 1, observedAt)],
            ReleaseSelectionAttestationPolicy.RequiredChecks);
        var state = new AuthenticatedReleaseChannelState(
            1,
            manifest.Channel,
            42,
            manifest.ReleaseVersion,
            receipt.ManifestSha256,
            receipt.BundleSha256,
            TargetCommit,
            manifest.Tag,
            1,
            receipt.TrustedRootSha256,
            observedAt,
            observedAt,
            receipt.VerificationMode,
            []);
        var authentication = new AuthenticatedLauncherReleaseEvidence(
            evidenceDirectory,
            manifestPath,
            bundlePath,
            "0.1.0",
            receipt,
            new(authenticatedManifest, state, observedAt));
        return new(manifest, artifact, authentication);
    }

    private static LauncherReleaseArtifact Artifact(byte[] archive) => new(
        new Uri(
            "https://github.com/Guffawaffle/stfc-mod-bridge/releases/download/v0.2.0/"
            + "stfc-mod-bridge-win-x64.zip"),
        "stfc-mod-bridge-win-x64.zip",
        archive.LongLength,
        Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),
        "0.2.0",
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
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());

    private static LauncherUpdateRecoveryPreparation? InspectRecovery(string state, string target) =>
        LauncherUpdateRecovery.InspectBeforeStartup(
            state,
            target,
            JournalProtector,
            new FakeAuthenticityVerifier(),
            new FakeIdentityReader());

    private static RecoveryFixture CreateRecoveryTransaction(string state, string target, string contents)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Directory.CreateDirectory(Path.Combine(state, "self-update", transactionId)).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(transactionRoot, "backup")).FullName;
        WritePayload(backup, contents);
        var runnerPath = Path.Combine(transactionRoot, ModBridgeProductIdentity.UpdaterExecutableName);
        File.WriteAllText(runnerPath, "recovery-runner");
        var previousFiles = Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories)
            .Select(path => FileRecord(backup, path))
            .ToArray();
        var launcherPath = Path.Combine(backup, ModBridgeProductIdentity.ExecutableName);
        var verifierPath = Path.Combine(backup, ModBridgeProductIdentity.ReleaseVerifierExecutableName);
        var plan = new LauncherUpdatePlan(
            2,
            transactionId,
            123,
            state,
            Path.Combine(transactionRoot, "stage"),
            target,
            backup,
            Path.Combine(transactionRoot, "startup.ack"),
            ModBridgeProductIdentity.ExecutableName,
            ModBridgeProductIdentity.UpdaterExecutableName,
            ModBridgeProductIdentity.ReleaseVerifierExecutableName,
            "v0.2.0",
            "0.1.0",
            null!,
            null!,
            null!,
            null!,
            null!,
            BoundFile(launcherPath),
            BoundFile(verifierPath),
            null!,
            null!,
            null!,
            BoundFile(runnerPath),
            [],
            previousFiles);
        File.WriteAllText(
            Path.Combine(transactionRoot, "plan.json"),
            JsonSerializer.Serialize(plan, PlanJsonOptions));
        _ = LauncherUpdateRecoveryJournalStore.Create(plan, JournalProtector);
        return new(transactionId, transactionRoot, backup);
    }

    private static LauncherUpdateBoundFile BoundFile(string path) => new(
        path,
        new FileInfo(path).Length,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsRecoveryJournalProtectionRejectsTampering()
    {
        var protector = new WindowsDpapiLauncherUpdateRecoveryJournalProtector();
        var plaintext = "independent recovery authority"u8.ToArray();
        var protectedBytes = protector.Protect(plaintext);

        CollectionAssert.AreEqual(plaintext, protector.Unprotect(protectedBytes));
        protectedBytes[^1] ^= 0x5a;
        Assert.ThrowsException<CryptographicException>(() => protector.Unprotect(protectedBytes));
    }

    private static void WritePayload(string root, string marker)
    {
        File.WriteAllText(Path.Combine(root, ModBridgeProductIdentity.ExecutableName), $"launcher-{marker}");
        File.WriteAllText(
            Path.Combine(root, ModBridgeProductIdentity.ReleaseVerifierExecutableName),
            $"verifier-{marker}");
        File.WriteAllText(Path.Combine(root, "old.txt"), marker);
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
        IReadOnlyList<LauncherUpdateFile> previousFiles,
        int schemaVersion = 1)
    {
        var plan = new LauncherUpdatePlan(
            schemaVersion,
            transactionId,
            123,
            state,
            Path.Combine(transactionRoot, "stage"),
            target,
            Path.Combine(transactionRoot, "backup"),
            Path.Combine(transactionRoot, "startup.ack"),
            "STFCModBridge.exe",
            schemaVersion == 2 ? ModBridgeProductIdentity.UpdaterExecutableName : string.Empty,
            schemaVersion == 2 ? ModBridgeProductIdentity.ReleaseVerifierExecutableName : string.Empty,
            string.Empty,
            string.Empty,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
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

    private sealed record RecoveryFixture(string TransactionId, string TransactionRoot, string Backup);

    private sealed class ReversingRecoveryJournalProtector : ILauncherUpdateRecoveryJournalProtector
    {
        public byte[] Protect(byte[] contents) => [.. contents.Reverse()];

        public byte[] Unprotect(byte[] protectedContents) => [.. protectedContents.Reverse()];
    }

    private sealed class FakeAuthenticityVerifier(bool isTrusted = true) : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(isTrusted, isTrusted ? "trusted" : "rejected");
    }

    private sealed class FakeIdentityReader : ILauncherArtifactIdentityReader
    {
        public LauncherReleaseIdentity ReadIdentity(string executablePath)
        {
            var verifierPath = Path.Combine(
                Path.GetDirectoryName(executablePath)!,
                ModBridgeProductIdentity.ReleaseVerifierExecutableName);
            return new(
                TargetCommit,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(verifierPath))).ToLowerInvariant());
        }
    }
}
