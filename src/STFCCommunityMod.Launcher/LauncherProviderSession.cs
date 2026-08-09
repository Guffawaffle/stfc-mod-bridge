using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

internal sealed class LauncherProviderSession(
    LauncherProviderSelectionResolution resolution,
    LauncherProviderShellAccess shellAccess,
    LauncherDistributionProvider provider,
    LauncherProviderReleaseChannel releaseChannel,
    LauncherStartupComposition startupComposition,
    ReviewedRuntimeActivation? reviewedRuntimeActivation,
    MainWindowViewModel viewModel) : IDisposable
{
    private readonly LauncherRuntimeCompositionSlot runtimeComposition = new(
        provider,
        releaseChannel,
        startupComposition,
        reviewedRuntimeActivation?.EvidenceSourceSha256);
    public LauncherProviderSelectionResolution Resolution { get; } = resolution;

    public LauncherProviderShellAccess ShellAccess { get; } = shellAccess;

    public LauncherDistributionProvider Provider { get; } = provider;

    public LauncherProviderReleaseChannel ReleaseChannel { get; } = releaseChannel;

    public LauncherStartupComposition StartupComposition => runtimeComposition.Current;

    public MainWindowViewModel ViewModel { get; } = viewModel;

    public LauncherProviderAtomicSwitchCoordinator SwitchCoordinator =>
        ViewModel.ProviderSwitchCoordinator
        ?? throw new InvalidOperationException("Provider-switch composition is unavailable.");

    public bool RefreshRuntimeActivation(ReviewedRuntimeActivation? activation) =>
        runtimeComposition.Refresh(activation);

    public void Dispose() => ViewModel.Dispose();
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
