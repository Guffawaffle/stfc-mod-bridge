using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

/// <summary>Game update evidence is independent of mod and account launch readiness.</summary>
internal sealed class GameInstallationViewModel(
    GameInstallationCoordinator coordinator,
    Func<string?> currentDirectory,
    Func<bool> mutationAvailable,
    Action operationCompleted) : INotifyPropertyChanged
{
    private GameInstallationSnapshot? snapshot;
    private string? target;
    private int? checkedVersion;
    private bool checkedUpdateAvailable;
    private bool isWorking;
    private bool isMutating;
    private string feedback = "Check the official service for a game client update.";

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Target => target ?? "Choose a game installation first.";
    public bool IsWorking => isWorking;
    public bool IsMutationInProgress => isMutating;
    public string Status => snapshot is null ? "Game client version not checked"
        : snapshot.RequiresRecovery ? $"Game client {snapshot.InstalledVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} · recovery required"
        : checkedVersion is not null && checkedUpdateAvailable
            ? $"Game client {snapshot.InstalledVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} → {checkedVersion} available"
            : $"Game client {snapshot.InstalledVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} · {snapshot.State}";
    public string Feedback => feedback;
    public string ProgressText => snapshot is null ? ""
        : $"{snapshot.Phase}{BytesProgress(snapshot)}";
    public bool HasProgress => snapshot?.ProgressPercent is >= 0 and <= 100;
    public double ProgressPercent => snapshot?.ProgressPercent is >= 0 and <= 100
        ? snapshot.ProgressPercent.Value : 0;
    public bool CanCheck => !isWorking && target is not null && mutationAvailable();
    public bool CanUpdate => CanCheck && checkedUpdateAvailable && checkedVersion is not null
        && snapshot is { State: "ready", RequiresRecovery: false };
    public bool CanRecover => CanCheck && snapshot?.RequiresRecovery == true;

    public void SetTarget(string? directory)
    {
        if (isWorking || target == directory) return;
        target = directory;
        snapshot = null;
        checkedVersion = null;
        checkedUpdateAvailable = false;
        feedback = directory is null ? "Choose the game folder that contains prime.exe."
            : "Check the official service for a game client update.";
        Notify();
    }

    public void NotifyAvailability() => Notify();

    public Task RefreshStatusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(check: false, recover: false, mutate: false, cancellationToken);
    public Task CheckAsync(CancellationToken cancellationToken = default) =>
        RunAsync(check: true, recover: false, mutate: false, cancellationToken);
    public Task UpdateAsync(CancellationToken cancellationToken = default) =>
        RunAsync(check: false, recover: false, mutate: true, cancellationToken);
    public Task RecoverAsync(CancellationToken cancellationToken = default) =>
        RunAsync(check: false, recover: true, mutate: true, cancellationToken);

    private async Task RunAsync(bool check, bool recover, bool mutate, CancellationToken cancellationToken)
    {
        SetTarget(currentDirectory());
        if (target is null || isWorking || !mutationAvailable()
            || mutate && (recover ? !CanRecover : !CanUpdate)) return;
        var admittedTarget = target;
        var expectedVersion = checkedVersion;
        isWorking = true;
        isMutating = mutate;
        feedback = mutate
            ? recover ? "Recovering the selected game installation…"
                : $"Updating the selected installation to game client {expectedVersion}…"
            : check ? "Checking the official game update service…" : "Reading game installation status…";
        Notify();
        try
        {
            var progress = new Progress<GameInstallationSnapshot>(observed =>
            {
                if (!isWorking || !GameInstallationCoordinator.SameDirectory(admittedTarget, observed.GameDirectory)) return;
                snapshot = observed;
                feedback = observed.Message ?? feedback;
                Notify();
            });
            var response = mutate
                ? recover ? await coordinator.RecoverAsync(admittedTarget, progress, cancellationToken)
                    : await coordinator.UpdateAsync(admittedTarget, expectedVersion!.Value, progress, cancellationToken)
                : check ? await coordinator.CheckAsync(admittedTarget, cancellationToken)
                    : await coordinator.ReadStatusAsync(admittedTarget, cancellationToken);
            if (response.Installation is { } observed) snapshot = observed;
            if (response.Ok && check && snapshot is { } checkedSnapshot)
            {
                checkedVersion = checkedSnapshot.AvailableVersion;
                checkedUpdateAvailable = checkedSnapshot.UpdateAvailable == true;
            }
            if (mutate || !response.Ok) { checkedVersion = null; checkedUpdateAvailable = false; }
            feedback = response.Ok
                ? response.Installation?.Message ?? (mutate
                    ? recover ? "Game update recovery completed. Recheck the client and mod readiness."
                        : "Game client update completed. Recheck mod compatibility before launching."
                    : check ? checkedUpdateAvailable ? $"Game client {checkedVersion} is available for this installation."
                        : "The official service reports no game client update." : "Game installation status refreshed.")
                : response.Error?.Message ?? "The shared game updater did not complete the operation.";
        }
        catch (OperationCanceledException)
        { feedback = "The game client check was canceled before starting a native transaction."; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException
            or System.Runtime.InteropServices.ExternalException or TypeLoadException or BadImageFormatException
            or System.Text.Json.JsonException)
        {
            checkedVersion = null;
            checkedUpdateAvailable = false;
            feedback = $"Game client operation failed: {exception.Message} Recheck this installation's status before retrying.";
        }
        finally
        {
            isWorking = false;
            isMutating = false;
            Notify();
            if (mutate) operationCompleted();
        }
    }

    private static string BytesProgress(GameInstallationSnapshot observed) =>
        observed.Phase == "downloading" && observed.DownloadedBytes is { } downloaded
            ? observed.DownloadBytes is > 0 ? $" · {downloaded:N0} / {observed.DownloadBytes:N0} bytes"
                : $" · {downloaded:N0} bytes downloaded"
            : observed.Phase == "extracting" && observed.ExtractedBytes is { } extracted
                ? observed.ExtractedTotalBytes is > 0 ? $" · {extracted:N0} / {observed.ExtractedTotalBytes:N0} bytes"
                    : $" · {extracted:N0} bytes extracted"
                : observed.Phase == "committing" && observed.CompletedFiles is { } completed && observed.TotalFiles is > 0
                    ? $" · {completed:N0} / {observed.TotalFiles:N0} files" : "";

    private void Notify()
    {
        foreach (var name in new[] { nameof(Target), nameof(IsWorking), nameof(IsMutationInProgress), nameof(Status), nameof(Feedback),
            nameof(ProgressText), nameof(HasProgress), nameof(ProgressPercent), nameof(CanCheck), nameof(CanUpdate),
            nameof(CanRecover) }) OnPropertyChanged(name);
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
