using System.ComponentModel;
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
    private readonly ILauncherUiPreferencesStore? uiPreferencesStore;
    private readonly List<SettingsRowViewModel> settings = [];
    private readonly SettingsActionCommand discardCommand;
    private readonly AsyncSettingsActionCommand saveCommand;
    private LauncherConfigurationEditSession? editSession;
    private string? loadedConfigurationPath;
    private string searchText = string.Empty;
    private SettingsSection selectedSection = SettingsSection.General;
    private string operationStatus = string.Empty;
    private bool isSearchVisible;

    public SettingsViewModel(
        LauncherConfigurationCatalog catalog,
        ICommand navigateHomeCommand,
        ICommand openRawTomlCommand,
        Func<string?> configurationPathProvider,
        AtomicTomlStore? store = null,
        ILauncherUiPreferencesStore? uiPreferencesStore = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        NavigateHomeCommand = navigateHomeCommand ?? throw new ArgumentNullException(nameof(navigateHomeCommand));
        OpenRawTomlCommand = openRawTomlCommand ?? throw new ArgumentNullException(nameof(openRawTomlCommand));
        this.configurationPathProvider =
            configurationPathProvider ?? throw new ArgumentNullException(nameof(configurationPathProvider));
        this.store = store ?? new AtomicTomlStore();
        this.uiPreferencesStore = uiPreferencesStore;
        isSearchVisible = uiPreferencesStore?.Load().SettingsSearchVisible ?? false;

        SourceIdentity = $"{catalog.Source.DisplayName} Community Mod";
        OpenRawTomlCommand.CanExecuteChanged += OpenRawTomlCommand_CanExecuteChanged;
        Sections = CreateSections();

        TryLoadConfiguration();
        settings.AddRange(catalog.VisibleSettings
            .Select(setting => new SettingsRowViewModel(
                setting,
                GetValueState(setting),
                editSession is not null,
                StageValue,
                StageRemove))
            .OrderBy(setting => ResolveSection(setting.Setting))
            .ThenBy(setting => setting.Title, StringComparer.OrdinalIgnoreCase)
            .ToList());

        FilteredSettings = CollectionViewSource.GetDefaultView(settings);
        FilteredSettings.Filter = ShouldInclude;
        FilteredSettings.CollectionChanged += (_, _) => NotifyFilterSummaryChanged();

        discardCommand = new SettingsActionCommand(Discard, () => HasPendingChanges);
        saveCommand = new AsyncSettingsActionCommand(SaveAsync, () => CanSave);
        SearchToggleCommand = new SettingsActionCommand(ToggleSearch);
        SelectSection(SettingsSection.General);
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

    public SettingsSection SelectedSection => selectedSection;

    public string WorkspaceTitle =>
        IsSearchActive ? "Search results" : SelectedSectionItem.Title;

    public string WorkspaceDescription =>
        IsSearchActive
            ? "Results across every mod setting category."
            : SelectedSectionItem.Description;

    public bool IsAboutSelected => !IsSearchActive && selectedSection == SettingsSection.About;

    public bool IsGeneralSelected => !IsSearchActive && selectedSection == SettingsSection.General;

    public bool IsSettingsListVisible => !IsAboutSelected;

    public int VisibleSettingCount => FilteredSettings.Cast<object>().Count();

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

    public string ConfigurationStatus =>
        IsConfigurationReady
            ? "Changes are staged until you save."
            : "Select a game folder with a supported configuration to enable editing.";

    public int PendingChangeCount => editSession?.PendingChangeCount ?? 0;

    public bool HasPendingChanges => PendingChangeCount > 0;

    public string PendingChangesText =>
        PendingChangeCount switch
        {
            0 => "No unsaved changes",
            1 => "1 unsaved change",
            _ => $"{PendingChangeCount} unsaved changes",
        };

    public bool CanSave =>
        IsConfigurationReady
        && HasPendingChanges
        && ConfigurationPathMatchesLoadedSession();

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

    private IReadOnlyList<SettingsSectionViewModel> CreateSections() =>
    [
        new(
            SettingsSection.General,
            "General",
            "Core mod behavior and ordinary preferences.",
            "General settings",
            SelectSection),
        new(
            SettingsSection.Interface,
            "Interface",
            "Game interface behavior and quality-of-life controls.",
            "Interface settings",
            SelectSection),
        new(
            SettingsSection.Graphics,
            "Graphics",
            "Display, scaling, loading, and zoom behavior.",
            "Graphics settings",
            SelectSection),
        new(
            SettingsSection.Notifications,
            "Notifications",
            "Choose which events alert you and how.",
            "Notification settings",
            SelectSection),
        new(
            SettingsSection.Hotkeys,
            "Hotkeys",
            "Review bindings and, in the next adapter, capture keys safely.",
            "Hotkey settings",
            SelectSection),
        new(
            SettingsSection.DataSync,
            "Data Sync",
            "Control supported sync feeds and destination behavior.",
            "Data Sync settings",
            SelectSection),
        new(
            SettingsSection.Advanced,
            "Advanced",
            "Experimental, patch, diagnostic, and support-directed controls.",
            "Advanced settings",
            SelectSection),
        new(
            SettingsSection.About,
            "About",
            "Release source, configuration ownership, and technical escape hatches.",
            "About launcher settings",
            SelectSection),
    ];

    private void SelectSection(SettingsSection section)
    {
        selectedSection = section;
        foreach (var item in Sections)
        {
            item.IsSelected = item.Id == section;
        }

        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(WorkspaceDescription));
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
            uiPreferencesStore.Save(new LauncherUiPreferences(IsSearchVisible));
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
            && (IsSearchActive || ResolveSection(setting.Setting) == selectedSection);
    }

    private static SettingsSection ResolveSection(LauncherConfigurationSetting setting)
    {
        if (setting.Control == LauncherConfigurationControl.NotificationPolicy
            || string.Equals(setting.Category, "notifications", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsSection.Notifications;
        }

        if (setting.Control == LauncherConfigurationControl.Keybinding
            || string.Equals(setting.Category, "input", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsSection.Hotkeys;
        }

        return setting.Category.ToLowerInvariant() switch
        {
            "graphics" => SettingsSection.Graphics,
            "ui" or "buffs" => SettingsSection.Interface,
            "sync" or "sidecar" => SettingsSection.DataSync,
            "advanced" or "patches" or "battle_log_decoder" => SettingsSection.Advanced,
            _ => setting.Stability is LauncherConfigurationStability.Advanced
                    or LauncherConfigurationStability.Experimental
                ? SettingsSection.Advanced
                : SettingsSection.General,
        };
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
            return new(SettingsRowViewModelValue(setting.DefaultValue), false, setting.Apply);
        }

        var state = editSession.GetState(setting);
        return new(
            state.EffectiveRenderedValue ?? state.DefaultValue,
            state.HasOverride,
            setting.Apply,
            state.IsStaged,
            state.IsRemoval);
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

        RefreshAllStates();
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

        RefreshAllStates();
        NotifySessionChanged();
        return true;
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
    }

    private void NotifySessionChanged()
    {
        OnPropertyChanged(nameof(IsConfigurationReady));
        OnPropertyChanged(nameof(ConfigurationStatus));
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingChangesText));
        OnPropertyChanged(nameof(CanSave));
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
        NotifyFilterSummaryChanged();
    }

    private void NotifyFilterSummaryChanged()
    {
        OnPropertyChanged(nameof(VisibleSettingCount));
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
