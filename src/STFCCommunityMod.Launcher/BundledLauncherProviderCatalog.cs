using System.IO;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal static class BundledLauncherProviderCatalog
{
    private const string CatalogResourceName =
        "STFCCommunityMod.Launcher.ProviderCatalog.v1.json";

    public static LauncherDistributionProvider LoadDefault()
    {
        using var stream = typeof(BundledLauncherProviderCatalog)
            .Assembly
            .GetManifestResourceStream(CatalogResourceName);
        if (stream is null)
        {
            throw new InvalidDataException(
                $"The bundled distribution-provider catalog '{CatalogResourceName}' is missing.");
        }

        return LauncherDistributionProviderCatalogLoader.Load(stream).DefaultProvider;
    }
}
