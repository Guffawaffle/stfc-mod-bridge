using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

internal sealed class LauncherProviderSession : IDisposable
{
    private readonly LauncherRuntimeCompositionSlot runtimeComposition;
    private readonly Func<LauncherBattlePreferences> battlePreferencesProvider;

    public LauncherProviderSession(
        LauncherProviderSelectionResolution resolution,
        LauncherProviderShellAccess shellAccess,
        LauncherDistributionProvider provider,
        LauncherProviderReleaseChannel releaseChannel,
        LauncherStartupComposition startupComposition,
        ReviewedRuntimeActivation? reviewedRuntimeActivation,
        Func<LauncherBattlePreferences> battlePreferencesProvider,
        MainWindowViewModel viewModel,
        Func<LauncherStartupComposition, SettingsViewModel> settingsFactory)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(releaseChannel);
        ArgumentNullException.ThrowIfNull(startupComposition);
        this.battlePreferencesProvider = battlePreferencesProvider
            ?? throw new ArgumentNullException(nameof(battlePreferencesProvider));
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
            GetBattleFeatures);
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

    public LauncherBattleFeatureSnapshot BattleFeatures => GetBattleFeatures();

    public MainWindowViewModel ViewModel => ApplicationComposition.SharedServices.Foundation;

    public LauncherProviderAtomicSwitchCoordinator SwitchCoordinator =>
        ViewModel.ProviderSwitchCoordinator
        ?? throw new InvalidOperationException("Provider-switch composition is unavailable.");

    public LauncherFeatureRemediationCoordinator? FeatureRemediationCoordinator =>
        ViewModel.FeatureRemediationCoordinator;

    public bool RefreshRuntimeComposition(ReviewedRuntimeActivation? activation) =>
        runtimeComposition.Refresh(activation, battlePreferencesProvider());

    public bool RefreshBattlePreferences() =>
        runtimeComposition.RefreshBattlePreferences(battlePreferencesProvider());

    public void Dispose() => ApplicationComposition.Dispose();

    private LauncherBattleFeatureSnapshot GetBattleFeatures()
    {
        RefreshBattlePreferences();
        return runtimeComposition.Current.BattleFeatures;
    }
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

    public bool RefreshBattlePreferences(LauncherBattlePreferences nextBattlePreferences)
    {
        ArgumentNullException.ThrowIfNull(nextBattlePreferences);
        if (battlePreferences == nextBattlePreferences)
        {
            return false;
        }
        Current = Current with
        {
            BattleFeatures = LauncherBattleFeatureComposer.Compose(
                Current.ActivationPlan,
                nextBattlePreferences),
        };
        battlePreferences = nextBattlePreferences;
        return true;
    }
}
