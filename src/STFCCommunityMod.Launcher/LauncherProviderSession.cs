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
            () => runtimeComposition.Current.ActivationPlan);
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

    public MainWindowViewModel ViewModel => ApplicationComposition.SharedServices.Foundation;

    public LauncherProviderAtomicSwitchCoordinator SwitchCoordinator =>
        ViewModel.ProviderSwitchCoordinator
        ?? throw new InvalidOperationException("Provider-switch composition is unavailable.");

    public LauncherFeatureRemediationCoordinator? FeatureRemediationCoordinator =>
        ViewModel.FeatureRemediationCoordinator;

    public bool RefreshRuntimeActivation(ReviewedRuntimeActivation? activation) =>
        runtimeComposition.Refresh(activation);

    public void Dispose() => ApplicationComposition.Dispose();
}

internal sealed class LauncherRuntimeCompositionSlot(
    LauncherDistributionProvider provider,
    LauncherProviderReleaseChannel releaseChannel,
    LauncherStartupComposition initial,
    string? initialEvidenceSha256)
{
    private string? evidenceSha256 = initialEvidenceSha256;

    public LauncherStartupComposition Current { get; private set; } = initial;

    public bool Refresh(ReviewedRuntimeActivation? activation)
    {
        var nextEvidence = activation?.EvidenceSourceSha256;
        if (string.Equals(evidenceSha256, nextEvidence, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        Current = LauncherStartupComposition.Create(provider, releaseChannel, activation);
        evidenceSha256 = nextEvidence;
        return true;
    }
}
