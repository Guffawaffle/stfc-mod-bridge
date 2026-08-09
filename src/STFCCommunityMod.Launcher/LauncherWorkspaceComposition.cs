using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

internal static class LauncherHomeWorkspaceIds
{
    public const string ModBridge = "home.mod-bridge";
}

internal interface ILauncherWorkspace<out TServices> : IDisposable
{
    string Id { get; }

    TServices SharedServices { get; }
}

internal interface ILauncherHomeWorkspace : ILauncherWorkspace<LauncherWorkspaceServices>
{
}

internal sealed class ModBridgeHomeWorkspace(LauncherWorkspaceServices sharedServices) :
    ILauncherHomeWorkspace
{
    public string Id => LauncherHomeWorkspaceIds.ModBridge;

    public LauncherWorkspaceServices SharedServices { get; } =
        sharedServices ?? throw new ArgumentNullException(nameof(sharedServices));

    public void Dispose()
    {
        // The provider session owns the shared services. A Home surface never does.
    }
}

internal sealed class LauncherWorkspaceServices(
    MainWindowViewModel foundation,
    Func<SettingsViewModel> settingsFactory)
    : IDisposable
{
    public MainWindowViewModel Foundation { get; } =
        foundation ?? throw new ArgumentNullException(nameof(foundation));

    public LauncherSettingsWorkspace Settings { get; } = new(settingsFactory);

    public LauncherDiagnosticsWorkspace Diagnostics { get; } = new(foundation);

    public void Dispose()
    {
        Settings.BeginSessionEndInvalidation();
        Foundation.Dispose();
    }
}

internal enum LauncherSettingsInvalidationReason
{
    RuntimeActivationChanged,
    ProviderSessionEnded,
}

internal sealed record LauncherSettingsInvalidatedEventArgs(
    SettingsViewModel Workspace,
    LauncherSettingsInvalidationReason Reason);

internal sealed class LauncherSettingsWorkspace(Func<SettingsViewModel> factory)
{
    private readonly object sync = new();
    private readonly Func<SettingsViewModel> factory =
        factory ?? throw new ArgumentNullException(nameof(factory));
    private SettingsViewModel? current;
    private bool isConstructing;
    private bool isSessionEnded;
    private Task? invalidationTask;

    public event EventHandler<LauncherSettingsInvalidatedEventArgs>? Invalidated;

    public SettingsViewModel? Current
    {
        get
        {
            lock (sync)
            {
                if (isSessionEnded)
                {
                    return null;
                }
                return current;
            }
        }
    }

    public bool HasPendingChanges
    {
        get
        {
            lock (sync)
            {
                return current is not null
                    && (current.HasPendingChanges || current.SyncWorkspace.HasPendingChanges);
            }
        }
    }

    public SettingsViewModel GetOrCreate()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(isSessionEnded, this);
            if (invalidationTask is not null)
            {
                throw new InvalidOperationException(
                    "Settings cannot be composed while the previous workspace is being invalidated.");
            }
            if (current is not null)
            {
                return current;
            }
            if (isConstructing)
            {
                throw new InvalidOperationException(
                    "The Settings factory cannot reenter shared Settings composition.");
            }

