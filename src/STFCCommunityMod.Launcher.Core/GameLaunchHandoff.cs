using System.Diagnostics;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherLaunchTarget
{
    PrimeExecutable,
    ScopelyLauncher,
}

public enum LauncherLaunchRecoveryAction
{
    None,
    SelectGameFolder,
    CloseRunningGame,
    InstallMod,
    RepairMod,
    RecoverModTransaction,
    InstallOrRepairScopelyLauncher,
    OpenDiagnostics,
    WaitForLauncherOperation,
}

public sealed record GameLaunchPresentation(
    string Status,
    LauncherHomeTone Tone,
    string ActionLabel,
    bool CanExecute,
    string AutomationName,
    LauncherLaunchTarget Target,
    string Reason,
    LauncherLaunchRecoveryAction NextAction)
{
    public string NextActionLabel => NextAction switch
    {
        LauncherLaunchRecoveryAction.None => string.Empty,
        LauncherLaunchRecoveryAction.SelectGameFolder => "Select the game folder",
        LauncherLaunchRecoveryAction.CloseRunningGame => "Close the running game",
        LauncherLaunchRecoveryAction.InstallMod => "Install the community mod",
        LauncherLaunchRecoveryAction.RepairMod => "Repair the community mod",
        LauncherLaunchRecoveryAction.RecoverModTransaction => "Recover the mod transaction",
        LauncherLaunchRecoveryAction.InstallOrRepairScopelyLauncher => "Install or repair the Scopely launcher",
        LauncherLaunchRecoveryAction.OpenDiagnostics => "Open Diagnostics",
        LauncherLaunchRecoveryAction.WaitForLauncherOperation => "Wait for the active Mod Bridge operation",
        _ => string.Empty,
    };
}

public enum GameLaunchHandoffState
{
    Completed,
    Busy,
    Blocked,
    Failed,
}

public sealed record GameLaunchHandoffResult(
    GameLaunchHandoffState State,
    string Message,
    GameLaunchPresentation Presentation,
    bool Changed);

public enum OfficialLauncherStartKind
{
    StartedNew,
    ReusedRunning,
}

public interface IOfficialLauncherProcess : IAsyncDisposable
{
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public sealed record OfficialLauncherStartResult(
    OfficialLauncherStartKind Kind,
    IOfficialLauncherProcess Process)
{
    public bool Changed => Kind == OfficialLauncherStartKind.StartedNew;
}

public interface IOfficialLauncherService
{
    bool IsAvailable { get; }

    Task<OfficialLauncherStartResult> StartAsync(CancellationToken cancellationToken);
}

public interface IGameExecutableLaunchService
{
    bool IsAvailable(string gameDirectory);

    Task StartAsync(string gameDirectory, CancellationToken cancellationToken);
}

public sealed class WindowsOfficialLauncherService : IOfficialLauncherService
{
    private readonly string launcherPath;
    private readonly Func<bool> isAvailable;
    private readonly Func<IOfficialLauncherProcess?> findRunningLauncher;
    private readonly Func<IOfficialLauncherProcess> startLauncher;

    public WindowsOfficialLauncherService(string launcherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        this.launcherPath = Path.GetFullPath(launcherPath);
        isAvailable = () => File.Exists(this.launcherPath);
        findRunningLauncher = TryFindRunningLauncher;
        startLauncher = StartLauncher;
    }

    internal WindowsOfficialLauncherService(
        Func<bool> isAvailable,
        Func<IOfficialLauncherProcess?> findRunningLauncher,
        Func<IOfficialLauncherProcess> startLauncher)
    {
        launcherPath = "launcher.exe";
        this.isAvailable = isAvailable ?? throw new ArgumentNullException(nameof(isAvailable));
        this.findRunningLauncher = findRunningLauncher ?? throw new ArgumentNullException(nameof(findRunningLauncher));
        this.startLauncher = startLauncher ?? throw new ArgumentNullException(nameof(startLauncher));
    }

    public bool IsAvailable => isAvailable();

