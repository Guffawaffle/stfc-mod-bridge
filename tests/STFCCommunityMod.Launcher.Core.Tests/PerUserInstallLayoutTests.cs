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
}
