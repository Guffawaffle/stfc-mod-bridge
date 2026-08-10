using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

internal sealed class LauncherProviderSession : IDisposable
{
    private readonly LauncherRuntimeCompositionSlot runtimeComposition;

    public LauncherProviderSession(
        LauncherProviderSelectionResolution resolution,
        LauncherProviderShellAccess shellAccess,
        LauncherDistributionProvider provider,
        LauncherProviderReleaseChannel releaseChannel,
        LauncherStartupComposition startupComposition,
        ReviewedRuntimeActivation? reviewedRuntimeActivation,
        MainWindowViewModel viewModel,
        Func<LauncherStartupComposition, SettingsViewModel> settingsFactory)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(releaseChannel);
        ArgumentNullException.ThrowIfNull(startupComposition);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(settingsFactory);
        Resolution = resolution;
        ShellAccess = shellAccess;
        Provider = provider;
        ReleaseChannel = releaseChannel;
        runtimeComposition = new(
            provider,
            releaseChannel,
            startupComposition,
            reviewedRuntimeActivation?.EvidenceSourceSha256);
        viewModel.ConfigureFeatureRemediation(
            () => runtimeComposition.Current.ActivationPlan,
            () => runtimeComposition.Current.BattleFeatures);
        ApplicationComposition = new(
            new(viewModel, () => settingsFactory(runtimeComposition.Current)),
            () => runtimeComposition.Current.ActivationPlan);
    }

    public LauncherApplicationComposition ApplicationComposition { get; }

    public LauncherProviderSelectionResolution Resolution { get; }

    public LauncherProviderShellAccess ShellAccess { get; }

    public LauncherDistributionProvider Provider { get; }

    public LauncherProviderReleaseChannel ReleaseChannel { get; }

    public LauncherStartupComposition StartupComposition => runtimeComposition.Current;

    public LauncherBattleFeatureSnapshot BattleFeatures => runtimeComposition.Current.BattleFeatures;

    public MainWindowViewModel ViewModel => ApplicationComposition.SharedServices.Foundation;

    public LauncherProviderAtomicSwitchCoordinator SwitchCoordinator =>
        ViewModel.ProviderSwitchCoordinator
        ?? throw new InvalidOperationException("Provider-switch composition is unavailable.");

    public LauncherFeatureRemediationCoordinator? FeatureRemediationCoordinator =>
        ViewModel.FeatureRemediationCoordinator;

    public bool RefreshRuntimeComposition(
        ReviewedRuntimeActivation? activation,
        LauncherBattlePreferences battlePreferences) =>
        runtimeComposition.Refresh(activation, battlePreferences);

    public void Dispose() => ApplicationComposition.Dispose();
}

internal sealed class LauncherRuntimeCompositionSlot(
    LauncherDistributionProvider provider,
    LauncherProviderReleaseChannel releaseChannel,
    LauncherStartupComposition initial,
    string? initialEvidenceSha256)
{
    private string? evidenceSha256 = initialEvidenceSha256;
    private LauncherBattlePreferences battlePreferences = new(
        initial.BattleFeatures.BattleCollection.Preference,
        initial.BattleFeatures.FleetCollection.Preference);

    public LauncherStartupComposition Current { get; private set; } = initial;

    public bool Refresh(
        ReviewedRuntimeActivation? activation,
        LauncherBattlePreferences nextBattlePreferences)
    {
        ArgumentNullException.ThrowIfNull(nextBattlePreferences);
        var nextEvidence = activation?.EvidenceSourceSha256;
        if (string.Equals(evidenceSha256, nextEvidence, StringComparison.OrdinalIgnoreCase)
            && battlePreferences == nextBattlePreferences)
        {
            return false;
        }
        Current = LauncherStartupComposition.Create(
            provider,
            releaseChannel,
            activation,
            nextBattlePreferences);
        evidenceSha256 = nextEvidence;
        battlePreferences = nextBattlePreferences;
        return true;
    }
}
