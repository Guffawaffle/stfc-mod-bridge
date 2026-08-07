using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherUpdateTransactionSecurityTests
{
    private const string TargetCommit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [TestMethod]
    public async Task ValidRetainedPlanReverifiesInstalledAuthorityImmediatelyBeforeSwap()
    {
        using var fixture = new UpdateFixture();
        var runtime = fixture.Load();
        var verifier = new FakeReleaseVerifier(fixture.Receipt);

        await LauncherUpdateTransactionSecurity.VerifyImmediatelyBeforeSwapAsync(
            runtime,
            verifier,
            new TrustedAuthenticityVerifier(),
            new PairedIdentityReader(),
            () => fixture.ObservedAt);

        Assert.AreEqual(1, verifier.CallCount);
        Assert.AreEqual("healthy", File.ReadAllText(fixture.HealthySentinel));
    }

    [TestMethod]
    public async Task EveryMutablePlanOrStagedRoleFailsClosedAfterExpectationsAreRetained()
    {
        var roles = new[]
        {
            "plan", "manifest", "bundle", "receipt", "trusted-root", "archive",
            "current-launcher", "current-verifier", "candidate-launcher", "candidate-updater",
            "candidate-verifier", "runner-updater",
        };
        foreach (var role in roles)
        {
            using var fixture = new UpdateFixture();
            var runtime = fixture.Load();
            await using (var stream = new FileStream(fixture.Paths[role], FileMode.Append, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x7f);
            }
            var verifier = new FakeReleaseVerifier(fixture.Receipt);

            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                LauncherUpdateTransactionSecurity.VerifyImmediatelyBeforeSwapAsync(
                    runtime,
                    verifier,
                    new TrustedAuthenticityVerifier(),
                    new PairedIdentityReader(),
                    () => fixture.ObservedAt));

            Assert.AreEqual(0, verifier.CallCount, $"{role} mutation reached the authority helper.");
            Assert.AreEqual("healthy", File.ReadAllText(fixture.HealthySentinel), role);
        }
    }

    [TestMethod]
    public async Task CandidateLauncherCannotPairItselfWithDifferentHelperBytes()
    {
        using var fixture = new UpdateFixture();
        var runtime = fixture.Load();
        var verifier = new FakeReleaseVerifier(fixture.Receipt);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            LauncherUpdateTransactionSecurity.VerifyImmediatelyBeforeSwapAsync(
                runtime,
                verifier,
                new TrustedAuthenticityVerifier(),
                new PairedIdentityReader(mismatchCandidate: true),
                () => fixture.ObservedAt));

        Assert.AreEqual(0, verifier.CallCount);
        Assert.AreEqual("healthy", File.ReadAllText(fixture.HealthySentinel));
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly TemporaryDirectory temporary = new();
        private readonly string stateRoot;
        private readonly string targetRoot;
        private readonly string transactionRoot;
        private readonly string stageRoot;
        private readonly string evidenceRoot;

        internal UpdateFixture()
        {
            stateRoot = temporary.CreateDirectory("state");
            targetRoot = temporary.CreateDirectory("program");
            var transactionId = Guid.NewGuid().ToString("N");
            transactionRoot = Directory.CreateDirectory(
                Path.Combine(stateRoot, "self-update", transactionId)).FullName;
            stageRoot = Directory.CreateDirectory(Path.Combine(transactionRoot, "stage")).FullName;
            evidenceRoot = Directory.CreateDirectory(Path.Combine(transactionRoot, "evidence")).FullName;

            var currentLauncher = Write(targetRoot, ModBridgeProductIdentity.ExecutableName, "current launcher");
            var currentVerifier = Write(
                targetRoot,
                ModBridgeProductIdentity.ReleaseVerifierExecutableName,
                "current verifier");
            HealthySentinel = Write(targetRoot, "healthy.txt", "healthy");
            var candidateLauncher = Write(stageRoot, ModBridgeProductIdentity.ExecutableName, "candidate launcher");
            var candidateUpdater = Write(stageRoot, ModBridgeProductIdentity.UpdaterExecutableName, "candidate updater");
            var candidateVerifier = Write(
                stageRoot,
                ModBridgeProductIdentity.ReleaseVerifierExecutableName,
                "candidate verifier");
            var runnerUpdater = Write(
                transactionRoot,
                ModBridgeProductIdentity.UpdaterExecutableName,
                "candidate updater");
            var archive = Write(transactionRoot, ModBridgeProductIdentity.UpdateArchiveName, "archive bytes");
            var trustedRoot = Path.Combine(evidenceRoot, "trusted-root.public-good.v1.json");
            File.WriteAllBytes(trustedRoot, ReleaseSelectionTrustedRoot.GetNormalizedBytes());

            var issuedAt = WholeSecond(DateTimeOffset.UtcNow.AddMinutes(-35));
            ObservedAt = issuedAt.AddMinutes(35);
            var bundle = Write(evidenceRoot, ReleaseSelectionAttestationPolicy.BundleName, "{}");
            var manifest = Path.Combine(evidenceRoot, ReleaseSelectionAttestationPolicy.ManifestName);
            var archiveFile = Bound(archive);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                releaseSequence = 42,
                issuedAt = FormatUtc(issuedAt),
                expiresAt = FormatUtc(issuedAt.AddDays(45)),
                releaseVersion = "0.2.0",
                tag = "v0.2.0",
                channel = "stable",
                releaseState = "active",
                minimumLauncherVersion = "0.1.0",
                source = new { repository = ReleaseSelectionAttestationPolicy.Repository, targetCommit = TargetCommit },
                manifestAuthenticity = new { scheme = AuthenticatedReleaseManifestPolicy.AuthenticityScheme },
                artifacts = new object[]
                {
                    new
                    {
                        id = "windows-mod-bridge-archive-x64",
                        kind = "windows-mod-bridge",
                        platform = "windows",
                        architecture = "x64",
                        fileName = ModBridgeProductIdentity.UpdateArchiveName,
                        mediaType = "application/zip",
                        size = archiveFile.Size,
                        sha256 = archiveFile.Sha256,
                        authenticity = new
                        {
                            scheme = "authenticode",
                            scope = "contents",
                            signedFiles = new[]
                            {
                                ModBridgeProductIdentity.ExecutableName,
                                ModBridgeProductIdentity.ReleaseVerifierExecutableName,
                                ModBridgeProductIdentity.UpdaterExecutableName,
                            },
                        },
                    },
                    new
                    {
                        id = "windows-mod-bridge-msix-x64",
                        kind = "windows-mod-bridge-package",
                        platform = "windows",
                        architecture = "x64",
                        fileName = ModBridgeProductIdentity.PackageName,
                        mediaType = "application/msix",
                        size = 1,
                        sha256 = new string('e', 64),
                        authenticity = new { scheme = "authenticode", scope = "artifact" },
                    },
                },
                withdrawals = Array.Empty<object>(),
            });
            File.WriteAllBytes(manifest, manifestBytes);
            var manifestFile = Bound(manifest);
            var bundleFile = Bound(bundle);
            Receipt = new(
                1,
                true,
                ReleaseSelectionAttestationPolicy.VerificationMode,
                ReleaseSelectionAttestationPolicy.Repository,
                ReleaseSelectionAttestationPolicy.RepositoryId,
                ReleaseSelectionAttestationPolicy.OwnerId,
                ReleaseSelectionAttestationPolicy.Workflow,
                "refs/tags/v0.2.0",
                TargetCommit,
                ReleaseSelectionAttestationPolicy.Event,
                ReleaseSelectionAttestationPolicy.Runner,
                ReleaseSelectionAttestationPolicy.StatementType,
                ReleaseSelectionAttestationPolicy.PredicateType,
                ReleaseSelectionAttestationPolicy.BuildType,
                ReleaseSelectionAttestationPolicy.ManifestName,
                manifestFile.Sha256,
                bundleFile.Sha256,
                ReleaseSelectionAttestationPolicy.TrustEpoch,
                ReleaseSelectionAttestationPolicy.TrustedRootSha256,
                ReleaseSelectionAttestationPolicy.FulcioIssuer,
                $"https://github.com/{ReleaseSelectionAttestationPolicy.Repository}/"
                    + $"{ReleaseSelectionAttestationPolicy.Workflow}@refs/tags/v0.2.0",
                [new(ReleaseSelectionAttestationPolicy.AcceptedRekorLogIds.First(), 7, issuedAt.AddMinutes(1))],
                ReleaseSelectionAttestationPolicy.RequiredChecks);
            var receiptPath = Path.Combine(evidenceRoot, "release-selection-receipt.json");
            File.WriteAllBytes(receiptPath, ReleaseSelectionVerificationReceiptSerializer.Serialize(Receipt, true));

            var state = new AuthenticatedReleaseChannelState(
                1,
                "stable",
                42,
                "0.2.0",
                manifestFile.Sha256,
                bundleFile.Sha256,
                TargetCommit,
                "v0.2.0",
                1,
                ReleaseSelectionAttestationPolicy.TrustedRootSha256,
                ObservedAt,
                ObservedAt,
                ReleaseSelectionAttestationPolicy.VerificationMode,
                []);
            new AuthenticatedReleaseStateStore(stateRoot).Advance(state);

            var plan = new LauncherUpdatePlan(
                2,
                transactionId,
                123,
                stateRoot,
                stageRoot,
                targetRoot,
                Path.Combine(transactionRoot, "backup"),
                Path.Combine(transactionRoot, "startup.ack"),
                ModBridgeProductIdentity.ExecutableName,
                ModBridgeProductIdentity.UpdaterExecutableName,
                ModBridgeProductIdentity.ReleaseVerifierExecutableName,
                "v0.2.0",
                "0.1.0",
                manifestFile,
                bundleFile,
                Bound(receiptPath),
                Bound(trustedRoot),
                archiveFile,
                Bound(currentLauncher),
                Bound(currentVerifier),
                Bound(candidateLauncher),
                Bound(candidateUpdater),
                Bound(candidateVerifier),
                Bound(runnerUpdater),
                Enumerate(stageRoot),
                Enumerate(targetRoot));
            PlanPath = Path.Combine(transactionRoot, "plan.json");
            File.WriteAllBytes(
                PlanPath,
                JsonSerializer.SerializeToUtf8Bytes(plan, PlanJsonOptions));
            PlanSha256 = Hash(PlanPath);
            Paths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["plan"] = PlanPath,
                ["manifest"] = manifest,
                ["bundle"] = bundle,
                ["receipt"] = receiptPath,
                ["trusted-root"] = trustedRoot,
                ["archive"] = archive,
                ["current-launcher"] = currentLauncher,
                ["current-verifier"] = currentVerifier,
                ["candidate-launcher"] = candidateLauncher,
                ["candidate-updater"] = candidateUpdater,
                ["candidate-verifier"] = candidateVerifier,
                ["runner-updater"] = runnerUpdater,
            };
        }

        internal DateTimeOffset ObservedAt { get; }
        internal string HealthySentinel { get; }
        internal string PlanPath { get; }
        internal string PlanSha256 { get; }
        internal ReleaseSelectionVerificationReceipt Receipt { get; }
        internal IReadOnlyDictionary<string, string> Paths { get; }

        internal LauncherUpdateRuntimePlan Load() => LauncherUpdateTransactionSecurity.LoadAndRetain(
            PlanPath,
            PlanSha256,
            stateRoot,
            targetRoot);

        public void Dispose() => temporary.Dispose();

        private static string Write(string root, string name, string contents)
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, contents);
            return path;
        }

        private static LauncherUpdateBoundFile Bound(string path)
        {
            var info = new FileInfo(path);
            return new(info.FullName, info.Length, Hash(path));
        }

        private static LauncherUpdateFile[] Enumerate(string root) => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new LauncherUpdateFile(
                Path.GetRelativePath(root, path),
                new FileInfo(path).Length,
                Hash(path)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        private static string Hash(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private static DateTimeOffset WholeSecond(DateTimeOffset value) => new(
            value.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
            TimeSpan.Zero);

        private static string FormatUtc(DateTimeOffset value) => value.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FakeReleaseVerifier(ReleaseSelectionVerificationReceipt receipt)
        : IReleaseSelectionEvidenceVerifier
    {
        internal int CallCount { get; private set; }

        public Task<ReleaseSelectionVerificationReceipt> VerifyAsync(
            ReleaseSelectionVerificationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(receipt);
        }
    }

    private sealed class TrustedAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(true, "trusted");
    }

    private sealed class PairedIdentityReader(bool mismatchCandidate = false) : ILauncherArtifactIdentityReader
    {
        public LauncherReleaseIdentity ReadIdentity(string executablePath)
        {
            var verifierPath = Path.Combine(
                Path.GetDirectoryName(executablePath)!,
                ModBridgeProductIdentity.ReleaseVerifierExecutableName);
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(verifierPath))).ToLowerInvariant();
            if (mismatchCandidate && executablePath.Contains("stage", StringComparison.OrdinalIgnoreCase))
            {
                digest = new string('f', 64);
            }
            return new(TargetCommit, digest);
        }
    }
}
