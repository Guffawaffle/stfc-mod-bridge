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

    public static LauncherDistributionProvider LoadDefault() => Load().DefaultProvider;

    public static LauncherDistributionProvider LoadSelected(string stateDirectory)
    {
        var context = LoadStartupContext(stateDirectory);
        if (context.Selection.Provider is null)
        {
            throw new InvalidDataException(context.Selection.Message);
        }
        return context.Selection.Provider;
    }

    public static LauncherProviderStartupContext LoadStartupContext(string stateDirectory)
    {
        var catalog = Load();
        var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
        var resolution = LauncherProviderSelectionResolver.Resolve(catalog, selectionStore.Load());
        if (!resolution.IsResolved)
        {
            throw new InvalidDataException(resolution.Message);
        }
        return new(catalog, selectionStore, resolution);
    }
}
