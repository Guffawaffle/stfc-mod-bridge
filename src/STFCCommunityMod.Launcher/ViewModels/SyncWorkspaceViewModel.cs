using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public enum SyncBooleanOverrideChoice
{
    UseGlobal,
    Enabled,
    Disabled,
}

public enum SyncProxyOverrideChoice
{
    UseGlobal,
    NoProxy,
    Custom,
}

public sealed record SyncOverrideChoice(SyncBooleanOverrideChoice Value, string Label);

public sealed record SyncFleetRuntimeModeOption(string Value, string Label);

public sealed class SyncWorkspaceViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<SyncOverrideChoice> OverrideChoices =
    [
        new(SyncBooleanOverrideChoice.UseGlobal, "Use inherited"),
        new(SyncBooleanOverrideChoice.Enabled, "On"),
        new(SyncBooleanOverrideChoice.Disabled, "Off"),
    ];

    private readonly Func<string?> configurationPathProvider;
    private readonly Func<bool> hasSiblingPendingChanges;
    private readonly IConfigurationRepository repository;
    private readonly Func<ConfigurationWorkspace?> configurationWorkspaceProvider;
    private readonly SettingsActionCommand discardCommand;
    private readonly AsyncSettingsActionCommand saveCommand;
    private readonly object lifecycleSync = new();
    private readonly HashSet<string> customProxyEditors = new(StringComparer.Ordinal);
    private SyncTopologyEditSession? workspace;
    private Task? activeSave;
    private Task? invalidationTask;
    private string operationStatus = string.Empty;
    private bool migrateLegacyRoot;
    private SyncWorkspaceTabViewModel? selectedTab;
    private SyncAddDestinationWizardViewModel? addWizard;
    private bool restartRequired;
    private bool isInvalidating;
    private bool isInvalidated;

    public SyncWorkspaceViewModel(
        Func<string?> configurationPathProvider,
        IConfigurationRepository repository,
        Func<bool>? hasSiblingPendingChanges = null,
        Func<ConfigurationWorkspace?>? configurationWorkspaceProvider = null)
    {
        this.configurationPathProvider =
            configurationPathProvider ?? throw new ArgumentNullException(nameof(configurationPathProvider));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.hasSiblingPendingChanges = hasSiblingPendingChanges ?? (() => false);
        this.configurationWorkspaceProvider = configurationWorkspaceProvider ?? (() => null);
        discardCommand = new(Discard, () => !isInvalidating && !isInvalidated && HasPendingChanges);
        saveCommand = new(SaveAsync, () => CanSave);
        OpenAddDestinationCommand = new SettingsActionCommand(OpenAddDestination, () => IsConfigurationReady);
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? StateChanged;

    public event EventHandler? Committed;

    public ObservableCollection<SyncTargetCardViewModel> Targets { get; } = [];

    public ObservableCollection<SyncGlobalFeedViewModel> GlobalFeeds { get; } = [];

    public ObservableCollection<SyncWorkspaceTabViewModel> Tabs { get; } = [];

    internal static IReadOnlyList<SyncOverrideChoice> BooleanOverrideChoices => OverrideChoices;

    public ICommand OpenAddDestinationCommand { get; }

    public ICommand DiscardCommand => discardCommand;

    public ICommand SaveCommand => saveCommand;

    public bool IsConfigurationReady => !isInvalidating && !isInvalidated && workspace is not null;

    public bool HasPendingChanges => workspace?.HasPendingChanges ?? false;

    public bool IsStale => workspace?.IsStale ?? false;

    public SyncWorkspaceTabViewModel? SelectedTab
    {
        get => selectedTab;
        set
        {
            if (SetField(ref selectedTab, value))
            {
                foreach (var tab in Tabs)
                {
                    tab.IsSelected = ReferenceEquals(tab, value);
                }
                OnPropertyChanged(nameof(IsGlobalTabSelected));
                OnPropertyChanged(nameof(SelectedDestination));
            }
        }
    }

    public bool IsGlobalTabSelected => SelectedTab?.IsGlobal ?? true;

    public SyncTargetCardViewModel? SelectedDestination => SelectedTab?.Destination;

    public SyncAddDestinationWizardViewModel? AddWizard
    {
        get => addWizard;
        private set
        {
            if (SetField(ref addWizard, value))
            {
                OnPropertyChanged(nameof(IsAddWizardOpen));
            }
        }
    }

    public bool IsAddWizardOpen => AddWizard is not null;

    public bool RestartRequired
    {
        get => restartRequired;
        private set => SetField(ref restartRequired, value);
    }

    public bool IsCommittable =>
        workspace is not null
        && workspace.Desired.Resolve().IsCommittable
        && workspace.PreparePlan(MigrateLegacyRoot).IsValid;

    public bool HasLegacyRootTarget => workspace?.HasLegacyRootTarget ?? false;

    public bool MigrateLegacyRoot
    {
        get => migrateLegacyRoot;
        set
        {
            if (isInvalidating || isInvalidated)
            {
                return;
            }
            if (SetField(ref migrateLegacyRoot, value))
            {
                Rebuild();
            }
        }
    }

    public bool CanSave =>
        IsConfigurationReady
        && HasPendingChanges
        && IsCommittable
        && !IsStale
        && !hasSiblingPendingChanges();

    public string ConfigurationStatus => IsConfigurationReady
        ? "Changes are staged until Save. The running game keeps its startup topology until restart."
        : "Select a game folder with a supported configuration to set up Data Sync.";

    public string PendingChangesText => HasPendingChanges ? "Unsaved data sync changes" : "No unsaved data sync changes";

    public string SaveAvailability => IsStale
        ? "The TOML changed outside Mod Bridge. Reload before saving."
        : hasSiblingPendingChanges()
            ? "Save or discard the pending non-sync settings before saving Data Sync."
        : !IsCommittable
            ? $"Fix the target validation errors before saving. {BlockingValidationSummary}"
            : CanSave
                ? "Save all staged Data Sync changes atomically."
                : "Stage a valid Data Sync change before saving.";

    private string BlockingValidationSummary => workspace?.Desired.Resolve().Diagnostics
        .FirstOrDefault(item => item.Severity == SyncTopologyDiagnosticSeverity.Error)?.Message
        ?? string.Empty;

    public string OperationStatus
    {
        get => operationStatus;
        private set => SetField(ref operationStatus, value);
    }

    public string GlobalProxy
    {
        get => workspace?.Desired.GlobalDefaults.Proxy ?? string.Empty;
        set
        {
            if (workspace is null || value == GlobalProxy)
            {
                return;
            }

            Stage(workspace.Desired.WithGlobalDefaults(workspace.Desired.GlobalDefaults.WithProxy(value ?? string.Empty)));
        }
    }

    public bool GlobalVerifySsl
    {
        get => workspace?.Desired.GlobalDefaults.VerifySsl ?? true;
        set
        {
            if (workspace is null || value == GlobalVerifySsl)
            {
                return;
            }

            Stage(workspace.Desired.WithGlobalDefaults(workspace.Desired.GlobalDefaults.WithVerifySsl(value)));
        }
    }

    public bool GlobalUnsafeTls
    {
        get => workspace?.Desired.GlobalDefaults.AllowUnsafeTlsWithoutCertificateValidation ?? false;
        set
        {
            if (workspace is null || value == GlobalUnsafeTls)
            {
                return;
            }

            Stage(workspace.Desired.WithGlobalDefaults(workspace.Desired.GlobalDefaults.WithUnsafeTls(value)));
        }
    }

    public void Reload()
    {
        if (isInvalidating || isInvalidated || HasPendingChanges)
        {
            return;
        }

        workspace = null;
        if (configurationWorkspaceProvider() is { } configurationWorkspace)
        {
            var sharedLoad = configurationWorkspace.CreateSyncTopologyEditSession(out workspace);
            OperationStatus = sharedLoad.IsValid && workspace is not null
                ? sharedLoad.Diagnostics.FirstOrDefault(item => item.Severity == SyncTopologyDiagnosticSeverity.Error)?.Message
                    ?? string.Empty
                : $"Data Sync is unavailable because the topology could not be loaded: {sharedLoad.Error?.Message}";
            Rebuild();
            return;
        }

        var read = repository.Read(configurationPathProvider());
        if (!read.IsSuccess || read.Snapshot is null)
        {
            OperationStatus = read.State == ConfigurationRepositoryReadState.Invalid
                ? $"Data Sync is unavailable because the TOML is unsafe to edit: {read.ValidationError?.Message}"
                : read.State == ConfigurationRepositoryReadState.IoFailure
                    ? $"Data Sync is unavailable: {read.Error}"
                    : string.Empty;
            Rebuild();
            return;
        }

        var load = SyncTopologyEditSession.Load(read.Snapshot, out workspace);
        if (!load.IsValid || workspace is null)
        {
            OperationStatus = $"Data Sync is unavailable because the topology could not be loaded: {load.Error?.Message}";
        }
        else
        {
            OperationStatus = load.Diagnostics.FirstOrDefault(item => item.Severity == SyncTopologyDiagnosticSeverity.Error)?.Message
                ?? string.Empty;
        }

        Rebuild();
    }

    internal Task InvalidateAsync()
    {
        TaskCompletionSource completion;
        Task? save;
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
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            invalidationTask = completion.Task;
            save = activeSave;
        }
        Rebuild();
        _ = CompleteInvalidationAsync(save, completion);
        return completion.Task;
    }

    internal SyncTargetDraft? GetTarget(string name) =>
        workspace?.Desired.Targets.GetValueOrDefault(name);

    internal SyncResolvedTarget? GetResolvedTarget(string name) =>
        workspace?.Desired.Resolve().Targets.FirstOrDefault(target => target.Name == name);

    internal bool GetGlobalFeed(SyncDataKind kind) =>
        workspace?.Desired.GlobalDefaults.DataKinds.GetValueOrDefault(kind) ?? false;

    internal string GetGlobalProxy() => workspace?.Desired.GlobalDefaults.Proxy ?? string.Empty;

    internal bool GetGlobalVerifySsl() => workspace?.Desired.GlobalDefaults.VerifySsl ?? true;

    internal bool GetGlobalUnsafeTls() =>
        workspace?.Desired.GlobalDefaults.AllowUnsafeTlsWithoutCertificateValidation ?? false;

    internal bool IsCustomProxyEditor(string name) => customProxyEditors.Contains(name);

    internal void SetCustomProxyEditor(string name, bool enabled)
    {
        if (enabled)
        {
            customProxyEditors.Add(name);
        }
        else
        {
            customProxyEditors.Remove(name);
        }
    }

    internal IReadOnlyList<SyncTopologyDiagnostic> GetDiagnostics(string name) =>
        workspace?.Desired.Resolve().Diagnostics.Where(item => item.TargetName == name).ToArray() ?? [];

    internal void UpdateTarget(string name, Func<SyncTargetDraft, SyncTargetDraft> update)
    {
        if (workspace is null
            || workspace.Desired.Targets.GetValueOrDefault(name)?.UsesNamedPipe == true)
        {
            return;
        }

        Apply(workspace.Desired.UpdateTarget(name, update));
    }

    internal void RemoveTarget(string name)
    {
        if (workspace is not null
            && workspace.Desired.Targets.GetValueOrDefault(name)?.UsesNamedPipe != true)
        {
            Apply(workspace.Desired.RemoveTarget(name));
        }
    }

    internal void DuplicateTarget(string name)
    {
        if (workspace is null)
        {
            return;
        }

        var baseName = name + "-copy";
        var candidate = baseName;
        for (var suffix = 2; workspace.Desired.Targets.ContainsKey(candidate); ++suffix)
        {
            candidate = $"{baseName}-{suffix}";
        }

        var duplicate = workspace.Desired.DuplicateTarget(name, candidate);
        if (duplicate.Succeeded)
        {
            duplicate = duplicate.Topology.SetTargetEnabled(candidate, true);
        }
        Apply(duplicate);
        SelectTab("destination:" + candidate);
    }

    internal void SetGlobalFeed(SyncDataKind kind, bool enabled)
    {
        if (workspace is null)
        {
            return;
        }

        Stage(workspace.Desired.WithGlobalDefaults(workspace.Desired.GlobalDefaults.WithDataKind(kind, enabled)));
    }

    private void OpenAddDestination()
    {
        if (workspace is null)
        {
            return;
        }

        AddWizard = new SyncAddDestinationWizardViewModel(this, workspace.Desired);
    }

    internal void CancelAddDestination() => AddWizard = null;

    internal void CompleteAddDestination(SyncAddDestinationWizardViewModel wizard)
    {
        if (workspace is null || !ReferenceEquals(AddWizard, wizard))
        {
            return;
        }

        // Presets only prefill ordinary destination fields. They never create a distinct runtime type
        // or lock the user to the preset's suggested identity.
        var transition = workspace.Desired.AddTarget(wizard.Identity, wizard.Kind);
        if (!transition.Succeeded)
        {
            wizard.SetError(transition.Diagnostic?.Message ?? "The destination could not be staged.");
            return;
        }

        var identity = transition.Topology.Targets.Keys.Except(workspace.Desired.Targets.Keys, StringComparer.Ordinal).Single();
        var desired = transition.Topology.SetTargetEnabled(identity, true).Topology;
        var update = desired.UpdateTarget(
            identity,
            target =>
            {
                var changed = target.WithConnection(
                    wizard.Endpoint,
                    string.IsNullOrWhiteSpace(wizard.Token)
                        ? target.Token
                        : SyncSecret.FromPlainText(wizard.Token));
                foreach (var feed in wizard.Feeds)
                {
                    changed = changed.WithDataOverride(feed.Kind, SyncOverride.Explicit(feed.IsEnabled));
                }

                if (wizard.PresetId is not null)
                {
                    var documentedFeeds = wizard.Feeds.Select(feed => feed.Kind).ToHashSet();
                    foreach (var kind in SyncTargetTypeCatalog.Get(wizard.Kind).SupportedDataKinds
                                 .Where(kind => !documentedFeeds.Contains(kind)))
                    {
                        changed = changed.WithDataOverride(kind, SyncOverride.Explicit(false));
                    }
                }

                return changed;
            });
        if (!update.Succeeded)
        {
            wizard.SetError(update.Diagnostic?.Message ?? "The destination could not be configured.");
            return;
        }

        var candidateErrors = update.Topology.Resolve().Diagnostics
            .Where(item => item.TargetName == identity && item.Severity == SyncTopologyDiagnosticSeverity.Error)
            .ToArray();
        if (candidateErrors.Length > 0)
        {
            wizard.SetError(string.Join(" ", candidateErrors.Select(item => item.Message)));
            return;
        }

        Stage(update.Topology);
        AddWizard = null;
        SelectTab("destination:" + identity);
    }

    private void Apply(SyncTopologyTransitionResult transition)
    {
        if (!transition.Succeeded)
        {
            OperationStatus = transition.Diagnostic?.Message ?? "The sync change could not be staged.";
            return;
        }

        Stage(transition.Topology);
    }

    private void Stage(SyncDesiredTopology desired)
    {
        if (isInvalidating || isInvalidated || workspace is null)
        {
            return;
        }
        workspace.Stage(desired);
        OperationStatus = string.Empty;
        Rebuild();
    }

    private void Discard()
    {
        workspace?.Discard();
        customProxyEditors.Clear();
        OperationStatus = "Unsaved Data Sync changes discarded.";
        Rebuild();
    }

    internal Task SaveAsync()
    {
        TaskCompletionSource completion;
        SyncTopologyEditSession editingSession;
        ConfigurationWorkspace? configurationWorkspace;
        lock (lifecycleSync)
        {
            if (activeSave is not null)
            {
                return activeSave;
            }
            if (isInvalidating || isInvalidated || workspace is null)
            {
                return Task.CompletedTask;
            }
            editingSession = workspace;
            configurationWorkspace = configurationWorkspaceProvider();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            activeSave = completion.Task;
        }
        _ = CompleteSaveAsync(editingSession, configurationWorkspace, completion);
        return completion.Task;
    }

    private async Task SaveCoreAsync(
        SyncTopologyEditSession editingSession,
        ConfigurationWorkspace? configurationWorkspace)
    {
        OperationStatus = "Saving Data Sync…";
        var result = configurationWorkspace is null
            ? await editingSession.CommitAsync(MigrateLegacyRoot)
            : await configurationWorkspace.CommitSyncAsync(editingSession, MigrateLegacyRoot);
        if (isInvalidating || isInvalidated)
        {
            return;
        }
        OperationStatus = result.State switch
        {
            AtomicTomlWriteState.Succeeded => "Data Sync saved. Restart the game to activate the new topology.",
            AtomicTomlWriteState.NoChange => "No Data Sync changes were needed.",
            AtomicTomlWriteState.Conflict => "The TOML changed outside Mod Bridge. External edits were preserved; reload before saving.",
            AtomicTomlWriteState.Busy => "Another Mod Bridge change is still in progress. Nothing was written; try saving Data Sync again when it finishes.",
            AtomicTomlWriteState.Invalid => $"Nothing was written: {result.ValidationError?.Message ?? FirstPlanDiagnostic(result)}",
            _ => $"Data Sync could not be saved: {result.Error}",
        };
        if (result.State is AtomicTomlWriteState.Succeeded or AtomicTomlWriteState.NoChange)
        {
            migrateLegacyRoot = false;
            RestartRequired = result.State == AtomicTomlWriteState.Succeeded;
            Committed?.Invoke(this, EventArgs.Empty);
        }
        Rebuild();
    }

    private async Task CompleteSaveAsync(
        SyncTopologyEditSession editingSession,
        ConfigurationWorkspace? configurationWorkspace,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await SaveCoreAsync(editingSession, configurationWorkspace);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (lifecycleSync)
            {
                activeSave = null;
            }
        }
        if (failure is not null && !isInvalidating && !isInvalidated)
        {
            OperationStatus = $"Data Sync could not be saved: {failure.Message}";
            Rebuild();
        }
        completion.SetResult();
    }

    private async Task CompleteInvalidationAsync(Task? save, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await (save ?? Task.CompletedTask);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            workspace?.Discard();
            workspace = null;
            AddWizard = null;
            customProxyEditors.Clear();
            isInvalidated = true;
            isInvalidating = false;
            OperationStatus = "This Data Sync workspace was replaced by newer runtime or provider evidence.";
            Rebuild();
            lock (lifecycleSync)
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

    private void Rebuild()
    {
        var selectedId = SelectedTab?.Id ?? "global";
        Targets.Clear();
        GlobalFeeds.Clear();
        Tabs.Clear();
        customProxyEditors.RemoveWhere(name => workspace is null || !workspace.Desired.Targets.ContainsKey(name));
        Tabs.Add(SyncWorkspaceTabViewModel.Global());
        if (workspace is not null)
        {
            foreach (var kind in SyncTargetTypeCatalog.Feeds.Keys
                         .Where(kind => SyncTargetTypeCatalog.All.Values.Any(type =>
                             type.ExposurePolicy == SyncTargetExposurePolicy.Creatable
                             && type.InheritsGlobalSync
                             && type.SupportedDataKinds.Contains(kind)))
                         .OrderBy(kind => SyncTargetTypeCatalog.GetFeed(kind).DisplayName, StringComparer.Ordinal))
            {
                GlobalFeeds.Add(new(this, kind));
            }

            foreach (var target in workspace.Desired.Targets.Values.OrderBy(target => target.Name, StringComparer.Ordinal))
            {
                if (SyncTargetTypeCatalog.Get(target.Kind).ExposurePolicy == SyncTargetExposurePolicy.Hidden)
                {
                    continue;
                }

                var destination = new SyncTargetCardViewModel(this, target.Name);
                Targets.Add(destination);
                Tabs.Add(SyncWorkspaceTabViewModel.ForDestination(destination));
            }
        }

        SelectedTab = Tabs.FirstOrDefault(tab => tab.Id == selectedId) ?? Tabs[0];

        OnPropertyChanged(string.Empty);
        discardCommand.RaiseCanExecuteChanged();
        saveCommand.RaiseCanExecuteChanged();
        (OpenAddDestinationCommand as SettingsActionCommand)?.RaiseCanExecuteChanged();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectTab(string id) => SelectedTab = Tabs.FirstOrDefault(tab => tab.Id == id) ?? SelectedTab;

    private static string? FirstPlanDiagnostic(SyncTopologyPersistenceCommitResult result) =>
        result.Plan is { Diagnostics.Count: > 0 } plan ? plan.Diagnostics[0].Message : null;

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
        PropertyChanged?.Invoke(this, new(propertyName));
}

public sealed class SyncGlobalFeedViewModel(SyncWorkspaceViewModel owner, SyncDataKind kind) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Label => SyncTargetTypeCatalog.GetFeed(kind).DisplayName;
    public string Description => SyncTargetTypeCatalog.GetFeed(kind).Description;
    public bool IsEnabled
    {
        get => owner.IsConfigurationReady && owner.GetGlobalFeed(kind);
        set
        {
            owner.SetGlobalFeed(kind, value);
            PropertyChanged?.Invoke(this, new(nameof(IsEnabled)));
        }
    }
}

