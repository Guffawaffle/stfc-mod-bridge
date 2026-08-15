using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed class ProviderConfigurationHistoryEntryViewModel
{
    private readonly SettingsActionCommand reviewCommand;

    internal ProviderConfigurationHistoryEntryViewModel(
        ProviderConfigurationHistoryEntry entry,
        string providerDisplayName,
        Action<ProviderConfigurationHistoryEntryViewModel> review,
        Func<bool> canReview)
    {
        Entry = entry;
        ProviderDisplayName = providerDisplayName;
        CreatedAtText = entry.Receipt.CreatedAtUtc
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);
        HashShort = entry.Receipt.ContentSha256[..Math.Min(12, entry.Receipt.ContentSha256.Length)];
        ReasonText = FormatReason(entry.Receipt.Reason);
        CompatibilityText = entry.CompatibilityState switch
        {
            ProviderConfigurationCompatibilityState.Compatible => "Compatible",
            ProviderConfigurationCompatibilityState.Attention => "Review warnings",
            ProviderConfigurationCompatibilityState.Unknown => "Compatibility unknown",
            ProviderConfigurationCompatibilityState.Blocked => "Restore blocked",
            _ => "Unreadable",
        };
        RestoredText = entry.Receipt.WasRestored
            ? entry.Receipt.RestoredAtUtc is { } restoredAt
                ? $"Restored {restoredAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
                : "Restored previously"
            : "Not restored";
        AutomationName =
            $"{ProviderDisplayName} configuration from {CreatedAtText}, hash {HashShort}, "
            + $"{ReasonText}, {CompatibilityText}, {RestoredText}";
        reviewCommand = new(() => review(this), () => entry.CanRestore && canReview());
    }

    internal ProviderConfigurationHistoryEntry Entry { get; }

    public string ProviderDisplayName { get; }

    public string CreatedAtText { get; }

    public string HashShort { get; }

    public string ReasonText { get; }

    public string CompatibilityText { get; }

    public string CompatibilitySummary => Entry.CompatibilitySummary;

    public string RestoredText { get; }

    public string AutomationName { get; }

    public ICommand ReviewCommand => reviewCommand;

    internal void NotifyCanReviewChanged() => reviewCommand.RaiseCanExecuteChanged();

    private static string FormatReason(string reason) =>
        reason switch
        {
            "configuration-save" or "settings-save" => "Settings save",
            "data-sync-save" => "Data Sync save",
            "configuration-migration" => "Configuration cleanup",
            "manual-restore" => "Configuration restore",
            "provider-switch" => "Release source switch",
            _ => reason.Replace('-', ' '),
        };
}

public sealed record ProviderConfigurationRestoreReviewViewModel(
    ProviderConfigurationRestorePreview Preview,
    string ProviderDisplayName,
    string CreatedAtText,
    string HashShort,
    string ReasonText,
    string CompatibilityText,
    string CompatibilitySummary,
    string DestinationPath)
{
    public string ConfirmationInstruction =>
        $"Type '{Preview.ConfirmationText}' to confirm restoring this provider's configuration.";
}

public sealed class ProviderConfigurationHistoryViewModel : INotifyPropertyChanged
{
    private readonly ProviderConfigurationRestoreCoordinator coordinator;
    private readonly Func<bool> hasSiblingPendingChanges;
    private readonly Action restored;
    private readonly AsyncSettingsActionCommand refreshCommand;
    private readonly AsyncSettingsActionCommand restoreCommand;
    private readonly SettingsActionCommand cancelReviewCommand;
    private readonly object lifecycleSync = new();
    private ProviderConfigurationRestoreReviewViewModel? selectedReview;
    private Task? activeOperation;
    private Task? invalidationTask;
    private string confirmationText = string.Empty;
    private string operationStatus = string.Empty;
    private bool isBusy;
    private bool requiresRecovery;
    private bool isInvalidating;
    private bool isInvalidated;

