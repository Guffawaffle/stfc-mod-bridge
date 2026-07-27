using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly LauncherEnvironmentProbe environmentProbe;
    private LauncherEnvironmentSnapshot snapshot;
    private string selectionFeedback = "Selection is explicit: the launcher will never choose a folder silently.";

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

    public string SelectedGameDirectory =>
        snapshot.SelectedGameDirectory ?? "No game folder has been confirmed.";

    public string DiscoverySummary
    {
        get
        {
            var validCount = snapshot.Discovery.ValidCandidates.Count;
            var inspectedCount = snapshot.Discovery.Candidates.Count;
            return $"Inspected {inspectedCount} bounded candidate{(inspectedCount == 1 ? string.Empty : "s")}; "
                + $"{validCount} valid.";
        }
    }

    public IReadOnlyList<string> CandidateSummaries =>
        snapshot.Discovery.Candidates
            .Select(
                candidate =>
                {
                    var state = candidate.Validation.IsValid ? "VALID" : candidate.Validation.Code.ToString().ToUpperInvariant();
                    var provenance = string.Join(
                        ", ",
                        candidate.Evidence.Select(evidence => evidence.Source).Distinct());
                    return $"{state} • {candidate.Confidence} • {candidate.GameDirectory} • {provenance}";
                })
            .ToArray();

    public IReadOnlyList<string> HealthSummaries =>
        snapshot.HealthDimensions
            .Select(dimension => $"{dimension.Title}: {dimension.Detail}")
            .ToArray();

    public string SelectionFeedback => selectionFeedback;

    public string? InitialBrowseDirectory => snapshot.SelectedGameDirectory;

    public ICommand RefreshCommand { get; }

    public static MainWindowViewModel CreateDefault()
    {
        var installLayout = PerUserInstallLayout.FromCurrentUser();
        var installDiscovery = new GameInstallDiscovery(
            new JsonGameInstallSelectionStore(installLayout.StateDirectory),
            [
                OfficialLauncherSettingsCandidateProvider.FromCurrentUser(),
                BoundedGameInstallCandidateProvider.FromCurrentMachine(),
            ]);

        return new(
            new LauncherEnvironmentProbe(
                new SystemGameProcessInspector(),
                installLayout,
                installDiscovery));
    }

    public void ConfirmManualSelection(string gameDirectory)
    {
        var candidate = environmentProbe.ConfirmManualSelection(gameDirectory);
        selectionFeedback = candidate.Validation.Message;
        Refresh();
        OnPropertyChanged(nameof(SelectionFeedback));
    }

    private void Refresh()
    {
        snapshot = environmentProbe.Capture();
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(ProcessStatus));
        OnPropertyChanged(nameof(ProgramDirectory));
        OnPropertyChanged(nameof(SelectedGameDirectory));
        OnPropertyChanged(nameof(DiscoverySummary));
        OnPropertyChanged(nameof(CandidateSummaries));
        OnPropertyChanged(nameof(HealthSummaries));
        OnPropertyChanged(nameof(InitialBrowseDirectory));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
