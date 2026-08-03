namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherShellLifecycleControllerTests
{
    [TestMethod]
    public void GameProcessChangeRefreshesHomeOnly()
    {
        var target = new RecordingRefreshTarget();
        var controller = new LauncherShellLifecycleController(target);

        controller.HandleGameProcessChanged();

        Assert.AreEqual(1, target.HomeRefreshCount);
        Assert.AreEqual(0, target.ConfigurationAvailabilityRefreshCount);
        Assert.AreEqual(0, target.ConfigurationDocumentReloadCount);
    }

    [TestMethod]
    public void GameInstallationChangeRefreshesConfigurationDocument()
    {
        var target = new RecordingRefreshTarget();
        var controller = new LauncherShellLifecycleController(target);

        controller.HandleGameInstallationChanged();

        Assert.AreEqual(1, target.HomeRefreshCount);
        Assert.AreEqual(1, target.ConfigurationAvailabilityRefreshCount);
        Assert.AreEqual(1, target.ConfigurationDocumentReloadCount);
    }

    private sealed class RecordingRefreshTarget : ILauncherShellRefreshTarget
    {
        public int HomeRefreshCount { get; private set; }

        public int ConfigurationAvailabilityRefreshCount { get; private set; }

        public int ConfigurationDocumentReloadCount { get; private set; }

        public void RefreshHome()
        {
            ++HomeRefreshCount;
        }

        public void RefreshConfigurationAvailability()
        {
            ++ConfigurationAvailabilityRefreshCount;
        }

        public void ReloadConfigurationDocument()
        {
            ++ConfigurationDocumentReloadCount;
        }
    }
}