    public ProviderConfigurationHistoryViewModel(
        ProviderConfigurationRestoreCoordinator coordinator,
        string providerId,
        string providerDisplayName,
        Func<bool> hasSiblingPendingChanges,
        Action restored)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerDisplayName);
        this.hasSiblingPendingChanges = hasSiblingPendingChanges
            ?? throw new ArgumentNullException(nameof(hasSiblingPendingChanges));
        this.restored = restored ?? throw new ArgumentNullException(nameof(restored));
        ProviderId = providerId;
        ProviderDisplayName = providerDisplayName;
        refreshCommand = new(RefreshAsync, () => !IsBusy && !isInvalidating && !isInvalidated);
        restoreCommand = new(RestoreAsync, () => CanRestore);
        cancelReviewCommand = new(CancelReview, () => SelectedReview is not null && !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProviderConfigurationHistoryEntryViewModel> Entries { get; } = [];

    public string ProviderId { get; }

    public string ProviderDisplayName { get; }

    public string Description =>
        $"Protected local history for {ProviderDisplayName}. Entries contain metadata only; configuration values remain encrypted.";

    public ICommand RefreshCommand => refreshCommand;

    public ICommand RestoreCommand => restoreCommand;

    public ICommand CancelReviewCommand => cancelReviewCommand;

    public bool HasEntries => Entries.Count > 0;

    public bool IsEmpty => !HasEntries && !IsBusy;

    public string EmptyText =>
        $"No verified {ProviderDisplayName} configuration history exists for this game installation yet.";

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                NotifyCommandStates();
            }
        }
    }

    public ProviderConfigurationRestoreReviewViewModel? SelectedReview
    {
        get => selectedReview;
        private set
        {
            if (SetField(ref selectedReview, value))
            {
                OnPropertyChanged(nameof(IsReviewVisible));
                cancelReviewCommand.RaiseCanExecuteChanged();
                restoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsReviewVisible => SelectedReview is not null;

    public string ConfirmationText
    {
        get => confirmationText;
        set
        {
            if (SetField(ref confirmationText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanRestore));
                restoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanRestore =>
        !IsBusy
        && !isInvalidating
        && !isInvalidated
        && !requiresRecovery
        && SelectedReview is not null
        && !hasSiblingPendingChanges()
        && string.Equals(
            ConfirmationText,
            SelectedReview.Preview.ConfirmationText,
            StringComparison.Ordinal);

    public string OperationStatus
    {
        get => operationStatus;
        private set => SetField(ref operationStatus, value);
    }

    internal Task RefreshAsync() => RunOperationAsync(RefreshCoreAsync);

    internal Task RestoreAsync() =>
        CanRestore ? RunOperationAsync(RestoreCoreAsync) : Task.CompletedTask;

    internal Task InvalidateAsync()
    {
        lock (lifecycleSync)
        {
            if (invalidationTask is not null)
            {
                return invalidationTask;
            }
            if (isInvalidated)
            {
                return Task.CompletedTask;
            }
            isInvalidating = true;
            invalidationTask = CompleteInvalidationAsync(activeOperation);
        }
        NotifyCommandStates();
        return invalidationTask;
    }

    private async Task RefreshCoreAsync()
    {
        try
        {
            var recovery = await coordinator.RecoverAsync();
            requiresRecovery = recovery.State is ProviderConfigurationRestoreResultState.Busy
                or ProviderConfigurationRestoreResultState.RecoveryRequired;
            OperationStatus = recovery.State == ProviderConfigurationRestoreResultState.NoIncompleteRestore
                ? string.Empty
                : recovery.Message;
            if (recovery.State == ProviderConfigurationRestoreResultState.Succeeded)
            {
                try
                {
                    restored();
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    OperationStatus =
                        $"{recovery.Message} The Settings workspace could not refresh: {exception.Message}";
                }
            }
            LoadEntries();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            requiresRecovery = true;
            Entries.Clear();
            NotifyEntriesChanged();
            OperationStatus = $"Configuration history is unavailable: {exception.Message}";
        }
    }

    internal void NotifySiblingDraftStateChanged()
    {
        OnPropertyChanged(nameof(CanRestore));
        NotifyCommandStates();
    }

    private void Review(ProviderConfigurationHistoryEntryViewModel entry)
    {
        if (hasSiblingPendingChanges())
        {
            OperationStatus =
                "Save or discard pending Settings and Data Sync changes before reviewing a restore.";
            NotifyCommandStates();
            return;
        }
        try
        {
            var preview = coordinator.Preview(
                entry.Entry.Receipt.BackupId);
            SelectedReview = new(
                preview,
                entry.ProviderDisplayName,
                entry.CreatedAtText,
                entry.HashShort,
                entry.ReasonText,
                entry.CompatibilityText,
                entry.CompatibilitySummary,
                preview.DestinationPath);
            ConfirmationText = string.Empty;
            OperationStatus = string.Empty;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            SelectedReview = null;
            ConfirmationText = string.Empty;
            OperationStatus = $"That history entry cannot be reviewed: {exception.Message}";
        }
    }

    private async Task RestoreCoreAsync()
    {
        var review = SelectedReview;
        if (review is null)
        {
            return;
        }
        try
        {
            var result = await coordinator.ExecuteAsync(review.Preview, ConfirmationText);
            OperationStatus = result.Message;
            requiresRecovery = result.State == ProviderConfigurationRestoreResultState.RecoveryRequired;
            if (result.State == ProviderConfigurationRestoreResultState.Succeeded)
            {
                SelectedReview = null;
                ConfirmationText = string.Empty;
                try
                {
                    restored();
                    LoadEntries();
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    OperationStatus =
                        $"{result.Message} The Settings workspace could not refresh: {exception.Message}";
                }
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            requiresRecovery = true;
            SelectedReview = null;
            ConfirmationText = string.Empty;
            OperationStatus =
                $"The configuration restore did not finish cleanly: {exception.Message} Refresh history to recover it before another change.";
        }
    }

    private void CancelReview()
    {
        SelectedReview = null;
        ConfirmationText = string.Empty;
        OperationStatus = "Restore canceled. No configuration was changed.";
    }

    private void LoadEntries()
    {
        var entries = coordinator.LoadHistory()
            .Select(entry => new ProviderConfigurationHistoryEntryViewModel(
                entry,
                ProviderDisplayName,
                Review,
                CanReview))
            .ToArray();
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }
        NotifyEntriesChanged();
    }

    private bool CanReview() =>
        !IsBusy
        && !isInvalidating
        && !isInvalidated
        && !requiresRecovery
        && !hasSiblingPendingChanges();

    private Task RunOperationAsync(Func<Task> operation)
    {
        TaskCompletionSource completion;
        lock (lifecycleSync)
        {
            if (isInvalidating || isInvalidated)
            {
                return Task.CompletedTask;
            }
            if (activeOperation is not null)
            {
                return activeOperation;
            }
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            activeOperation = completion.Task;
        }
        IsBusy = true;
        _ = CompleteOperationAsync(operation, completion);
        return completion.Task;
    }

    private async Task CompleteOperationAsync(
        Func<Task> operation,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (lifecycleSync)
            {
                activeOperation = null;
            }
            IsBusy = false;
        }
        if (failure is null)
        {
            completion.SetResult();
        }
        else
        {
            completion.SetException(failure);
        }
    }

    private async Task CompleteInvalidationAsync(Task? pendingOperation)
    {
        try
        {
            if (pendingOperation is not null)
            {
                await pendingOperation;
            }
        }
        finally
        {
            lock (lifecycleSync)
            {
                isInvalidated = true;
                isInvalidating = false;
            }
            SelectedReview = null;
            ConfirmationText = string.Empty;
            NotifyCommandStates();
        }
    }

    private void NotifyEntriesChanged()
    {
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void NotifyCommandStates()
    {
        refreshCommand.RaiseCanExecuteChanged();
        restoreCommand.RaiseCanExecuteChanged();
        cancelReviewCommand.RaiseCanExecuteChanged();
        foreach (var entry in Entries)
        {
            entry.NotifyCanReviewChanged();
        }
    }

    private static bool IsExpectedFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or CryptographicException
            or JsonException;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
