namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherProviderModBinding(
    string ProviderId,
    string ReleaseChannelId,
    string Repository,
    string? ManifestAssetName,
    string? WindowsPublisher,
    string? WindowsArtifactSigningIdentityEku,
    LauncherProviderReleaseDiscoveryKind DiscoveryKind,
    LauncherProviderArtifactTrustKind? TrustKind,
    ReviewedReleaseCertification? ReviewedCertification,
    bool IsAvailable,
    string UnavailableReason)
{
    public static LauncherProviderModBinding Resolve(
        LauncherDistributionProvider provider,
        LauncherProviderReleaseChannel selectedChannel,
        ReviewedReleaseCertificationCatalog? reviewedCertifications = null,
        string? providerResolutionFailure = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(selectedChannel);
        if (!provider.ReleaseChannels.TryGetValue(selectedChannel.Id, out var channel))
        {
            throw new InvalidDataException(
                $"Release channel '{selectedChannel.Id}' is not registered for provider '{provider.Id}'.");
        }
        var unavailable = LauncherProviderCapabilityIds.ContractCapabilities
            .Where(capability => capability is
                LauncherProviderCapabilityIds.ReleaseDiscovery
                or LauncherProviderCapabilityIds.ArtifactTrust)
            .Where(capability =>
                provider.GetCapabilityStatus(capability)
                    != LauncherProviderCapabilityStatus.Supported)
            .ToArray();
        var reason = !string.IsNullOrWhiteSpace(providerResolutionFailure)
            ? providerResolutionFailure
            : unavailable.Length > 0
                ? $"{provider.DisplayName} provider capabilities are unknown or unsupported: "
                    + $"{string.Join(", ", unavailable)}. Mod download and installation fail closed."
                : !provider.CanUseReleaseDiscoveryFor(channel)
                    ? $"Provider '{provider.Id}' channel '{channel.Id}' has no supported release discovery contract."
                    : string.Empty;
        if (string.IsNullOrWhiteSpace(reason) && !provider.CanAuthenticateWindowsArtifact)
        {
            reason = $"Provider '{provider.Id}' has no supported Windows artifact trust contract.";
        }
        var reviewedCertification = reviewedCertifications?.Find(provider.Id, channel.Id);
        if (string.IsNullOrWhiteSpace(reason)
            && provider.ArtifactPolicy.TrustKind == LauncherProviderArtifactTrustKind.ReviewedExactHash
            && reviewedCertification is null)
        {
            reason = $"Provider '{provider.Id}' channel '{channel.Id}' has no launcher-reviewed release certification.";
        }
        var isAvailable = string.IsNullOrWhiteSpace(reason);
        return new(
            provider.Id,
            channel.Id,
            channel.Repository,
            channel.ManifestAssetName,
            provider.ArtifactPolicy.WindowsPublisher,
            provider.ArtifactPolicy.WindowsArtifactSigningIdentityEku,
            channel.DiscoveryKind,
            provider.ArtifactPolicy.TrustKind,
            reviewedCertification,
            isAvailable,
            isAvailable ? string.Empty : reason);
    }
}

public sealed class UnavailableWindowsReleaseDiscoveryClient(string reason)
    : IWindowsReleaseDiscoveryClient
{
    public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default)
    {
        _ = channel;
        _ = currentLauncherVersion;
        _ = cancellationToken;
        return Task.FromException<WindowsReleaseDiscovery>(
            new InvalidOperationException(reason));
    }
}

public sealed class UnavailableLauncherReleaseDiscoveryClient(string reason)
    : ILauncherReleaseDiscoveryClient
{
    public Task<LauncherReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default)
    {
        _ = channel;
        _ = currentLauncherVersion;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<LauncherReleaseDiscovery>(
            new InvalidOperationException(reason));
    }
}

public sealed class FailClosedModArtifactAuthenticityVerifier(string reason)
    : IModArtifactAuthenticityVerifier
{
    public ModArtifactAuthenticityResult Verify(string artifactPath)
    {
        _ = artifactPath;
        return new(false, reason);
    }
}
