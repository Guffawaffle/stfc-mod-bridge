using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class GameInstallValidatorTests
{
    [TestMethod]
    public void ValidateAcceptsNonAsciiDirectoryContainingPrime()
    {
        using var temporaryDirectory = new TemporaryDirectory("艦隊 ゲーム");
        TemporaryDirectory.CreateFile(temporaryDirectory.Path, "prime.exe");

        var result = GameInstallValidator.Validate(temporaryDirectory.Path);

        Assert.AreEqual(GameInstallValidationCode.Valid, result.Code);
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(
            Path.Combine(temporaryDirectory.Path, "prime.exe"),
            result.PrimeExecutablePath);
    }

    [TestMethod]
    public void ValidateRejectsOfficialLauncherDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        TemporaryDirectory.CreateFile(temporaryDirectory.Path, "Star Trek Fleet Command.exe");

        var result = GameInstallValidator.Validate(temporaryDirectory.Path);

        Assert.AreEqual(GameInstallValidationCode.OfficialLauncherDirectory, result.Code);
        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message, "Scopely launcher");
    }

    [TestMethod]
    public void ValidateDoesNotDescendFromLauncherRoot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var nestedGameDirectory = temporaryDirectory.CreateDirectory("default", "game");
        TemporaryDirectory.CreateFile(nestedGameDirectory, "prime.exe");

        var result = GameInstallValidator.Validate(temporaryDirectory.Path);

        Assert.AreEqual(GameInstallValidationCode.OfficialLauncherDirectory, result.Code);
        Assert.IsFalse(result.IsValid);
    }

    [TestMethod]
    public void ValidateRejectsMissingDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var missingPath = Path.Combine(temporaryDirectory.Path, "not-installed");

        var result = GameInstallValidator.Validate(missingPath);

        Assert.AreEqual(GameInstallValidationCode.DirectoryMissing, result.Code);
        Assert.IsFalse(result.IsValid);
    }
}
