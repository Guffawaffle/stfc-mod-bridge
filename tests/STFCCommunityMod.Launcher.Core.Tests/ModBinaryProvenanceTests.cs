namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ModBinaryProvenanceTests
{
    private const string Sha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string IdentityComment =
        "stfc-identity-v1;distribution=guffawaffle.stfc-community-mod;source=git:abcdef;"
        + "base=abcdef;build=ax:123;mode=release;channel=local";

    [TestMethod]
    public void IdentityParserReadsTheSchemaOneContract()
    {
        var result = ModBuildIdentityCommentParser.Parse(IdentityComment);

        Assert.AreEqual(ModBuildIdentityParseState.Valid, result.State);
        Assert.IsNotNull(result.Identity);
        Assert.AreEqual("guffawaffle.stfc-community-mod", result.Identity.DistributionId);
        Assert.AreEqual("git:abcdef", result.Identity.SourceStateId);
        Assert.AreEqual("ax:123", result.Identity.BuildInvocationId);
        Assert.AreEqual("local", result.Identity.BuildChannel);
    }

    [TestMethod]
    public void IdentityParserAcceptsTheSharedNetnivDistributionContract()
    {
        var result = ModBuildIdentityCommentParser.Parse(
            IdentityComment.Replace(
                "guffawaffle.stfc-community-mod",
                "netniv.stfc-community-mod",
                StringComparison.Ordinal));

        Assert.AreEqual(ModBuildIdentityParseState.Valid, result.State);
        Assert.AreEqual("netniv.stfc-community-mod", result.Identity?.DistributionId);
    }

    [TestMethod]
    public void IdentityParserRequiresCanonicalFieldsButReadersAreOrderTolerant()
    {
        var reordered =
            "stfc-identity-v1;channel=local;mode=release;build=ax:123;base=abcdef;"
            + "source=git:abcdef;distribution=guffawaffle.stfc-community-mod";

        var result = ModBuildIdentityCommentParser.Parse(reordered);

        Assert.AreEqual(ModBuildIdentityParseState.Valid, result.State);
        Assert.AreEqual("ax:123", result.Identity?.BuildInvocationId);
    }

    [TestMethod]
    public void IdentityParserDistinguishesUnmarkedFromMalformedIdentity()
    {
        Assert.AreEqual(
            ModBuildIdentityParseState.Unmarked,
            ModBuildIdentityCommentParser.Parse("ordinary version comments").State);
        Assert.AreEqual(
            ModBuildIdentityParseState.Malformed,
            ModBuildIdentityCommentParser.Parse(
                IdentityComment + ";distribution=duplicate").State);
        Assert.AreEqual(
            ModBuildIdentityParseState.Malformed,
            ModBuildIdentityCommentParser.Parse(
                IdentityComment.Replace(";channel=local", string.Empty, StringComparison.Ordinal)).State);
        Assert.AreEqual(
            ModBuildIdentityParseState.Malformed,
            ModBuildIdentityCommentParser.Parse(
                IdentityComment.Replace("ax:123", "unsafe value", StringComparison.Ordinal)).State);
        Assert.AreEqual(
            ModBuildIdentityParseState.Malformed,
            ModBuildIdentityCommentParser.Parse(
                IdentityComment.Replace("stfc-identity-v1", "stfc-identity-v2", StringComparison.Ordinal)).State);
    }

    [TestMethod]
    public void ReviewedHashTakesPrecedenceOverDescriptiveMetadata()
    {
        var known = KnownArtifact();
        var resolver = new ModBinaryProvenanceResolver(
            new FakeMetadataReader(new("9.9.9.9", "custom", "ordinary comments")),
            new([known]));

        var result = resolver.Resolve("version.dll", Sha256, 42);

        Assert.AreEqual(ModBinaryProvenanceState.KnownProviderArtifact, result.State);
        Assert.AreEqual("netniv", result.DetectedProviderId);
        Assert.AreEqual("netniv.stfc-community-mod", result.DetectedRuntimeDistributionId);
        Assert.AreEqual(known, result.KnownArtifact);
    }

    [TestMethod]
    public void UnreviewedMarkerIsLineageButNotOfficialReleaseProof()
    {
        var resolver = new ModBinaryProvenanceResolver(
            new FakeMetadataReader(new("2.1.0.0", "local", IdentityComment)),
            KnownModArtifactCatalog.Empty);

        var result = resolver.Resolve("version.dll", Sha256, 42);

        Assert.AreEqual(ModBinaryProvenanceState.SelfDeclaredLineage, result.State);
        Assert.AreEqual("guffawaffle.stfc-community-mod", result.DetectedRuntimeDistributionId);
        Assert.IsNull(result.DetectedProviderId);
        StringAssert.Contains(result.Detail, "not official-release authenticity proof");
    }

    [TestMethod]
    public void MetadataFailureDoesNotEraseTheArtifactHash()
    {
        var resolver = new ModBinaryProvenanceResolver(
            new ThrowingMetadataReader(),
            KnownModArtifactCatalog.Empty);

        var result = resolver.Resolve("version.dll", Sha256, 42);

        Assert.AreEqual(ModBinaryProvenanceState.MetadataUnavailable, result.State);
        Assert.AreEqual(Sha256, result.Sha256);
        Assert.AreEqual(42, result.Size);
    }

    [TestMethod]
    public void DeploymentVersionReaderRejectsMismatchedOrMalformedDeclaredLineage()
    {
        var matching = new WindowsModArtifactVersionReader(
            "guffawaffle.stfc-community-mod",
            new FakeMetadataReader(new("2.1.0.0", "local", IdentityComment)));
        Assert.AreEqual("2.1.0.0", matching.ReadVersion("version.dll"));
        Assert.AreEqual("local", matching.ReadProductVersion("version.dll"));

        var mismatched = new WindowsModArtifactVersionReader(
            "netniv.stfc-community-mod",
            new FakeMetadataReader(new("2.1.0.0", "local", IdentityComment)));
        Assert.ThrowsException<InvalidDataException>(() => mismatched.ReadVersion("version.dll"));

        var malformed = new WindowsModArtifactVersionReader(
            "guffawaffle.stfc-community-mod",
            new FakeMetadataReader(new("2.1.0.0", "local", IdentityComment + ";extra=value")));
        Assert.ThrowsException<InvalidDataException>(() => malformed.ReadVersion("version.dll"));
    }

    [TestMethod]
    public void DeploymentVersionReaderAllowsReviewedPreMarkerArtifact()
    {
        var reader = new WindowsModArtifactVersionReader(
            "guffawaffle.stfc-community-mod",
            new FakeMetadataReader(new("2.1.0.0", "v2.1.0-guffa.8", string.Empty)));

        Assert.AreEqual("2.1.0.0", reader.ReadVersion("version.dll"));
    }

    [TestMethod]
    public void KnownArtifactCatalogRejectsAmbiguousOrInvalidIdentity()
    {
        Assert.ThrowsException<InvalidDataException>(
            () => new KnownModArtifactCatalog([KnownArtifact(), KnownArtifact()]));
        Assert.ThrowsException<InvalidDataException>(
            () => new KnownModArtifactCatalog([KnownArtifact() with { Sha256 = "not-a-hash" }]));
    }

    private static KnownModArtifactIdentity KnownArtifact() => new(
        "netniv",
        "netniv.stfc-community-mod",
        "stable",
        "1.1.4",
        42,
        Sha256,
        "github-release:v1.1.4",
        new DateTimeOffset(2026, 7, 19, 15, 55, 25, TimeSpan.Zero));

    private sealed class FakeMetadataReader(ModBinaryVersionMetadata metadata)
        : IModBinaryVersionMetadataReader
    {
        public ModBinaryVersionMetadata Read(string path) => metadata;
    }

    private sealed class ThrowingMetadataReader : IModBinaryVersionMetadataReader
    {
        public ModBinaryVersionMetadata Read(string path) => throw new IOException("Injected failure.");
    }
}
