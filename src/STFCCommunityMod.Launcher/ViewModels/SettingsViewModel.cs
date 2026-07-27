using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public const string AllCategories = "All categories";
    public const string OtherCategory = "Other";

    private readonly List<SettingsRowViewModel> settings;
    private string searchText = string.Empty;
    private string selectedCategory = AllCategories;

    public SettingsViewModel(
        IEnumerable<LauncherConfigurationSetting> settings,
        string sourceIdentity,
        ICommand openRawTomlCommand,
        Func<LauncherConfigurationSetting, ICommand> removeOverrideCommandFactory,
        Func<LauncherConfigurationSetting, SettingsValueState>? valueStateSelector = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(openRawTomlCommand);
        ArgumentNullException.ThrowIfNull(removeOverrideCommandFactory);

        SourceIdentity = string.IsNullOrWhiteSpace(sourceIdentity)
            ? throw new ArgumentException("A release source identity is required.", nameof(sourceIdentity))
            : sourceIdentity;
        OpenRawTomlCommand = openRawTomlCommand;
        OpenRawTomlCommand.CanExecuteChanged += OpenRawTomlCommand_CanExecuteChanged;

        var stateSelector = valueStateSelector
            ?? (setting => new SettingsValueState(setting.DefaultValue, false, setting.Apply));

        this.settings = settings
            .Select(setting => new SettingsRowViewModel(
                setting,
                stateSelector(setting),
                removeOverrideCommandFactory(setting)))
            .OrderBy(setting => setting.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(setting => setting.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Categories =
        [
            AllCategories,
            .. this.settings
                .Select(setting => setting.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase),
        ];

        FilteredSettings = CollectionViewSource.GetDefaultView(this.settings);
        FilteredSettings.Filter = ShouldInclude;
        FilteredSettings.CollectionChanged += (_, _) => NotifyFilterSummaryChanged();
    }

    public SettingsViewModel(
        IEnumerable<LauncherConfigurationSetting> settings,
        string sourceIdentity,
        ICommand openRawTomlCommand,
        Action<LauncherConfigurationSetting> removeOverride,
        Func<LauncherConfigurationSetting, SettingsValueState>? valueStateSelector = null)
        : this(
            settings,
            sourceIdentity,
            openRawTomlCommand,
            CreateRemoveOverrideCommandFactory(removeOverride),
            valueStateSelector)
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceIdentity { get; }

    public IReadOnlyList<string> Categories { get; }

    public ICollectionView FilteredSettings { get; }

    public ICommand OpenRawTomlCommand { get; }

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
            RefreshFilter();
        }
    }

    public string SelectedCategory
    {
        get => selectedCategory;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? AllCategories : value;
            if (selectedCategory == normalized)
            {
                return;
            }

            selectedCategory = normalized;
            OnPropertyChanged();
            RefreshFilter();
        }
    }

    public int VisibleSettingCount => FilteredSettings.Cast<object>().Count();

    public Visibility EmptyStateVisibility => FilteredSettings.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public bool CanOpenRawToml => OpenRawTomlCommand.CanExecute(null);

    public string OpenRawTomlAvailability => CanOpenRawToml
        ? "Open the active configuration as raw TOML."
        : "Raw TOML becomes available after the launcher selects an active configuration.";

    private bool ShouldInclude(object item)
    {
        if (item is not SettingsRowViewModel setting)
        {
            return false;
        }

        var categoryMatches = SelectedCategory == AllCategories
            || string.Equals(setting.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase);
        var searchMatches = string.IsNullOrWhiteSpace(SearchText) || setting.Matches(SearchText.Trim());
        return categoryMatches && searchMatches;
    }

    private static Func<LauncherConfigurationSetting, ICommand> CreateRemoveOverrideCommandFactory(
        Action<LauncherConfigurationSetting> removeOverride)
    {
        ArgumentNullException.ThrowIfNull(removeOverride);
        return setting => new SettingsActionCommand(() => removeOverride(setting));
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
