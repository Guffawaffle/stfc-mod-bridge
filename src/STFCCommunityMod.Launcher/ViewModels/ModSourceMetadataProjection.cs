using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

internal static class ModSourceMetadataProjection
{
    public static string From(
        ModInstallationEvidence installation,
        LauncherDistributionProviderCatalog providerCatalog,
        string selectedSourceMetadata)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(providerCatalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedSourceMetadata);
        var provenance = installation.BinaryProvenance;
        if (provenance?.KnownArtifact is { } known
            && providerCatalog.TryGetProvider(known.ProviderId, out var knownProvider)
            && knownProvider is not null)
        {
            var track = knownProvider.ReleaseChannels.TryGetValue(known.TrackId, out var channel)
                ? channel.DisplayName
                : known.TrackId;
            return $"Installed: {knownProvider.DisplayName} · {track} · reviewed hash";
        }
        if (provenance?.BuildIdentity is { } identity)
        {
            var provider = providerCatalog.Providers.Values.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.RuntimeDistributionId,
                    identity.DistributionId,
                    StringComparison.Ordinal));
            return provider is null
                ? $"Installed: custom build · {identity.DistributionId}"
                : $"Installed: {provider.DisplayName} · custom build";
        }
        return provenance?.State switch
        {
            ModBinaryProvenanceState.MalformedIdentity => "Installed: custom build · malformed identity",
            ModBinaryProvenanceState.CustomUnattributed or ModBinaryProvenanceState.MetadataUnavailable =>
                $"Installed: custom build · selected source {selectedSourceMetadata}",
            _ => selectedSourceMetadata,
        };
    }
}
