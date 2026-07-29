using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherConfigurationApplySummaryTests
{
    [TestMethod]
    public void EmptySetHasNoPendingSummary()
    {
        var summary = LauncherConfigurationApplySummary.From([]);

        Assert.AreEqual(LauncherConfigurationApplySummaryKind.None, summary.Kind);
        Assert.AreEqual("No pending changes", summary.Text);
    }

    [TestMethod]
    public void HomogeneousSetsUseTheirExactApplyBoundary()
    {
        var immediate = LauncherConfigurationApplySummary.From(
            [LauncherConfigurationApplyBehavior.Live, LauncherConfigurationApplyBehavior.Live]);
        var nextLaunch = LauncherConfigurationApplySummary.From(
            [
                LauncherConfigurationApplyBehavior.NextSession,
                LauncherConfigurationApplyBehavior.NextSession,
            ]);

        Assert.AreEqual(LauncherConfigurationApplySummaryKind.Immediate, immediate.Kind);
        Assert.AreEqual("Applies immediately", immediate.Text);
        Assert.AreEqual(LauncherConfigurationApplySummaryKind.NextLaunch, nextLaunch.Kind);
        Assert.AreEqual("Applies next launch", nextLaunch.Text);
    }

    [TestMethod]
    public void MixedLiveAndNextSessionRequiresRelaunchCopy()
    {
        var summary = LauncherConfigurationApplySummary.From(
            [
                LauncherConfigurationApplyBehavior.Live,
                LauncherConfigurationApplyBehavior.NextSession,
            ]);

        Assert.AreEqual(LauncherConfigurationApplySummaryKind.MixedRelaunch, summary.Kind);
        Assert.AreEqual("Some changes require a relaunch", summary.Text);
    }

    [TestMethod]
    public void RestartRequirementWinsOverEveryOtherBoundary()
    {
        var summary = LauncherConfigurationApplySummary.From(
            [
                LauncherConfigurationApplyBehavior.Live,
                LauncherConfigurationApplyBehavior.NextSession,
                LauncherConfigurationApplyBehavior.RestartRequired,
            ]);

        Assert.AreEqual(LauncherConfigurationApplySummaryKind.RestartRequired, summary.Kind);
        Assert.AreEqual("One or more changes require a game restart", summary.Text);
    }

    [TestMethod]
    public void UnsupportedBehaviorFailsClosed()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LauncherConfigurationApplySummary.From([(LauncherConfigurationApplyBehavior)99]));
    }
}
