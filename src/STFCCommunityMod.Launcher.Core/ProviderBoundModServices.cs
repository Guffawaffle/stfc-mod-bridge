namespace STFCCommunityMod.Launcher.Core;

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

public sealed class FailClosedModArtifactAuthenticityVerifier(string reason)
    : IModArtifactAuthenticityVerifier
{
    public ModArtifactAuthenticityResult Verify(string artifactPath)
    {
        _ = artifactPath;
        return new(false, reason);
    }
}
