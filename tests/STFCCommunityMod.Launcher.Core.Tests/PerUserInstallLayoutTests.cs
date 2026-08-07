using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class PerUserInstallLayoutTests
{
    [TestMethod]
    public void FromLocalApplicationDataUsesSeparateProgramAndStateDirectories()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "Güff", "AppData", "Local");

        var result = PerUserInstallLayout.FromLocalApplicationData(localAppData);

        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(localAppData), "Programs", "STFC Mod Bridge"),
            result.ProgramDirectory);
        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(localAppData), "STFC Mod Bridge"),
            result.StateDirectory);
        Assert.AreNotEqual(result.ProgramDirectory, result.StateDirectory);
    }

    [TestMethod]
    public void FromLocalApplicationDataRejectsMissingRoot()
    {
        Assert.ThrowsException<ArgumentException>(
            () => PerUserInstallLayout.FromLocalApplicationData(" "));
    }

    [TestMethod]
    public void ProductIdentityOwnsGreenfieldInstallAndArtifactNames()
    {
        Assert.AreEqual("STFC Mod Bridge", ModBridgeProductIdentity.ProductName);
        Assert.AreEqual("Mod Bridge", ModBridgeProductIdentity.ShortName);
        Assert.AreEqual("Install · Configure · Diagnose · Run", ModBridgeProductIdentity.Descriptor);
        Assert.AreEqual("STFC Mod Bridge", ModBridgeProductIdentity.ProgramDirectoryName);
        Assert.AreEqual("STFC Mod Bridge", ModBridgeProductIdentity.StateDirectoryName);
        Assert.AreEqual("STFCModBridge.exe", ModBridgeProductIdentity.ExecutableName);
        Assert.AreEqual("STFCModBridge.Updater.exe", ModBridgeProductIdentity.UpdaterExecutableName);
        Assert.AreEqual("STFCModBridge", ModBridgeProductIdentity.ProcessName);
        Assert.AreEqual("STFCModBridge.msix", ModBridgeProductIdentity.PackageName);
        Assert.AreEqual("STFCModBridge.appinstaller", ModBridgeProductIdentity.AppInstallerName);
        Assert.AreEqual("stfc-mod-bridge-win-x64.zip", ModBridgeProductIdentity.UpdateArchiveName);
        Assert.AreEqual("stfc-mod-bridge-release-manifest.json", ModBridgeProductIdentity.ReleaseManifestName);
    }
}
