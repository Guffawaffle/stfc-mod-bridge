using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class LauncherStartupCompositionTests
{
    [TestMethod]
    public void ResolvedNonDefaultReleaseChannelFlowsIntoSettingsDiagnostics()
    {
        var provider = BundledLauncherProviderCatalog.Load().GetProvider("guffawaffle");
        var preview = provider.ReleaseChannels["preview"];

        var composition = LauncherStartupComposition.Create(provider, preview);

        Assert.AreEqual("Preview", composition.SettingsDiagnostics.ReleaseChannelDisplayName);
        Assert.AreEqual("Guffawaffle/stfc-mod", composition.SettingsDiagnostics.ReleaseRepository);
    }

    [TestMethod]
    public void ReleaseChannelFromAnotherProviderIsRejected()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        var provider = catalog.GetProvider("guffawaffle");
        var foreignChannel = catalog.GetProvider("netniv").DefaultReleaseChannel;

        _ = Assert.ThrowsException<ArgumentException>(
            () => LauncherStartupComposition.Create(provider, foreignChannel));
    }
}
