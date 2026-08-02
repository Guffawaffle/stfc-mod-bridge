namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherBootstrapInstallResult(string LauncherPath, bool ReplacedExistingInstallation);

public sealed class LauncherBootstrapInstaller(
    string stateDirectory,
    string programDirectory,
    IModArtifactAuthenticityVerifier authenticityVerifier,
    Func<bool>? isLauncherRunning = null)
{
    private readonly string stateDirectory = Path.GetFullPath(stateDirectory);
    private readonly string programDirectory = Path.GetFullPath(programDirectory);
    private readonly LauncherOperationLock operationLock = new(stateDirectory);
    private readonly Func<bool> isLauncherRunning = isLauncherRunning ?? (() => false);

    public async Task<LauncherBootstrapInstallResult> InstallAsync(
        byte[] archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (isLauncherRunning())
        {
            throw new InvalidOperationException($"Close {ModControlProductIdentity.ProductName} before installing or updating it.");
        }

        await using var lease = await operationLock.TryAcquireAsync(cancellationToken)
            ?? throw new InvalidOperationException("Another Mod Control operation is already in progress.");
        LauncherUpdateRecovery.RecoverBeforeSetup(stateDirectory, programDirectory);
        var transactionRoot = Path.Combine(stateDirectory, "bootstrap", Guid.NewGuid().ToString("N"));
        var stageDirectory = Path.Combine(transactionRoot, "stage");
        var backupDirectory = Path.Combine(transactionRoot, "backup");
        var hadPrevious = Directory.Exists(programDirectory);
        var movedPrevious = false;
        var installed = false;

        try
        {
            LauncherArchiveExtractor.Extract(archive, stageDirectory);
            VerifyExecutable(Path.Combine(stageDirectory, ModControlProductIdentity.ExecutableName));
            VerifyExecutable(Path.Combine(stageDirectory, ModControlProductIdentity.UpdaterExecutableName));

            Directory.CreateDirectory(Path.GetDirectoryName(programDirectory)!);
            if (hadPrevious)
            {
                Directory.Move(programDirectory, backupDirectory);
                movedPrevious = true;
            }

            Directory.Move(stageDirectory, programDirectory);
            installed = true;
            if (movedPrevious)
            {
                Directory.Delete(backupDirectory, recursive: true);
                movedPrevious = false;
            }

            return new(
                Path.Combine(programDirectory, ModControlProductIdentity.ExecutableName),
                hadPrevious);
        }
        catch
        {
            if (installed && Directory.Exists(programDirectory))
            {
                Directory.Delete(programDirectory, recursive: true);
            }
            if (movedPrevious && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, programDirectory);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
            {
                try
                {
                    Directory.Delete(transactionRoot, recursive: true);
                }
                catch (IOException)
                {
                    // A committed install remains valid if antivirus briefly retains a staging handle.
                }
                catch (UnauthorizedAccessException)
                {
                    // The next install uses a new transaction and can leave this bounded residue alone.
                }
            }
        }
    }

    private void VerifyExecutable(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Mod Control package is missing {Path.GetFileName(path)}.");
        }
        var result = authenticityVerifier.Verify(path);
        if (!result.IsTrusted)
        {
            throw new InvalidDataException($"Mod Control package signature verification failed: {result.Message}");
        }
    }
}