public sealed class SyncTargetCardViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<SyncFleetRuntimeModeOption> FleetRuntimeModes =
    [
        new(string.Empty, "Use target default (Normal)"),
        new("normal", "Normal"),
        new("request_only", "Requests only"),
        new("snapshot_only", "Snapshots only"),
        new("enqueue_no_transport", "Queue without transport"),
    ];

    private readonly SyncWorkspaceViewModel owner;
    private readonly string name;
    private string replacementToken = string.Empty;

    internal SyncTargetCardViewModel(SyncWorkspaceViewModel owner, string name)
    {
        this.owner = owner;
        this.name = name;
        RemoveCommand = new SettingsActionCommand(
            () => owner.RemoveTarget(name),
            () => !IsLauncherManagedLocalIpc);
        DuplicateCommand = new SettingsActionCommand(
            () => owner.DuplicateTarget(name),
            () => Definition.MaximumInstances > 1);
        RemoveUnsupportedCapabilitiesCommand = new SettingsActionCommand(
            RemoveUnsupportedCapabilities,
            () => HasUnsupportedCapabilities);
        ClearTokenCommand = new SettingsActionCommand(ClearToken, () => !IsLauncherManagedLocalIpc);
        ReplaceTokenCommand = new SettingsActionCommand(
            ReplaceToken,
            () => !IsLauncherManagedLocalIpc && !string.IsNullOrWhiteSpace(replacementToken));
        var definition = SyncTargetTypeCatalog.Get(Draft.Kind);
        var preset = SyncTargetTypeCatalog.FindPresetByUrl(Draft.Url);
        var supportedFeeds = (preset?.TargetKind == Draft.Kind
                ? preset.SupportedDataKinds
                : definition.SupportedDataKinds)
            .Where(definition.SupportedDataKinds.Contains);
        Feeds = supportedFeeds
            .OrderBy(kind => SyncTargetTypeCatalog.GetFeed(kind).DisplayName, StringComparer.Ordinal)
            .Select(kind => new SyncTargetFeedViewModel(owner, name, kind))
            .ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private SyncTargetDraft Draft => owner.GetTarget(name)!;
    public SyncTargetTypeDefinition Definition => SyncTargetTypeCatalog.Get(Draft.Kind);
    public string Name => name;
    public string KindLabel => Definition.Kind == SyncTargetKind.LocalSidecar ? Definition.DisplayName : string.Empty;
    public bool ShowKindLabel => !string.IsNullOrEmpty(KindLabel);
    public string AdapterDescription => Definition.Description;
    public string InheritanceLabel => Definition.InheritsGlobalSync ? "Global" : "Default";
    public string WireContract => Definition.WireContract;
    public bool IsLauncherManagedLocalIpc => Draft.UsesNamedPipe;
    public bool ShowEditableTargetControls => !IsLauncherManagedLocalIpc;
    public bool CanDisable => Definition.SupportsDisabledState && !IsLauncherManagedLocalIpc;
    public bool ShowTypeSpecificControls => Definition.SupportsBattlelogEnrichment || Definition.SupportsFleetRuntimeMode;
    public bool ShowBattlelogEnrichment => Definition.SupportsBattlelogEnrichment;
    public bool ShowFleetRuntimeMode => Definition.SupportsFleetRuntimeMode;
    public bool IsEnabled
    {
        get => Draft.Enabled;
        set { owner.UpdateTarget(name, target => target.WithEnabled(value)); NotifyAll(); }
    }
    public string Url
    {
        get => Draft.Url;
        set { owner.UpdateTarget(name, target => target.WithConnection(value ?? string.Empty, target.Token)); NotifyAll(); }
    }
    public string TokenStatus => IsLauncherManagedLocalIpc
        ? "Credential is launcher-managed and cannot be edited here."
        : Draft.Token.IsConfigured ? "Saved token configured" : "No token configured";
    public string ProxyText
    {
        get => Draft.Proxy.IsExplicit ? Draft.Proxy.Value : string.Empty;
        set
        {
            owner.SetCustomProxyEditor(name, true);
            owner.UpdateTarget(name, target => target.WithProxy(SyncOverride.Explicit(value ?? string.Empty)));
            NotifyAll();
        }
    }
    public SyncProxyOverrideChoice ProxyChoice
    {
        get => owner.IsCustomProxyEditor(name)
            ? SyncProxyOverrideChoice.Custom
            : !Draft.Proxy.IsExplicit
            ? SyncProxyOverrideChoice.UseGlobal
            : string.IsNullOrEmpty(Draft.Proxy.Value)
                ? SyncProxyOverrideChoice.NoProxy
                : SyncProxyOverrideChoice.Custom;
        set
        {
            if (value == SyncProxyOverrideChoice.Custom)
            {
                owner.SetCustomProxyEditor(name, true);
                NotifyAll();
                return;
            }

            owner.SetCustomProxyEditor(name, false);
            owner.UpdateTarget(
                name,
                target => value switch
                {
                    SyncProxyOverrideChoice.UseGlobal => target.WithProxy(SyncOverride.Inherited<string>()),
                    SyncProxyOverrideChoice.NoProxy => target.WithProxy(SyncOverride.Explicit(string.Empty)),
                    _ => target,
                });
            NotifyAll();
        }
    }
    public bool IsCustomProxy => ProxyChoice == SyncProxyOverrideChoice.Custom;
    public string ProxySummary
    {
        get
        {
            if (owner.IsCustomProxyEditor(name) && (!Draft.Proxy.IsExplicit || string.IsNullOrEmpty(Draft.Proxy.Value)))
            {
                return "Enter a custom proxy URL.";
            }

            if (!Draft.Proxy.IsExplicit)
            {
                var inherited = SyncTargetTypeCatalog.Get(Draft.Kind).InheritsGlobalSync ? owner.GetGlobalProxy() : string.Empty;
                return $"Inherited: {(string.IsNullOrEmpty(inherited) ? "no proxy" : inherited)}";
            }

            return string.IsNullOrEmpty(Draft.Proxy.Value) ? "Explicitly cleared" : "Target override";
        }
    }
    public SyncBooleanOverrideChoice VerifySslChoice
    {
        get => ToChoice(Draft.VerifySsl);
        set { owner.UpdateTarget(name, target => target.WithVerifySsl(FromChoice(value))); NotifyAll(); }
    }
    public SyncBooleanOverrideChoice UnsafeTlsChoice
    {
        get => ToChoice(Draft.AllowUnsafeTlsWithoutCertificateValidation);
        set { owner.UpdateTarget(name, target => target.WithUnsafeTls(FromChoice(value))); NotifyAll(); }
    }
    public bool EffectiveVerifySsl
    {
        get
        {
            var inherited = Definition.InheritsGlobalSync ? owner.GetGlobalVerifySsl() : true;
            return Draft.VerifySsl.IsExplicit ? Draft.VerifySsl.Value : inherited;
        }
    }
    public SyncBooleanOverrideChoice BattlelogEnrichmentChoice
    {
        get => ToChoice(Draft.BattlelogEnrichment);
        set { owner.UpdateTarget(name, target => target.WithBattlelogEnrichment(FromChoice(value))); NotifyAll(); }
    }
    public string FleetRuntimeModeChoice
    {
        get => Draft.FleetRuntimeMode.IsExplicit ? Draft.FleetRuntimeMode.Value : string.Empty;
        set
        {
            owner.UpdateTarget(
                name,
                target => target.WithFleetRuntimeMode(
                    string.IsNullOrEmpty(value) ? SyncOverride.Inherited<string>() : SyncOverride.Explicit(value)));
            NotifyAll();
        }
    }
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WPF binds this property through each target card instance.")]
    public IReadOnlyList<SyncOverrideChoice> BooleanChoices => SyncWorkspaceViewModel.BooleanOverrideChoices;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WPF binds this property through each target card instance.")]
    public IReadOnlyList<SyncFleetRuntimeModeOption> FleetRuntimeModeChoices => FleetRuntimeModes;
    public string ValidationSummary
    {
        get
        {
            var findings = owner.GetDiagnostics(name)
                .Where(item => item.Severity is SyncTopologyDiagnosticSeverity.Warning or SyncTopologyDiagnosticSeverity.Error)
                .ToArray();
            var summary = findings.Length == 0 ? "Ready" : string.Join(" ", findings.Select(item => item.Message));
            return HasUnsupportedCapabilities
                ? summary + " Open destination actions to remove unsupported settings if you want to clean up this destination."
                : summary;
        }
    }
    public bool NeedsAttention => owner.GetDiagnostics(name).Any(item =>
        item.Severity is SyncTopologyDiagnosticSeverity.Warning or SyncTopologyDiagnosticSeverity.Error);
    public bool HasUnsupportedCapabilities => owner.GetDiagnostics(name).Any(item =>
        item.Code == "SYNC_CAPABILITY_UNSUPPORTED");
    public string ReadinessLabel => NeedsAttention ? "Needs attention" : "Ready";
    public string EffectiveFeeds => string.Join(", ", Feeds.Where(feed => feed.EffectiveEnabled).Select(feed => feed.Label).DefaultIfEmpty("None"));
    public IReadOnlyList<SyncTargetFeedViewModel> Feeds { get; }
    public ICommand RemoveCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand RemoveUnsupportedCapabilitiesCommand { get; }
    public ICommand ClearTokenCommand { get; }
    public ICommand ReplaceTokenCommand { get; }

    public void SetReplacementToken(string value)
    {
        replacementToken = value ?? string.Empty;
        (ReplaceTokenCommand as SettingsActionCommand)?.RaiseCanExecuteChanged();
    }

    private void ReplaceToken()
    {
        owner.UpdateTarget(name, target => target.WithConnection(target.Url, SyncSecret.FromPlainText(replacementToken)));
        replacementToken = string.Empty;
        NotifyAll();
    }

    private void ClearToken()
    {
        owner.UpdateTarget(name, target => target.WithConnection(target.Url, SyncSecret.Missing));
        replacementToken = string.Empty;
        NotifyAll();
    }

    private void RemoveUnsupportedCapabilities()
    {
        var definition = Definition;
        owner.UpdateTarget(
            name,
            target =>
            {
                var changed = target;
                foreach (var kind in target.DataOverrides.Keys
                             .Where(kind => !definition.SupportedDataKinds.Contains(kind))
                             .ToArray())
                {
                    changed = changed.WithDataOverride(kind, SyncOverride.Inherited<bool>());
                }

                if (target.BattlelogEnrichment.IsExplicit && !definition.SupportsBattlelogEnrichment)
                {
                    changed = changed.WithBattlelogEnrichment(SyncOverride.Inherited<bool>());
                }

                if (target.FleetRuntimeMode.IsExplicit && !definition.SupportsFleetRuntimeMode)
                {
                    changed = changed.WithFleetRuntimeMode(SyncOverride.Inherited<string>());
                }

                return changed;
            });
    }

    private void NotifyAll() => PropertyChanged?.Invoke(this, new(string.Empty));

    private static SyncBooleanOverrideChoice ToChoice(SyncOverride<bool> value) =>
        !value.IsExplicit
            ? SyncBooleanOverrideChoice.UseGlobal
            : value.Value ? SyncBooleanOverrideChoice.Enabled : SyncBooleanOverrideChoice.Disabled;

    private static SyncOverride<bool> FromChoice(SyncBooleanOverrideChoice value) => value switch
    {
        SyncBooleanOverrideChoice.Enabled => SyncOverride.Explicit(true),
        SyncBooleanOverrideChoice.Disabled => SyncOverride.Explicit(false),
        _ => SyncOverride.Inherited<bool>(),
    };
}

