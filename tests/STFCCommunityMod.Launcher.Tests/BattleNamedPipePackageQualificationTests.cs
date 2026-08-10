namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class BattleNamedPipePackageQualificationTests
{
    [TestMethod]
    public void UnrelatedStartupArgumentsDoNotEnterQualification()
    {
        Assert.IsFalse(BattleNamedPipePackageQualification.TryRun([], out var exitCode));
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void MalformedQualificationFailsClosedWithoutStartingTheShell()
    {
        Assert.IsTrue(BattleNamedPipePackageQualification.TryRun(
            [BattleNamedPipePackageQualification.Argument, "unknown"],
            out var exitCode));
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public void StandaloneQualificationExercisesTheRealPipeHostAndExactProcessAuthorizer()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Battle named-pipe package qualification requires Windows.");
        }

        Assert.IsTrue(BattleNamedPipePackageQualification.TryRun(
            [
                BattleNamedPipePackageQualification.Argument,
                BattleNamedPipePackageQualification.StandaloneMode,
            ],
            out var exitCode));
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void MsixQualificationRejectsAnUnpackagedProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Battle named-pipe package qualification requires Windows.");
        }

        Assert.IsTrue(BattleNamedPipePackageQualification.TryRun(
            [
                BattleNamedPipePackageQualification.Argument,
                BattleNamedPipePackageQualification.MsixMode,
            ],
            out var exitCode));
        Assert.AreEqual(1, exitCode);
    }
}
