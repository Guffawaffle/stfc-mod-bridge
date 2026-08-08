using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal static class LauncherProviderPresentation
{
    public static string Describe(LauncherDistributionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return $"{provider.DefaultReleaseChannel.DisplayName} channel"
            + Environment.NewLine
            + provider.Description;
    }
}
