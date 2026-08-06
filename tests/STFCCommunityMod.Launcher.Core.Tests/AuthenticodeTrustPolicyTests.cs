using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class AuthenticodeTrustPolicyTests
{
    private const string PublisherSubject =
        "CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118";
    private static readonly DateTimeOffset EvaluationTime = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CompleteRfc3161EvidenceIsTrusted()
    {
        var result = Evaluate(Signature(0), Signature(1));

        Assert.IsTrue(result.IsTrusted);
        Assert.AreEqual(2, result.Evidence!.Signatures.Count);
        Assert.AreEqual(AuthenticodeRevocationMode.CachedOnly, result.Evidence.RevocationMode);
        StringAssert.Contains(result.Evidence.RevocationFreshness, "Not established");
    }

    [TestMethod]
    public void AnyRejectedSignatureFailsClosed()
    {
        var rejected = Signature(1) with { TrustPolicyPassed = false };

        var result = Evaluate(Signature(0), rejected);

        Assert.IsFalse(result.IsTrusted);
        StringAssert.Contains(result.Message, "one or more");
    }

    [TestMethod]
    public void UnexpectedFullPublisherIdentityFailsWithoutEchoingIt()
    {
        const string unexpectedIdentityHash = "BAD0BAD0";
        var unexpected = Signature(0) with
        {
            PublisherMatched = false,
            SignerIdentitySha256 = unexpectedIdentityHash,
        };

        var result = Evaluate(unexpected);

        Assert.IsFalse(result.IsTrusted);
        StringAssert.Contains(result.Message, "unexpected publisher identity");
        Assert.IsFalse(result.Message.Contains(unexpectedIdentityHash, StringComparison.Ordinal));
    }

    [TestMethod]
    public void MissingCodeSigningEkuFailsClosed()
    {
        var result = Evaluate(Signature(0) with { HasCodeSigningEku = false });

        Assert.IsFalse(result.IsTrusted);
        StringAssert.Contains(result.Message, "code-signing EKU");
    }

    [TestMethod]
    public void UnexpectedDurableArtifactSigningIdentityFailsClosed()
    {
        var result = Evaluate(Signature(0) with { DurableIdentityMatched = false });

        Assert.IsFalse(result.IsTrusted);
        StringAssert.Contains(result.Message, "durable Artifact Signing identity");
    }

    [DataTestMethod]
    [DataRow(AuthenticodeTimestampKind.None)]
    [DataRow(AuthenticodeTimestampKind.LegacyAuthenticode)]
    public void MissingRfc3161TimestampFailsClosed(AuthenticodeTimestampKind timestampKind)
    {
        var result = Evaluate(Signature(0) with { TimestampKind = timestampKind });

        Assert.IsFalse(result.IsTrusted);
        StringAssert.Contains(result.Message, "RFC 3161");
    }

    [TestMethod]
    public void Rfc3161AttributeWithoutWindowsVerifiedTimeFailsClosed()
    {
        var result = Evaluate(Signature(0) with { VerifiedAsOfUtc = null });

        Assert.IsFalse(result.IsTrusted);
        StringAssert.Contains(result.Message, "Windows-verified RFC 3161");
    }

    [TestMethod]
    public void OnlineModeRecordsPermissionWithoutClaimingFreshness()
    {
        var result = AuthenticodeTrustPolicy.Evaluate(
            AuthenticodeRevocationMode.OnlineRetrievalAllowed,
            EvaluationTime,
            [Signature(0)]);

        Assert.IsTrue(result.IsTrusted);
        Assert.AreEqual(AuthenticodeRevocationMode.OnlineRetrievalAllowed, result.Evidence!.RevocationMode);
        StringAssert.Contains(result.Evidence.RevocationFreshness, "Not established");
    }

    [TestMethod]
    public void InvalidRevocationModeCannotFallThroughToNetworkEnabledPolicy()
    {
        var verifier = new WindowsAuthenticodeVerifier(
            PublisherSubject,
            LauncherSelfUpdateAuthority.WindowsArtifactSigningIdentityEku);

        var result = verifier.Verify("not-used", (AuthenticodeRevocationMode)42);

        Assert.IsFalse(result.IsTrusted);
        StringAssert.Contains(result.Message, "revocation mode is invalid");
    }

    [TestMethod]
    public void DurableIdentityMustBeAnOid()
    {
        Assert.ThrowsException<ArgumentException>(() => new WindowsAuthenticodeVerifier(PublisherSubject, "not-an-oid"));
    }

    [TestMethod]
    public void OptedInSignedReleasePayloadSatisfiesRuntimePolicy()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Runtime Authenticode verification requires Windows.");
        }
        var releaseRoot = Environment.GetEnvironmentVariable("STFC_MOD_BRIDGE_SIGNED_RELEASE_ROOT");
        if (string.IsNullOrWhiteSpace(releaseRoot))
        {
            Assert.Inconclusive("Set STFC_MOD_BRIDGE_SIGNED_RELEASE_ROOT to verify a signed release payload.");
        }

        var verifier = new WindowsAuthenticodeVerifier(
            PublisherSubject,
            LauncherSelfUpdateAuthority.WindowsArtifactSigningIdentityEku);
        var artifacts = new[]
        {
            Path.Combine(releaseRoot, "app", "STFCModBridge.exe"),
            Path.Combine(releaseRoot, "app", "STFCModBridge.Updater.exe"),
        };
        foreach (var artifact in artifacts)
        {
            Assert.IsTrue(File.Exists(artifact), $"Signed release artifact was not found: {artifact}");
            var result = verifier.Verify(artifact);
            Assert.IsTrue(result.IsTrusted, $"{Path.GetFileName(artifact)}: {result.Message}");
        }
    }

    private static ModArtifactAuthenticityResult Evaluate(params AuthenticodeSignatureEvidence[] signatures) =>
        AuthenticodeTrustPolicy.Evaluate(AuthenticodeRevocationMode.CachedOnly, EvaluationTime, signatures);

    private static AuthenticodeSignatureEvidence Signature(int index) => new(
        index,
        TrustPolicyPassed: true,
        PublisherMatched: true,
        HasCodeSigningEku: true,
        DurableIdentityMatched: true,
        AuthenticodeTimestampKind.Rfc3161,
        new DateTimeOffset(2026, 8, 2, 21, 43, 49, TimeSpan.Zero),
        "C74B3BAD5483C706160B84ACF59F9127F4659AE52C67A499145E75032D1F3F43");
}
