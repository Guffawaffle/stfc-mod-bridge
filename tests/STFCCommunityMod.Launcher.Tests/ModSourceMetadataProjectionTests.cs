using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ModSourceMetadataProjectionTests
{
    private const string Sha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [TestMethod]
    public void KnownHashProjectsDetectedProviderInsteadOfSelectedSource()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        var artifact = new KnownModArtifactIdentity(
            "netniv",
            "netniv.stfc-community-mod",
            "stable",
            "1.1.4",
            42,
            Sha256,
            "github-release:v1.1.4",
            DateTimeOffset.UnixEpoch);
        var installation = Installation(new(
            ModBinaryProvenanceState.KnownProviderArtifact,
            Sha256,
            42,
            "1.1.4.0",
            "1.1.4.0",
            KnownArtifact: artifact));

        var result = ModSourceMetadataProjection.From(installation, catalog, "Guffawaffle · Stable");

        Assert.AreEqual("Installed: NetniV · Stable · reviewed hash", result);
    }

    [TestMethod]
    public void SelfDeclaredMarkerProjectsCustomProviderLineage()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        var identity = new ModBuildIdentity(
            1,
            "guffawaffle.stfc-community-mod",
            "git:abc",
            "abc",
            "ax:123",
            "release",
            "local");
        var installation = Installation(new(
            ModBinaryProvenanceState.SelfDeclaredLineage,
            Sha256,
            42,
            "2.1.0.0",
            "local",
            BuildIdentity: identity));

        var result = ModSourceMetadataProjection.From(installation, catalog, "Guffawaffle · Stable");

        Assert.AreEqual("Installed: Guffawaffle · custom build", result);
    }

    [TestMethod]
    public void UnmarkedCustomBuildDoesNotGuessSelectedProvider()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        var installation = Installation(new(
            ModBinaryProvenanceState.CustomUnattributed,
            Sha256,
            42,
            "2.1.0.0",
            "local"));

        var result = ModSourceMetadataProjection.From(installation, catalog, "Guffawaffle · Stable");

        Assert.AreEqual("Installed: custom build · selected source Guffawaffle · Stable", result);
    }

    private static ModInstallationEvidence Installation(ModBinaryProvenance provenance) => new(
        ModInstallationEvidenceState.ManualInstallation,
        false,
        provenance.FileVersion,
        InstalledSha256: provenance.Sha256,
        BinaryProvenance: provenance);
}
