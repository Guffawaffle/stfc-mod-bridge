using System.IO;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal static class BundledLauncherAboutCatalog
{
    private const string ResourceName =
        "STFCCommunityMod.Launcher.AboutContent.v1.json";

    public static LauncherAboutCatalog Load()
    {
        var assembly = typeof(BundledLauncherAboutCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidDataException(
                $"The bundled About catalog '{ResourceName}' is missing.");
        }

        return LauncherAboutCatalogLoader.Load(stream);
    }
}
