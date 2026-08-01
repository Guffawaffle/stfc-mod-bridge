using System.Diagnostics;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

public enum GameLaunchMode
{
    Modded,
    Unmodded,
}

public sealed record GameLaunchPresentation(
    string Status,
    LauncherHomeTone Tone,
    string ActionLabel,
    bool CanExecute,
    string AutomationName,
    GameLaunchMode Mode);

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

public interface IOfficialLauncherProcess : IAsyncDisposable
{
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IOfficialLauncherService
{
    bool IsAvailable { get; }

    Task<IOfficialLauncherProcess> StartAsync(CancellationToken cancellationToken);
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

    public Task<IOfficialLauncherProcess> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            throw new FileNotFoundException("The official Star Trek Fleet Command launcher is unavailable.", launcherPath);
        }

        var existingProcess = TryFindRunningLauncher();
        var process = Process.Start(new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(launcherPath),
        });
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start the official Star Trek Fleet Command launcher.");
        }
        if (existingProcess is not null)
        {
            process.Dispose();
            return Task.FromResult<IOfficialLauncherProcess>(new TrackedProcess(existingProcess));
        }
        return Task.FromResult<IOfficialLauncherProcess>(new TrackedProcess(process));
    }

    private Process? TryFindRunningLauncher()
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
                // A process outside this exact install is not a supported handoff target.
            }
        }
        foreach (var process in processes)
        {
            if (!ReferenceEquals(process, match))
            {
                process.Dispose();
            }
        }
        return match;
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

public sealed class GameLaunchHandoffCoordinator(
    string stateDirectory,
    ModDeploymentService deploymentService,
    IOfficialLauncherService officialLauncherService,
    IGameProcessInspector gameProcessInspector)
{
    private readonly LauncherOperationLock operationLock = new(stateDirectory);

    public GameLaunchPresentation CapturePresentation(
        string? gameDirectory,
        GameLaunchMode mode = GameLaunchMode.Modded)
    {
        if (mode == GameLaunchMode.Unmodded)
        {
            return new(
                "Unmodded unavailable",
                LauncherHomeTone.Warning,
                "Launch unmodded",
                false,
                "Launch unmodded unavailable: the launcher cannot safely disable and restore version.dll yet",
                mode);
        }

        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return Blocked("Game folder needed", "Select a valid game folder before launching.", mode);
        }

        GameInstallValidation validation;
        try
        {
            validation = GameInstallValidator.Validate(gameDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return Blocked("Game folder needed", exception.Message, mode);
        }
        if (!validation.IsValid)
        {
            return Blocked("Game folder needed", validation.Message, mode);
        }

        if (gameProcessInspector.IsGameRunning())
        {
            return new(
                "Running",
                LauncherHomeTone.Success,
                "Game running",
                false,
                "Launch game unavailable: Star Trek Fleet Command is already running",
                mode);
        }

        try
        {
            var journal = deploymentService.ReadJournal();
            if (journal is not null
                && journal.Phase is not (ModDeploymentPhase.Committed
                    or ModDeploymentPhase.RolledBack
                    or ModDeploymentPhase.Failed))
            {
                return Blocked("Recovery required", "Recover the incomplete mod transaction before launching.", mode);
            }

            var state = deploymentService.ReadInstalledState();
            var targetPath = Path.Combine(validation.GameDirectory, "version.dll");
            if (state is null)
            {
                return Blocked("Mod required", "Install or adopt the community mod before a modded launch.", mode);
            }
            if (!PathEquals(state.GameDirectory, validation.GameDirectory)
                || !File.Exists(targetPath)
                || !string.Equals(ComputeSha256(targetPath), state.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Blocked("Repair required", "Repair the launcher-managed mod before launching.", mode);
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Blocked("Repair required", exception.Message, mode);
        }

        if (!officialLauncherService.IsAvailable)
        {
            return Blocked(
                "Official launcher needed",
                "Install or repair the official Star Trek Fleet Command launcher before launching.",
                mode);
        }

        return new(
            "Ready to play",
            LauncherHomeTone.Success,
            "Launch game",
            true,
            "Launch the modded game through the official Star Trek Fleet Command launcher",
            mode);
    }

    public async Task<GameLaunchHandoffResult> LaunchAsync(
        string gameDirectory,
        GameLaunchMode mode = GameLaunchMode.Modded,
        CancellationToken cancellationToken = default)
    {
        var initial = CapturePresentation(gameDirectory, mode);
        if (!initial.CanExecute)
        {
            return new(GameLaunchHandoffState.Blocked, initial.AutomationName, initial);
        }

        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new(
                GameLaunchHandoffState.Busy,
                "Another launcher operation is active. Wait for it to finish before launching.",
                initial with { Status = "Operation in progress", CanExecute = false });
        }

        var revalidated = CapturePresentation(gameDirectory, mode);
        if (!revalidated.CanExecute)
        {
            return new(GameLaunchHandoffState.Blocked, revalidated.AutomationName, revalidated);
        }

        try
        {
            await using var process = await officialLauncherService.StartAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var refreshed = CapturePresentation(gameDirectory, mode);
            return new(
                GameLaunchHandoffState.Completed,
                gameProcessInspector.IsGameRunning()
                    ? "The official launcher handed off to Star Trek Fleet Command."
                    : "The official launcher closed; game and mod health were refreshed.",
                refreshed);
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
            var refreshed = CapturePresentation(gameDirectory, mode);
            return new(
                GameLaunchHandoffState.Failed,
                $"The official launcher could not be started: {exception.Message}",
                refreshed);
        }
    }

    private static GameLaunchPresentation Blocked(string status, string explanation, GameLaunchMode mode) => new(
        status,
        LauncherHomeTone.Warning,
        "Launch game",
        false,
        $"Launch game unavailable: {explanation}",
        mode);

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
