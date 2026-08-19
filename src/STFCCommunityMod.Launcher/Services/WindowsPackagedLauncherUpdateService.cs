using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace STFCCommunityMod.Launcher.Services;

internal enum PackagedLauncherUpdateAvailability
{
    NoUpdates,
    Available,
    Required,
    AssociationUnavailable,
    Error,
}

internal sealed record PackagedLauncherUpdateCheck(
    PackagedLauncherUpdateAvailability Availability,
    string Message,
    Uri? AppInstallerUri)
{
    internal bool CanOpenUpdateSource =>
        Availability is PackagedLauncherUpdateAvailability.Available
            or PackagedLauncherUpdateAvailability.Required
        && AppInstallerUri is not null;
}

internal interface IPackagedLauncherUpdateService
{
    Task<PackagedLauncherUpdateCheck> CheckAsync(CancellationToken cancellationToken = default);

    void OpenUpdateSource(Uri appInstallerUri);
}

internal sealed class WindowsPackagedLauncherUpdateService : IPackagedLauncherUpdateService
{
    public async Task<PackagedLauncherUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!Core.WindowsPackageIdentity.IsCurrentProcessPackaged)
        {
            throw new InvalidOperationException("A packaged update check requires an installed MSIX application.");
        }

        var currentPackage = Package.Current;
        var package = new PackageManager().FindPackageForUser(string.Empty, currentPackage.Id.FullName);
        if (package is null)
        {
            throw new InvalidOperationException("Windows could not resolve the current Mod Bridge package.");
        }

        var appInstallerInfo = package.GetAppInstallerInfo();
        var appInstallerUri = appInstallerInfo?.Uri;
        if (appInstallerUri is null || !IsSupportedAppInstallerUri(appInstallerUri))
        {
            return new(
                PackagedLauncherUpdateAvailability.AssociationUnavailable,
                "Windows has no HTTPS App Installer update source associated with this Mod Bridge installation. "
                    + "Reinstall from the signed-release STFCModBridge.appinstaller entry point to restore update checks.",
                null);
        }

        var result = await package.CheckUpdateAvailabilityAsync().AsTask(cancellationToken);
        return FromWindowsAvailability(result.Availability, result.ExtendedError, appInstallerUri);
    }

    public void OpenUpdateSource(Uri appInstallerUri)
    {
        _ = Process.Start(BuildUpdateSourceStartInfo(appInstallerUri));
    }

    internal static PackagedLauncherUpdateCheck FromWindowsAvailability(
        PackageUpdateAvailability availability,
        Exception? extendedError,
        Uri appInstallerUri) => availability switch
        {
            PackageUpdateAvailability.NoUpdates => new(
                PackagedLauncherUpdateAvailability.NoUpdates,
                "This installed Mod Bridge package is current.",
                appInstallerUri),
            PackageUpdateAvailability.Available => new(
                PackagedLauncherUpdateAvailability.Available,
                "A Mod Bridge update is available.",
                appInstallerUri),
            PackageUpdateAvailability.Required => new(
                PackagedLauncherUpdateAvailability.Required,
                "The associated App Installer source marks an update as required.",
                appInstallerUri),
            PackageUpdateAvailability.Unknown => new(
                PackagedLauncherUpdateAvailability.AssociationUnavailable,
                "Windows could not find update information associated with this Mod Bridge package.",
                null),
            PackageUpdateAvailability.Error => new(
                PackagedLauncherUpdateAvailability.Error,
                extendedError is null
                    ? "Windows App Installer could not check for a Mod Bridge update."
                    : $"Windows App Installer could not check for a Mod Bridge update: {extendedError.Message}",
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(availability), availability, null),
        };

    internal static ProcessStartInfo BuildUpdateSourceStartInfo(Uri appInstallerUri)
    {
        if (!IsSupportedAppInstallerUri(appInstallerUri))
        {
            throw new ArgumentException(
                "The App Installer source must be an absolute HTTPS .appinstaller URI.",
                nameof(appInstallerUri));
        }

        return new ProcessStartInfo(appInstallerUri.AbsoluteUri)
        {
            UseShellExecute = true,
        };
    }

    private static bool IsSupportedAppInstallerUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath.EndsWith(".appinstaller", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);
}
