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
    SetUpProfileSupport,
}

public sealed record GameLaunchPresentation(
    string Status,
    LauncherHomeTone Tone,
    string ActionLabel,
    bool CanExecute,
    string AutomationName,
    LauncherLaunchTarget Target,
    string Reason,
    LauncherLaunchRecoveryAction NextAction,
    bool RequiresUserOverride = false)
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
        LauncherLaunchRecoveryAction.SetUpProfileSupport => "Set up profile support",
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

    Task<IGameExecutableProcess> StartAsync(string gameDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public interface IGameExecutableProcess : IAsyncDisposable
{
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IGameExecutableProcessIdentity
{
    int ProcessId { get; }
    long ProcessStartUtcTicks { get; }
    string ExecutablePath { get; }
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

    public Task<IGameExecutableProcess> StartAsync(string gameDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryResolvePrimePath(gameDirectory, out var primePath) || !File.Exists(primePath))
        {
            throw new FileNotFoundException("The selected game folder does not contain prime.exe.", primePath);
        }

        var startInfo = new ProcessStartInfo(primePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(primePath),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start prime.exe.");
        }
        return Task.FromResult<IGameExecutableProcess>(new TrackedGameProcess(process));
    }

    private sealed class TrackedGameProcess(Process process) : IGameExecutableProcess, IGameExecutableProcessIdentity
    {
        public int ProcessId => process.Id;
        public long ProcessStartUtcTicks => process.StartTime.ToUniversalTime().Ticks;
        public string ExecutablePath => process.MainModule?.FileName ?? string.Empty;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
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

public sealed partial class GameLaunchHandoffCoordinator(
    string stateDirectory,
    ModDeploymentService deploymentService,
    IGameExecutableLaunchService gameExecutableLaunchService,
    IOfficialLauncherService officialLauncherService,
    IGameProcessInspector gameProcessInspector,
    NativeLauncherProfilesStore? profileStore = null)
{
    private readonly LauncherOperationLock operationLock = new(stateDirectory);
    private readonly NativeLauncherProfilesStore profilesStore = profileStore ?? new(stateDirectory);
    private readonly JsonGameInstallSelectionStore installSelectionStore = new(stateDirectory);

    public GameLaunchPresentation CapturePresentation(
        string? gameDirectory,
        LauncherLaunchTarget target,
        ModInstallationEvidence? capturedInstallation = null,
        LauncherProfile? requiredProfile = null)
    {
        if (HasIncompleteDeployment(gameDirectory))
        {
            var action = target == LauncherLaunchTarget.ScopelyLauncher
                ? "Open Scopely launcher"
                : "Launch prime.exe";
            return Blocked(
                "Recovery required",
                action,
                "Recover the incomplete mod transaction before launching.",
                target,
                LauncherLaunchRecoveryAction.RecoverModTransaction);
        }
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

        var health = CapturePrimeHealth(gameDirectory, target, capturedInstallation, requiredProfile);
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

    private bool HasIncompleteDeployment(string? gameDirectory)
    {
        try
        {
            var journal = deploymentService.ReadJournal();
            return journal is not null
                && (gameDirectory is null || GameDirectoryIdentity.SameLocation(journal.GameDirectory, gameDirectory))
                && journal.Phase is not (ModDeploymentPhase.Committed
                    or ModDeploymentPhase.RolledBack
                    or ModDeploymentPhase.Failed);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or System.Text.Json.JsonException
                or ArgumentException
                or NotSupportedException)
        {
            return true;
        }
    }

    public async Task<GameLaunchHandoffResult> LaunchAsync(
        string? gameDirectory,
        LauncherLaunchTarget target,
        bool allowUnverifiedProxy = false,
        LauncherProfile? requiredProfile = null,
        string? defaultGameDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var initial = CapturePresentation(gameDirectory, target, requiredProfile: requiredProfile);
        if (!initial.CanExecute)
        {
            return new(GameLaunchHandoffState.Blocked, initial.AutomationName, initial, Changed: false);
        }
        if (initial.RequiresUserOverride && !allowUnverifiedProxy)
        {
            return new(
                GameLaunchHandoffState.Blocked,
                "Review the version.dll warning and choose Launch anyway to continue.",
                initial,
                Changed: false);
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

        var revalidated = CapturePresentation(gameDirectory, target, requiredProfile: requiredProfile);
        if (!revalidated.CanExecute)
        {
            return new(GameLaunchHandoffState.Blocked, revalidated.AutomationName, revalidated, Changed: false);
        }
        if (revalidated.RequiresUserOverride && !allowUnverifiedProxy)
        {
            return new(
                GameLaunchHandoffState.Blocked,
                "The version.dll state changed and now requires explicit Launch anyway approval.",
                revalidated,
                Changed: false);
        }

        var officialLauncherWithoutSelectedGame = target == LauncherLaunchTarget.ScopelyLauncher
            && requiredProfile is null && gameDirectory is null;
        var savedDefault = installSelectionStore.Load();
        if (savedDefault.State == GameInstallSelectionState.Invalid
            && !officialLauncherWithoutSelectedGame && requiredProfile is null)
        {
            return new(GameLaunchHandoffState.Blocked,
                savedDefault.Error ?? "The Default game selection could not be read.",
                revalidated, Changed: false);
        }
        var displayedDefault = requiredProfile is null ? gameDirectory : defaultGameDirectory;
        var currentDefault = savedDefault.State == GameInstallSelectionState.Loaded
            ? savedDefault.Selection!.GameDirectory
            : displayedDefault;
        if (savedDefault.State == GameInstallSelectionState.Loaded
            && !officialLauncherWithoutSelectedGame && requiredProfile is null
            && (displayedDefault is null
                || !GameDirectoryIdentity.SameLocation(displayedDefault, currentDefault!)))
        {
            return new(GameLaunchHandoffState.Blocked,
                "The Default game folder changed in another window. Review the launch button and try again.",
                revalidated, Changed: false);
        }

        string? selectedProfileId;
        try { selectedProfileId = profilesStore.LoadSelectedId(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or NotSupportedException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new(GameLaunchHandoffState.Blocked, exception.Message, revalidated, Changed: false);
        }
        if (selectedProfileId != (requiredProfile?.Id ?? profilesStore.ResolveDefault().Id))
            return new(GameLaunchHandoffState.Blocked,
                "The selected launch profile changed. Review the launch button and try again.",
                revalidated, Changed: false);

        if (requiredProfile is not null)
        {
            var catalog = profilesStore.Load();
            if (catalog.State == LauncherProfilesLoadState.Invalid || catalog.Snapshot is null)
                return new(GameLaunchHandoffState.Blocked,
                    catalog.Error ?? "The shared profile catalog is unavailable.", revalidated, Changed: false);
            var selected = catalog.Snapshot.SelectedProfile;
            if (selected?.Id != requiredProfile.Id || selected.Kind != requiredProfile.Kind
                || selected.PreferredInstallationId != requiredProfile.PreferredInstallationId
                || selected.Revision != requiredProfile.Revision
                || !GameDirectoryIdentity.SameLocation(
                    string.IsNullOrWhiteSpace(selected.GameDirectory) && selected.IsDefault
                        ? defaultGameDirectory ?? gameDirectory ?? string.Empty : selected.GameDirectory,
                    requiredProfile.GameDirectory))
            {
                return new(GameLaunchHandoffState.Blocked,
                    "The named launch selection changed. Review it and try again.", revalidated, Changed: false);
            }
            if ((!requiredProfile.IsDefault && target != LauncherLaunchTarget.PrimeExecutable)
                || !GameDirectoryIdentity.SameLocation(gameDirectory ?? string.Empty, requiredProfile.GameDirectory)
)
            {
                return new(GameLaunchHandoffState.Blocked,
                    "The named profile must launch prime.exe from its recorded game folder.",
                    revalidated, Changed: false);
            }
            if (!requiredProfile.IsDefault)
            {
                var contract = LauncherProfileLaunchContract.Inspect(requiredProfile.GameDirectory, requiredProfile.Id);
                if (!contract.IsValid)
                    return new(GameLaunchHandoffState.Blocked, contract.Message, revalidated, Changed: false);
            }
            if (!string.IsNullOrWhiteSpace(requiredProfile.PreferredInstallationId))
            {
                var registered = profilesStore.InstallationPaths(requiredProfile.PreferredInstallationId);
                if (registered.State != "available"
                    || !GameDirectoryIdentity.SameLocation(registered.GameDirectory, requiredProfile.GameDirectory))
                    return new(GameLaunchHandoffState.Blocked,
                        "The saved game installation is missing or has changed. Select an installation before launching.", revalidated, false);
            }
        }

        string? capturedInstallationId = null;
        if (target == LauncherLaunchTarget.PrimeExecutable && requiredProfile?.IsDefault == true)
        {
            try
            {
                var captured = await profilesStore.ResolveOperationTargetAsync(gameDirectory!,
                    requiredProfile.PreferredInstallationId, cancellationToken);
                gameDirectory = captured.GameDirectory;
                capturedInstallationId = captured.Id;
                requiredProfile = requiredProfile with { GameDirectory = gameDirectory };
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return new(GameLaunchHandoffState.Blocked, exception.Message, revalidated, Changed: false);
            }
        }
        return target == LauncherLaunchTarget.ScopelyLauncher
            ? await LaunchScopelyAsync(gameDirectory, cancellationToken)
            : await LaunchPrimeAsync(
                gameDirectory
                    ?? throw new InvalidOperationException("The revalidated prime.exe target has no game directory."),
                requiredProfile,
                cancellationToken, capturedInstallationId);
    }

    public Task<GameLaunchHandoffResult> LaunchProfileAsync(
        LauncherProfile profile,
        bool allowUnverifiedProxy = false,
        string? defaultGameDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.IsDefault && string.IsNullOrWhiteSpace(profile.GameDirectory))
            profile = profile with { GameDirectory = defaultGameDirectory ?? string.Empty };
        return LaunchAsync(profile.GameDirectory, LauncherLaunchTarget.PrimeExecutable,
            allowUnverifiedProxy, profile, defaultGameDirectory, cancellationToken);
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
                or System.ComponentModel.Win32Exception
                or TypeLoadException or BadImageFormatException)
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
        LauncherProfile? profile,
        CancellationToken cancellationToken,
        string? capturedInstallationId = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (profile is { IsDefault: false })
            {
                var launched = await profilesStore.LaunchAsync(profile, cancellationToken);
                if (!launched.Ok || launched.Readiness != "ready" || launched.ProcessId is not > 0)
                {
                    var started = launched.ProcessId is > 0;
                    var explanation = launched.Error?.Message ?? "The runtime did not confirm profile isolation readiness.";
                    if (started) explanation += $" Game process {launched.ProcessId} may still be running; inspect it before retrying.";
                    return new(GameLaunchHandoffState.Failed, explanation,
                        CapturePresentation(gameDirectory, LauncherLaunchTarget.PrimeExecutable, requiredProfile: profile), started);
                }
                return new(GameLaunchHandoffState.Completed,
                    $"{profile.Name} started with isolated preferences (process {launched.ProcessId}).",
                    CapturePresentation(gameDirectory, LauncherLaunchTarget.PrimeExecutable, requiredProfile: profile), Changed: true);
            }
            IDisposable? installationLease = capturedInstallationId is null
                ? profilesStore.AcquireInstallationLease(gameDirectory)
                : profilesStore.AcquireInstallationLease(gameDirectory, capturedInstallationId);
            try
            {
                if (profile is { PreferredInstallationId.Length: > 0 })
                {
                    var registered = profilesStore.InstallationPaths(profile.PreferredInstallationId);
                    if (registered.State != "available" || !GameDirectoryIdentity.SameLocation(registered.GameDirectory, gameDirectory))
                        throw new InvalidOperationException("The selected installation changed before launch. Select the intended installation again.");
                }
                var child = await gameExecutableLaunchService.StartAsync(gameDirectory, [], cancellationToken);
                RememberOrdinarySession(profile, gameDirectory, child);
                GameInstallationLaunchCustody.Retain(child, installationLease);
                installationLease = null;
            }
            finally { installationLease?.Dispose(); }
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
                or System.ComponentModel.Win32Exception
                or NotSupportedException or TypeLoadException or BadImageFormatException
                or System.Text.Json.JsonException)
        {
            return new(
                GameLaunchHandoffState.Failed,
                $"prime.exe could not be started: {exception.Message}",
                CapturePresentation(gameDirectory, LauncherLaunchTarget.PrimeExecutable),
                Changed: false);
        }
    }

    private GameLaunchPresentation? CapturePrimeHealth(
        string? gameDirectory,
        LauncherLaunchTarget target,
        ModInstallationEvidence? capturedInstallation = null,
        LauncherProfile? requiredProfile = null)
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

        if (requiredProfile is { PreferredInstallationId.Length: > 0 } bound
            && bound.InstallationState != "available")
            return Blocked("Needs setup", "Launch prime.exe",
                "The saved installation is missing or has changed. Select an installation for the next launch.",
                target, LauncherLaunchRecoveryAction.SelectGameFolder);

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

        if (requiredProfile is { IsDefault: false })
        {
            var support = LauncherProfileLaunchContract.Inspect(validation.GameDirectory!, requiredProfile.Id);
            if (!support.IsValid)
                return Blocked("Needs setup", "Launch prime.exe", support.Message, target,
                    LauncherLaunchRecoveryAction.SetUpProfileSupport);
        }

        var processState = gameProcessInspector.Inspect(validation.GameDirectory);
        if (processState == GameProcessInspectionState.Unattributable)
        {
            return Blocked(
                "Needs attention",
                "Launch prime.exe",
                "A prime.exe process is running but could not be attributed safely.",
                target,
                LauncherLaunchRecoveryAction.CloseRunningGame,
                LauncherHomeTone.Warning);
        }
        if (processState == GameProcessInspectionState.RunningTarget
            && (requiredProfile is null || requiredProfile.IsDefault && !AllRunningProcessesHaveKnownSessions(validation.GameDirectory!)
                || !requiredProfile.IsDefault && !LauncherProfileLaunchContract.Inspect(
                validation.GameDirectory!, requiredProfile.Id).IsValid))
        {
            return Blocked(
                "Running",
                "Launch prime.exe",
                "Star Trek Fleet Command is already running.",
                target,
                LauncherLaunchRecoveryAction.CloseRunningGame,
                LauncherHomeTone.Success);
        }

        if (capturedInstallation is not null)
        {
            return capturedInstallation.State switch
            {
                ModInstallationEvidenceState.RecoveryRequired => Blocked(
                    "Recovery required",
                    "Launch prime.exe",
                    "Recover the incomplete mod transaction before launching.",
                    target,
                    LauncherLaunchRecoveryAction.RecoverModTransaction),
                ModInstallationEvidenceState.NotInstalled
                    or ModInstallationEvidenceState.ManagedMissing => ReadyWithoutMod(target),
                ModInstallationEvidenceState.ManagedChanged
                    or ModInstallationEvidenceState.Unavailable =>
                    CapturePrimeDeploymentHealth(validation.GameDirectory, target),
                ModInstallationEvidenceState.ManualInstallation =>
                    CapturePrimeDeploymentHealth(validation.GameDirectory, target),
                ModInstallationEvidenceState.ManagedVerified => null,
                _ => CapturePrimeDeploymentHealth(validation.GameDirectory, target),
            };
        }

        return CapturePrimeDeploymentHealth(validation.GameDirectory, target);
    }

    private GameLaunchPresentation? CapturePrimeDeploymentHealth(
        string gameDirectory,
        LauncherLaunchTarget target)
    {
        try
        {
            var journal = deploymentService.ReadJournal();
            if (journal is not null
                && GameDirectoryIdentity.SameLocation(journal.GameDirectory, gameDirectory)
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

            var targetPath = Path.Combine(gameDirectory, "version.dll");
            var targetExists = File.Exists(targetPath);
            if (!targetExists)
            {
                return ReadyWithoutMod(target);
            }

            var state = deploymentService.ReadInstalledState(gameDirectory);
            if (state is null)
            {
                return RequiresOverride(
                    target,
                    "This game folder contains version.dll, but Mod Bridge did not install or record it. Windows may "
                    + "load it automatically, and Mod Bridge cannot vouch for its source or behavior.");
            }
            else if (!PathEquals(state.GameDirectory, gameDirectory))
            {
                return RequiresOverride(
                    target,
                    "This game folder contains version.dll, but it is not the verified Mod Bridge-managed file "
                    + "recorded for this installation. Windows may load it automatically.");
            }
            else if (!string.Equals(
                         ComputeSha256(targetPath),
                         state.Sha256,
                         StringComparison.OrdinalIgnoreCase))
            {
                return RequiresOverride(
                    target,
                    "The Mod Bridge-managed version.dll has changed since it was installed. Windows may still "
                    + "load it automatically, and Mod Bridge cannot vouch for what it will do.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Text.Json.JsonException
                or CryptographicException)
        {
            return RequiresOverride(
                target,
                "Mod Bridge could not verify the local version.dll deployment state. Windows may load a present "
                + "proxy automatically, and Mod Bridge cannot vouch for what it will do.",
                LauncherLaunchRecoveryAction.OpenDiagnostics);
        }

        return null;
    }

    private static GameLaunchPresentation ReadyWithoutMod(LauncherLaunchTarget target) => new(
        "Ready without mod",
        LauncherHomeTone.Warning,
        "Launch prime.exe",
        true,
        "Launch prime.exe without the community mod",
        target,
        "No version.dll is present in the selected game folder. The game will start without the community mod.",
        LauncherLaunchRecoveryAction.InstallMod);

    private static GameLaunchPresentation RequiresOverride(
        LauncherLaunchTarget target,
        string explanation,
        LauncherLaunchRecoveryAction nextAction = LauncherLaunchRecoveryAction.RepairMod) => new(
            "Mod needs attention",
            LauncherHomeTone.Warning,
            "Launch prime.exe",
            true,
            $"Launch prime.exe requires confirmation: {explanation}",
            target,
            explanation,
            nextAction,
            RequiresUserOverride: true);

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
