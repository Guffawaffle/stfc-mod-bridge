namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class WindowsPackageIdentityTests
{
    [TestMethod]
    public void OrdinaryTestProcessHasNoWindowsPackageIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows package identity detection requires Windows.");
        }

        Assert.IsFalse(WindowsPackageIdentity.IsCurrentProcessPackaged);
        Assert.IsNull(WindowsPackageIdentity.CurrentInstallDirectory);
    }
}
