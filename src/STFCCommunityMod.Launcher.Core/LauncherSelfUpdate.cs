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
    string UpdaterPath);

public sealed record LauncherUpdateRecoveryResult(int ExaminedTransactions, int RestoredBackups);

public static class LauncherUpdateRecovery
{
    private sealed record RecoveryTransaction(
        string TransactionRoot,
        LauncherUpdatePlan Plan,
        bool HasBackup);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

            RejectReparsePoints(transactionRoot);
            var plan = JsonSerializer.Deserialize<LauncherUpdatePlan>(File.ReadAllText(planPath), JsonOptions)
                ?? throw new InvalidDataException("An abandoned Mod Control update plan is empty.");
            ValidateRecoveryPlan(plan, transactionId, transactionRoot, stateRoot, targetRoot);
            if (Directory.Exists(Path.Combine(transactionRoot, "failed-target")))
            {
                throw new InvalidDataException(
                    "An abandoned Mod Control update contains an unexpected failed-target directory.");
            }
            var hasBackup = Directory.Exists(plan.BackupDirectory);
            if (hasBackup)
            {
                RejectReparsePoints(plan.BackupDirectory);
                VerifyPayload(plan.BackupDirectory, plan.PreviousFiles);
            }
            transactions.Add(new(transactionRoot, plan, hasBackup));
        }

        var backups = transactions.Where(transaction => transaction.HasBackup).ToArray();
        if (backups.Length > 1)
        {
            throw new InvalidDataException(
                "Multiple abandoned Mod Control update backups require manual recovery.");
        }

        var restored = 0;
        if (backups.Length == 1)
        {
            var recovery = backups[0];
            var failedTarget = Path.Combine(recovery.TransactionRoot, "failed-target");
            var movedCurrent = false;
            if (Directory.Exists(targetRoot))
            {
                RejectReparsePoints(targetRoot);
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
        if (plan.SchemaVersion != 1
            || !string.Equals(plan.TransactionId, transactionId, StringComparison.Ordinal)
            || !PathEquals(plan.StateRoot, stateRoot)
            || !PathEquals(plan.TargetDirectory, targetRoot)
            || !PathEquals(plan.StageDirectory, Path.Combine(transactionRoot, "stage"))
            || !PathEquals(plan.BackupDirectory, Path.Combine(transactionRoot, "backup"))
            || !PathEquals(plan.AcknowledgementPath, Path.Combine(transactionRoot, "startup.ack"))
            || plan.LauncherRelativePath != ModControlProductIdentity.ExecutableName)
        {
            throw new InvalidDataException("An abandoned Mod Control update plan has invalid recovery paths.");
        }
    }

    private static void VerifyPayload(string root, IReadOnlyList<LauncherUpdateFile> expected)
    {
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new LauncherUpdateFile(
                Path.GetRelativePath(root, path),
                new FileInfo(path).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .ToArray();
        if (actual.Length != expected.Count)
        {
            throw new InvalidDataException("An abandoned Mod Control update backup changed file count.");
        }
        foreach (var expectedFile in expected)
        {
            var actualFile = actual.SingleOrDefault(file =>
                string.Equals(file.RelativePath, expectedFile.RelativePath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("An abandoned Mod Control update backup changed file identity.");
            if (actualFile.Size != expectedFile.Size
                || !string.Equals(actualFile.Sha256, expectedFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("An abandoned Mod Control update backup failed verification.");
            }
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePoints(string root)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Mod Control update recovery refuses filesystem links or reparse points.");
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Mod Control update recovery refuses filesystem links or reparse points.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                }
            }
        }
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
            throw new InvalidDataException("Mod Control updates require HTTPS.");
        }
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("The Mod Control archive exceeds its manifest bound.");
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
                throw new InvalidDataException("The Mod Control archive exceeds its manifest bound.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}

public interface ILauncherArtifactIdentityReader
{
    string? ReadSourceCommit(string executablePath);
}

public sealed class WindowsLauncherArtifactIdentityReader : ILauncherArtifactIdentityReader
{
    public string? ReadSourceCommit(string executablePath)
    {
        var productVersion = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
        var separator = productVersion?.LastIndexOf('+') ?? -1;
        return separator >= 0 ? productVersion![(separator + 1)..] : null;
    }
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
        var artifact = discovery.LauncherArtifact
            ?? throw new InvalidDataException("The release does not provide a supported Mod Control artifact.");
        if (string.Equals(currentSourceCommit, artifact.TargetCommit, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                LauncherUpdatePreparationState.UpToDate,
                $"Mod Control {artifact.ReleaseVersion} is already current.",
                artifact.ReleaseVersion,
                programDirectory,
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
            throw new InvalidDataException("The Mod Control archive does not match the release manifest.");
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Path.Combine(stateDirectory, "self-update", transactionId);
        var stageDirectory = Path.Combine(transactionRoot, "stage");
        Directory.CreateDirectory(stageDirectory);
        LauncherArchiveExtractor.Extract(download.Contents, stageDirectory);

        var launcherPath = Path.Combine(stageDirectory, ModControlProductIdentity.ExecutableName);
        var updaterPath = Path.Combine(stageDirectory, ModControlProductIdentity.UpdaterExecutableName);
        VerifySignedExecutable(launcherPath);
        VerifySignedExecutable(updaterPath);
        if (!string.Equals(identityReader.ReadSourceCommit(launcherPath), artifact.TargetCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The signed Mod Control source identity does not match the release manifest.");
        }

        var files = Directory.EnumerateFiles(stageDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new LauncherUpdateFile(
                Path.GetRelativePath(stageDirectory, path),
                new FileInfo(path).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var plan = new LauncherUpdatePlan(
            1,
            transactionId,
            parentProcessId,
            stateDirectory,
            stageDirectory,
            programDirectory,
            Path.Combine(transactionRoot, "backup"),
            Path.Combine(transactionRoot, "startup.ack"),
            ModControlProductIdentity.ExecutableName,
            files,
            Directory.Exists(programDirectory) ? EnumerateFiles(programDirectory) : []);
        var planPath = Path.Combine(transactionRoot, "plan.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, JsonOptions), cancellationToken);
        var runnerPath = Path.Combine(transactionRoot, ModControlProductIdentity.UpdaterExecutableName);
        File.Copy(updaterPath, runnerPath);
        return new(
            LauncherUpdatePreparationState.Ready,
            $"Mod Control {artifact.ReleaseVersion} is verified and ready to install after exit.",
            artifact.ReleaseVersion,
            programDirectory,
            planPath,
            runnerPath);
    }

    public static void StartUpdater(LauncherUpdatePreparation preparation)
    {
        if (preparation.State != LauncherUpdatePreparationState.Ready)
        {
            throw new InvalidOperationException("Only a ready Mod Control update can start.");
        }
        _ = Process.Start(new ProcessStartInfo(preparation.UpdaterPath, $"--plan \"{preparation.PlanPath}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(preparation.UpdaterPath),
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Windows did not start the Mod Control update helper.");
    }

    private void VerifySignedExecutable(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Mod Control archive is missing {Path.GetFileName(path)}.");
        }
        var result = authenticityVerifier.Verify(path);
        if (!result.IsTrusted)
        {
            throw new InvalidDataException($"Mod Control update signature verification failed: {result.Message}");
        }
    }

    private static LauncherUpdateFile[] EnumerateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new LauncherUpdateFile(
                Path.GetRelativePath(root, path),
                new FileInfo(path).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
}
