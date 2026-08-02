using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public enum ObservableActionStatus
{
    Idle,
    Working,
    CompletedChanged,
    CompletedUnchanged,
    Failed,
    Unavailable,
}

public enum ObservableActionResultKind
{
    Changed,
    Unchanged,
    Failed,
}

public sealed record ObservableActionResult(
    ObservableActionResultKind Kind,
    string Message)
{
    public static ObservableActionResult Changed(string message) =>
        new(ObservableActionResultKind.Changed, message);

    public static ObservableActionResult Unchanged(string message) =>
        new(ObservableActionResultKind.Unchanged, message);

    public static ObservableActionResult Failed(string message) =>
        new(ObservableActionResultKind.Failed, message);
}

/// <summary>
/// Independent feedback channels for operations that can have different availability
/// and lifecycles. Keeping these states distinct prevents one action from disabling or
/// overwriting another action's feedback.
/// </summary>
public sealed class LauncherActionFeedbackChannels
{
    public ObservableActionState Refresh { get; } = new();

    public ObservableActionState Mod { get; } = new();

    public ObservableActionState Launch { get; } = new();

    public ObservableActionState LauncherUpdate { get; } = new();

    public bool CanStartModMaintenance(bool externallyAvailable, bool conflictingWork) =>
        externallyAvailable
        && !conflictingWork
        && Mod.IsCommandAvailable
        && !Mod.IsWorking;

    public void CompleteModDeployment(ModDeploymentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess)
        {
            Mod.Complete(result.Changed, result.Message);
        }
        else
        {
            Mod.Fail(result.Message);
        }
    }
}

/// <summary>
/// Reusable, textual operation feedback that deliberately keeps command availability
/// separate from the result lifecycle. A working action may remain keyboard focused and
/// command-enabled while <see cref="TryBegin"/> rejects duplicate activation.
/// </summary>
public sealed class ObservableActionState : INotifyPropertyChanged
{
    private ObservableActionStatus status = ObservableActionStatus.Idle;
    private string statusText = string.Empty;
    private bool isCommandAvailable = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableActionStatus Status => status;

    public string StatusText => statusText;

    public string AutomationAnnouncement => statusText;

    public bool HasStatus => !string.IsNullOrWhiteSpace(statusText);

    public bool IsWorking => status == ObservableActionStatus.Working;

    public bool IsCommandAvailable => isCommandAvailable;

    public bool TryBegin(string acceptedMessage)
    {
        if (!isCommandAvailable || IsWorking)
        {
            return false;
        }

        SetStatus(ObservableActionStatus.Working, acceptedMessage);
        return true;
    }

    public void Complete(bool changed, string message) =>
        SetStatus(
            changed ? ObservableActionStatus.CompletedChanged : ObservableActionStatus.CompletedUnchanged,
            message);

    public void Fail(string message) =>
        SetStatus(ObservableActionStatus.Failed, message);

    public void Cancel(string message) =>
        SetStatus(ObservableActionStatus.Idle, message);

    public void SetAvailability(bool available, string unavailableMessage)
    {
        if (isCommandAvailable != available)
        {
            isCommandAvailable = available;
            OnPropertyChanged(nameof(IsCommandAvailable));
        }

        if (!available && !IsWorking)
        {
            SetStatus(ObservableActionStatus.Unavailable, unavailableMessage);
        }
        else if (available && status == ObservableActionStatus.Unavailable)
        {
            SetStatusCore(ObservableActionStatus.Idle, string.Empty);
        }
    }

    private void SetStatus(ObservableActionStatus nextStatus, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message, nameof(message));
        SetStatusCore(nextStatus, message);
    }

    private void SetStatusCore(ObservableActionStatus nextStatus, string message)
    {
        var wasWorking = IsWorking;
        var hadStatus = HasStatus;
        var statusChanged = status != nextStatus;
        var textChanged = !string.Equals(statusText, message, StringComparison.Ordinal);
        status = nextStatus;
        statusText = message;
        if (statusChanged)
        {
            OnPropertyChanged(nameof(Status));
        }
        if (wasWorking != IsWorking)
        {
            OnPropertyChanged(nameof(IsWorking));
        }
        if (textChanged)
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(AutomationAnnouncement));
        }
        if (hadStatus != HasStatus)
        {
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Executes one observable action at a time. CanExecute represents external command
/// availability only; the action state rejects repeated activation while work is active.
/// </summary>
public sealed class ObservableActionCommand : ICommand
{
    private readonly ObservableActionState state;
    private readonly string acceptedMessage;
    private readonly Func<Task<ObservableActionResult>> execute;
    private readonly Func<Exception, string>? failureMessage;

    public ObservableActionCommand(
        ObservableActionState state,
        string acceptedMessage,
        Func<Task<ObservableActionResult>> execute,
        Func<Exception, string>? failureMessage = null)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedMessage);
        this.acceptedMessage = acceptedMessage;
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.failureMessage = failureMessage;
        state.PropertyChanged += State_PropertyChanged;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => state.IsCommandAvailable;

    public async void Execute(object? parameter)
    {
        _ = parameter;
        if (!state.TryBegin(acceptedMessage))
        {
            return;
        }

        try
        {
            var result = await execute();
            if (result.Kind == ObservableActionResultKind.Failed)
            {
                state.Fail(result.Message);
            }
            else
            {
                state.Complete(result.Kind == ObservableActionResultKind.Changed, result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            state.Cancel("The action was canceled.");
        }
        catch (Exception exception)
        {
            state.Fail(failureMessage?.Invoke(exception) ?? $"The action failed: {exception.Message}");
        }
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName == nameof(ObservableActionState.IsCommandAvailable))
        {
            NotifyCanExecuteChanged();
        }
    }
}