public sealed class SyncTargetFeedViewModel(
    SyncWorkspaceViewModel owner,
    string targetName,
    SyncDataKind kind) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public SyncDataKind Kind => kind;
    public string Label => SyncTargetTypeCatalog.GetFeed(kind).DisplayName;
    public string InheritanceLabel => SyncTargetTypeCatalog.Get(owner.GetTarget(targetName)!.Kind).InheritsGlobalSync
        ? "Global"
        : "Default";
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WPF binds this property through each feed row instance.")]
    public IReadOnlyList<SyncOverrideChoice> Choices => SyncWorkspaceViewModel.BooleanOverrideChoices;
    public SyncBooleanOverrideChoice Choice
    {
        get
        {
            var target = owner.GetTarget(targetName)!;
            var configured = target.DataOverrides.GetValueOrDefault(kind, SyncOverride.Inherited<bool>());
            return !configured.IsExplicit
                ? SyncBooleanOverrideChoice.UseGlobal
                : configured.Value ? SyncBooleanOverrideChoice.Enabled : SyncBooleanOverrideChoice.Disabled;
        }
        set
        {
            var configured = value switch
            {
                SyncBooleanOverrideChoice.Enabled => SyncOverride.Explicit(true),
                SyncBooleanOverrideChoice.Disabled => SyncOverride.Explicit(false),
                _ => SyncOverride.Inherited<bool>(),
            };
            owner.UpdateTarget(targetName, target => target.WithDataOverride(kind, configured));
            PropertyChanged?.Invoke(this, new(string.Empty));
        }
    }
    public bool EffectiveEnabled
    {
        get
        {
            var target = owner.GetTarget(targetName)!;
            var configured = target.DataOverrides.GetValueOrDefault(kind, SyncOverride.Inherited<bool>());
            var inherited = SyncTargetTypeCatalog.Get(target.Kind).InheritsGlobalSync
                && owner.GetGlobalFeed(kind);
            return configured.IsExplicit ? configured.Value : inherited;
        }
    }
    public string EffectiveSummary => $"Effective: {(EffectiveEnabled ? "On" : "Off")} · {(Choice == SyncBooleanOverrideChoice.UseGlobal ? "inherited" : "target override")}";
}

