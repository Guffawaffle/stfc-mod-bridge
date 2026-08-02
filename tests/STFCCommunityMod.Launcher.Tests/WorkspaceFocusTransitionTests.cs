namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class WorkspaceFocusTransitionTests
{
    [TestMethod]
    public void EnterFocusesWorkspaceAndExitReturnsFocusExactlyOnce()
    {
        var transition = new WorkspaceFocusTransition();
        var entryFocusCount = 0;
        var returnFocusCount = 0;

        transition.Enter(
            () => entryFocusCount++,
            () => returnFocusCount++);

        Assert.AreEqual(1, entryFocusCount);
        Assert.AreEqual(0, returnFocusCount);

        transition.Exit();
        transition.Exit();

        Assert.AreEqual(1, returnFocusCount);
    }

    [TestMethod]
    public void ReenterReplacesStaleReturnTarget()
    {
        var transition = new WorkspaceFocusTransition();
        var firstReturnCount = 0;
        var secondReturnCount = 0;

        transition.Enter(() => { }, () => firstReturnCount++);
        transition.Enter(() => { }, () => secondReturnCount++);
        transition.Exit();

        Assert.AreEqual(0, firstReturnCount);
        Assert.AreEqual(1, secondReturnCount);
    }
}
