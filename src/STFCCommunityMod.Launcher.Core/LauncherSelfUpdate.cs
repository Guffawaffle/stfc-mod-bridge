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
            throw new InvalidDataException("Launcher updates require HTTPS.");
        }
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("The launcher archive exceeds its manifest bound.");
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
                throw new InvalidDataException("The launcher archive exceeds its manifest bound.");
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
        WindowsReleaseDiscovery discovery,
        string currentSourceCommit,
        int parentProcessId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var artifact = discovery.LauncherArtifact
            ?? throw new InvalidDataException("The release does not provide a supported launcher artifact.");
        if (string.Equals(currentSourceCommit, artifact.TargetCommit, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                LauncherUpdatePreparationState.UpToDate,
                $"Launcher {artifact.ReleaseVersion} is already current.",
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
            throw new InvalidDataException("The launcher archive does not match the release manifest.");
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Path.Combine(stateDirectory, "self-update", transactionId);
        var stageDirectory = Path.Combine(transactionRoot, "stage");
        Directory.CreateDirectory(stageDirectory);
        LauncherArchiveExtractor.Extract(download.Contents, stageDirectory);

        var launcherPath = Path.Combine(stageDirectory, "STFCCommunityMod.Launcher.exe");
        var updaterPath = Path.Combine(stageDirectory, "STFCCommunityMod.Launcher.Updater.exe");
        VerifySignedExecutable(launcherPath);
        VerifySignedExecutable(updaterPath);
        if (!string.Equals(identityReader.ReadSourceCommit(launcherPath), artifact.TargetCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The signed launcher source identity does not match the release manifest.");
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
            "STFCCommunityMod.Launcher.exe",
            files,
            Directory.Exists(programDirectory) ? EnumerateFiles(programDirectory) : []);
        var planPath = Path.Combine(transactionRoot, "plan.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, JsonOptions), cancellationToken);
        var runnerPath = Path.Combine(transactionRoot, "STFCCommunityMod.Launcher.Updater.exe");
        File.Copy(updaterPath, runnerPath);
        return new(
            LauncherUpdatePreparationState.Ready,
            $"Launcher {artifact.ReleaseVersion} is verified and ready to install after exit.",
            artifact.ReleaseVersion,
            programDirectory,
            planPath,
            runnerPath);
    }

    public static void StartUpdater(LauncherUpdatePreparation preparation)
    {
        if (preparation.State != LauncherUpdatePreparationState.Ready)
        {
            throw new InvalidOperationException("Only a ready launcher update can start.");
        }
        _ = Process.Start(new ProcessStartInfo(preparation.UpdaterPath, $"--plan \"{preparation.PlanPath}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(preparation.UpdaterPath),
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Windows did not start the launcher update helper.");
    }

    private void VerifySignedExecutable(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Launcher archive is missing {Path.GetFileName(path)}.");
        }
        var result = authenticityVerifier.Verify(path);
        if (!result.IsTrusted)
        {
            throw new InvalidDataException($"Launcher update signature verification failed: {result.Message}");
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
