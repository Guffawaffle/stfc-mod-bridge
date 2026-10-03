using System.IO;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

/// <summary>
/// Installed repository observations select configuration applicability independently
/// of artifact trust. Historical fallback applies only when no observation exists.
/// </summary>
internal sealed class LauncherInstalledConfigurationResolver(
    LauncherDistributionProviderCatalog providers,
    ReviewedReleaseCertificationCatalog reviewedReleases,
    IModDeploymentStateReader stateReader)
{
    public LauncherConfigurationCatalog ResolveCatalog(
        LauncherProviderSelection selection,
        string? gameDirectory,
        LauncherConfigurationCatalog? historicalFallback = null)
    {
        var provider = providers.GetProvider(selection.ProviderId);
        ModInstalledArtifactState? receipt = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(gameDirectory))
            {
                var validation = GameInstallValidator.Validate(gameDirectory);
                if (!validation.IsValid)
                {
                    return Unavailable(provider, selection, "unknown", "unknown");
                }
                receipt = stateReader.ReadInstalledState(validation.GameDirectory);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return Unavailable(provider, selection, "unknown", "unknown");
        }

        if (receipt is not null && (receipt.ProviderId != selection.ProviderId
            || receipt.ReleaseChannelId != selection.ReleaseChannelId
            || receipt.RuntimeDistributionId != provider.RuntimeDistributionId))
        {
            return Unavailable(provider, selection,
                receipt.RepositoryRelease?.Tag[1..] ?? "unknown",
                receipt.RepositoryRelease?.SourceCommit ?? "unknown");
        }
        if (receipt?.RepositoryRelease is { } observation)
        {
            var version = observation.Tag[1..];
            try
            {
                return BundledLauncherProviderCatalog.LoadConfigurationCatalog(
                    provider,
                    new(selection.ProviderId, selection.ReleaseChannelId, version, observation.SourceCommit));
            }
            catch (LauncherConfigurationSchemaException)
            {
                return Unavailable(provider, selection, version, observation.SourceCommit);
            }
        }

        // Keep existing no-observation behavior, including custom/manual installs.
        try
        {
            var result = historicalFallback
                ?? BundledLauncherProviderCatalog.LoadConfigurationCatalog(provider);
            var trackMatches = result.Identity.TrackId == selection.ReleaseChannelId
                || (result.Identity.TrackId == "unversioned"
                    && selection.ReleaseChannelId == provider.DefaultReleaseChannelId);
            return result.Source.StableId == selection.ProviderId && trackMatches
                ? result
                : Unavailable(provider, selection, "unknown", "unknown");
        }
        catch (LauncherConfigurationSchemaException)
        {
            return Unavailable(provider, selection, "unknown", "unknown");
        }
    }

    public LauncherConfigurationDiagnosisEvidence ResolveEvidence(
        LauncherProviderSelection selection,
        string? gameDirectory,
        LauncherConfigurationCatalog? historicalFallback = null) =>
        LauncherConfigurationDiagnosisEvidence.Supported(
            selection.ProviderId,
            selection.ReleaseChannelId,
            ResolveCatalog(selection, gameDirectory, historicalFallback));

    public LauncherConfigurationDiagnosisEvidence ResolveSwitchEvidence(
        LauncherProviderSelection selection,
        LauncherProviderSelection activeSelection,
        string? gameDirectory,
        LauncherConfigurationCatalog? historicalFallback = null)
    {
        if (selection == activeSelection)
        {
            return ResolveEvidence(selection, gameDirectory, historicalFallback);
        }
        // Discovery has not installed the prospective repository release yet.
        // Raw TOML preservation/history can proceed without guessed typed meaning.
        return selection.ProviderId == "netniv"
            ? LauncherConfigurationDiagnosisEvidence.Unavailable(
                selection.ProviderId, selection.ReleaseChannelId, LauncherProviderCapabilityStatus.Unknown)
            : BundledLauncherProviderCatalog.LoadConfigurationDiagnosisEvidence(
                providers, reviewedReleases, selection);
    }

    public static IModDeploymentStateReader CreateReadOnlyStateReader(string stateDirectory)
    {
        var dependencies = new ReadOnlyDependencies();
        return new ModDeploymentService(
            stateDirectory, dependencies, dependencies, dependencies, _ => true,
            new("netniv", "stable", "netniv.stfc-community-mod"));
    }

    private static LauncherConfigurationCatalog Unavailable(
        LauncherDistributionProvider provider,
        LauncherProviderSelection selection,
        string releaseVersion,
        string sourceCommit) =>
        LauncherConfigurationCatalog.CreateUnavailable(
            new(
                provider.Id switch
                {
                    "netniv" => LauncherConfigurationSourceId.Netniv,
                    "guffawaffle" => LauncherConfigurationSourceId.Guffawaffle,
                    "profiles" => LauncherConfigurationSourceId.Profiles,
                    _ => throw new InvalidDataException("No configuration source identity is registered."),
                },
                provider.ReleaseChannels.GetValueOrDefault(selection.ReleaseChannelId)?.Repository
                    ?? provider.DefaultReleaseChannel.Repository),
            selection.ReleaseChannelId, releaseVersion, sourceCommit);

    private sealed class ReadOnlyDependencies :
        IModArtifactDownloader, IModArtifactVersionReader, IModArtifactAuthenticityVerifier
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The installed configuration state reader cannot download.");
        public string? ReadVersion(string artifactPath) =>
            throw new InvalidOperationException("The installed configuration state reader cannot inspect candidates.");
        public ModArtifactAuthenticityResult Verify(string artifactPath) =>
            throw new InvalidOperationException("The installed configuration state reader cannot authorize deployment.");
    }
}
