using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed class SettingsSectionViewModel : INotifyPropertyChanged
{
    private bool isSelected;

    public SettingsSectionViewModel(
        LauncherSettingsSection id,
        string title,
        string description,
        string automationName,
        Action<LauncherSettingsSection> select)
    {
        Id = id;
        Title = title;
        Description = description;
        AutomationName = automationName;
        SelectCommand = new SettingsActionCommand(() => select(id));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LauncherSettingsSection Id { get; }

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
