using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class SystemGameProcessInspectorTests
{
    [TestMethod]
    public void SameInstallProcessBlocksTarget()
    {
        using var target = new TemporaryDirectory();
        var executable = Path.Combine(target.Path, "prime.exe");
        var inspector = CreateInspector(executable);

        Assert.IsTrue(inspector.IsGameRunning(target.Path));
        Assert.AreEqual(GameProcessInspectionState.RunningTarget, inspector.Inspect(target.Path));
    }

    [TestMethod]
    public void DifferentInstallProcessDoesNotBlockTarget()
    {
        using var target = new TemporaryDirectory();
        using var other = new TemporaryDirectory();
        var inspector = CreateInspector(Path.Combine(other.Path, "prime.exe"));

        Assert.IsFalse(inspector.IsGameRunning(target.Path));
        Assert.AreEqual(GameProcessInspectionState.NotRunning, inspector.Inspect(target.Path));
    }

    [TestMethod]
    public void UninspectablePrimeProcessFailsClosed()
    {
        using var target = new TemporaryDirectory();
        var inspector = new SystemGameProcessInspector(
            () => [new(null, IsInspectable: false)]);

        Assert.IsTrue(inspector.IsGameRunning(target.Path));
        Assert.AreEqual(GameProcessInspectionState.Unattributable, inspector.Inspect(target.Path));
    }

    [TestMethod]
    public void UninspectablePrimeTakesPrecedenceOverAttributedTarget()
    {
        using var target = new TemporaryDirectory();
        var inspector = new SystemGameProcessInspector(
            () =>
            [
                new(Path.Combine(target.Path, "prime.exe")),
                new(null, IsInspectable: false),
            ]);

        Assert.AreEqual(GameProcessInspectionState.Unattributable, inspector.Inspect(target.Path));
    }

    private static SystemGameProcessInspector CreateInspector(string executablePath) =>
        new(() => [new(executablePath)]);
}
