using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherReleaseArtifact(
    Uri DownloadUri,
    string FileName,
    long Size,
    string Sha256,
    string ReleaseVersion,
    string TargetCommit);

public sealed record LauncherUpdateFile(string RelativePath, long Size, string Sha256);

public sealed record LauncherUpdateBoundFile(string Path, long Size, string Sha256);

public sealed record LauncherUpdatePlan(
    int SchemaVersion,
    string TransactionId,
    int ParentProcessId,
    string StateRoot,
    string StageDirectory,
    string TargetDirectory,
    string BackupDirectory,
    string AcknowledgementPath,
    string LauncherRelativePath,
    string UpdaterRelativePath,
    string ReleaseVerifierRelativePath,
    string ExpectedTag,
    string InstalledReleaseVersion,
    LauncherUpdateBoundFile Manifest,
    LauncherUpdateBoundFile Bundle,
    LauncherUpdateBoundFile Receipt,
    LauncherUpdateBoundFile TrustedRoot,
    LauncherUpdateBoundFile Archive,
    LauncherUpdateBoundFile CurrentLauncher,
    LauncherUpdateBoundFile CurrentReleaseVerifier,
    LauncherUpdateBoundFile CandidateLauncher,
    LauncherUpdateBoundFile CandidateUpdater,
    LauncherUpdateBoundFile CandidateReleaseVerifier,
    LauncherUpdateBoundFile RunnerUpdater,
    IReadOnlyList<LauncherUpdateFile> Files,
    IReadOnlyList<LauncherUpdateFile> PreviousFiles);

public enum LauncherUpdatePreparationState
{
    Ready,
    UpToDate,
}

public sealed record LauncherUpdatePreparation(
    LauncherUpdatePreparationState State,
    string Message,
    string ReleaseVersion,
    string TargetDirectory,
    string PlanPath,
    string UpdaterPath,
    string PlanSha256);

public sealed record LauncherUpdateRecoveryResult(int ExaminedTransactions, int RestoredBackups);

public static class LauncherUpdateRecovery
{
    private sealed record RecoveryTransaction(
        string TransactionRoot,
        LauncherUpdatePlan Plan,
        bool HasBackup);

    public static LauncherUpdateRecoveryResult RecoverBeforeSetup(
        string stateDirectory,
        string programDirectory) =>
        RecoverBeforeSetup(stateDirectory, programDirectory, Directory.Move);

    internal static LauncherUpdateRecoveryResult RecoverBeforeSetup(
        string stateDirectory,
        string programDirectory,
        Action<string, string> moveDirectory)
    {
        ArgumentNullException.ThrowIfNull(moveDirectory);
        var stateRoot = Path.GetFullPath(stateDirectory);
        var targetRoot = Path.GetFullPath(programDirectory);
        var updateRoot = Path.Combine(stateRoot, "self-update");
        if (!Directory.Exists(updateRoot))
        {
            return new(0, 0);
        }

        var transactions = new List<RecoveryTransaction>();
        foreach (var transactionRoot in Directory.EnumerateDirectories(updateRoot).Order(StringComparer.Ordinal))
        {
            var transactionId = Path.GetFileName(transactionRoot);
            if (!Guid.TryParseExact(transactionId, "N", out _))
            {
                continue;
            }
            var planPath = Path.Combine(transactionRoot, "plan.json");
            if (!File.Exists(planPath))
            {
                continue;
            }

            LauncherFilesystemSafety.RejectReparsePoints(transactionRoot, "Mod Bridge update recovery");
            var plan = LauncherUpdateTransactionSecurity.ParseForRecovery(planPath);
            ValidateRecoveryPlan(plan, transactionId, transactionRoot, stateRoot, targetRoot);
            if (Directory.Exists(Path.Combine(transactionRoot, "failed-target")))
            {
                throw new InvalidDataException(
                    "An abandoned Mod Bridge update contains an unexpected failed-target directory.");
            }
            var hasBackup = Directory.Exists(plan.BackupDirectory);
            if (hasBackup)
            {
                LauncherFilesystemSafety.RejectReparsePoints(plan.BackupDirectory, "Mod Bridge update recovery");
                VerifyPayload(plan.BackupDirectory, plan.PreviousFiles);
            }
            transactions.Add(new(transactionRoot, plan, hasBackup));
        }

        var backups = transactions.Where(transaction => transaction.HasBackup).ToArray();
        if (backups.Length > 1)
        {
            throw new InvalidDataException(
                "Multiple abandoned Mod Bridge update backups require manual recovery.");
        }

        var restored = 0;
        if (backups.Length == 1)
        {
            var recovery = backups[0];
            var failedTarget = Path.Combine(recovery.TransactionRoot, "failed-target");
            var movedCurrent = false;
            if (Directory.Exists(targetRoot))
            {
                LauncherFilesystemSafety.RejectReparsePoints(targetRoot, "Mod Bridge update recovery");
                moveDirectory(targetRoot, failedTarget);
                movedCurrent = true;
            }
            try
            {
                moveDirectory(recovery.Plan.BackupDirectory, targetRoot);
            }
            catch
            {
                if (movedCurrent && Directory.Exists(failedTarget) && !Directory.Exists(targetRoot))
                {
                    Directory.Move(failedTarget, targetRoot);
                }
                throw;
            }
            if (movedCurrent)
            {
                Directory.Delete(failedTarget, recursive: true);
            }
            restored = 1;
        }

        foreach (var transaction in transactions)
        {
            Directory.Delete(transaction.TransactionRoot, recursive: true);
        }
        return new(transactions.Count, restored);
    }

