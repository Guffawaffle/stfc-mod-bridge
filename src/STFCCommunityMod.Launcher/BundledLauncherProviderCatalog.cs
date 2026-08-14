using System.IO;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal sealed record LauncherProviderStartupContext(
    LauncherDistributionProviderCatalog Catalog,
    ILauncherProviderSelectionStore SelectionStore,
    LauncherProviderSelectionResolution Selection);

internal static class BundledLauncherProviderCatalog
{
    private const string CatalogResourceName =
        "STFCCommunityMod.Launcher.ProviderCatalog.v1.json";
    private const string KnownWindowsArtifactsResourceName =
        "STFCCommunityMod.Launcher.KnownWindowsArtifacts.v1.json";
    private const string ReviewedWindowsReleasesResourceName =
        "STFCCommunityMod.Launcher.ReviewedWindowsReleases.v1.json";

    public static LauncherDistributionProviderCatalog Load()
    {
        var assembly = typeof(BundledLauncherProviderCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(CatalogResourceName);
        if (stream is null)
        {
            throw new InvalidDataException(
                $"The bundled distribution-provider catalog '{CatalogResourceName}' is missing.");
        }

        return LauncherDistributionProviderCatalogLoader.Load(
            stream,
            assembly.GetManifestResourceStream);
    }

    public static LauncherProviderStartupContext LoadStartupContext(string stateDirectory)
    {
        var catalog = Load();
        var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
        LauncherProviderSelectionResolution resolution;
        try
        {
            resolution = LauncherProviderSelectionResolver.Resolve(catalog, selectionStore.Load());
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or System.Text.Json.JsonException
                or NotSupportedException)
        {
            resolution = LauncherProviderSelectionResolver.Invalid(exception.Message);
        }
        return new(catalog, selectionStore, resolution);
    }

    public static KnownModArtifactCatalog LoadKnownWindowsArtifacts(
        LauncherDistributionProviderCatalog providerCatalog)
    {
        ArgumentNullException.ThrowIfNull(providerCatalog);
        using var stream = typeof(BundledLauncherProviderCatalog).Assembly.GetManifestResourceStream(
            KnownWindowsArtifactsResourceName);
        if (stream is null)
        {
            throw new InvalidDataException(
                $"The bundled known-artifact catalog '{KnownWindowsArtifactsResourceName}' is missing.");
        }
        return KnownModArtifactCatalogLoader.Load(stream, providerCatalog);
    }

    public static ReviewedReleaseCertificationCatalog LoadReviewedWindowsReleases(
        LauncherDistributionProviderCatalog providerCatalog)
    {
        ArgumentNullException.ThrowIfNull(providerCatalog);
        using var stream = typeof(BundledLauncherProviderCatalog).Assembly.GetManifestResourceStream(
            ReviewedWindowsReleasesResourceName);
        if (stream is null)
        {
            throw new InvalidDataException(
                $"The bundled reviewed-release catalog '{ReviewedWindowsReleasesResourceName}' is missing.");
        }
        return ReviewedReleaseCertificationCatalogLoader.Load(stream, providerCatalog);
    }

    public static LauncherConfigurationCatalog LoadConfigurationCatalog(
        LauncherDistributionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.GetCapabilityStatus(LauncherProviderCapabilityIds.ConfigurationCatalog)
                != LauncherProviderCapabilityStatus.Supported
            || provider.ConfigurationSchema.Status != LauncherProviderCapabilityStatus.Supported
            || string.IsNullOrWhiteSpace(provider.ConfigurationSchema.ResourceName))
        {
            throw new LauncherConfigurationSchemaException(
                $"{provider.DisplayName} has no verified configuration catalog. "
                + "Capability status is unknown, so settings editing is disabled rather than inferred.");
        }

        var schemaContents = ReadConfigurationSchema(provider);
        if (LauncherConfigurationSchemaSetLoader.IsSchemaSet(schemaContents))
        {
            var providerCatalog = Load();
            var certification = LoadReviewedWindowsReleases(providerCatalog).Find(
                provider.Id,
                provider.DefaultReleaseChannelId)
                ?? throw new LauncherConfigurationSchemaException(
                    $"{provider.DisplayName} has no exact reviewed release identity for its configuration catalog.");
            return LoadConfigurationCatalog(
                provider,
                new(
                    provider.Id,
                    certification.ChannelId,
                    certification.ReleaseVersion,
                    certification.SourceCommit));
        }

        using var schemaStream = new MemoryStream(schemaContents, writable: false);
        return ValidateCatalogOwner(
            provider,
            LauncherConfigurationSchemaLoader.Load(schemaStream));
    }

