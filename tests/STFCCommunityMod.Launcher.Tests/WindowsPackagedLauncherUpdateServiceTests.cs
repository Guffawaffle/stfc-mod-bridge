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
    public void AvailableUpdatesCanOpenTheAssociatedUpdateSource(PackageUpdateAvailability windowsAvailability)
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
        Assert.IsTrue(result.CanOpenUpdateSource);
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
        Assert.IsFalse(result.CanOpenUpdateSource);
        StringAssert.Contains(result.Message, "current");
    }

    [TestMethod]
    public void UnknownAndErrorResultsCannotOpenTheUpdateSource()
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
        Assert.IsFalse(unknown.CanOpenUpdateSource);
        Assert.AreEqual(PackagedLauncherUpdateAvailability.Error, error.Availability);
        Assert.IsNull(error.AppInstallerUri);
        Assert.IsFalse(error.CanOpenUpdateSource);
        StringAssert.Contains(error.Message, "test failure");
    }

    [TestMethod]
    public void UpdateSourceLaunchUsesTheAssociatedHttpsUriWithoutAProtocolRewrite()
    {
        var source = new Uri("https://updates.example.test/preview/STFC Mod Bridge.appinstaller?channel=preview");

        var startInfo = WindowsPackagedLauncherUpdateService.BuildUpdateSourceStartInfo(source);

        Assert.AreEqual(source.AbsoluteUri, startInfo.FileName);
        Assert.IsTrue(startInfo.UseShellExecute);
        Assert.IsFalse(startInfo.FileName.Contains("ms-appinstaller", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("http://updates.example.test/STFCModBridge.appinstaller")]
    [DataRow("https://user@updates.example.test/STFCModBridge.appinstaller")]
    [DataRow("https://updates.example.test/STFCModBridge.appinstaller#fragment")]
    [DataRow("https://updates.example.test/STFCModBridge.msix")]
    [DataRow("https://updates.example.test/check-for-update")]
    public void UpdateSourceLaunchRejectsUnreviewedSourceShapes(string source)
    {
        Assert.ThrowsException<ArgumentException>(() =>
            WindowsPackagedLauncherUpdateService.BuildUpdateSourceStartInfo(new Uri(source)));
    }
}
