using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.LocalGameIntegration.Tests;

[TestClass]
public sealed class LocalGameIntegrationTargetTests
{
    [TestMethod]
    public void DisabledContractDoesNotInspectConfiguredPath()
    {
        var result = LocalGameIntegrationTarget.Resolve(false, "not-a-valid-path");

        Assert.AreEqual(LocalGameIntegrationTargetState.Disabled, result.State);
        Assert.IsNull(result.Validation);
    }

    [TestMethod]
    public void EnabledContractRequiresExplicitPath()
    {
        var result = LocalGameIntegrationTarget.Resolve(true, null);

        Assert.AreEqual(LocalGameIntegrationTargetState.MissingPath, result.State);
        Assert.IsNull(result.Validation);
    }

    [TestMethod]
    public void EnabledContractUsesProductionGameValidation()
    {
        var directory = Directory.CreateTempSubdirectory("stfc-bridge-local-contract-");
        try
        {
            var invalid = LocalGameIntegrationTarget.Resolve(true, directory.FullName);
            File.WriteAllBytes(Path.Combine(directory.FullName, "prime.exe"), [0]);
            var valid = LocalGameIntegrationTarget.Resolve(true, directory.FullName);

            Assert.AreEqual(LocalGameIntegrationTargetState.Invalid, invalid.State);
            Assert.AreEqual(
                GameInstallValidationCode.PrimeExecutableMissing,
                invalid.Validation?.Code);
            Assert.AreEqual(LocalGameIntegrationTargetState.Ready, valid.State);
            Assert.IsTrue(valid.Validation?.IsValid);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
