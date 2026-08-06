using System.Text;
using System.Text.Json.Nodes;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ReleaseSelectionVerificationReceiptTests
{
    private const string ManifestDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BundleDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Tag = "v1.2.3-rc.4";

    [TestMethod]
    public void ClosedReceiptParsesAndMatchesOriginalRequest()
    {
        var receipt = Parse(ValidReceipt());

        Assert.IsTrue(receipt.Verified);
        Assert.AreEqual("refs/tags/v1.2.3-rc.4", receipt.SourceRef);
        Assert.AreEqual(1, receipt.RekorEntries.Count);
        Assert.AreEqual(TimeSpan.Zero, receipt.RekorEntries[0].IntegratedTime.Offset);
    }

    [TestMethod]
    public void UnknownReceiptPropertyFailsClosed()
    {
        var json = ValidReceipt();
        json["allowUnsafe"] = true;

        var exception = Assert.ThrowsException<InvalidDataException>(() => Parse(json));

        StringAssert.Contains(exception.Message, "unknown property");
    }

    [TestMethod]
    public void DuplicateReceiptPropertyFailsClosed()
    {
        var receipt = ValidReceipt().ToJsonString();
        receipt = receipt[..^1] + ",\"verified\":true}";

        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            ReleaseSelectionVerificationReceiptParser.Parse(
                Encoding.UTF8.GetBytes(receipt),
                Request(),
                ManifestDigest,
                BundleDigest));

        StringAssert.Contains(exception.Message, "duplicate property");
    }

    [TestMethod]
    public void MismatchedManifestDigestFailsClosed()
    {
        var json = ValidReceipt();
        json["manifestSha256"] = new string('c', 64);

        var exception = Assert.ThrowsException<InvalidDataException>(() => Parse(json));

        StringAssert.Contains(exception.Message, "mismatched");
    }

    [TestMethod]
    public void MissingCheckFailsClosed()
    {
        var json = ValidReceipt();
        json["checks"]!.AsArray().RemoveAt(0);

        var exception = Assert.ThrowsException<InvalidDataException>(() => Parse(json));

        StringAssert.Contains(exception.Message, "check set");
    }

    [TestMethod]
    public void UnexpectedRepositoryIdentityFailsClosed()
    {
        var json = ValidReceipt();
        json["repositoryId"] = "1";

        var exception = Assert.ThrowsException<InvalidDataException>(() => Parse(json));

        StringAssert.Contains(exception.Message, "identity policy");
    }

    [TestMethod]
    public void DuplicateRekorEntryFailsClosed()
    {
        var json = ValidReceipt();
        json["rekorEntries"]!.AsArray().Add(json["rekorEntries"]![0]!.DeepClone());

        var exception = Assert.ThrowsException<InvalidDataException>(() => Parse(json));

        StringAssert.Contains(exception.Message, "exactly one");
    }

    [TestMethod]
    public void UnrecognizedRekorLogFailsClosed()
    {
        var json = ValidReceipt();
        json["rekorEntries"]!.AsArray()[0]!["logId"] = new string('d', 64);

        var exception = Assert.ThrowsException<InvalidDataException>(() => Parse(json));

        StringAssert.Contains(exception.Message, "valid Rekor entry");
    }

    [TestMethod]
    [DataRow("trustEpoch", "2")]
    [DataRow("trustedRootSha256", "\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\"")]
    public void UnknownRootEpochOrDigestFailsClosed(string property, string replacementJson)
    {
        var json = ValidReceipt();
        json[property] = JsonNode.Parse(replacementJson);

        var exception = Assert.ThrowsException<InvalidDataException>(() => Parse(json));

        StringAssert.Contains(exception.Message, "identity policy");
    }

    [TestMethod]
    public void MalformedReceiptCorpusAlwaysFailsClosedWithoutEscapingParserContract()
    {
        var valid = Encoding.UTF8.GetBytes(ValidReceipt().ToJsonString());
        var corpus = new List<byte[]>
        {
            Array.Empty<byte>(),
            new byte[] { 0xff, 0xfe, 0xfd },
            Encoding.UTF8.GetBytes("{}{}"),
            Encoding.UTF8.GetBytes("{/*comment*/}"),
            Encoding.UTF8.GetBytes(new string('[', 10) + new string(']', 10)),
            new byte[ReleaseSelectionAttestationPolicy.MaximumReceiptBytes + 1],
        };
        for (var length = 1; length < Math.Min(valid.Length, 256); length++)
        {
            corpus.Add(valid[..length]);
        }

        for (var index = 0; index < corpus.Count; index++)
        {
            Assert.ThrowsException<InvalidDataException>(() =>
            {
                _ = ReleaseSelectionVerificationReceiptParser.Parse(corpus[index], Request(), ManifestDigest, BundleDigest);
            }, $"Malformed receipt corpus item {index} was accepted.");
        }
    }

    [TestMethod]
    public void RequestFactoryRejectsNonCanonicalTag()
    {
        var exception = Assert.ThrowsException<ArgumentException>(() =>
            ReleaseSelectionAttestationPolicy.CreateRequest(
                Path.Combine(Path.GetTempPath(), ReleaseSelectionAttestationPolicy.ManifestName),
                Path.Combine(Path.GetTempPath(), ReleaseSelectionAttestationPolicy.BundleName),
                "refs/tags/v1.2.3"));

        StringAssert.Contains(exception.Message, "not canonical");
    }

    [TestMethod]
    public void RequestFactoryRejectsWrongEvidenceBasenames()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            ReleaseSelectionAttestationPolicy.CreateRequest(
                Path.Combine(Path.GetTempPath(), "other.json"),
                Path.Combine(Path.GetTempPath(), ReleaseSelectionAttestationPolicy.BundleName),
                Tag));
    }

    private static ReleaseSelectionVerificationReceipt Parse(JsonObject json) =>
        ReleaseSelectionVerificationReceiptParser.Parse(
            Encoding.UTF8.GetBytes(json.ToJsonString()),
            Request(),
            ManifestDigest,
            BundleDigest);

    private static ReleaseSelectionVerificationRequest Request() =>
        ReleaseSelectionAttestationPolicy.CreateRequest(
            Path.Combine(Path.GetTempPath(), ReleaseSelectionAttestationPolicy.ManifestName),
            Path.Combine(Path.GetTempPath(), ReleaseSelectionAttestationPolicy.BundleName),
            Tag);

    private static JsonObject ValidReceipt() => new()
    {
        ["schemaVersion"] = 1,
        ["verified"] = true,
        ["verificationMode"] = "offline",
        ["repository"] = "Guffawaffle/stfc-mod-bridge",
        ["repositoryId"] = "1320037274",
        ["ownerId"] = "105761663",
        ["workflow"] = ".github/workflows/release.yml",
        ["sourceRef"] = "refs/tags/v1.2.3-rc.4",
        ["sourceCommit"] = "37c61305a553ec155c05186a0e6549c70b4ed489",
        ["event"] = "push",
        ["runner"] = "github-hosted",
        ["statementType"] = "https://in-toto.io/Statement/v1",
        ["predicateType"] = "https://slsa.dev/provenance/v1",
        ["buildType"] = "https://actions.github.io/buildtypes/workflow/v1",
        ["subjectName"] = "stfc-mod-bridge-release-manifest.json",
        ["manifestSha256"] = ManifestDigest,
        ["bundleSha256"] = BundleDigest,
        ["trustEpoch"] = 1,
        ["trustedRootSha256"] = "844a1c6de3986c9f02070266b25e0d1a2fa99ceccc89f6b9ad90aae47b62a16e",
        ["fulcioIssuer"] = "https://token.actions.githubusercontent.com",
        ["fulcioSan"] = "https://github.com/Guffawaffle/stfc-mod-bridge/.github/workflows/release.yml@refs/tags/v1.2.3-rc.4",
        ["rekorEntries"] = new JsonArray
        {
            new JsonObject
            {
                ["logId"] = "c0d23d6ad406973f9559f3ba2d1ca01f84147d8ffc5b8445c224f98b9591801d",
                ["logIndex"] = 2343065850,
                ["integratedTime"] = "2026-08-05T07:45:02Z",
            },
        },
        ["checks"] = new JsonArray(
            ReleaseSelectionAttestationPolicy.RequiredChecks
                .Select(check => (JsonNode?)JsonValue.Create(check))
                .ToArray()),
    };
}
