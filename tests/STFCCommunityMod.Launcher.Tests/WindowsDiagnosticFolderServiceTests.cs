using STFCCommunityMod.Launcher.Services;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class WindowsDiagnosticFolderServiceTests
{
    [TestMethod]
    public void MissingDirectoryFailsWithoutStartingExplorer()
    {
        var service = new WindowsDiagnosticFolderService();

        var opened = service.TryOpen(null, out var message);

        Assert.IsFalse(opened);
        Assert.AreEqual("The requested folder is not available.", message);
    }
}
