namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherReleaseIdentityTests
{
    [TestMethod]
    public void SignedProductVersionCarriesCommitAndVerifierDigestWithoutChangingDisplayVersion()
    {
        var commit = new string('a', 40);
        var verifier = new string('b', 64);
        var productVersion = $"0.2.0-rc.1+commit.{commit}.verifier.{verifier}";

        var identity = LauncherReleaseIdentityParser.Parse(productVersion);

        Assert.AreEqual(commit, identity.SourceCommit);
        Assert.AreEqual(verifier, identity.ReleaseVerifierSha256);
        Assert.IsTrue(identity.HasReleaseVerifierPairing);
        Assert.AreEqual("0.2.0-rc.1", LauncherInstalledProduct.NormalizeVersion(productVersion));
    }

    [TestMethod]
    public void MissingOrSentinelPairingFailsClosed()
    {
        var missing = LauncherReleaseIdentityParser.Parse("0.2.0+abcdef");
        var sentinel = LauncherReleaseIdentityParser.Parse(
            $"0.2.0+commit.unknown.verifier.{new string('0', 64)}");

        Assert.IsNull(missing.SourceCommit);
        Assert.IsNull(missing.ReleaseVerifierSha256);
        Assert.IsFalse(missing.HasReleaseVerifierPairing);
        Assert.IsNull(sentinel.SourceCommit);
        Assert.IsFalse(sentinel.HasReleaseVerifierPairing);
    }
}
