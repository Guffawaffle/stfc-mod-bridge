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
            Path.Combine(Path.GetFullPath(localAppData), "Programs", "STFC Community Mod Launcher"),
            result.ProgramDirectory);
        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(localAppData), "STFC Community Mod Launcher"),
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
    public void PublicIdentityIsSeparatedFromRetainedUpgradeIdentifiers()
    {
        Assert.AreEqual("STFC Mod Control", ModControlProductIdentity.ProductName);
        Assert.AreEqual("Mod Control", ModControlProductIdentity.ShortName);
        Assert.AreEqual("Install · Configure · Diagnose · Run", ModControlProductIdentity.Descriptor);
        Assert.AreEqual("STFC Community Mod Launcher", ModControlProductIdentity.LegacyProgramDirectoryName);
        Assert.AreEqual("STFC Community Mod Launcher", ModControlProductIdentity.LegacyStateDirectoryName);
        Assert.AreEqual("STFCCommunityMod.Launcher.exe", ModControlProductIdentity.LegacyExecutableName);
        Assert.AreEqual("STFCCommunityMod.Launcher", ModControlProductIdentity.LegacyProcessName);
        Assert.AreNotEqual(
            ModControlProductIdentity.ProductName,
            ModControlProductIdentity.LegacyProgramDirectoryName);
    }
}
