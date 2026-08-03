using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal static class LauncherProviderPresentation
{
    public static string Describe(LauncherDistributionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var statuses = LauncherProviderCapabilityIds.ContractCapabilities
            .Select(provider.GetCapabilityStatus)
            .ToArray();
        var supported = statuses.Count(status => status == LauncherProviderCapabilityStatus.Supported);
        var unknown = statuses.Count(status => status == LauncherProviderCapabilityStatus.Unknown);
        var unavailable = statuses.Length - supported - unknown;
        var integration = supported == statuses.Length
            ? "Full Bridge integration"
            : supported == 0 && unavailable == 0
                ? "Bridge compatibility not established"
                : supported == 0 && unknown == 0
                    ? "Bridge integration unavailable"
                    : $"Bridge integration: {supported} of {statuses.Length} capabilities supported";
        return $"{provider.DefaultReleaseChannel.DisplayName} channel · {integration}"
            + Environment.NewLine
            + provider.Description;
    }
}
