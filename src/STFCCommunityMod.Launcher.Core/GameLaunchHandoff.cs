using System.Diagnostics;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherLaunchTarget
{
    PrimeExecutable,
    ScopelyLauncher,
}

public sealed record GameLaunchPresentation(
    string Status,
    LauncherHomeTone Tone,
    string ActionLabel,
    bool CanExecute,
    string AutomationName,
    LauncherLaunchTarget Target);

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
    GameLaunchPresentation Presentation);

public interface IOfficialLauncherService
{
    bool IsAvailable { get; }

    Task StartAsync(CancellationToken cancellationToken);
}

public interface IGameExecutableLaunchService
{
    bool IsAvailable(string gameDirectory);

    Task StartAsync(string gameDirectory, CancellationToken cancellationToken);
}

public sealed class WindowsOfficialLauncherService : IOfficialLauncherService
{
    private readonly string launcherPath;

    public WindowsOfficialLauncherService(string launcherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        this.launcherPath = Path.GetFullPath(launcherPath);
    }

    public bool IsAvailable => File.Exists(launcherPath);

    public static WindowsOfficialLauncherService FromCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Windows did not provide a per-user LocalApplicationData directory.");
        }
        return new(Path.Combine(localApplicationData, "Star Trek Fleet Command", "launcher.exe"));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            throw new FileNotFoundException("The official Star Trek Fleet Command launcher is unavailable.", launcherPath);
        }

        using var process = Process.Start(new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(launcherPath),
        });
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start the official Star Trek Fleet Command launcher.");
        }
        return Task.CompletedTask;
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
                    "Official launcher available",
                    LauncherHomeTone.Success,
                    "Open Scopely launcher",
                    true,
                    "Open Scopely launcher",
                    target)
                : Blocked(
                    "Official launcher needed",
                    "Open Scopely launcher",
                    "The supported per-user Scopely launcher could not be found. Open Diagnostics for recovery details.",
                    target);
        }

        var health = CapturePrimeHealth(gameDirectory, target);
        return health ?? new(
            "Ready to play",
            LauncherHomeTone.Success,
            "Launch prime.exe",
            true,
            "Launch prime.exe directly with the community mod",
            target);
    }

    public async Task<GameLaunchHandoffResult> LaunchAsync(
        string? gameDirectory,
        LauncherLaunchTarget target,
        CancellationToken cancellationToken = default)
    {
        var initial = CapturePresentation(gameDirectory, target);
        if (!initial.CanExecute)
        {
            return new(GameLaunchHandoffState.Blocked, initial.AutomationName, initial);
        }

        if (target == LauncherLaunchTarget.ScopelyLauncher)
        {
            return await StartAsync(
                () => officialLauncherService.StartAsync(cancellationToken),
                "The Scopely launcher opened.",
                "The Scopely launcher could not be opened",
                gameDirectory,
                target,
                cancellationToken);
        }

        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new(
                GameLaunchHandoffState.Busy,
                "Another launcher operation is active. Wait for it to finish before launching prime.exe.",
                initial with { Status = "Operation in progress", CanExecute = false });
        }

        var revalidated = CapturePresentation(gameDirectory, target);
        if (!revalidated.CanExecute || gameDirectory is null)
        {
            return new(GameLaunchHandoffState.Blocked, revalidated.AutomationName, revalidated);
        }

        return await StartAsync(
            () => gameExecutableLaunchService.StartAsync(gameDirectory, cancellationToken),
            "prime.exe started.",
            "prime.exe could not be started",
            gameDirectory,
            target,
            cancellationToken);
    }

    private async Task<GameLaunchHandoffResult> StartAsync(
        Func<Task> start,
        string successMessage,
        string failurePrefix,
        string? gameDirectory,
        LauncherLaunchTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await start();
            return new(
                GameLaunchHandoffState.Completed,
                successMessage,
                CapturePresentation(gameDirectory, target));
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
                $"{failurePrefix}: {exception.Message}",
                CapturePresentation(gameDirectory, target));
        }
    }

    private GameLaunchPresentation? CapturePrimeHealth(string? gameDirectory, LauncherLaunchTarget target)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return Blocked(
                "Game folder needed",
                "Launch prime.exe",
                "Select a valid game folder before launching. Open Diagnostics for recovery details.",
                target);
        }

        GameInstallValidation validation;
        try
        {
            validation = GameInstallValidator.Validate(gameDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return Blocked("Game folder needed", "Launch prime.exe", exception.Message, target);
        }
        if (!validation.IsValid || !gameExecutableLaunchService.IsAvailable(validation.GameDirectory))
        {
            return Blocked(
                "Game folder needed",
                "Launch prime.exe",
                $"{validation.Message} Open Diagnostics for recovery details.",
                target);
        }

        if (gameProcessInspector.IsGameRunning())
        {
            return Blocked(
                "Running",
                "Launch prime.exe",
                "Star Trek Fleet Command is already running.",
                target,
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
                    target);
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
                        target);
                }
            }
            else if (!PathEquals(state.GameDirectory, validation.GameDirectory)
                || !File.Exists(targetPath)
                || !string.Equals(ComputeSha256(targetPath), state.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Blocked(
                    "Repair required",
                    "Launch prime.exe",
                    "Repair the launcher-managed mod before launching.",
                    target);
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Blocked("Repair required", "Launch prime.exe", exception.Message, target);
        }

        return null;
    }

    private static GameLaunchPresentation Blocked(
        string status,
        string actionLabel,
        string explanation,
        LauncherLaunchTarget target,
        LauncherHomeTone tone = LauncherHomeTone.Warning) => new(
            status,
            tone,
            actionLabel,
            false,
            $"{actionLabel} unavailable: {explanation}",
            target);

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
