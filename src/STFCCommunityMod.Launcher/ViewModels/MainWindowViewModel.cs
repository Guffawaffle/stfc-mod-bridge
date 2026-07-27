using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly LauncherEnvironmentProbe environmentProbe;
    private LauncherEnvironmentSnapshot snapshot;

    private MainWindowViewModel(LauncherEnvironmentProbe environmentProbe)
    {
        this.environmentProbe = environmentProbe;
        snapshot = environmentProbe.Capture();
        RefreshCommand = new RelayCommand(Refresh);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusTitle => snapshot.StatusTitle;

    public string StatusDetail => snapshot.StatusDetail;

    public string ProcessStatus => snapshot.IsGameRunning ? "STFC is running" : "STFC is not running";

    public string ProgramDirectory => snapshot.InstallLayout.ProgramDirectory;

    public ICommand RefreshCommand { get; }

    public static MainWindowViewModel CreateDefault()
    {
        return new(
            new LauncherEnvironmentProbe(
                new SystemGameProcessInspector(),
                PerUserInstallLayout.FromCurrentUser()));
    }

    private void Refresh()
    {
        snapshot = environmentProbe.Capture();
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(ProcessStatus));
        OnPropertyChanged(nameof(ProgramDirectory));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
