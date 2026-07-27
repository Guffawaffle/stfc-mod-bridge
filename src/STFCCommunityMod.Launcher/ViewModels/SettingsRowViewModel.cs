using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed class SettingsRowViewModel : INotifyPropertyChanged
{
    private readonly ICommand requestedRemoveOverrideCommand;
    private readonly SettingsActionCommand removeOverrideCommand;

    internal SettingsRowViewModel(
        LauncherConfigurationSetting setting,
        SettingsValueState valueState,
        ICommand removeOverrideCommand)
    {
        Setting = setting;
        requestedRemoveOverrideCommand = removeOverrideCommand
            ?? throw new ArgumentNullException(nameof(removeOverrideCommand));

        Path = setting.Path;
        Title = string.IsNullOrWhiteSpace(setting.Title) ? setting.Path : setting.Title;
        Description = string.IsNullOrWhiteSpace(setting.Description)
            ? "No description is available for this setting."
            : setting.Description;
        Category = FormatCategory(setting.Category);
        Control = FormatMetadata(setting.Control);
        ValueKind = FormatMetadata(setting.ValueKind);
        DefaultValue = FormatValue(setting.DefaultValue);
        EffectiveValue = FormatValue(valueState.EffectiveValue ?? setting.DefaultValue);
        HasOverride = valueState.HasOverride;
        ApplyState = string.IsNullOrWhiteSpace(valueState.ApplyState)
            ? "Apply behavior is not available."
            : valueState.ApplyState;
        Stability = FormatMetadata(setting.Stability);
        Platforms = FormatMetadata(setting.Platforms);
        SourceSupport = FormatMetadata(setting.SourceSupport);

        this.removeOverrideCommand = new SettingsActionCommand(
            () => requestedRemoveOverrideCommand.Execute(null),
            () => HasOverride && requestedRemoveOverrideCommand.CanExecute(null));
        requestedRemoveOverrideCommand.CanExecuteChanged += RemoveOverrideCommand_CanExecuteChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LauncherConfigurationSetting Setting { get; }

    public string Path { get; }

    public string Title { get; }

    public string Description { get; }

    public string Category { get; }

    public string Control { get; }

    public string ValueKind { get; }

    public string DefaultValue { get; }

    public string EffectiveValue { get; }

    public bool HasOverride { get; }

    public string EffectiveState => HasOverride ? "Override" : "Default";

    public string ApplyState { get; }

    public string Stability { get; }

    public string Platforms { get; }

    public string SourceSupport { get; }

    public string EditorLabel => $"{Control} · {ValueKind}";

    public string EditorAutomationName => $"{Title} editor placeholder. {Control} control for {ValueKind} values.";

    public ICommand RemoveOverrideCommand => removeOverrideCommand;

    public bool CanRemoveOverride => removeOverrideCommand.CanExecute(null);

    public string RemoveOverrideAvailability => HasOverride
        ? CanRemoveOverride
            ? $"Remove the override for {Title} and use its default."
            : "Override removal becomes available after the launcher selects a writable configuration."
        : "This setting already uses its default value.";

    internal bool Matches(string searchText)
    {
        return Contains(Path, searchText)
            || Contains(Title, searchText)
            || Contains(Description, searchText)
            || Contains(Category, searchText)
            || Contains(Control, searchText)
            || Contains(ValueKind, searchText);
    }

    private static bool Contains(string candidate, string searchText)
    {
        return candidate.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatMetadata(object? value)
    {
        var formatted = FormatValue(value);
        return formatted == "Not specified" ? "Unspecified" : formatted;
    }

    private static string FormatCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return SettingsViewModel.OtherCategory;
        }

        var words = category.Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(words);
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "Not specified",
            bool booleanValue => booleanValue ? "true" : "false",
            string stringValue when string.IsNullOrEmpty(stringValue) => "(empty)",
            string stringValue => stringValue,
            IEnumerable values => string.Join(", ", values.Cast<object?>().Select(FormatValue)),
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture) ?? "Not specified",
            _ => value.ToString() ?? "Not specified",
        };
    }

    private void RemoveOverrideCommand_CanExecuteChanged(object? sender, EventArgs e)
    {
        removeOverrideCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveOverride));
        OnPropertyChanged(nameof(RemoveOverrideAvailability));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
