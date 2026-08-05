using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

internal sealed class LauncherProviderSession(
    LauncherProviderSelectionResolution resolution,
    LauncherProviderShellAccess shellAccess,
    LauncherDistributionProvider provider,
    LauncherProviderReleaseChannel releaseChannel,
    LauncherStartupComposition startupComposition,
    MainWindowViewModel viewModel) : IDisposable
{
    public LauncherProviderSelectionResolution Resolution { get; } = resolution;

    public LauncherProviderShellAccess ShellAccess { get; } = shellAccess;

    public LauncherDistributionProvider Provider { get; } = provider;

    public LauncherProviderReleaseChannel ReleaseChannel { get; } = releaseChannel;

    public LauncherStartupComposition StartupComposition { get; } = startupComposition;

    public MainWindowViewModel ViewModel { get; } = viewModel;

    public LauncherProviderAtomicSwitchCoordinator SwitchCoordinator =>
        ViewModel.ProviderSwitchCoordinator
        ?? throw new InvalidOperationException("Provider-switch composition is unavailable.");

    public void Dispose() => ViewModel.Dispose();
}
