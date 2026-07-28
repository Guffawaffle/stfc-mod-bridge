using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace STFCCommunityMod.Launcher.ViewModels;

public enum SettingsSection
{
    General,
    Interface,
    Graphics,
    Notifications,
    Hotkeys,
    DataSync,
    Advanced,
    About,
}

public sealed class SettingsSectionViewModel : INotifyPropertyChanged
{
    private bool isSelected;

    public SettingsSectionViewModel(
        SettingsSection id,
        string title,
        string description,
        string automationName,
        Action<SettingsSection> select)
    {
        Id = id;
        Title = title;
        Description = description;
        AutomationName = automationName;
        SelectCommand = new SettingsActionCommand(() => select(id));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsSection Id { get; }

    public string Title { get; }

    public string Description { get; }

    public string AutomationName { get; }

    public ICommand SelectCommand { get; }

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
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
