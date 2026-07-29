using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly LauncherConfigurationCatalog catalog;
    private readonly AtomicTomlStore store;
    private readonly Func<string?> configurationPathProvider;
    private readonly ILauncherSettingsLayoutProvider layoutProvider;
    private readonly LauncherSettingsActivationDiagnostics settingsDiagnostics;
    private readonly ILauncherUiPreferencesStore? uiPreferencesStore;
    private readonly List<SettingsRowViewModel> settings = [];
    private readonly IReadOnlyDictionary<string, LauncherSettingsPlacement> placementsByPath;
    private readonly Dictionary<string, SettingsRowViewModel> settingsByPath;
    private readonly HashSet<string> invalidInputPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SettingsActionCommand discardCommand;
    private readonly AsyncSettingsActionCommand saveCommand;
    private LauncherConfigurationEditSession? editSession;
    private string? loadedConfigurationPath;
    private string searchText = string.Empty;
    private LauncherSettingsSection selectedSection = LauncherSettingsSection.General;
    private string operationStatus = string.Empty;
    private bool isSearchVisible;

    public SettingsViewModel(
        LauncherConfigurationCatalog catalog,
        ICommand navigateHomeCommand,
        ICommand openRawTomlCommand,
        Func<string?> configurationPathProvider,
        ILauncherSettingsLayoutProvider layoutProvider,
        LauncherSettingsActivationDiagnostics settingsDiagnostics,
        AtomicTomlStore? store = null,
        ILauncherUiPreferencesStore? uiPreferencesStore = null)
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
        this.store = store ?? new AtomicTomlStore();
        this.uiPreferencesStore = uiPreferencesStore;
        isSearchVisible = uiPreferencesStore?.Load().SettingsSearchVisible ?? false;

        SourceIdentity = $"{catalog.Source.DisplayName} Community Mod";
        OpenRawTomlCommand.CanExecuteChanged += OpenRawTomlCommand_CanExecuteChanged;
        Sections = CreateSections();
        discardCommand = new SettingsActionCommand(Discard, () => HasPendingChanges);
        saveCommand = new AsyncSettingsActionCommand(SaveAsync, () => CanSave);

        TryLoadConfiguration();
        placementsByPath = CreatePlacements();
        settings.AddRange(catalog.VisibleSettings
            .Select(setting => new SettingsRowViewModel(
                setting,
                placementsByPath[setting.Path],
                GetValueState(setting),
                editSession is not null,
                StageValue,
                StageRemove,
                RevertDraft,
                SetInputValidity))
            .OrderBy(setting => placementsByPath[setting.Path].Section)
            .ThenBy(setting => placementsByPath[setting.Path].GroupOrder)
            .ThenBy(setting => setting.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(setting => placementsByPath[setting.Path].FamilyOrder)
            .ThenBy(setting => placementsByPath[setting.Path].FamilyId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(setting => placementsByPath[setting.Path].MemberOrder)
            .ThenBy(setting => placementsByPath[setting.Path].SortKey, StringComparer.OrdinalIgnoreCase)
            .ToList());
        settingsByPath = settings.ToDictionary(
            setting => setting.Path,
            StringComparer.OrdinalIgnoreCase);
        RefreshKeybindingConflicts();

        FilteredSettings = CollectionViewSource.GetDefaultView(settings);
        FilteredSettings.Filter = ShouldInclude;
        if (layoutProvider.ShowGroupHeadings)
        {
            FilteredSettings.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(SettingsRowViewModel.Group)));
        }
        FilteredSettings.CollectionChanged += (_, _) => NotifyFilterSummaryChanged();

        SearchToggleCommand = new SettingsActionCommand(ToggleSearch);
        SelectSection(layoutProvider.Sections[0].Id);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceIdentity { get; }

    public IReadOnlyList<SettingsSectionViewModel> Sections { get; }

    public ICollectionView FilteredSettings { get; }

    public ICommand NavigateHomeCommand { get; }

    public ICommand OpenRawTomlCommand { get; }

    public ICommand DiscardCommand => discardCommand;

    public ICommand SaveCommand => saveCommand;

    public ICommand SearchToggleCommand { get; }

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
            RefreshFilter();
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
            OnPropertyChanged(nameof(SearchToggleHelp));
            SaveUiPreferences();
        }
    }

    public string SearchToggleHelp =>
        IsSearchVisible ? "Close settings search" : "Search all settings";

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

    public bool IsSettingsListVisible => !IsAboutSelected;

    public int VisibleSettingCount => FilteredSettings.Cast<object>().Count();

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
        IsSettingsListVisible && FilteredSettings.IsEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool CanOpenRawToml => OpenRawTomlCommand.CanExecute(null);

    public string OpenRawTomlAvailability =>
        CanOpenRawToml
            ? "Open the active configuration as raw TOML."
            : "Raw TOML becomes available after the launcher selects an active configuration.";

    public bool IsConfigurationReady => editSession is not null;

    public string DetectedRuntime => settingsDiagnostics.DetectedRuntime;

    public string SemanticGroupingStatus =>
        settingsDiagnostics.SemanticGroupingStatus;

    public string SemanticGroupingReason =>
        settingsDiagnostics.SemanticGroupingReason;

    public string SettingsLayoutName =>
        settingsDiagnostics.SettingsLayoutName;

    public string ConfigurationStatus =>
        IsConfigurationReady
            ? "Changes are staged until you save."
            : "Select a game folder with a supported configuration to enable editing.";

    public int PendingChangeCount => editSession?.PendingChangeCount ?? 0;

    public bool HasPendingChanges => PendingChangeCount > 0;

    public bool HasInvalidInput => invalidInputPaths.Count > 0;

    public string PendingChangesText =>
        PendingChangeCount switch
        {
            0 => "No unsaved changes",
            1 => "1 unsaved change",
            _ => $"{PendingChangeCount} unsaved changes",
        };

    public string PendingApplyTimingText =>
        LauncherConfigurationApplySummary.From(
            settings
                .Where(setting => setting.IsDirty)
                .Select(setting => setting.Setting.ApplyBehavior))
            .Text;

    public bool CanSave =>
        IsConfigurationReady
        && HasPendingChanges
        && !HasInvalidInput
        && ConfigurationPathMatchesLoadedSession();

    public string SaveAvailability =>
        HasInvalidInput
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

        TryLoadConfiguration();
        RefreshAllStates();
        NotifySessionChanged();
    }

    private SettingsSectionViewModel SelectedSectionItem =>
        Sections.Single(section => section.Id == selectedSection);

    private ReadOnlyCollection<SettingsSectionViewModel> CreateSections()
    {
        if (layoutProvider.Sections.Count == 0
            || layoutProvider.Sections.Any(
                section => section.Id == LauncherSettingsSection.About))
        {
            throw new InvalidOperationException(
                "The settings layout must provide at least one content section and must not own About.");
        }

        var duplicateSection = layoutProvider.Sections
            .GroupBy(section => section.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSection is not null)
        {
            throw new InvalidOperationException(
                $"The settings layout defines section '{duplicateSection.Key}' more than once.");
        }

        var sections = layoutProvider.Sections
            .Select(
                section => new SettingsSectionViewModel(
                    section.Id,
                    section.Title,
                    section.Description,
                    section.AutomationName,
                    SelectSection))
            .ToList();
        sections.Add(
            new(
                LauncherSettingsSection.About,
                "About",
                "Release source, configuration ownership, and technical escape hatches.",
                "About launcher settings",
                SelectSection));
        return sections.AsReadOnly();
    }

    private ReadOnlyDictionary<string, LauncherSettingsPlacement> CreatePlacements()
    {
        var declaredSections = layoutProvider.Sections
            .Select(section => section.Id)
            .ToHashSet();
        var placements = new Dictionary<string, LauncherSettingsPlacement>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var setting in catalog.VisibleSettings)
        {
            var placement = layoutProvider.Place(setting);
            if (!declaredSections.Contains(placement.Section))
            {
                throw new InvalidOperationException(
                    $"Settings layout '{layoutProvider.Id}' placed '{setting.Path}' "
                    + $"in undeclared section '{placement.Section}'.");
            }

            placements.Add(setting.Path, placement);
        }

        return new(placements);
    }

    private void SelectSection(LauncherSettingsSection section)
    {
        selectedSection = section;
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
        OnPropertyChanged(nameof(IsSettingsListVisible));
        RefreshFilter();
    }

    private void ToggleSearch()
    {
        if (IsSearchVisible)
        {
            SearchText = string.Empty;
            IsSearchVisible = false;
            return;
        }

        IsSearchVisible = true;
    }

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

    private bool ShouldInclude(object item)
    {
        if (item is not SettingsRowViewModel setting || IsAboutSelected)
        {
            return false;
        }

        var searchMatches =
            string.IsNullOrWhiteSpace(SearchText)
            || setting.Matches(SearchText.Trim());
        return searchMatches
            && (IsSearchActive
                || placementsByPath[setting.Path].Section == selectedSection);
    }

    private void TryLoadConfiguration()
    {
        editSession = null;
        loadedConfigurationPath = null;
        var path = configurationPathProvider();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            OperationStatus = string.Empty;
            return;
        }

        try
        {
            var contents = File.ReadAllBytes(path);
            var load = LauncherConfigurationEditSession.Load(contents, catalog, out var session);
            if (!load.IsValid || session is null)
            {
                OperationStatus =
                    $"Editing is unavailable because the TOML could not be loaded safely: {load.Error?.Message}";
                return;
            }

            editSession = session;
            loadedConfigurationPath = Path.GetFullPath(path);
            OperationStatus = string.Empty;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            OperationStatus = $"Editing is unavailable: {exception.Message}";
        }
    }

    private SettingsValueState GetValueState(LauncherConfigurationSetting setting)
    {
        if (editSession is null)
        {
            var defaultValue = SettingsRowViewModelValue(setting.DefaultValue);
            return new(
                defaultValue,
                defaultValue,
                false,
                defaultValue,
                false,
                false);
        }

        var state = editSession.GetState(setting);
        return new(
            state.DefaultValue,
            state.SavedEffectiveValue,
            state.SavedHasOverride,
            state.DraftEffectiveValue,
            state.DraftHasOverride,
            state.IsDirty);
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
        if (editSession is null)
        {
            return false;
        }

        var result = editSession.StageSet(setting, renderedTomlValue);
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

    private bool StageRemove(LauncherConfigurationSetting setting)
    {
        if (editSession is null)
        {
            return false;
        }

        var result = editSession.StageRemove(setting);
        OperationStatus = result.IsValid ? string.Empty : result.Error?.Message ?? "The override could not be removed.";
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
        if (editSession is null)
        {
            return false;
        }

        var result = editSession.Revert(setting);
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
            ? invalidInputPaths.Remove(setting.Path)
            : invalidInputPaths.Add(setting.Path);
        if (!changed)
        {
            return;
        }

        OnPropertyChanged(nameof(HasInvalidInput));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveAvailability));
        saveCommand.RaiseCanExecuteChanged();
    }

    private void Discard()
    {
        var selectedConfigurationChanged = !ConfigurationPathMatchesLoadedSession();
        editSession?.Discard();
        if (selectedConfigurationChanged)
        {
            TryLoadConfiguration();
        }

        OperationStatus = selectedConfigurationChanged
            ? "Unsaved changes discarded and the selected configuration reloaded."
            : "Unsaved changes discarded.";
        RefreshAllStates();
        NotifySessionChanged();
    }

    private async Task SaveAsync()
    {
        if (editSession is null)
        {
            return;
        }

        if (!ConfigurationPathMatchesLoadedSession())
        {
            OperationStatus =
                "The selected configuration changed after this editing session began. Discard these edits and reload before saving.";
            return;
        }

        OperationStatus = "Saving changes…";
        var result = await editSession.SaveAsync(loadedConfigurationPath, store);
        OperationStatus = result.State switch
        {
            AtomicTomlWriteState.Succeeded =>
                "Changes saved. A backup of the previous TOML is available beside the configuration.",
            AtomicTomlWriteState.NoChange => "No configuration changes were needed.",
            AtomicTomlWriteState.Conflict =>
                "The TOML changed outside the launcher. Those external edits were preserved; reload before saving.",
            AtomicTomlWriteState.Invalid =>
                $"Nothing was written because the TOML is not safe to update: {result.ValidationError?.Message}",
            AtomicTomlWriteState.NoConfigurationSelected =>
                "Select a supported configuration before saving.",
            _ => $"The changes could not be saved: {result.Error}",
        };

        RefreshAllStates();
        NotifySessionChanged();
    }

    private void RefreshAllStates()
    {
        foreach (var setting in settings)
        {
            setting.UpdateState(GetValueState(setting.Setting), editSession is not null);
        }

        RefreshKeybindingConflicts();
    }

    private void RefreshState(LauncherConfigurationSetting setting)
    {
        if (settingsByPath.TryGetValue(setting.Path, out var row))
        {
            row.UpdateState(GetValueState(setting), editSession is not null);
        }
    }

    private void RefreshKeybindingConflicts()
    {
        var keybindings = settings
            .Where(row => row.IsKeybindingEditor)
            .ToArray();
        foreach (var row in keybindings)
        {
            row.SetKeybindingConflict(null);
        }

        var assignments = keybindings
            .Select(row => row.ReadKeybindingAssignment())
            .OfType<LauncherKeybindingAssignment>()
            .ToArray();
        var conflicts = LauncherKeybindingConflictDetector.FindConflicts(assignments);
        foreach (var row in keybindings)
        {
            var rowConflicts = conflicts
                .Where(conflict =>
                    string.Equals(conflict.First.Path, row.Path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(conflict.Second.Path, row.Path, StringComparison.OrdinalIgnoreCase))
                .Select(conflict =>
                {
                    var other = string.Equals(
                        conflict.First.Path,
                        row.Path,
                        StringComparison.OrdinalIgnoreCase)
                        ? conflict.Second
                        : conflict.First;
                    return $"{conflict.Chord.Display} conflicts with {other.Title}.";
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (rowConflicts.Length > 0)
            {
                row.SetKeybindingConflict(string.Join(' ', rowConflicts));
            }
        }
    }

    private void NotifySessionChanged()
    {
        OnPropertyChanged(nameof(IsConfigurationReady));
        OnPropertyChanged(nameof(ConfigurationStatus));
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingChangesText));
        OnPropertyChanged(nameof(PendingApplyTimingText));
        OnPropertyChanged(nameof(HasInvalidInput));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveAvailability));
        discardCommand.RaiseCanExecuteChanged();
        saveCommand.RaiseCanExecuteChanged();
    }

    private bool ConfigurationPathMatchesLoadedSession()
    {
        var currentPath = configurationPathProvider();
        if (string.IsNullOrWhiteSpace(currentPath)
            || string.IsNullOrWhiteSpace(loadedConfigurationPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(currentPath),
                loadedConfigurationPath,
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

    private void RefreshFilter()
    {
        FilteredSettings.Refresh();
        UpdateVisibleFamilyHeaders();
        NotifyFilterSummaryChanged();
    }

    private void UpdateVisibleFamilyHeaders()
    {
        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in settings)
        {
            setting.SetFamilyHeaderVisible(false);
        }

        foreach (var setting in FilteredSettings.Cast<SettingsRowViewModel>())
        {
            if (setting.FamilyId.Length > 0 && seenFamilies.Add(setting.FamilyId))
            {
                setting.SetFamilyHeaderVisible(true);
            }
        }
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