    public static WindowsOfficialLauncherService FromCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Windows did not provide a per-user LocalApplicationData directory.");
        }
        return new(Path.Combine(localApplicationData, "Star Trek Fleet Command", "launcher.exe"));
    }

    public async Task<OfficialLauncherStartResult> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            throw new FileNotFoundException("The Scopely launcher is unavailable.", launcherPath);
        }

        var existingProcess = findRunningLauncher();
        if (existingProcess is not null)
        {
            try
            {
                await using var activationProcess = startLauncher();
            }
            catch
            {
                await existingProcess.DisposeAsync();
                throw;
            }
            return new OfficialLauncherStartResult(
                OfficialLauncherStartKind.ReusedRunning,
                existingProcess);
        }

        return new OfficialLauncherStartResult(
            OfficialLauncherStartKind.StartedNew,
            startLauncher());
    }

    private TrackedProcess StartLauncher()
    {
        var process = Process.Start(new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(launcherPath),
        });
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start the Scopely launcher.");
        }
        return new TrackedProcess(process);
    }

    private TrackedProcess? TryFindRunningLauncher()
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(launcherPath));
        Process? match = null;
        foreach (var process in processes)
        {
            try
            {
                if (string.Equals(
                        Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                        launcherPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    match = process;
                    break;
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException
                    or ArgumentException)
            {
                // Only an exact, safely inspected executable is a reusable launcher process.
            }
        }
        foreach (var process in processes)
        {
            if (!ReferenceEquals(process, match))
            {
                process.Dispose();
            }
        }
        return match is null ? null : new TrackedProcess(match);
    }

    private sealed class TrackedProcess(Process process) : IOfficialLauncherProcess
    {
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class WindowsGameExecutableLaunchService : IGameExecutableLaunchService
{
    public bool IsAvailable(string gameDirectory) =>
        TryResolvePrimePath(gameDirectory, out var primePath) && File.Exists(primePath);

    public Task StartAsync(string gameDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolvePrimePath(gameDirectory, out var primePath) || !File.Exists(primePath))
        {
            throw new FileNotFoundException("The selected game folder does not contain prime.exe.", primePath);
        }

        using var process = Process.Start(new ProcessStartInfo(primePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(primePath),
        });
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start prime.exe.");
        }
        return Task.CompletedTask;
    }

    private static bool TryResolvePrimePath(string gameDirectory, out string primePath)
    {
        try
        {
            primePath = Path.Combine(Path.GetFullPath(gameDirectory), "prime.exe");
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            primePath = string.Empty;
            return false;
        }
    }
}

