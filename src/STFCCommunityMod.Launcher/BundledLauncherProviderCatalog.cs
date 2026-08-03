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

        using var schemaStream = typeof(BundledLauncherProviderCatalog).Assembly.GetManifestResourceStream(
            provider.ConfigurationSchema.ResourceName);
        if (schemaStream is null)
        {
            throw new LauncherConfigurationSchemaException(
                $"The packaged {provider.DisplayName} configuration catalog is missing.");
        }

        var catalog = LauncherConfigurationSchemaLoader.Load(schemaStream);
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
