namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherInstalledProductTests
{
    [TestMethod]
    public void MissingProgramDirectoryIsAnInstall()
    {
        var action = LauncherInstalledProduct.DetermineSetupAction(
            programDirectoryExists: false,
            launcherExists: false,
            installedProductVersion: null,
            setupVersion: "0.1.0-rc.5");

        Assert.AreEqual(LauncherSetupAction.Install, action);
    }

    [TestMethod]
    public void IncompleteOrSameVersionInstallationIsARepair()
    {
        Assert.AreEqual(
            LauncherSetupAction.Repair,
            LauncherInstalledProduct.DetermineSetupAction(true, false, null, "0.1.0-rc.5"));
        Assert.AreEqual(
            LauncherSetupAction.Repair,
            LauncherInstalledProduct.DetermineSetupAction(
                true,
                true,
                "0.1.0-rc.5+old-commit",
                "0.1.0-rc.5+new-commit"));
    }

    [TestMethod]
    public void NewerPrereleaseIsAnUpdateButOlderSetupIsARepair()
    {
        Assert.AreEqual(
            LauncherSetupAction.Update,
            LauncherInstalledProduct.DetermineSetupAction(true, true, "0.1.0-rc.4", "0.1.0-rc.5"));
        Assert.AreEqual(
            LauncherSetupAction.Repair,
            LauncherInstalledProduct.DetermineSetupAction(true, true, "0.1.0-rc.5", "0.1.0-rc.4"));
    }

    [TestMethod]
    public void StableVersionIsNewerThanItsPrerelease()
    {
        var action = LauncherInstalledProduct.DetermineSetupAction(
            true,
            true,
            "0.1.0-rc.5",
            "0.1.0");

        Assert.AreEqual(LauncherSetupAction.Update, action);
    }

    [TestMethod]
    public void ProductVersionNormalizationRemovesOnlyBuildMetadata()
    {
        Assert.AreEqual("0.1.0-rc.5", LauncherInstalledProduct.NormalizeVersion(" 0.1.0-rc.5+abcdef "));
        Assert.IsNull(LauncherInstalledProduct.NormalizeVersion("  "));
    }
}