    public static LauncherConfigurationDiagnosisEvidence LoadConfigurationDiagnosisEvidence(
        LauncherDistributionProviderCatalog providerCatalog,
        ReviewedReleaseCertificationCatalog reviewedReleases,
        LauncherProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(providerCatalog);
        ArgumentNullException.ThrowIfNull(reviewedReleases);
        ArgumentNullException.ThrowIfNull(selection);
        if (!providerCatalog.TryGetProvider(selection.ProviderId, out var provider)
            || provider is null
            || !provider.ReleaseChannels.ContainsKey(selection.ReleaseChannelId))
        {
            return LauncherConfigurationDiagnosisEvidence.Unavailable(
                selection.ProviderId,
                selection.ReleaseChannelId,
                LauncherProviderCapabilityStatus.Unknown);
        }

        var capabilityStatus = provider.GetCapabilityStatus(
            LauncherProviderCapabilityIds.ConfigurationCatalog);
        if (capabilityStatus != LauncherProviderCapabilityStatus.Supported)
        {
            return LauncherConfigurationDiagnosisEvidence.Unavailable(
                selection.ProviderId,
                selection.ReleaseChannelId,
                capabilityStatus);
        }

        try
        {
            var schemaContents = ReadConfigurationSchema(provider);
            LauncherConfigurationCatalog configurationCatalog;
            if (LauncherConfigurationSchemaSetLoader.IsSchemaSet(schemaContents))
            {
                var certification = reviewedReleases.Find(
                    selection.ProviderId,
                    selection.ReleaseChannelId);
                if (certification is null)
                {
                    return LauncherConfigurationDiagnosisEvidence.Unavailable(
                        selection.ProviderId,
                        selection.ReleaseChannelId,
                        LauncherProviderCapabilityStatus.Unknown);
                }
                configurationCatalog = LoadConfigurationCatalog(
                    provider,
                    new(
                        selection.ProviderId,
                        selection.ReleaseChannelId,
                        certification.ReleaseVersion,
                        certification.SourceCommit));
            }
            else
            {
                if (!string.Equals(
                        selection.ReleaseChannelId,
                        provider.DefaultReleaseChannelId,
                        StringComparison.Ordinal))
                {
                    return LauncherConfigurationDiagnosisEvidence.Unavailable(
                        selection.ProviderId,
                        selection.ReleaseChannelId,
                        LauncherProviderCapabilityStatus.Unknown);
                }
                using var schemaStream = new MemoryStream(schemaContents, writable: false);
                configurationCatalog = ValidateCatalogOwner(
                    provider,
                    LauncherConfigurationSchemaLoader.Load(schemaStream));
            }
            return LauncherConfigurationDiagnosisEvidence.Supported(
                selection.ProviderId,
                selection.ReleaseChannelId,
                configurationCatalog);
        }
        catch (LauncherConfigurationSchemaException)
        {
            return LauncherConfigurationDiagnosisEvidence.Unavailable(
                selection.ProviderId,
                selection.ReleaseChannelId,
                LauncherProviderCapabilityStatus.Unknown);
        }
    }

    internal static LauncherConfigurationCatalog LoadConfigurationCatalog(
        LauncherDistributionProvider provider,
        LauncherConfigurationCatalogApplicability applicability)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(applicability);
        if (provider.GetCapabilityStatus(LauncherProviderCapabilityIds.ConfigurationCatalog)
                != LauncherProviderCapabilityStatus.Supported
            || provider.ConfigurationSchema.Status != LauncherProviderCapabilityStatus.Supported
            || string.IsNullOrWhiteSpace(provider.ConfigurationSchema.ResourceName))
        {
            throw new LauncherConfigurationSchemaException(
                $"{provider.DisplayName} has no versioned configuration catalog set.");
        }

        var schemaContents = ReadConfigurationSchema(provider);
        if (!LauncherConfigurationSchemaSetLoader.IsSchemaSet(schemaContents))
        {
            throw new LauncherConfigurationSchemaException(
                $"{provider.DisplayName} configuration metadata is not a versioned schema set.");
        }
        using var schemaStream = new MemoryStream(schemaContents, writable: false);
        return ValidateCatalogOwner(
            provider,
            LauncherConfigurationSchemaSetLoader.Load(schemaStream, applicability));
    }

    private static byte[] ReadConfigurationSchema(LauncherDistributionProvider provider)
    {
        using var schemaStream = typeof(BundledLauncherProviderCatalog).Assembly.GetManifestResourceStream(
            provider.ConfigurationSchema.ResourceName!);
        if (schemaStream is null)
        {
            throw new LauncherConfigurationSchemaException(
                $"The packaged {provider.DisplayName} configuration catalog is missing.");
        }
        using var buffer = new MemoryStream();
        schemaStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static LauncherConfigurationCatalog ValidateCatalogOwner(
        LauncherDistributionProvider provider,
        LauncherConfigurationCatalog catalog)
    {
        if (!string.Equals(catalog.Source.StableId, provider.Id, StringComparison.Ordinal))
        {
            throw new LauncherConfigurationSchemaException(
                $"The packaged configuration catalog belongs to provider '{catalog.Source.StableId}', "
                + $"not the selected provider '{provider.Id}'. Settings and Data Sync editing "
                + "are disabled rather than projecting capabilities from the wrong source.");
        }

        return catalog;
    }
}
