using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly LauncherConfigurationCatalog catalog;
    private readonly IConfigurationRepository repository;
    private readonly Func<string?> configurationPathProvider;
    private readonly ILauncherSettingsLayoutProvider layoutProvider;
    private readonly LauncherSettingsActivationDiagnostics settingsDiagnostics;
    private readonly ILauncherUiPreferencesStore? uiPreferencesStore;
    private readonly LauncherSettingsProjectionQuery projectionQuery;
    private readonly SettingsProjectionCollection projectedItems = [];
    private readonly SettingsEditorDraftStore editorDraftStore = new();
    private readonly Dictionary<string, SettingsRowViewModel> projectedRowsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> keybindingIssueMessages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> editorInvalidInputPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> keybindingInvalidPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SettingsActionCommand discardCommand;
    private readonly AsyncSettingsActionCommand saveCommand;
    private readonly SettingsActionCommand searchClearCommand;
    private readonly SettingsActionCommand enablePatchEditingCommand;
    private readonly SettingsActionCommand lockPatchEditingCommand;
    private readonly object lifecycleSync = new();
    private ConfigurationWorkspace? workspace;
    private Task? activeSave;
    private Task? invalidationTask;
    private string searchText = string.Empty;
    private LauncherSettingsSection selectedSection = LauncherSettingsSection.General;
    private string operationStatus = string.Empty;
    private bool isSearchVisible;
    private bool isPatchEditingUnlocked;
    private bool isInvalidating;
    private bool isInvalidated;
    private int projectionRevision;

    public SettingsViewModel(
        LauncherConfigurationCatalog catalog,
        ICommand navigateHomeCommand,
        ICommand openRawTomlCommand,
        Func<string?> configurationPathProvider,
        ILauncherSettingsLayoutProvider layoutProvider,
        LauncherSettingsActivationDiagnostics settingsDiagnostics,
        IConfigurationRepository? repository = null,
        ILauncherUiPreferencesStore? uiPreferencesStore = null,
        Action<Uri>? openExternalUri = null,
        Action? openDataFolder = null,
        Action? manageApplication = null,
        Action? openReleaseSecurityGuidance = null,
        ProviderConfigurationRestoreCoordinator? configurationHistoryCoordinator = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        NavigateHomeCommand = navigateHomeCommand ?? throw new ArgumentNullException(nameof(navigateHomeCommand));
        OpenRawTomlCommand = openRawTomlCommand ?? throw new ArgumentNullException(nameof(openRawTomlCommand));
        this.configurationPathProvider =
            configurationPathProvider ?? throw new ArgumentNullException(nameof(configurationPathProvider));
        this.layoutProvider =
            layoutProvider ?? throw new ArgumentNullException(nameof(layoutProvider));
        this.settingsDiagnostics =
            settingsDiagnostics ?? throw new ArgumentNullException(nameof(settingsDiagnostics));
        this.repository = repository ?? new TomlConfigurationRepository();
        this.uiPreferencesStore = uiPreferencesStore;
        isSearchVisible = uiPreferencesStore?.Load().SettingsSearchVisible ?? false;

        SourceIdentity = $"{catalog.Source.DisplayName} Community Mod";
        About = new(
            BundledLauncherAboutCatalog.Load(),
            catalog,
            settingsDiagnostics,
            openExternalUri,
            openDataFolder,
            manageApplication,
            openReleaseSecurityGuidance);
        OpenRawTomlCommand.CanExecuteChanged += OpenRawTomlCommand_CanExecuteChanged;
        discardCommand = new SettingsActionCommand(
            Discard,
            () => !isInvalidating && !isInvalidated && HasPendingChanges);
        saveCommand = new AsyncSettingsActionCommand(SaveAsync, () => CanSave);
        enablePatchEditingCommand = new SettingsActionCommand(
            EnablePatchEditing,
            () => IsConfigurationReady && !IsPatchEditingUnlocked && PatchSettings.Count > 0);
        lockPatchEditingCommand = new SettingsActionCommand(
            LockPatchEditing,
            () => IsPatchEditingUnlocked);

        SyncWorkspace = new(configurationPathProvider, this.repository, () => HasPendingChanges, () => workspace);
        SyncWorkspace.StateChanged += SyncWorkspace_StateChanged;
        SyncWorkspace.Committed += SyncWorkspace_Committed;
        ConfigurationHistory = configurationHistoryCoordinator is null
            ? null
            : new(
                configurationHistoryCoordinator,
                catalog.Source.StableId,
                catalog.Source.DisplayName,
                () => HasPendingChanges || SyncWorkspace.HasPendingChanges,
                ReloadAfterHistoryRestore);
        Sections = CreateSections();

        TryLoadConfiguration();
        SyncWorkspace.Reload();
        projectionQuery = new(catalog, layoutProvider);
        RefreshKeybindingConflicts();

        SearchOpenCommand = new SettingsActionCommand(OpenSearch);
        SearchCloseCommand = new SettingsActionCommand(CloseSearch);
        searchClearCommand = new SettingsActionCommand(
            ClearSearch,
            () => IsSearchActive);
        SelectSection(Sections[0].Id);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceIdentity { get; }

    public LauncherAboutViewModel About { get; }

    public IReadOnlyList<SettingsSectionViewModel> Sections { get; }

    public IReadOnlyList<SettingsListItemViewModel> FilteredSettings =>
        projectedItems;

    public SettingsProjectionSnapshot ProjectionSnapshot { get; private set; } =
        new(0, 0, 0, 0, []);

    public ICommand NavigateHomeCommand { get; }

    public ICommand OpenRawTomlCommand { get; }

    public ICommand DiscardCommand => discardCommand;

    public ICommand SaveCommand => saveCommand;

    public ICommand SearchOpenCommand { get; }

    public ICommand SearchCloseCommand { get; }

    public ICommand SearchClearCommand => searchClearCommand;

    public ICommand EnablePatchEditingCommand => enablePatchEditingCommand;

    public ICommand LockPatchEditingCommand => lockPatchEditingCommand;

    public SyncWorkspaceViewModel SyncWorkspace { get; }

    public ProviderConfigurationHistoryViewModel? ConfigurationHistory { get; }

    public string SearchText
    {
        get => searchText;
        set
        {
            var normalized = value ?? string.Empty;
            if (searchText == normalized)
            {
                return;
            }

            searchText = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearchActive));
            OnPropertyChanged(nameof(WorkspaceTitle));
            OnPropertyChanged(nameof(WorkspaceDescription));
            OnPropertyChanged(nameof(IsAboutSelected));
            OnPropertyChanged(nameof(IsGeneralSelected));
            OnPropertyChanged(nameof(IsAdvancedSelected));
            OnPropertyChanged(nameof(IsDataSyncSelected));
            OnPropertyChanged(nameof(IsConfigurationHistorySelected));
            OnPropertyChanged(nameof(IsSettingsListVisible));
            OnPropertyChanged(nameof(IsSettingsFooterVisible));
            searchClearCommand.RaiseCanExecuteChanged();
            RebuildProjection();
        }
    }

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsSearchVisible
    {
        get => isSearchVisible;
        private set
        {
            if (isSearchVisible == value)
            {
                return;
            }

            isSearchVisible = value;
            OnPropertyChanged();
            SaveUiPreferences();
        }
    }

    public bool IsPatchEditingUnlocked
    {
        get => isPatchEditingUnlocked;
        private set
        {
            if (isPatchEditingUnlocked == value)
            {
                return;
            }

            isPatchEditingUnlocked = value;
            OnPropertyChanged();
            enablePatchEditingCommand.RaiseCanExecuteChanged();
            lockPatchEditingCommand.RaiseCanExecuteChanged();
            RebuildProjection();
        }
    }

    public LauncherSettingsSection SelectedSection => selectedSection;

    public string WorkspaceTitle =>
        IsSearchActive ? "Search results" : SelectedSectionItem.Title;

    public string WorkspaceDescription =>
        IsSearchActive
            ? "Results across every mod setting category."
            : SelectedSectionItem.Description;

    public bool IsAboutSelected =>
        !IsSearchActive && selectedSection == LauncherSettingsSection.About;

    public bool IsGeneralSelected =>
        !IsSearchActive && selectedSection == LauncherSettingsSection.General;

    public bool IsAdvancedSelected =>
        !IsSearchActive && selectedSection == LauncherSettingsSection.Advanced;

    public bool IsDataSyncSelected =>
        !IsSearchActive && selectedSection == LauncherSettingsSection.DataSync;

    public bool IsConfigurationHistorySelected =>
        !IsSearchActive && selectedSection == LauncherSettingsSection.ConfigurationHistory;

    public bool IsSettingsListVisible =>
        !IsAboutSelected && !IsDataSyncSelected && !IsConfigurationHistorySelected;

    public bool IsSettingsFooterVisible =>
        HasPendingChanges && !IsDataSyncSelected && !IsConfigurationHistorySelected;

    public int VisibleSettingCount =>
        projectedItems.OfType<SettingsRowViewModel>().Count()
        + projectedItems.OfType<AdvancedPatchEditingGateViewModel>()
            .Where(gate => gate.IsLocked)
            .Sum(gate => gate.SettingCount);

    public string VisibleItemsSummary
    {
        get
        {
            var noun = IsSearchActive
                ? "results"
                : SelectedSection == LauncherSettingsSection.Hotkeys
                    ? "actions"
                    : "settings";
            return $"{VisibleSettingCount} {noun} shown · {ConfigurationStatus}";
        }
    }

    public Visibility EmptyStateVisibility =>
        IsSettingsListVisible && VisibleSettingCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool CanOpenRawToml => OpenRawTomlCommand.CanExecute(null);

    public string OpenRawTomlAvailability =>
        CanOpenRawToml
            ? "Open the active configuration as raw TOML."
            : "Raw TOML becomes available after Mod Bridge selects an active configuration.";

    public bool IsConfigurationReady => !isInvalidating && !isInvalidated && workspace is not null;

    public string DetectedRuntime => settingsDiagnostics.DetectedRuntime;

    public string SemanticGroupingStatus =>
        settingsDiagnostics.SemanticGroupingStatus;

    public string SemanticGroupingReason =>
        settingsDiagnostics.SemanticGroupingReason;

    public string SettingsLayoutName =>
        settingsDiagnostics.SettingsLayoutName;

    public string ConfigurationStatus =>
        IsConfigurationReady
            ? workspace!.DocumentExists
                ? "Changes are staged until you save."
                : "No TOML exists yet. Your first saved change will create it."
            : "Select a game folder with a supported configuration to enable editing.";

    public int PendingChangeCount => workspace?.PendingChangeCount ?? 0;

    public bool HasPendingChanges => PendingChangeCount > 0;

    public bool HasInvalidInput =>
        editorInvalidInputPaths.Count > 0 || keybindingInvalidPaths.Count > 0;

    public string PendingChangesText =>
        PendingChangeCount switch
        {
            0 => "No unsaved changes",
            1 => "1 unsaved change",
            _ => $"{PendingChangeCount} unsaved changes",
        };

    public string PendingApplyTimingText =>
        LauncherConfigurationApplySummary.From(
            catalog.VisibleSettings
                .Where(setting => GetValueState(setting).IsDirty)
                .Select(setting => setting.ApplyBehavior))
            .Text;

    public bool CanSave =>
        !isInvalidating
        && !isInvalidated
        && IsConfigurationReady
        && HasPendingChanges
        && !HasInvalidInput
        && !SyncWorkspace.HasPendingChanges
        && ConfigurationPathMatchesLoadedSession();

    public string SaveAvailability =>
        SyncWorkspace.HasPendingChanges
            ? "Save or discard the pending sync setup before saving other settings."
            : HasInvalidInput
            ? "Fix the highlighted setting before saving."
            : CanSave
                ? "Save all staged configuration changes."
                : "Stage a valid configuration change before saving.";

    public string OperationStatus
    {
        get => operationStatus;
        private set
        {
            if (operationStatus == value)
            {
                return;
            }

            operationStatus = value;
            OnPropertyChanged();
        }
    }

    public void ReloadConfiguration()
    {
        if (isInvalidating || isInvalidated)
        {
            return;
        }
        if (HasPendingChanges)
        {
            if (!ConfigurationPathMatchesLoadedSession())
            {
                OperationStatus =
                    "The selected configuration changed while edits were staged. Discard those edits before reloading.";
                NotifySessionChanged();
            }

            return;
        }

        ClearEditorDrafts();
        TryLoadConfiguration();
        SyncWorkspace.Reload();
        RefreshAllStates();
        NotifySessionChanged();
    }

    private void ReloadAfterHistoryRestore()
    {
        ClearEditorDrafts();
        TryLoadConfiguration();
        SyncWorkspace.Reload();
        RefreshAllStates();
        NotifySessionChanged();
    }

    internal Task InvalidateAsync()
    {
        TaskCompletionSource completion;
        Task? settingsSave;
        Task syncInvalidation;
        Task historyInvalidation;
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
            settingsSave = activeSave;
            syncInvalidation = SyncWorkspace.InvalidateAsync();
            historyInvalidation = ConfigurationHistory?.InvalidateAsync() ?? Task.CompletedTask;
        }
        RefreshAllStates();
        NotifySessionChanged();
        _ = CompleteInvalidationAsync(
            settingsSave,
            syncInvalidation,
            historyInvalidation,
            completion);
        return completion.Task;
    }

    private SettingsSectionViewModel SelectedSectionItem =>
        Sections.Single(section => section.Id == selectedSection);

    private IReadOnlyList<LauncherConfigurationSetting> PatchSettings =>
        catalog.VisibleSettings.Where(IsPatchSetting).ToArray();

    private ReadOnlyCollection<SettingsSectionViewModel> CreateSections()
    {
        if (layoutProvider.Sections.Count == 0
            || layoutProvider.Sections.Any(
                section => section.Id is LauncherSettingsSection.About
                    or LauncherSettingsSection.ConfigurationHistory))
        {
            throw new InvalidOperationException(
                "The settings layout must provide content and must not own Configuration history or About.");
        }

        var duplicateSection = layoutProvider.Sections
            .GroupBy(section => section.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSection is not null)
        {
            throw new InvalidOperationException(
                $"The settings layout defines section '{duplicateSection.Key}' more than once.");
        }

        var populatedSections = catalog.VisibleSettings
            .Select(setting => layoutProvider.Place(setting).Section)
            .ToHashSet();
        var sections = layoutProvider.Sections
            .Where(section => populatedSections.Contains(section.Id))
            .Select(
                section => new SettingsSectionViewModel(
                    section.Id,
                    section.Title,
                    section.Description,
                    section.AutomationName,
                    SelectSection))
            .ToList();
        if (sections.Count == 0)
        {
            throw new InvalidOperationException(
                $"Settings layout '{layoutProvider.Id}' has no populated content section.");
        }
        if (ConfigurationHistory is not null)
        {
            sections.Add(
                new(
                    LauncherSettingsSection.ConfigurationHistory,
                    "History",
                    "Review and restore protected configuration history for this release source.",
                    "Configuration history",
                    SelectSection));
        }
        sections.Add(
            new(
                LauncherSettingsSection.About,
                "About",
                "Product identity, build provenance, credits, and third-party notices.",
                "About STFC Mod Bridge",
                SelectSection));
        return sections.AsReadOnly();
    }

    private void SelectSection(LauncherSettingsSection section)
    {
        selectedSection = section;
        if ((section is LauncherSettingsSection.About
                or LauncherSettingsSection.DataSync
                or LauncherSettingsSection.ConfigurationHistory)
            && !IsSearchActive)
        {
            IsSearchVisible = false;
        }

        foreach (var item in Sections)
        {
            item.IsSelected = item.Id == section;
        }

        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(WorkspaceDescription));
        OnPropertyChanged(nameof(VisibleItemsSummary));
        OnPropertyChanged(nameof(IsAboutSelected));
        OnPropertyChanged(nameof(IsGeneralSelected));
        OnPropertyChanged(nameof(IsAdvancedSelected));
        OnPropertyChanged(nameof(IsDataSyncSelected));
        OnPropertyChanged(nameof(IsConfigurationHistorySelected));
        OnPropertyChanged(nameof(IsSettingsListVisible));
        OnPropertyChanged(nameof(IsSettingsFooterVisible));
        if (section == LauncherSettingsSection.DataSync)
        {
            SyncWorkspace.Reload();
        }
        else if (section == LauncherSettingsSection.ConfigurationHistory
            && ConfigurationHistory is not null)
        {
            _ = ConfigurationHistory.RefreshAsync();
        }
        RebuildProjection();
    }

    private void OpenSearch() => IsSearchVisible = true;

    private void ClearSearch() => SearchText = string.Empty;

    private void CloseSearch()
    {
        ClearSearch();
        IsSearchVisible = false;
    }

    private void EnablePatchEditing() => IsPatchEditingUnlocked = true;

    private void LockPatchEditing() => IsPatchEditingUnlocked = false;

    private void SaveUiPreferences()
    {
        if (uiPreferencesStore is null)
        {
            return;
        }

        try
        {
            var preferences = uiPreferencesStore.Load();
            uiPreferencesStore.Save(
                preferences with { SettingsSearchVisible = IsSearchVisible });
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // UI preferences are best-effort and must never block configuration editing.
        }
    }

    private void TryLoadConfiguration()
    {
        if (isInvalidating || isInvalidated)
        {
            return;
        }
        workspace = null;
        var path = configurationPathProvider();
        var load = ConfigurationWorkspace.Load(
            path,
            catalog,
            repository,
            out var loadedWorkspace);
        if (load.State == ConfigurationRepositoryReadState.NoConfigurationSelected)
        {
            OperationStatus = string.Empty;
            RefreshPatchEditingAvailability();
            return;
        }

        if (!load.IsSuccess || loadedWorkspace is null)
        {
            OperationStatus = load.State == ConfigurationRepositoryReadState.Invalid
                ? $"Editing is unavailable because the TOML could not be loaded safely: {load.ValidationError?.Message}"
                : $"Editing is unavailable: {load.Error}";
            RefreshPatchEditingAvailability();
            return;
        }

        workspace = loadedWorkspace;
        OperationStatus = string.Empty;
        RefreshPatchEditingAvailability();
    }

    private void RefreshPatchEditingAvailability()
    {
        if (workspace is null && IsPatchEditingUnlocked)
        {
            IsPatchEditingUnlocked = false;
            return;
        }

        enablePatchEditingCommand.RaiseCanExecuteChanged();
        lockPatchEditingCommand.RaiseCanExecuteChanged();
    }

    private SettingsValueState GetValueState(LauncherConfigurationSetting setting)
    {
        if (workspace is null)
        {
            var defaultValue = SettingsRowViewModelValue(setting.DefaultValue);
            return new(
                defaultValue,
                defaultValue,
                false,
                defaultValue,
                false,
                false,
                LauncherConfigurationValueOrigin.ProviderDefault,
                LauncherConfigurationValueOrigin.ProviderDefault,
                []);
        }

        var state = workspace.GetState(setting);
        return new(
            state.DefaultValue,
            state.SavedEffectiveValue,
            state.SavedHasOverride,
            state.DraftEffectiveValue,
            state.DraftHasOverride,
            state.IsDirty,
            state.SavedOrigin,
            state.DraftOrigin,
            state.CompatibilitySourcePaths);
    }

    private static object? SettingsRowViewModelValue(JsonElement defaultValue) =>
        defaultValue.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => defaultValue.GetString(),
            JsonValueKind.Number when defaultValue.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => defaultValue.GetDouble(),
            _ => defaultValue,
        };

    private bool StageValue(
        LauncherConfigurationSetting setting,
        string renderedTomlValue)
    {
        if (isInvalidating
            || isInvalidated
            || workspace is null
            || IsPatchSetting(setting) && !IsPatchEditingUnlocked)
        {
            return false;
        }

        var result = workspace.StageSet(setting, renderedTomlValue);
        OperationStatus = result.IsValid ? string.Empty : result.Error?.Message ?? "The change is not valid.";
        if (!result.IsValid)
        {
            return false;
        }

        RefreshState(setting);
        RefreshKeybindingConflicts();
        NotifySessionChanged();
        return true;
    }

    private bool RevertDraft(LauncherConfigurationSetting setting)
    {
        if (isInvalidating
            || isInvalidated
            || workspace is null
            || IsPatchSetting(setting) && !IsPatchEditingUnlocked)
        {
            return false;
        }

        var result = workspace.Revert(setting);
        OperationStatus = result.IsValid ? string.Empty : result.Error?.Message ?? "The change could not be reverted.";
        if (!result.IsValid)
        {
            return false;
        }

        RefreshState(setting);
        RefreshKeybindingConflicts();
        NotifySessionChanged();
        return true;
    }

    private void SetInputValidity(
        LauncherConfigurationSetting setting,
        bool isValid)
    {
        var changed = isValid
            ? editorInvalidInputPaths.Remove(setting.Path)
            : editorInvalidInputPaths.Add(setting.Path);
        if (!changed)
        {
            return;
        }

        NotifyValidationChanged();
    }

    private void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(HasInvalidInput));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveAvailability));
        saveCommand.RaiseCanExecuteChanged();
    }

    private void Discard()
    {
        var selectedConfigurationChanged = !ConfigurationPathMatchesLoadedSession();
        workspace?.Discard();
        if (selectedConfigurationChanged)
        {
            TryLoadConfiguration();
            SyncWorkspace.Reload();
        }

        ClearEditorDrafts();
        OperationStatus = selectedConfigurationChanged
            ? "Unsaved changes discarded and the selected configuration reloaded."
            : "Unsaved changes discarded.";
        RefreshAllStates();
        SyncWorkspace.Reload();
        NotifySessionChanged();
    }

    internal Task SaveAsync()
    {
        TaskCompletionSource completion;
        ConfigurationWorkspace editingSession;
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
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            activeSave = completion.Task;
        }
        _ = CompleteSaveAsync(editingSession, completion);
        return completion.Task;
    }

    private async Task SaveCoreAsync(ConfigurationWorkspace editingSession)
    {
        if (!ConfigurationPathMatchesLoadedSession())
        {
            OperationStatus =
                "The selected configuration changed after this editing session began. Discard these edits and reload before saving.";
            return;
        }

        OperationStatus = "Saving changes…";
        var result = await editingSession.CommitAsync();
        if (isInvalidating || isInvalidated)
        {
            return;
        }
        OperationStatus = result.State switch
        {
            AtomicTomlWriteState.Succeeded when result.BackupReceipt is not null =>
                $"Changes saved. Protected provider backup {result.BackupReceipt.BackupId} was verified.",
            AtomicTomlWriteState.Succeeded => "Changes saved.",
            AtomicTomlWriteState.NoChange => "No configuration changes were needed.",
            AtomicTomlWriteState.Conflict =>
                "The TOML changed outside Mod Bridge. Those external edits were preserved; reload before saving.",
            AtomicTomlWriteState.Busy =>
                "Another Mod Bridge change is still in progress. Nothing was written; try saving again when it finishes.",
            AtomicTomlWriteState.Invalid =>
                $"Nothing was written because the TOML is not safe to update: {result.ValidationError?.Message}",
            AtomicTomlWriteState.NoConfigurationSelected =>
                "Select a supported configuration before saving.",
            _ => $"The changes could not be saved: {result.Error}",
        };

        RefreshAllStates();
        if (result.IsSuccess)
        {
            SyncWorkspace.Reload();
        }
        NotifySessionChanged();
    }

    private async Task CompleteSaveAsync(
        ConfigurationWorkspace editingSession,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await SaveCoreAsync(editingSession);
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
            OperationStatus = $"The changes could not be saved: {failure.Message}";
            RefreshAllStates();
            NotifySessionChanged();
        }
        completion.SetResult();
    }

    private async Task CompleteInvalidationAsync(
        Task? settingsSave,
        Task syncInvalidation,
        Task historyInvalidation,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await Task.WhenAll(
                settingsSave ?? Task.CompletedTask,
                syncInvalidation,
                historyInvalidation);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            workspace?.Discard();
            ClearEditorDrafts();
            workspace = null;
            isInvalidated = true;
            isInvalidating = false;
            OperationStatus = "This Settings workspace was replaced by newer runtime or provider evidence.";
            RefreshAllStates();
            NotifySessionChanged();
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

    private void RefreshAllStates()
    {
        var hasPatchGate = projectedItems.OfType<AdvancedPatchEditingGateViewModel>().Any();
        foreach (var setting in projectedRowsByPath.Values)
        {
            setting.UpdateState(GetValueState(setting.Setting), IsConfigurationReady);
        }

        RefreshKeybindingConflicts();
        if (hasPatchGate)
        {
            RebuildProjection();
        }
        ConfigurationHistory?.NotifySiblingDraftStateChanged();
    }

    private void RefreshState(LauncherConfigurationSetting setting)
    {
        if (projectedRowsByPath.TryGetValue(setting.Path, out var row))
        {
            row.UpdateState(GetValueState(setting), workspace is not null);
        }
    }

    private void RefreshKeybindingConflicts()
    {
        var keybindings = catalog.VisibleSettings
            .Where(setting =>
                setting.Control == LauncherConfigurationControl.Keybinding
                && setting.ValueKind == LauncherConfigurationValueKind.Keybinding)
            .ToArray();

        var projections = keybindings
            .Select(setting =>
            {
                var state = GetValueState(setting);
                return (
                    Setting: setting,
                    State: state,
                    Assignment: ReadKeybindingAssignment(setting, state));
            })
            .ToArray();
        var assignments = projections
            .Select(projection => projection.Assignment)
            .OfType<LauncherKeybindingAssignment>()
            .ToArray();
        var conflicts = LauncherKeybindingConflictDetector.FindConflicts(assignments);
        var previousInvalidCount = keybindingInvalidPaths.Count;
        keybindingInvalidPaths.Clear();
        keybindingIssueMessages.Clear();
        foreach (var projection in projections)
        {
            var setting = projection.Setting;
            if (projection.Assignment is null)
            {
                if (projection.State.IsDirty)
                {
                    keybindingInvalidPaths.Add(setting.Path);
                }
                keybindingIssueMessages[setting.Path] =
                    "The configured shortcut is invalid; the runtime default is shown.";
                continue;
            }

            var settingConflicts = conflicts
                .Where(conflict =>
                    string.Equals(conflict.First.Path, setting.Path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(conflict.Second.Path, setting.Path, StringComparison.OrdinalIgnoreCase))
                .Select(conflict =>
                {
                    var other = string.Equals(
                        conflict.First.Path,
                        setting.Path,
                        StringComparison.OrdinalIgnoreCase)
                        ? conflict.Second
                        : conflict.First;
                    return $"{conflict.Chord.Display} conflicts with {other.Title}.";
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (settingConflicts.Length > 0)
            {
                if (projection.State.IsDirty)
                {
                    keybindingInvalidPaths.Add(setting.Path);
                }
                keybindingIssueMessages[setting.Path] =
                    string.Join(' ', settingConflicts);
            }
        }

        foreach (var row in projectedRowsByPath.Values.Where(row => row.IsKeybindingEditor))
        {
            row.SetKeybindingConflict(
                keybindingIssueMessages.GetValueOrDefault(row.Path));
        }

        if (previousInvalidCount != keybindingInvalidPaths.Count)
        {
            NotifyValidationChanged();
        }
    }

    private static LauncherKeybindingAssignment? ReadKeybindingAssignment(
        LauncherConfigurationSetting setting,
        SettingsValueState state)
    {
        if (state.DraftValue is not string text)
        {
            return null;
        }

        if (state.DraftHasOverride
            && !LauncherTomlValue.TryReadString(text, out text))
        {
            return null;
        }

        var binding = LauncherKeybindingValue.Parse(text);
        return binding.IsValid
            ? new LauncherKeybindingAssignment(setting, binding)
            : null;
    }

    private void NotifySessionChanged()
    {
        OnPropertyChanged(nameof(IsConfigurationReady));
        OnPropertyChanged(nameof(ConfigurationStatus));
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(IsSettingsFooterVisible));
        OnPropertyChanged(nameof(PendingChangesText));
        OnPropertyChanged(nameof(PendingApplyTimingText));
        OnPropertyChanged(nameof(HasInvalidInput));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveAvailability));
        discardCommand.RaiseCanExecuteChanged();
        saveCommand.RaiseCanExecuteChanged();
        ConfigurationHistory?.NotifySiblingDraftStateChanged();
    }

    private bool ConfigurationPathMatchesLoadedSession()
    {
        var currentPath = configurationPathProvider();
        if (string.IsNullOrWhiteSpace(currentPath)
            || workspace is null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(currentPath),
                workspace.DocumentPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private void SyncWorkspace_StateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (isInvalidating || isInvalidated)
        {
            return;
        }
        NotifySessionChanged();
    }

    private void SyncWorkspace_Committed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (isInvalidating || isInvalidated)
        {
            return;
        }
        if (!HasPendingChanges)
        {
            TryLoadConfiguration();
            RefreshAllStates();
        }

        NotifySessionChanged();
    }

    private void RebuildProjection()
    {
        var projection = IsAboutSelected || IsConfigurationHistorySelected
            ? []
            : projectionQuery.Project(selectedSection, SearchText);
        var items = new List<SettingsListItemViewModel>(projection.Count);
        projectedRowsByPath.Clear();
        var constructedPaths = new List<string>();
        var groupHeaderCount = 0;
        var familyHeaderCount = 0;
        var pendingDecorators = new List<LauncherSettingsProjectionItem>();
        var projectedPatchSettings = projection
            .OfType<LauncherSettingRowProjection>()
            .Select(row => row.Setting)
            .Where(IsPatchSetting)
            .ToArray();

        foreach (var item in projection)
        {
            if (item is LauncherSettingsGroupHeaderProjection
                or LauncherSettingsFamilyHeaderProjection)
            {
                pendingDecorators.Add(item);
                continue;
            }

            if (item is not LauncherSettingRowProjection settingProjection)
            {
                continue;
            }

            var isPatchSetting = IsPatchSetting(settingProjection.Setting);
            if (isPatchSetting && !IsPatchEditingUnlocked)
            {
                pendingDecorators.Clear();
                continue;
            }

            AddDecorators(pendingDecorators, items, ref groupHeaderCount, ref familyHeaderCount);
            switch (item)
            {
                case LauncherSettingRowProjection setting:
                    var row = new SettingsRowViewModel(
                        setting.Setting,
                        GetValueState(setting.Setting),
                        workspace is not null,
                        StageValue,
                        RevertDraft,
                        SetInputValidity,
                        editorDraftStore);
                    row.SetKeybindingConflict(
                        keybindingIssueMessages.GetValueOrDefault(row.Path));
                    items.Add(row);
                    projectedRowsByPath.Add(row.Path, row);
                    constructedPaths.Add(row.Path);
                    break;
            }
        }

        if (projectedPatchSettings.Length > 0)
        {
            items.Add(CreatePatchGate(projectedPatchSettings));
        }

        projectedItems.ReplaceAll(items);
        ProjectionSnapshot = new(
            ++projectionRevision,
            constructedPaths.Count,
            groupHeaderCount,
            familyHeaderCount,
            constructedPaths.AsReadOnly());
        OnPropertyChanged(nameof(ProjectionSnapshot));
        NotifyFilterSummaryChanged();
    }

    private AdvancedPatchEditingGateViewModel CreatePatchGate(
        IReadOnlyList<LauncherConfigurationSetting> patchSettings)
    {
        var summaries = patchSettings
            .Select(setting =>
            {
                var state = GetValueState(setting);
                return new AdvancedPatchSummaryItemViewModel(
                    setting.Presentation.Label,
                    FormatPatchSummaryValue(state.DraftValue ?? setting.DefaultValue),
                    state.IsDirty,
                    IsConfigurationReady,
                    state.DraftHasOverride);
            })
            .ToArray();
        return new(
            IsPatchEditingUnlocked,
            IsConfigurationReady,
            summaries,
            EnablePatchEditingCommand,
            LockPatchEditingCommand);
    }

    private static void AddDecorators(
        List<LauncherSettingsProjectionItem> pendingDecorators,
        List<SettingsListItemViewModel> items,
        ref int groupHeaderCount,
        ref int familyHeaderCount)
    {
        foreach (var decorator in pendingDecorators)
        {
            if (decorator is LauncherSettingsGroupHeaderProjection group)
            {
                items.Add(new SettingsGroupHeaderViewModel(group.Label));
                ++groupHeaderCount;
            }
            else if (decorator is LauncherSettingsFamilyHeaderProjection family)
            {
                items.Add(
                    new SettingsFamilyHeaderViewModel(
                        family.Id,
                        family.Label,
                        family.Description));
                ++familyHeaderCount;
            }
        }

        pendingDecorators.Clear();
    }

    private static bool IsPatchSetting(LauncherConfigurationSetting setting) =>
        string.Equals(setting.Category, "patches", StringComparison.OrdinalIgnoreCase);

    private static string FormatPatchSummaryValue(object? value) =>
        value switch
        {
            null => "Not specified",
            JsonElement element when element.ValueKind == JsonValueKind.True => "On",
            JsonElement element when element.ValueKind == JsonValueKind.False => "Off",
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? "(empty)",
            JsonElement element => element.GetRawText(),
            bool boolean => boolean ? "On" : "Off",
            string text when bool.TryParse(text, out var boolean) => boolean ? "On" : "Off",
            string text when LauncherTomlValue.TryReadString(text, out var parsed) => parsed,
            _ => value.ToString() ?? "Not specified",
        };

    private void ClearEditorDrafts()
    {
        editorDraftStore.Clear();
        editorInvalidInputPaths.Clear();
    }

    private void NotifyFilterSummaryChanged()
    {
        OnPropertyChanged(nameof(VisibleSettingCount));
        OnPropertyChanged(nameof(VisibleItemsSummary));
        OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    private void OpenRawTomlCommand_CanExecuteChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanOpenRawToml));
        OnPropertyChanged(nameof(OpenRawTomlAvailability));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
