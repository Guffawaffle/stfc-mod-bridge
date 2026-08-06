namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherInstalledProductTests
{
    [TestMethod]
    public void ProductVersionNormalizationRemovesOnlyBuildMetadata()
    {
        Assert.AreEqual("0.1.0-rc.5", LauncherInstalledProduct.NormalizeVersion(" 0.1.0-rc.5+abcdef "));
        Assert.IsNull(LauncherInstalledProduct.NormalizeVersion("  "));
    }
}