public sealed class SyncWorkspaceTabViewModel : INotifyPropertyChanged
{
    private bool isSelected;
    private SyncWorkspaceTabViewModel(string id, string displayName, SyncTargetCardViewModel? destination)
    {
        Id = id;
        DisplayName = displayName;
        Destination = destination;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public SyncTargetCardViewModel? Destination { get; }
    public bool IsGlobal => Destination is null;
    public string AdapterLabel => Destination?.KindLabel ?? string.Empty;
    public string StatusLabel => Destination?.ReadinessLabel ?? string.Empty;
    public string SecondaryLabel => string.IsNullOrEmpty(AdapterLabel)
        ? StatusLabel
        : $"{AdapterLabel} · {StatusLabel}";
    public string AutomationName => IsGlobal
        ? "Global Data Sync defaults"
        : string.IsNullOrEmpty(AdapterLabel)
            ? $"{DisplayName}, {StatusLabel}"
            : $"{DisplayName}, {AdapterLabel}, {StatusLabel}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsSelected
    {
        get => isSelected;
        internal set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    public static SyncWorkspaceTabViewModel Global() => new("global", "Global defaults", null);

    public static SyncWorkspaceTabViewModel ForDestination(SyncTargetCardViewModel destination) =>
        new("destination:" + destination.Name, destination.Name, destination);
}

public enum SyncAddDestinationStep
{
    Choose,
    Configure,
    FeedsAndReview,
}

public sealed class SyncAddChoiceViewModel : INotifyPropertyChanged
{
    private bool isSelected;
    public SyncAddChoiceViewModel(SyncTargetTypeDefinition definition, SyncTargetPreset? preset = null)
    {
        Definition = definition;
        Preset = preset;
    }

    public SyncTargetTypeDefinition Definition { get; }
    public SyncTargetPreset? Preset { get; }
    public SyncTargetKind Kind => Definition.Kind;
    public string Title => Preset?.DisplayName ?? "Custom sync";
    public string Eyebrow => Preset is null ? "Custom destination" : "Known preset";
    public string Description => Preset?.Description ?? "Configure an ordinary sync destination from scratch.";
    public string CapabilitySummary => Preset is null
        ? Definition.CapabilitySummary
        : $"{Preset.SupportedDataKinds.Count} documented feeds";
    public bool IsAvailable { get; init; } = true;
    public string Availability => IsAvailable ? string.Empty : "Already configured";
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsSelected
    {
        get => isSelected;
        internal set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }
}

public sealed class SyncWizardFeedViewModel : INotifyPropertyChanged
{
    private bool isEnabled;

    public SyncWizardFeedViewModel(SyncDataKind kind, bool isEnabled)
    {
        Kind = kind;
        this.isEnabled = isEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public SyncDataKind Kind { get; }
    public string Label => SyncTargetTypeCatalog.GetFeed(Kind).DisplayName;
    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled == value)
            {
                return;
            }

            isEnabled = value;
            PropertyChanged?.Invoke(this, new(nameof(IsEnabled)));
        }
    }
}