    private static void ValidateRecoveryPlan(
        LauncherUpdatePlan plan,
        string transactionId,
        string transactionRoot,
        string stateRoot,
        string targetRoot)
    {
        if (plan.SchemaVersion is not (1 or 2)
            || !string.Equals(plan.TransactionId, transactionId, StringComparison.Ordinal)
            || !PathEquals(plan.StateRoot, stateRoot)
            || !PathEquals(plan.TargetDirectory, targetRoot)
            || !PathEquals(plan.StageDirectory, Path.Combine(transactionRoot, "stage"))
            || !PathEquals(plan.BackupDirectory, Path.Combine(transactionRoot, "backup"))
            || !PathEquals(plan.AcknowledgementPath, Path.Combine(transactionRoot, "startup.ack"))
            || plan.LauncherRelativePath != ModBridgeProductIdentity.ExecutableName)
        {
            throw new InvalidDataException("An abandoned Mod Bridge update plan has invalid recovery paths.");
        }
        if (plan.SchemaVersion == 2
            && (plan.UpdaterRelativePath != ModBridgeProductIdentity.UpdaterExecutableName
                || plan.ReleaseVerifierRelativePath != ModBridgeProductIdentity.ReleaseVerifierExecutableName))
        {
            throw new InvalidDataException("An abandoned Mod Bridge update plan has invalid executable roles.");
        }
    }

