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
}
