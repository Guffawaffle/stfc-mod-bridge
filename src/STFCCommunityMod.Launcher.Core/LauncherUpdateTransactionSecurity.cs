using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherUpdateRuntimePlan(
    string PlanPath,
    string PlanSha256,
    LauncherUpdatePlan Plan,
    ReleaseSelectionVerificationReceipt ExpectedReceipt);

public static class LauncherUpdateTransactionSecurity
{
    private const int MaximumPlanBytes = 256 * 1024;
    private const int MaximumPayloadFiles = 128;
    private const long MaximumArchiveBytes = 768L * 1024L * 1024L;
    private const long MaximumPortableExecutableBytes = 256L * 1024L * 1024L;
    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static LauncherUpdateRuntimePlan LoadAndRetain(
        string planPath,
        string expectedPlanSha256,
        string expectedStateRoot,
        string expectedTargetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPlanSha256);
        var fullPlanPath = Path.GetFullPath(planPath);
        var planBytes = ReadBoundFile(
            new(fullPlanPath, new FileInfo(fullPlanPath).Length, expectedPlanSha256),
            MaximumPlanBytes,
            "update plan");
        RejectDuplicateProperties(planBytes, "update plan");
        LauncherUpdatePlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<LauncherUpdatePlan>(planBytes, PlanJsonOptions)
                ?? throw new InvalidDataException("The update plan is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The update plan is outside schema v2.", exception);
        }
        ValidatePlan(plan, fullPlanPath, expectedStateRoot, expectedTargetRoot);
        var manifestBytes = ReadBoundFile(
            plan.Manifest,
            ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes,
            "release manifest");
        _ = ReadBoundFile(
            plan.Bundle,
            ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes,
            "release-selection bundle");
        var receiptBytes = ReadBoundFile(
            plan.Receipt,
            ReleaseSelectionAttestationPolicy.MaximumReceiptBytes,
            "release-selection receipt");
        _ = ReadBoundFile(
            plan.TrustedRoot,
            ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes,
            "release-selection trust root");
        var request = ReleaseSelectionAttestationPolicy.CreateRequest(
            plan.Manifest.Path,
            plan.Bundle.Path,
            plan.ExpectedTag);
        var receipt = ReleaseSelectionVerificationReceiptParser.Parse(
            receiptBytes,
            request,
            plan.Manifest.Sha256,
            plan.Bundle.Sha256);
        if (!AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                receipt.TrustedRootSha256,
                plan.TrustedRoot.Sha256)
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                receipt.ManifestSha256,
                Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant()))
        {
            throw new InvalidDataException("The retained release-selection receipt is not bound to the staged authority inputs.");
        }
        return new(fullPlanPath, expectedPlanSha256, plan, receipt);
    }

    internal static LauncherUpdatePlan ParseForRecovery(string planPath)
    {
        var fullPath = Path.GetFullPath(planPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists
            || info.Length is <= 0 or > MaximumPlanBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("An abandoned Mod Bridge update plan is missing or outside its size bound.");
        }
        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.LongLength != info.Length)
        {
            throw new InvalidDataException("An abandoned Mod Bridge update plan changed while it was read.");
        }
        RejectDuplicateProperties(bytes, "abandoned update plan");
        try
        {
            return JsonSerializer.Deserialize<LauncherUpdatePlan>(bytes, PlanJsonOptions)
                ?? throw new InvalidDataException("An abandoned Mod Bridge update plan is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("An abandoned Mod Bridge update plan is outside its closed schema.", exception);
        }
    }

    public static Task VerifyImmediatelyBeforeSwapAsync(
        LauncherUpdateRuntimePlan runtimePlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimePlan);
        var authenticityVerifier = new WindowsAuthenticodeVerifier(
            LauncherSelfUpdateAuthority.WindowsArtifactPublisher,
            LauncherSelfUpdateAuthority.WindowsArtifactSigningIdentityEku);
        IReleaseSelectionEvidenceVerifier releaseVerifier = new InstalledReleaseSelectionEvidenceVerifier(
            runtimePlan.Plan.CurrentReleaseVerifier.Path,
            runtimePlan.Plan.CurrentReleaseVerifier.Sha256,
            authenticityVerifier,
            TimeSpan.FromMinutes(2));
        return VerifyImmediatelyBeforeSwapAsync(
            runtimePlan,
            releaseVerifier,
            authenticityVerifier,
            new WindowsLauncherArtifactIdentityReader(),
            () => DateTimeOffset.UtcNow,
            cancellationToken);
    }

    internal static async Task VerifyImmediatelyBeforeSwapAsync(
        LauncherUpdateRuntimePlan runtimePlan,
        IReleaseSelectionEvidenceVerifier releaseVerifier,
        IModArtifactAuthenticityVerifier authenticityVerifier,
        ILauncherArtifactIdentityReader identityReader,
        Func<DateTimeOffset> utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimePlan);
        ArgumentNullException.ThrowIfNull(releaseVerifier);
        ArgumentNullException.ThrowIfNull(authenticityVerifier);
        ArgumentNullException.ThrowIfNull(identityReader);
        ArgumentNullException.ThrowIfNull(utcNow);
        var plan = runtimePlan.Plan;
        var transactionRoot = Path.GetDirectoryName(runtimePlan.PlanPath)!;
        LauncherFilesystemSafety.RejectReparsePoints(transactionRoot, "Mod Bridge update commit");
        LauncherFilesystemSafety.RejectReparsePoints(plan.TargetDirectory, "Mod Bridge update commit");

        _ = ReadBoundFile(
            new(runtimePlan.PlanPath, new FileInfo(runtimePlan.PlanPath).Length, runtimePlan.PlanSha256),
            MaximumPlanBytes,
            "update plan");
        var manifestBytes = ReadBoundFile(
            plan.Manifest,
            ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes,
            "release manifest");
        _ = ReadBoundFile(
            plan.Bundle,
            ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes,
            "release-selection bundle");
        _ = ReadBoundFile(
            plan.Receipt,
            ReleaseSelectionAttestationPolicy.MaximumReceiptBytes,
            "release-selection receipt");
        _ = ReadBoundFile(
            plan.TrustedRoot,
            ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes,
            "release-selection trust root");
        VerifyBoundFile(plan.Archive, MaximumArchiveBytes, "release archive");
        VerifyBoundPortableExecutable(plan.CurrentLauncher, authenticityVerifier, "installed launcher");
        VerifyBoundPortableExecutable(plan.CurrentReleaseVerifier, authenticityVerifier, "installed release verifier");
        VerifyBoundPortableExecutable(plan.CandidateLauncher, authenticityVerifier, "candidate launcher");
        VerifyBoundPortableExecutable(plan.CandidateUpdater, authenticityVerifier, "candidate updater");
        VerifyBoundPortableExecutable(plan.CandidateReleaseVerifier, authenticityVerifier, "candidate release verifier");
        VerifyBoundPortableExecutable(plan.RunnerUpdater, authenticityVerifier, "running updater");
        VerifyPayload(plan.TargetDirectory, plan.PreviousFiles);
        VerifyPayload(plan.StageDirectory, plan.Files);

        var currentIdentity = identityReader.ReadIdentity(plan.CurrentLauncher.Path);
        var candidateIdentity = identityReader.ReadIdentity(plan.CandidateLauncher.Path);
        if (!currentIdentity.HasReleaseVerifierPairing
            || !candidateIdentity.HasReleaseVerifierPairing
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                currentIdentity.ReleaseVerifierSha256!,
                plan.CurrentReleaseVerifier.Sha256)
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                candidateIdentity.ReleaseVerifierSha256!,
                plan.CandidateReleaseVerifier.Sha256)
            || candidateIdentity.SourceCommit != runtimePlan.ExpectedReceipt.SourceCommit)
        {
            throw new InvalidDataException("The current or candidate launcher/helper pairing changed before commit.");
        }

        var request = ReleaseSelectionAttestationPolicy.CreateRequest(
            plan.Manifest.Path,
            plan.Bundle.Path,
            plan.ExpectedTag);
        var receipt = await releaseVerifier.VerifyAsync(request, cancellationToken);
        if (!ReceiptsEqual(receipt, runtimePlan.ExpectedReceipt))
        {
            throw new InvalidDataException("The release verifier returned different authority evidence before commit.");
        }
        var manifest = AuthenticatedReleaseManifestParser.Parse(manifestBytes);
        var previousState = new AuthenticatedReleaseStateStore(plan.StateRoot).Load(manifest.Channel)
            ?? throw new InvalidDataException("Authenticated release state disappeared before commit.");
        _ = AuthenticatedReleaseManifestPolicy.Evaluate(
            manifest,
            receipt,
            plan.InstalledReleaseVersion,
            utcNow(),
            previousState);
        var archive = manifest.Artifacts.SingleOrDefault(candidate =>
            candidate.Id == "windows-mod-bridge-archive-x64");
        if (manifest.Tag != plan.ExpectedTag
            || archive is null
            || archive.Size != plan.Archive.Size
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(archive.Sha256, plan.Archive.Sha256))
        {
            throw new InvalidDataException("The authenticated manifest no longer selects the staged archive.");
        }
    }

    private static void ValidatePlan(
        LauncherUpdatePlan plan,
        string planPath,
        string expectedStateRoot,
        string expectedTargetRoot)
    {
        var stateRoot = Path.GetFullPath(expectedStateRoot);
        var targetRoot = Path.GetFullPath(expectedTargetRoot);
        var transactionRoot = Path.Combine(stateRoot, "self-update", plan.TransactionId);
        var stageRoot = Path.Combine(transactionRoot, "stage");
        var evidenceRoot = Path.Combine(transactionRoot, "evidence");
        if (plan.SchemaVersion != 2
            || !Guid.TryParseExact(plan.TransactionId, "N", out _)
            || plan.ParentProcessId <= 0
            || !PathEquals(plan.StateRoot, stateRoot)
            || !PathEquals(plan.TargetDirectory, targetRoot)
            || !PathEquals(Path.GetDirectoryName(planPath)!, transactionRoot)
            || !PathEquals(plan.StageDirectory, stageRoot)
            || !PathEquals(plan.BackupDirectory, Path.Combine(transactionRoot, "backup"))
            || !PathEquals(plan.AcknowledgementPath, Path.Combine(transactionRoot, "startup.ack"))
            || plan.LauncherRelativePath != ModBridgeProductIdentity.ExecutableName
            || plan.UpdaterRelativePath != ModBridgeProductIdentity.UpdaterExecutableName
            || plan.ReleaseVerifierRelativePath != ModBridgeProductIdentity.ReleaseVerifierExecutableName
            || string.IsNullOrWhiteSpace(plan.ExpectedTag)
            || string.IsNullOrWhiteSpace(plan.InstalledReleaseVersion)
            || plan.Files is null
            || plan.PreviousFiles is null)
        {
            throw new InvalidDataException("The update plan identity or fixed paths are invalid.");
        }
        ValidateBoundPath(plan.Manifest, Path.Combine(evidenceRoot, ReleaseSelectionAttestationPolicy.ManifestName));
        ValidateBoundPath(plan.Bundle, Path.Combine(evidenceRoot, ReleaseSelectionAttestationPolicy.BundleName));
        ValidateBoundPath(plan.Receipt, Path.Combine(evidenceRoot, "release-selection-receipt.json"));
        ValidateBoundPath(plan.TrustedRoot, Path.Combine(evidenceRoot, "trusted-root.public-good.v1.json"));
        ValidateBoundPath(plan.Archive, Path.Combine(transactionRoot, ModBridgeProductIdentity.UpdateArchiveName));
        ValidateBoundPath(plan.CurrentLauncher, Path.Combine(targetRoot, ModBridgeProductIdentity.ExecutableName));
        ValidateBoundPath(
            plan.CurrentReleaseVerifier,
            Path.Combine(targetRoot, ModBridgeProductIdentity.ReleaseVerifierExecutableName));
        ValidateBoundPath(plan.CandidateLauncher, Path.Combine(stageRoot, ModBridgeProductIdentity.ExecutableName));
        ValidateBoundPath(plan.CandidateUpdater, Path.Combine(stageRoot, ModBridgeProductIdentity.UpdaterExecutableName));
        ValidateBoundPath(
            plan.CandidateReleaseVerifier,
            Path.Combine(stageRoot, ModBridgeProductIdentity.ReleaseVerifierExecutableName));
        ValidateBoundPath(plan.RunnerUpdater, Path.Combine(transactionRoot, ModBridgeProductIdentity.UpdaterExecutableName));
        ValidatePayloadRecords(plan.Files, requireFiles: true);
        ValidatePayloadRecords(plan.PreviousFiles, requireFiles: true);
        if (plan.Files.Count(file => file.RelativePath == ModBridgeProductIdentity.ExecutableName) != 1
            || plan.Files.Count(file => file.RelativePath == ModBridgeProductIdentity.UpdaterExecutableName) != 1
            || plan.Files.Count(file => file.RelativePath == ModBridgeProductIdentity.ReleaseVerifierExecutableName) != 1)
        {
            throw new InvalidDataException("The staged update payload does not contain the three reviewed executable roles.");
        }
    }

    internal static void ValidatePayloadRecords(IReadOnlyList<LauncherUpdateFile> files, bool requireFiles)
    {
        if (files.Count > MaximumPayloadFiles || (requireFiles && files.Count == 0))
        {
            throw new InvalidDataException("The update payload file inventory is outside its bound.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var normalized = file.RelativePath.Replace('\\', '/');
            var components = normalized.Split('/');
            if (string.IsNullOrWhiteSpace(file.RelativePath)
                || Path.IsPathFullyQualified(file.RelativePath)
                || file.RelativePath.Contains(':')
                || components.Any(component => component is "" or "." or "..")
                || file.Size <= 0
                || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(file.Sha256)
                || !names.Add(normalized))
            {
                throw new InvalidDataException("The update payload file inventory is invalid.");
            }
        }
    }

    private static void ValidateBoundPath(LauncherUpdateBoundFile? file, string expectedPath)
    {
        if (file is null
            || !PathEquals(file.Path, expectedPath)
            || file.Size <= 0
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(file.Sha256))
        {
            throw new InvalidDataException("An update plan bound-file identity is invalid.");
        }
    }

    private static byte[] ReadBoundFile(LauncherUpdateBoundFile file, long maximumBytes, string context)
    {
        if (!ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(file.Sha256))
        {
            throw new InvalidDataException($"The {context} digest is invalid.");
        }
        using var stream = new FileStream(
            file.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != file.Size
            || stream.Length is <= 0
            || stream.Length > maximumBytes
            || (File.GetAttributes(file.Path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {context} size or file identity changed.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Length != file.Size
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes),
                Convert.FromHexString(file.Sha256)))
        {
            throw new InvalidDataException($"The {context} changed after it was staged.");
        }
        return bytes;
    }

    private static void VerifyBoundFile(LauncherUpdateBoundFile file, long maximumBytes, string context)
    {
        if (!ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(file.Sha256))
        {
            throw new InvalidDataException($"The {context} digest is invalid.");
        }
        using var stream = new FileStream(
            file.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != file.Size
            || stream.Length is <= 0
            || stream.Length > maximumBytes
            || (File.GetAttributes(file.Path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {context} size or file identity changed.");
        }
        var digest = SHA256.HashData(stream);
        if (stream.Length != file.Size
            || !CryptographicOperations.FixedTimeEquals(
                digest,
                Convert.FromHexString(file.Sha256)))
        {
            throw new InvalidDataException($"The {context} changed after it was staged.");
        }
    }

    private static void VerifyBoundPortableExecutable(
        LauncherUpdateBoundFile file,
        IModArtifactAuthenticityVerifier verifier,
        string context)
    {
        if (!ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(file.Sha256))
        {
            throw new InvalidDataException($"The {context} digest is invalid.");
        }
        using var fileLock = new FileStream(
            file.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (fileLock.Length != file.Size
            || fileLock.Length is <= 0 or > MaximumPortableExecutableBytes
            || (File.GetAttributes(file.Path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {context} size or file identity changed.");
        }
        var digest = SHA256.HashData(fileLock);
        if (fileLock.Length != file.Size
            || !CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(file.Sha256)))
        {
            throw new InvalidDataException($"The {context} changed after it was staged.");
        }
        var result = verifier.Verify(file.Path);
        if (!result.IsTrusted)
        {
            throw new InvalidDataException($"The {context} Authenticode policy failed: {result.Message}");
        }
    }

    private static void VerifyPayload(string root, IReadOnlyList<LauncherUpdateFile> expected)
    {
        LauncherFilesystemSafety.RejectReparsePoints(root, "Mod Bridge staged update payload");
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new LauncherUpdateFile(
                Path.GetRelativePath(root, path),
                new FileInfo(path).Length,
                HashFile(path)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (actual.Length != expected.Count)
        {
            throw new InvalidDataException("The staged update payload file count changed.");
        }
        foreach (var expectedFile in expected)
        {
            var actualFile = actual.SingleOrDefault(file =>
                string.Equals(file.RelativePath, expectedFile.RelativePath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The staged update payload file identity changed.");
            if (actualFile.Size != expectedFile.Size
                || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(actualFile.Sha256, expectedFile.Sha256))
            {
                throw new InvalidDataException("The staged update payload failed its commit-boundary hash check.");
            }
        }
    }

    private static bool ReceiptsEqual(
        ReleaseSelectionVerificationReceipt left,
        ReleaseSelectionVerificationReceipt right)
    {
        var leftBytes = ReleaseSelectionVerificationReceiptSerializer.Serialize(left);
        var rightBytes = ReleaseSelectionVerificationReceiptSerializer.Serialize(right);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(leftBytes), SHA256.HashData(rightBytes));
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void RejectDuplicateProperties(byte[] bytes, string context)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            RejectDuplicateProperties(document.RootElement, context);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {context} is not bounded canonical JSON.", exception);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string context)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"The {context} contains duplicate property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value, context);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, context);
            }
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