    private static void VerifyPayload(string root, IReadOnlyList<LauncherUpdateFile> expected)
    {
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new LauncherUpdateFile(
                Path.GetRelativePath(root, path),
                new FileInfo(path).Length,
                HashFile(path)))
            .ToArray();
        if (actual.Length != expected.Count)
        {
            throw new InvalidDataException("An abandoned Mod Bridge update backup changed file count.");
        }
        foreach (var expectedFile in expected)
        {
            var actualFile = actual.SingleOrDefault(file =>
                string.Equals(file.RelativePath, expectedFile.RelativePath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("An abandoned Mod Bridge update backup changed file identity.");
            if (actualFile.Size != expectedFile.Size
                || !string.Equals(actualFile.Sha256, expectedFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("An abandoned Mod Bridge update backup failed verification.");
            }
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

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

}

public interface ILauncherArchiveDownloader
{
    Task<ModArtifactDownload> DownloadAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken);
}

public sealed class HttpLauncherArchiveDownloader(HttpClient httpClient) : ILauncherArchiveDownloader
{
    public async Task<ModArtifactDownload> DownloadAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Mod Bridge updates require HTTPS.");
        }
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("The Mod Bridge archive exceeds its manifest bound.");
        }
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                return new(response.StatusCode, destination.ToArray(), response.Content.Headers.ContentLength);
            }
            if (destination.Length + count > maximumBytes)
            {
                throw new InvalidDataException("The Mod Bridge archive exceeds its manifest bound.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}

public interface ILauncherArtifactIdentityReader
{
    LauncherReleaseIdentity ReadIdentity(string executablePath);
}

public sealed class WindowsLauncherArtifactIdentityReader : ILauncherArtifactIdentityReader
{
    public LauncherReleaseIdentity ReadIdentity(string executablePath) =>
        LauncherReleaseIdentityParser.Parse(FileVersionInfo.GetVersionInfo(executablePath).ProductVersion);
}

public sealed class LauncherSelfUpdateService(
    string stateDirectory,
    string programDirectory,
    ILauncherArchiveDownloader downloader,
    IModArtifactAuthenticityVerifier authenticityVerifier,
    ILauncherArtifactIdentityReader identityReader)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string stateDirectory = Path.GetFullPath(stateDirectory);
    private readonly string programDirectory = Path.GetFullPath(programDirectory);

    public async Task<LauncherUpdatePreparation> PrepareAsync(
        LauncherReleaseDiscovery discovery,
        string currentSourceCommit,
        int parentProcessId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var authentication = discovery.Authentication
            ?? throw new InvalidDataException(
                "Mod Bridge standalone updates require authenticated release-selection evidence.");
        var artifact = BindAuthenticatedArtifact(discovery, authentication);
        if (string.Equals(currentSourceCommit, artifact.TargetCommit, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                LauncherUpdatePreparationState.UpToDate,
                $"Mod Bridge {artifact.ReleaseVersion} is already current. Action outcome: no replacement is needed."
                + Environment.NewLine
                + authentication.Summary,
                artifact.ReleaseVersion,
                programDirectory,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        var download = await downloader.DownloadAsync(artifact.DownloadUri, artifact.Size, cancellationToken);
        if (download.StatusCode != HttpStatusCode.OK
            || download.Contents.LongLength != artifact.Size
            || (download.DeclaredContentLength is not null && download.DeclaredContentLength != artifact.Size)
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(download.Contents),
                Convert.FromHexString(artifact.Sha256)))
        {
            throw new InvalidDataException("The Mod Bridge archive does not match the release manifest.");
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Path.Combine(stateDirectory, "self-update", transactionId);
        try
        {
            var stageDirectory = Path.Combine(transactionRoot, "stage");
            var evidenceDirectory = Path.Combine(transactionRoot, "evidence");
            Directory.CreateDirectory(stageDirectory);
            Directory.CreateDirectory(evidenceDirectory);
            LauncherFilesystemSafety.RejectReparsePoints(transactionRoot, "Mod Bridge self-update staging");

            var archivePath = Path.Combine(transactionRoot, ModBridgeProductIdentity.UpdateArchiveName);
            await WriteBoundBytesAsync(archivePath, download.Contents, artifact.Sha256, cancellationToken);
            LauncherArchiveExtractor.Extract(download.Contents, stageDirectory);

            var manifest = await CopyBoundFileAsync(
                authentication.ManifestPath,
                Path.Combine(evidenceDirectory, ReleaseSelectionAttestationPolicy.ManifestName),
                authentication.Receipt.ManifestSha256,
                ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes,
                cancellationToken);
            var bundle = await CopyBoundFileAsync(
                authentication.BundlePath,
                Path.Combine(evidenceDirectory, ReleaseSelectionAttestationPolicy.BundleName),
                authentication.Receipt.BundleSha256,
                ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes,
                cancellationToken);
            var receiptPath = Path.Combine(evidenceDirectory, "release-selection-receipt.json");
            var receiptBytes = ReleaseSelectionVerificationReceiptSerializer.Serialize(
                authentication.Receipt,
                writeIndented: true);
            var receipt = await WriteBoundBytesAsync(receiptPath, receiptBytes, expectedSha256: null, cancellationToken);
            var trustedRootPath = Path.Combine(evidenceDirectory, "trusted-root.public-good.v1.json");
            var trustedRoot = await WriteBoundBytesAsync(
                trustedRootPath,
                ReleaseSelectionTrustedRoot.GetNormalizedBytes(),
                ReleaseSelectionAttestationPolicy.TrustedRootSha256,
                cancellationToken);

            var launcherPath = Path.Combine(stageDirectory, ModBridgeProductIdentity.ExecutableName);
            var updaterPath = Path.Combine(stageDirectory, ModBridgeProductIdentity.UpdaterExecutableName);
            var candidateVerifierPath = Path.Combine(
                stageDirectory,
                ModBridgeProductIdentity.ReleaseVerifierExecutableName);
            VerifySignedExecutable(launcherPath);
            VerifySignedExecutable(updaterPath);
            VerifySignedExecutable(candidateVerifierPath);
            var candidateIdentity = identityReader.ReadIdentity(launcherPath);
            var candidateVerifier = BoundFile(candidateVerifierPath);
            if (!string.Equals(
                    candidateIdentity.SourceCommit,
                    artifact.TargetCommit,
                    StringComparison.OrdinalIgnoreCase)
                || !candidateIdentity.HasReleaseVerifierPairing
                || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                    candidateIdentity.ReleaseVerifierSha256!,
                    candidateVerifier.Sha256))
            {
                throw new InvalidDataException(
                    "The signed candidate launcher identity does not match its source or paired release verifier.");
            }

            var currentLauncherPath = Path.Combine(programDirectory, ModBridgeProductIdentity.ExecutableName);
            var currentVerifierPath = Path.Combine(
                programDirectory,
                ModBridgeProductIdentity.ReleaseVerifierExecutableName);
            VerifySignedExecutable(currentLauncherPath);
            VerifySignedExecutable(currentVerifierPath);
            var currentIdentity = identityReader.ReadIdentity(currentLauncherPath);
            var currentVerifier = BoundFile(currentVerifierPath);
            if (!currentIdentity.HasReleaseVerifierPairing
                || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                    currentIdentity.ReleaseVerifierSha256!,
                    currentVerifier.Sha256))
            {
                throw new InvalidDataException(
                    "The installed launcher does not match its paired release verifier.");
            }

            var files = EnumerateFiles(stageDirectory);
            var runnerPath = Path.Combine(transactionRoot, ModBridgeProductIdentity.UpdaterExecutableName);
            File.Copy(updaterPath, runnerPath);
            var plan = new LauncherUpdatePlan(
                2,
                transactionId,
                parentProcessId,
                stateDirectory,
                stageDirectory,
                programDirectory,
                Path.Combine(transactionRoot, "backup"),
                Path.Combine(transactionRoot, "startup.ack"),
                ModBridgeProductIdentity.ExecutableName,
                ModBridgeProductIdentity.UpdaterExecutableName,
                ModBridgeProductIdentity.ReleaseVerifierExecutableName,
                authentication.Acceptance.Manifest.Tag,
                authentication.InstalledReleaseVersion,
                manifest,
                bundle,
                receipt,
                trustedRoot,
                BoundFile(archivePath),
                BoundFile(currentLauncherPath),
                currentVerifier,
                BoundFile(launcherPath),
                BoundFile(updaterPath),
                candidateVerifier,
                BoundFile(runnerPath),
                files,
                Directory.Exists(programDirectory) ? EnumerateFiles(programDirectory) : []);
            var planPath = Path.Combine(transactionRoot, "plan.json");
            var planBytes = JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions);
            var planFile = await WriteBoundBytesAsync(planPath, planBytes, expectedSha256: null, cancellationToken);
            return new(
                LauncherUpdatePreparationState.Ready,
                $"Mod Bridge {artifact.ReleaseVersion} is verified and ready to install after exit. "
                + "Action outcome: the archive is staged; installation has not started."
                + Environment.NewLine
                + authentication.Summary,
                artifact.ReleaseVersion,
                programDirectory,
                planPath,
                runnerPath,
                planFile.Sha256);
        }
        catch
        {
            if (Directory.Exists(transactionRoot))
            {
                LauncherFilesystemSafety.RejectReparsePoints(transactionRoot, "Mod Bridge self-update cleanup");
                Directory.Delete(transactionRoot, recursive: true);
            }
            throw;
        }
    }

    public static void StartUpdater(LauncherUpdatePreparation preparation)
    {
        if (preparation.State != LauncherUpdatePreparationState.Ready)
        {
            throw new InvalidOperationException("Only a ready Mod Bridge update can start.");
        }
        var startInfo = new ProcessStartInfo(preparation.UpdaterPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(preparation.UpdaterPath),
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--plan");
        startInfo.ArgumentList.Add(preparation.PlanPath);
        startInfo.ArgumentList.Add("--plan-sha256");
        startInfo.ArgumentList.Add(preparation.PlanSha256);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the Mod Bridge update helper.");
    }

    private void VerifySignedExecutable(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Mod Bridge archive is missing {Path.GetFileName(path)}.");
        }
        var result = authenticityVerifier.Verify(path);
        if (!result.IsTrusted)
        {
            throw new InvalidDataException($"Mod Bridge update signature verification failed: {result.Message}");
        }
    }

    private static LauncherReleaseArtifact BindAuthenticatedArtifact(
        LauncherReleaseDiscovery discovery,
        AuthenticatedLauncherReleaseEvidence authentication)
    {
        var manifest = authentication.Acceptance.Manifest;
        var state = authentication.Acceptance.State;
        var receipt = authentication.Receipt;
        AuthenticatedReleaseManifestPolicy.ValidateState(state);
        _ = AuthenticatedReleaseManifestPolicy.Evaluate(
            manifest,
            receipt,
            authentication.InstalledReleaseVersion,
            DateTimeOffset.UtcNow,
            state);
        var matches = manifest.Artifacts
            .Where(candidate => candidate.Id == "windows-mod-bridge-archive-x64")
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("Authenticated evidence does not select exactly one Mod Bridge archive.");
        }
        var selected = matches[0];
        if (selected.Size <= 0
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(selected.Sha256))
        {
            throw new InvalidDataException("Authenticated Mod Bridge archive metadata is invalid.");
        }
        var expected = new LauncherReleaseArtifact(
            new Uri(
                $"https://github.com/{ReleaseSelectionAttestationPolicy.Repository}/releases/download/"
                + $"{Uri.EscapeDataString(manifest.Tag)}/{ModBridgeProductIdentity.UpdateArchiveName}"),
            ModBridgeProductIdentity.UpdateArchiveName,
            selected.Size,
            selected.Sha256,
            manifest.ReleaseVersion,
            manifest.Source.TargetCommit);
        var supplied = discovery.LauncherArtifact;
        if (discovery.Manifest.Tag != manifest.Tag
            || discovery.Manifest.ReleaseVersion != manifest.ReleaseVersion
            || discovery.Manifest.Channel != manifest.Channel
            || discovery.Manifest.Source != manifest.Source
            || discovery.Manifest.ManifestAuthenticityScheme != AuthenticatedReleaseManifestPolicy.AuthenticityScheme
            || receipt.SourceCommit != manifest.Source.TargetCommit
            || state.HighestReleaseSequence != manifest.ReleaseSequence
            || state.HighestReleaseVersion != manifest.ReleaseVersion
            || state.SourceCommit != manifest.Source.TargetCommit
            || state.Tag != manifest.Tag
            || state.TrustEpoch != receipt.TrustEpoch
            || state.VerificationMode != receipt.VerificationMode
            || authentication.Acceptance.EffectiveObservationUtc != state.LastObservedUtc
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                state.ManifestSha256,
                receipt.ManifestSha256)
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                state.BundleSha256,
                receipt.BundleSha256)
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                state.TrustedRootSha256,
                receipt.TrustedRootSha256)
            || supplied.DownloadUri != expected.DownloadUri
            || supplied.FileName != expected.FileName
            || supplied.Size != expected.Size
            || supplied.ReleaseVersion != expected.ReleaseVersion
            || supplied.TargetCommit != expected.TargetCommit
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(supplied.Sha256, expected.Sha256))
        {
            throw new InvalidDataException(
                "The staged Mod Bridge archive selection disagrees with authenticated release evidence.");
        }
        return expected;
    }

    private static LauncherUpdateFile[] EnumerateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new LauncherUpdateFile(
                Path.GetRelativePath(root, path),
                new FileInfo(path).Length,
                HashFile(path)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

    private static LauncherUpdateBoundFile BoundFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length <= 0 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The bound update file is missing or unsafe: {Path.GetFileName(fullPath)}");
        }
        return new(
            fullPath,
            info.Length,
            HashFile(fullPath));
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

    private static async Task<LauncherUpdateBoundFile> CopyBoundFileAsync(
        string sourcePath,
        string destinationPath,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length is <= 0 || source.Length > maximumBytes)
        {
            throw new InvalidDataException("Authenticated release evidence is outside its accepted size bound.");
        }
        var bytes = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(bytes, cancellationToken);
        return await WriteBoundBytesAsync(destinationPath, bytes, expectedSha256, cancellationToken);
    }

    private static async Task<LauncherUpdateBoundFile> WriteBoundBytesAsync(
        string path,
        byte[] bytes,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (expectedSha256 is not null
            && !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(digest, expectedSha256))
        {
            throw new InvalidDataException("A staged update input disagrees with its authenticated digest.");
        }
        await using var destination = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await destination.WriteAsync(bytes, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        return new(Path.GetFullPath(path), bytes.LongLength, digest);
    }
}