            isConstructing = true;
            try
            {
                return current = factory()
                    ?? throw new InvalidOperationException("The Settings factory returned null.");
            }
            finally
            {
                isConstructing = false;
            }
        }
    }

    public Task InvalidateAsync(LauncherSettingsInvalidationReason reason)
    {
        SettingsViewModel? invalidated;
        TaskCompletionSource completion;
        lock (sync)
        {
            if (reason == LauncherSettingsInvalidationReason.ProviderSessionEnded)
            {
                isSessionEnded = true;
            }
            if (isConstructing)
            {
                throw new InvalidOperationException(
                    "Settings cannot be invalidated while its shared workspace is being composed.");
            }
            if (invalidationTask is not null)
            {
                return invalidationTask;
            }
            invalidated = current;
            if (invalidated is null)
            {
                return Task.CompletedTask;
            }
            current = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            invalidationTask = completion.Task;
        }
        _ = CompleteInvalidationAsync(invalidated, reason, completion);
        return completion.Task;
    }

    public void BeginSessionEndInvalidation()
    {
        var invalidation = InvalidateAsync(LauncherSettingsInvalidationReason.ProviderSessionEnded);
        _ = invalidation.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task CompleteInvalidationAsync(
        SettingsViewModel invalidated,
        LauncherSettingsInvalidationReason reason,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await invalidated.InvalidateAsync();
            Invalidated?.Invoke(this, new(invalidated, reason));
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (sync)
            {
                invalidationTask = null;
            }
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
}

internal sealed class LauncherDiagnosticsWorkspace(MainWindowViewModel foundation)
{
    private readonly MainWindowViewModel foundation =
        foundation ?? throw new ArgumentNullException(nameof(foundation));

    public LauncherDiagnosticPreview BuildPreview() => foundation.BuildDiagnosticPreview();

    public bool CanRetryCandidateRecovery => foundation.CanRetryCandidateRecovery;

    public Task<ReviewedCandidateRecoveryResult?> RetryCandidateRecoveryAsync(
        CancellationToken cancellationToken = default) =>
        foundation.RetryCandidateRecoveryAsync(cancellationToken);
}

internal sealed record LauncherWorkspaceRegistration<TServices, TWorkspace>
    where TWorkspace : class, ILauncherWorkspace<TServices>
{
    public LauncherWorkspaceRegistration(
        string id,
        string? requiredFeatureId,
        string? requiredImplementationId,
        Func<TServices, TWorkspace> factory)
    {
        RequireContractId(id, nameof(id));
        if ((requiredFeatureId is null) != (requiredImplementationId is null))
        {
            throw new ArgumentException(
                "A workspace activation gate requires both a feature and implementation ID.");
        }
        if (requiredFeatureId is not null)
        {
            RequireContractId(requiredFeatureId, nameof(requiredFeatureId));
            RequireContractId(requiredImplementationId!, nameof(requiredImplementationId));
        }

        Id = id;
        RequiredFeatureId = requiredFeatureId;
        RequiredImplementationId = requiredImplementationId;
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public string Id { get; }

    public string? RequiredFeatureId { get; }

    public string? RequiredImplementationId { get; }

    public Func<TServices, TWorkspace> Factory { get; }

    private static void RequireContractId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 160
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '-' or '_' or ':' or '/')))
        {
            throw new ArgumentException(
                "Workspace contract IDs must be non-empty, bounded, and use safe characters.",
                parameterName);
        }
    }
}

internal enum LauncherWorkspaceActivationState
{
    Active,
    UnknownWorkspace,
    FeatureUnavailable,
    ImplementationMismatch,
}

internal sealed record LauncherWorkspaceActivation<TWorkspace>(
    LauncherWorkspaceActivationState State,
    TWorkspace? Workspace,
    LauncherFeatureDecision? FeatureDecision)
    where TWorkspace : class, IDisposable
{
    public bool IsActive => State == LauncherWorkspaceActivationState.Active;
}

