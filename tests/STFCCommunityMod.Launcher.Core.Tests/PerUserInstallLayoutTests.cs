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
            Path.Combine(Path.GetFullPath(localAppData), "Programs", "STFC Mod Control"),
            result.ProgramDirectory);
        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(localAppData), "STFC Mod Control"),
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
        Assert.AreEqual("STFC Mod Control", ModControlProductIdentity.ProductName);
        Assert.AreEqual("Mod Control", ModControlProductIdentity.ShortName);
        Assert.AreEqual("Install · Configure · Diagnose · Run", ModControlProductIdentity.Descriptor);
        Assert.AreEqual("STFC Mod Control", ModControlProductIdentity.ProgramDirectoryName);
        Assert.AreEqual("STFC Mod Control", ModControlProductIdentity.StateDirectoryName);
        Assert.AreEqual("STFCModControl.exe", ModControlProductIdentity.ExecutableName);
        Assert.AreEqual("STFCModControl.Updater.exe", ModControlProductIdentity.UpdaterExecutableName);
        Assert.AreEqual("STFCModControl.Setup.exe", ModControlProductIdentity.SetupExecutableName);
        Assert.AreEqual("STFCModControl", ModControlProductIdentity.ProcessName);
        Assert.AreEqual("stfc-mod-control-win-x64.zip", ModControlProductIdentity.UpdateArchiveName);
        Assert.AreEqual("stfc-mod-control-release-manifest.json", ModControlProductIdentity.ReleaseManifestName);
    }
}