public sealed class SyncWizardFieldViewModel : INotifyPropertyChanged
{
    private string value;

    public SyncWizardFieldViewModel(SyncConnectionFieldDefinition definition, string value = "")
    {
        Definition = definition;
        this.value = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public SyncConnectionFieldDefinition Definition { get; }
    public string Id => Definition.Id;
    public string DisplayName => Definition.DisplayName;
    public string Description => Definition.Description;
    public bool IsRequired => Definition.IsRequired;
    public bool IsSecret => Definition.IsSecret;
    public bool IsPlainText => !Definition.IsSecret;
    public string Value
    {
        get => value;
        set
        {
            if (this.value == value)
            {
                return;
            }

            this.value = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }
}

public sealed class SyncAddDestinationWizardViewModel : INotifyPropertyChanged
{
    private readonly SyncWorkspaceViewModel owner;
    private readonly SyncDesiredTopology baseline;
    private SyncAddChoiceViewModel? selectedChoice;
    private SyncAddDestinationStep step;
    private string identity = string.Empty;
    private string error = string.Empty;

    internal SyncAddDestinationWizardViewModel(SyncWorkspaceViewModel owner, SyncDesiredTopology baseline)
    {
        this.owner = owner;
        this.baseline = baseline;
        var choices = new List<SyncAddChoiceViewModel>();
        foreach (var definition in SyncTargetTypeCatalog.All.Values
                     .Where(type => type.ExposurePolicy == SyncTargetExposurePolicy.Creatable)
                     .OrderBy(type => type.DisplayName, StringComparer.Ordinal))
        {
            var available = baseline.Targets.Values.Count(target => target.Kind == definition.Kind) < definition.MaximumInstances;
            choices.Add(new(definition) { IsAvailable = available });
            choices.AddRange(SyncTargetTypeCatalog.GetPresets(definition.Kind).Select(
                preset => new SyncAddChoiceViewModel(definition, preset) { IsAvailable = available }));
        }

        Choices = choices;
        BackCommand = new SettingsActionCommand(Back, () => Step != SyncAddDestinationStep.Choose);
        NextCommand = new SettingsActionCommand(Next, CanContinue);
        CancelCommand = new SettingsActionCommand(owner.CancelAddDestination);
        FinishCommand = new SettingsActionCommand(() => owner.CompleteAddDestination(this), CanFinish);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<SyncAddChoiceViewModel> Choices { get; }
    public ObservableCollection<SyncWizardFeedViewModel> Feeds { get; } = [];
    public ObservableCollection<SyncWizardFieldViewModel> Fields { get; } = [];
    public ICommand BackCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand FinishCommand { get; }

    public SyncAddChoiceViewModel? SelectedChoice
    {
        get => selectedChoice;
        set
        {
            if (selectedChoice == value || value is { IsAvailable: false })
            {
                return;
            }

            selectedChoice = value;
            foreach (var choice in Choices)
            {
                choice.IsSelected = ReferenceEquals(choice, value);
            }
            if (value is not null)
            {
                Identity = value.Preset?.SuggestedIdentity
                    ?? value.Definition.FixedIdentity
                    ?? value.Definition.Id;
                Fields.Clear();
                foreach (var definition in value.Definition.ConnectionFields)
                {
                    var field = new SyncWizardFieldViewModel(
                        definition,
                        definition.Id == "endpoint"
                            ? value.Preset?.DefaultUrl ?? value.Definition.DefaultUrl
                            : string.Empty);
                    field.PropertyChanged += (_, _) => NotifyAll();
                    Fields.Add(field);
                }
                Feeds.Clear();
                var supportedFeeds = value.Preset?.SupportedDataKinds ?? value.Definition.SupportedDataKinds;
                foreach (var kind in supportedFeeds.OrderBy(kind => SyncTargetTypeCatalog.GetFeed(kind).DisplayName))
                {
                    var initial = value.Preset?.FeedDefaults.GetValueOrDefault(kind)
                        ?? (value.Definition.InheritsGlobalSync && baseline.GlobalDefaults.DataKinds.GetValueOrDefault(kind));
                    Feeds.Add(new(kind, initial));
                }
            }

            NotifyAll();
        }
    }

    public SyncAddDestinationStep Step
    {
        get => step;
        private set { step = value; NotifyAll(); }
    }
    public bool IsChooseStep => Step == SyncAddDestinationStep.Choose;
    public bool IsConfigureStep => Step == SyncAddDestinationStep.Configure;
    public bool IsFeedsStep => Step == SyncAddDestinationStep.FeedsAndReview;
    public bool IsLastStep => IsFeedsStep;
    public string StepTitle => Step switch
    {
        SyncAddDestinationStep.Choose => "Choose a preset or custom destination",
        SyncAddDestinationStep.Configure => "Configure destination",
        _ => "Choose feeds and review",
    };
    public string Identity { get => identity; set { identity = value?.Trim() ?? string.Empty; NotifyAll(); } }
    public string Endpoint { get => FieldValue("endpoint"); set => SetFieldValue("endpoint", value?.Trim() ?? string.Empty); }
    public string Token { get => FieldValue("token"); set => SetFieldValue("token", value ?? string.Empty); }
    public string Error { get => error; private set { error = value; NotifyAll(); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public SyncTargetKind Kind => SelectedChoice!.Kind;
    public string? PresetId => SelectedChoice?.Preset?.Id;
    public string ReviewSummary => SelectedChoice is null
        ? string.Empty
        : $"{Identity} · {Feeds.Count(feed => feed.IsEnabled)} of {Feeds.Count} feeds on";

    internal void SetError(string message) => Error = message;

    private void Back()
    {
        Error = string.Empty;
        Step--;
    }

    private void Next()
    {
        Error = string.Empty;
        if (Step == SyncAddDestinationStep.Configure
            && (string.IsNullOrWhiteSpace(Identity) || !RequiredFieldsComplete()))
        {
            Error = "Display name and every required connection field must be completed.";
            return;
        }

        Step++;
    }

    private bool CanContinue() => Step != SyncAddDestinationStep.FeedsAndReview && SelectedChoice is not null;
    private bool CanFinish() => IsFeedsStep && SelectedChoice is not null
        && !string.IsNullOrWhiteSpace(Identity) && RequiredFieldsComplete();

    private bool RequiredFieldsComplete() =>
        Fields.Where(field => field.IsRequired).All(field => !string.IsNullOrWhiteSpace(field.Value));

    private string FieldValue(string id) => Fields.FirstOrDefault(field => field.Id == id)?.Value ?? string.Empty;

    private void SetFieldValue(string id, string value)
    {
        if (Fields.FirstOrDefault(field => field.Id == id) is { } field)
        {
            field.Value = value;
        }
    }

    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new(string.Empty));
        (BackCommand as SettingsActionCommand)?.RaiseCanExecuteChanged();
        (NextCommand as SettingsActionCommand)?.RaiseCanExecuteChanged();
        (FinishCommand as SettingsActionCommand)?.RaiseCanExecuteChanged();
    }
}