public sealed class GameLaunchHandoffCoordinator(
    string stateDirectory,
    ModDeploymentService deploymentService,
    IGameExecutableLaunchService gameExecutableLaunchService,
    IOfficialLauncherService officialLauncherService,
    IGameProcessInspector gameProcessInspector)
{
    private readonly LauncherOperationLock operationLock = new(stateDirectory);

    public GameLaunchPresentation CapturePresentation(
        string? gameDirectory,
        LauncherLaunchTarget target)
    {
        if (target == LauncherLaunchTarget.ScopelyLauncher)
        {
            return officialLauncherService.IsAvailable
                ? new(
                    "Scopely launcher available",
                    LauncherHomeTone.Success,
                    "Open Scopely launcher",
                    true,
                    "Open Scopely launcher",
                    target,
                    "The supported per-user Scopely launcher is available.",
                    LauncherLaunchRecoveryAction.None)
                : Blocked(
                    "Scopely launcher needed",
                    "Open Scopely launcher",
                    "The supported per-user Scopely launcher could not be found.",
                    target,
                    LauncherLaunchRecoveryAction.InstallOrRepairScopelyLauncher);
        }

        var health = CapturePrimeHealth(gameDirectory, target);
        return health ?? new(
            "Ready to play",
            LauncherHomeTone.Success,
            "Launch prime.exe",
            true,
            "Launch prime.exe directly with the community mod",
            target,
            "The selected game and community mod are ready for a direct launch.",
            LauncherLaunchRecoveryAction.None);
    }

    public async Task<GameLaunchHandoffResult> LaunchAsync(
        string? gameDirectory,
        LauncherLaunchTarget target,
        CancellationToken cancellationToken = default)
    {
        var initial = CapturePresentation(gameDirectory, target);
        if (!initial.CanExecute)
        {
            return new(GameLaunchHandoffState.Blocked, initial.AutomationName, initial, Changed: false);
        }

        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            var busyPresentation = initial with
            {
                Status = "Operation in progress",
                CanExecute = false,
                Reason = "Another Mod Bridge operation currently owns the game-operation boundary.",
                NextAction = LauncherLaunchRecoveryAction.WaitForLauncherOperation,
                AutomationName = $"{initial.ActionLabel} unavailable: another Mod Bridge operation is active.",
            };
            return new(
                GameLaunchHandoffState.Busy,
                $"Another Mod Bridge operation is active. Wait for it to finish before using {initial.ActionLabel}.",
                busyPresentation,
                Changed: false);
        }

        var revalidated = CapturePresentation(gameDirectory, target);
        if (!revalidated.CanExecute)
        {
            return new(GameLaunchHandoffState.Blocked, revalidated.AutomationName, revalidated, Changed: false);
        }

        return target == LauncherLaunchTarget.ScopelyLauncher
            ? await LaunchScopelyAsync(gameDirectory, cancellationToken)
            : await LaunchPrimeAsync(
                gameDirectory
                    ?? throw new InvalidOperationException("The revalidated prime.exe target has no game directory."),
                cancellationToken);
    }

    private async Task<GameLaunchHandoffResult> LaunchScopelyAsync(
        string? gameDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startResult = await officialLauncherService.StartAsync(cancellationToken);
            await using var process = startResult.Process;
            await process.WaitForExitAsync(cancellationToken);
            var message = startResult.Kind == OfficialLauncherStartKind.StartedNew
                ? "The Scopely launcher opened and was tracked until it closed."
                : "The Scopely launcher was already running and was tracked until it closed; no new process was started.";
            return new(
                GameLaunchHandoffState.Completed,
                message,
                CapturePresentation(gameDirectory, LauncherLaunchTarget.ScopelyLauncher),
                startResult.Changed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return new(
                GameLaunchHandoffState.Failed,
                $"The Scopely launcher could not be opened or tracked safely: {exception.Message}",
                CapturePresentation(gameDirectory, LauncherLaunchTarget.ScopelyLauncher),
                Changed: false);
        }
    }

    private async Task<GameLaunchHandoffResult> LaunchPrimeAsync(
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await gameExecutableLaunchService.StartAsync(gameDirectory, cancellationToken);
            return new(
                GameLaunchHandoffState.Completed,
                "prime.exe started.",
                CapturePresentation(gameDirectory, LauncherLaunchTarget.PrimeExecutable),
                Changed: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return new(
                GameLaunchHandoffState.Failed,
                $"prime.exe could not be started: {exception.Message}",
                CapturePresentation(gameDirectory, LauncherLaunchTarget.PrimeExecutable),
                Changed: false);
        }
    }

    private GameLaunchPresentation? CapturePrimeHealth(string? gameDirectory, LauncherLaunchTarget target)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return Blocked(
                "Game folder needed",
                "Launch prime.exe",
                "No valid game folder is selected.",
                target,
                LauncherLaunchRecoveryAction.SelectGameFolder);
        }

        GameInstallValidation validation;
        try
        {
            validation = GameInstallValidator.Validate(gameDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return Blocked(
                "Game folder needed",
                "Launch prime.exe",
                "The selected game folder could not be validated safely.",
                target,
                LauncherLaunchRecoveryAction.OpenDiagnostics);
        }
        if (!validation.IsValid || !gameExecutableLaunchService.IsAvailable(validation.GameDirectory))
        {
            return Blocked(
                "Game folder needed",
                "Launch prime.exe",
                validation.Message,
                target,
                LauncherLaunchRecoveryAction.SelectGameFolder);
        }

        if (gameProcessInspector.IsGameRunning())
        {
            return Blocked(
                "Running",
                "Launch prime.exe",
                "Star Trek Fleet Command is already running.",
                target,
                LauncherLaunchRecoveryAction.CloseRunningGame,
                LauncherHomeTone.Success);
        }

        try
        {
            var journal = deploymentService.ReadJournal();
            if (journal is not null
                && journal.Phase is not (ModDeploymentPhase.Committed
                    or ModDeploymentPhase.RolledBack
                    or ModDeploymentPhase.Failed))
            {
                return Blocked(
                    "Recovery required",
                    "Launch prime.exe",
                    "Recover the incomplete mod transaction before launching.",
                    target,
                    LauncherLaunchRecoveryAction.RecoverModTransaction);
            }

            var state = deploymentService.ReadInstalledState();
            var targetPath = Path.Combine(validation.GameDirectory, "version.dll");
            if (state is null)
            {
                if (!File.Exists(targetPath))
                {
                    return Blocked(
                        "Mod required",
                        "Launch prime.exe",
                        "Install the community mod before a direct modded launch.",
                        target,
                        LauncherLaunchRecoveryAction.InstallMod);
                }
            }
            else if (!PathEquals(state.GameDirectory, validation.GameDirectory)
                || !File.Exists(targetPath)
                || !string.Equals(ComputeSha256(targetPath), state.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Blocked(
                    "Repair required",
                    "Launch prime.exe",
                    "Repair the Mod Bridge-managed mod before launching.",
                    target,
                    LauncherLaunchRecoveryAction.RepairMod);
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Blocked(
                "Repair required",
                "Launch prime.exe",
                "The community mod deployment state could not be validated safely.",
                target,
                LauncherLaunchRecoveryAction.OpenDiagnostics);
        }

        return null;
    }

    private static GameLaunchPresentation Blocked(
        string status,
        string actionLabel,
        string explanation,
        LauncherLaunchTarget target,
        LauncherLaunchRecoveryAction nextAction,
        LauncherHomeTone tone = LauncherHomeTone.Warning) => new(
            status,
            tone,
            actionLabel,
            false,
            $"{actionLabel} unavailable: {explanation}",
            target,
            explanation,
            nextAction);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