internal sealed class LauncherWorkspaceRegistry<TServices, TWorkspace> : IDisposable
    where TServices : class
    where TWorkspace : class, ILauncherWorkspace<TServices>
{
    private readonly object sync = new();
    private readonly TServices sharedServices;
    private readonly Func<LauncherActivationPlan> activationPlanProvider;
    private readonly Dictionary<string, LauncherWorkspaceRegistration<TServices, TWorkspace>> registrations;
    private readonly Dictionary<string, TWorkspace> activated = new(StringComparer.Ordinal);
    private string? constructingWorkspaceId;
    private bool isDisposed;

    public LauncherWorkspaceRegistry(
        TServices sharedServices,
        Func<LauncherActivationPlan> activationPlanProvider,
        IEnumerable<LauncherWorkspaceRegistration<TServices, TWorkspace>> registrations)
    {
        this.sharedServices = sharedServices ?? throw new ArgumentNullException(nameof(sharedServices));
        this.activationPlanProvider =
            activationPlanProvider ?? throw new ArgumentNullException(nameof(activationPlanProvider));
        ArgumentNullException.ThrowIfNull(registrations);
        var indexed = new Dictionary<string, LauncherWorkspaceRegistration<TServices, TWorkspace>>(
            StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!indexed.TryAdd(registration.Id, registration))
            {
                throw new ArgumentException(
                    $"Workspace '{registration.Id}' is registered more than once.",
                    nameof(registrations));
            }
        }
        this.registrations = indexed;
    }

    public LauncherWorkspaceActivation<TWorkspace> Activate(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (constructingWorkspaceId is not null)
            {
                throw new InvalidOperationException(
                    $"Workspace factory '{constructingWorkspaceId}' cannot activate another workspace while it is being constructed.");
            }
            if (!registrations.TryGetValue(workspaceId, out var registration))
            {
                return new(LauncherWorkspaceActivationState.UnknownWorkspace, null, null);
            }

            LauncherFeatureDecision? decision = null;
            if (registration.RequiredFeatureId is not null)
            {
                var eligibility = Evaluate(registration, GetActivationPlan());
                decision = eligibility.Decision;
                if (eligibility.State != LauncherWorkspaceActivationState.Active)
                {
                    return new(eligibility.State, null, decision);
                }
            }

            if (!activated.TryGetValue(registration.Id, out var workspace))
            {
                constructingWorkspaceId = registration.Id;
                try
                {
                    workspace = registration.Factory(sharedServices)
                        ?? throw new InvalidOperationException(
                            $"Workspace factory '{registration.Id}' returned null.");
                }
                finally
                {
                    constructingWorkspaceId = null;
                }
                if (isDisposed)
                {
                    workspace.Dispose();
                    throw new ObjectDisposedException(
                        GetType().FullName,
                        $"Workspace registry was disposed while '{registration.Id}' was being constructed.");
                }
                if (!string.Equals(workspace.Id, registration.Id, StringComparison.Ordinal)
                    || !ReferenceEquals(workspace.SharedServices, sharedServices))
                {
                    workspace.Dispose();
                    throw new InvalidOperationException(
                        $"Workspace factory '{registration.Id}' did not retain its registered ID and shared services.");
                }
                activated.Add(registration.Id, workspace);
            }
            return new(LauncherWorkspaceActivationState.Active, workspace, decision);
        }
    }

    public int RevalidateActivated()
    {
        List<TWorkspace> retired;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (constructingWorkspaceId is not null)
            {
                throw new InvalidOperationException(
                    $"Workspace factory '{constructingWorkspaceId}' cannot revalidate workspaces while it is being constructed.");
            }
            var gated = activated
                .Where(pair => registrations[pair.Key].RequiredFeatureId is not null)
                .ToArray();
            if (gated.Length == 0)
            {
                return 0;
            }

            var plan = GetActivationPlan();
            retired = [];
            foreach (var pair in gated)
            {
                if (Evaluate(registrations[pair.Key], plan).State
                    == LauncherWorkspaceActivationState.Active)
                {
                    continue;
                }
                activated.Remove(pair.Key);
                if (!activated.Values.Any(workspace => ReferenceEquals(workspace, pair.Value))
                    && !retired.Any(workspace => ReferenceEquals(workspace, pair.Value)))
                {
                    retired.Add(pair.Value);
                }
            }
        }

        foreach (var workspace in retired)
        {
            workspace.Dispose();
        }
        return retired.Count;
    }

    public void Dispose()
    {
        TWorkspace[] workspaces;
        lock (sync)
        {
            if (isDisposed)
            {
                return;
            }
            isDisposed = true;
            workspaces = new HashSet<TWorkspace>(
                activated.Values,
                ReferenceEqualityComparer.Instance).ToArray();
            activated.Clear();
        }
        foreach (var workspace in workspaces)
        {
            workspace.Dispose();
        }
    }

    private LauncherActivationPlan GetActivationPlan() =>
        activationPlanProvider()
        ?? throw new InvalidOperationException("The runtime activation plan is unavailable.");

    private static (LauncherWorkspaceActivationState State, LauncherFeatureDecision? Decision) Evaluate(
        LauncherWorkspaceRegistration<TServices, TWorkspace> registration,
        LauncherActivationPlan plan)
    {
        if (!plan.Features.TryGetValue(registration.RequiredFeatureId!, out var decision)
            || !decision.IsActive)
        {
            return (LauncherWorkspaceActivationState.FeatureUnavailable, decision);
        }
        return string.Equals(
            decision.SelectedImplementation,
            registration.RequiredImplementationId,
            StringComparison.Ordinal)
                ? (LauncherWorkspaceActivationState.Active, decision)
                : (LauncherWorkspaceActivationState.ImplementationMismatch, decision);
    }
}

internal sealed class LauncherApplicationComposition : IDisposable
{
    private readonly LauncherWorkspaceRegistry<LauncherWorkspaceServices, ILauncherHomeWorkspace> homeWorkspaces;

    public LauncherApplicationComposition(
        LauncherWorkspaceServices sharedServices,
        Func<LauncherActivationPlan> activationPlanProvider,
        IEnumerable<LauncherWorkspaceRegistration<LauncherWorkspaceServices, ILauncherHomeWorkspace>>?
            optionalHomeWorkspaces = null)
    {
        SharedServices = sharedServices ?? throw new ArgumentNullException(nameof(sharedServices));
        var registrations = new List<LauncherWorkspaceRegistration<LauncherWorkspaceServices, ILauncherHomeWorkspace>>
        {
            new(
                LauncherHomeWorkspaceIds.ModBridge,
                null,
                null,
                services => new ModBridgeHomeWorkspace(services)),
        };
        if (optionalHomeWorkspaces is not null)
        {
            registrations.AddRange(optionalHomeWorkspaces);
        }
        homeWorkspaces = new(SharedServices, activationPlanProvider, registrations);
    }

    public LauncherWorkspaceServices SharedServices { get; }

    public LauncherWorkspaceActivation<ILauncherHomeWorkspace> ActivateHome(string workspaceId) =>
        homeWorkspaces.Activate(workspaceId);

    public void RevalidateHomes() => homeWorkspaces.RevalidateActivated();

    public void Dispose()
    {
        homeWorkspaces.Dispose();
        SharedServices.Dispose();
    }
}
