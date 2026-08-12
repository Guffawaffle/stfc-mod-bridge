using STFCCommunityMod.Launcher.Services;
using Windows.ApplicationModel;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class WindowsPackagedLauncherUpdateServiceTests
{
    private static readonly Uri AppInstallerUri = new(
        "https://updates.example.test/preview/STFCModBridge.appinstaller");

    [TestMethod]
    [DataRow(PackageUpdateAvailability.Available)]
    [DataRow(PackageUpdateAvailability.Required)]
    public void AvailableUpdatesCanOpenTheAssociatedAppInstaller(PackageUpdateAvailability windowsAvailability)
    {
        var result = WindowsPackagedLauncherUpdateService.FromWindowsAvailability(
            windowsAvailability,
            null,
            AppInstallerUri);

        var expectedAvailability = windowsAvailability == PackageUpdateAvailability.Available
            ? PackagedLauncherUpdateAvailability.Available
            : PackagedLauncherUpdateAvailability.Required;
        Assert.AreEqual(expectedAvailability, result.Availability);
        Assert.AreEqual(AppInstallerUri, result.AppInstallerUri);
        Assert.IsTrue(result.CanOpenAppInstaller);
    }

    [TestMethod]
    public void NoUpdateStaysInTheBridge()
    {
        var result = WindowsPackagedLauncherUpdateService.FromWindowsAvailability(
            PackageUpdateAvailability.NoUpdates,
            null,
            AppInstallerUri);

        Assert.AreEqual(PackagedLauncherUpdateAvailability.NoUpdates, result.Availability);
        Assert.AreEqual(AppInstallerUri, result.AppInstallerUri);
        Assert.IsFalse(result.CanOpenAppInstaller);
        StringAssert.Contains(result.Message, "current");
    }

    [TestMethod]
    public void UnknownAndErrorResultsCannotOpenAppInstaller()
    {
        var unknown = WindowsPackagedLauncherUpdateService.FromWindowsAvailability(
            PackageUpdateAvailability.Unknown,
            null,
            AppInstallerUri);
        var error = WindowsPackagedLauncherUpdateService.FromWindowsAvailability(
            PackageUpdateAvailability.Error,
            new InvalidOperationException("test failure"),
            AppInstallerUri);

        Assert.AreEqual(PackagedLauncherUpdateAvailability.AssociationUnavailable, unknown.Availability);
        Assert.IsNull(unknown.AppInstallerUri);
        Assert.IsFalse(unknown.CanOpenAppInstaller);
        Assert.AreEqual(PackagedLauncherUpdateAvailability.Error, error.Availability);
        Assert.IsNull(error.AppInstallerUri);
        Assert.IsFalse(error.CanOpenAppInstaller);
        StringAssert.Contains(error.Message, "test failure");
    }

    [TestMethod]
    public void AppInstallerActivationUsesAnEncodedHttpsSource()
    {
        var source = new Uri("https://updates.example.test/preview/STFC Mod Bridge.appinstaller?channel=preview");

        var activation = WindowsPackagedLauncherUpdateService.BuildAppInstallerActivationUri(source);

        Assert.AreEqual("ms-appinstaller", activation.Scheme);
        Assert.AreEqual($"?source={Uri.EscapeDataString(source.AbsoluteUri)}", activation.Query);
    }

    [TestMethod]
    [DataRow("http://updates.example.test/STFCModBridge.appinstaller")]
    [DataRow("https://user@updates.example.test/STFCModBridge.appinstaller")]
    [DataRow("https://updates.example.test/STFCModBridge.appinstaller#fragment")]
    [DataRow("https://updates.example.test/STFCModBridge.msix")]
    [DataRow("https://updates.example.test/check-for-update")]
    public void AppInstallerActivationRejectsUnreviewedSourceShapes(string source)
    {
        Assert.ThrowsException<ArgumentException>(() =>
            WindowsPackagedLauncherUpdateService.BuildAppInstallerActivationUri(new Uri(source)));
    }
}
