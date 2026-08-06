using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class AuthenticatedReleaseManifestTests
{
    private const string ManifestDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BundleDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RootDigest = "844a1c6de3986c9f02070266b25e0d1a2fa99ceccc89f6b9ad90aae47b62a16e";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset IssuedAt = Utc(2026, 8, 6, 9, 30);
    private static readonly DateTimeOffset RekorTime = Utc(2026, 8, 6, 10, 0);
    private static readonly DateTimeOffset LocalNow = Utc(2026, 8, 6, 10, 5);

    [TestMethod]
    public void ValidV2ManifestProducesNonAuthorizingAcceptanceState()
    {
        var manifest = Parse(Manifest());

        var acceptance = AuthenticatedReleaseManifestPolicy.Evaluate(
            manifest,
            Receipt(),
            "0.1.0",
            LocalNow);

        Assert.AreEqual(2, manifest.SchemaVersion);
        Assert.AreEqual(42L, acceptance.State.HighestReleaseSequence);
        Assert.AreEqual("0.2.0", acceptance.State.HighestReleaseVersion);
        Assert.AreEqual(LocalNow, acceptance.State.FirstObservedUtc);
        Assert.AreEqual(LocalNow, acceptance.State.LastObservedUtc);
        Assert.AreEqual(ManifestDigest, acceptance.State.ManifestSha256);
        Assert.AreEqual(BundleDigest, acceptance.State.BundleSha256);
    }

    [TestMethod]
    public void LegacyParserCannotActivateAuthenticatedV2Manifest()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Manifest().ToJsonString()));

        Assert.ThrowsException<InvalidDataException>(() => WindowsReleaseManifestParser.Parse(stream));
    }

    [DataTestMethod]
    [DataRow("unknown")]
    [DataRow("timestamp-offset")]
    [DataRow("wrong-scheme")]
    [DataRow("duplicate-withdrawal")]
    [DataRow("ambiguous-sequence")]
    [DataRow("dot-signed-file")]
    [DataRow("dotdot-signed-file")]
    public void ClosedV2SchemaRejectsMalformedOrUnsupportedValues(string mutation)
    {
        var json = Manifest();
        switch (mutation)
        {
            case "unknown":
                json["surprise"] = true;
                break;
            case "timestamp-offset":
                json["issuedAt"] = "2026-08-06T09:30:00+00:00";
                break;
            case "wrong-scheme":
                json["manifestAuthenticity"]!["scheme"] = "none";
                break;
            case "duplicate-withdrawal":
                var withdrawal = Withdrawal("manifest-sha256", new string('c', 64));
                json["withdrawals"]!.AsArray().Add(withdrawal);
                json["withdrawals"]!.AsArray().Add(withdrawal.DeepClone());
                break;
            case "ambiguous-sequence":
                json["withdrawals"]!.AsArray().Add(Withdrawal("release-sequence", "042"));
                break;
            case "dot-signed-file":
                json["artifacts"]![0]!["authenticity"]!["signedFiles"] = new JsonArray(".");
                break;
            case "dotdot-signed-file":
                json["artifacts"]![0]!["authenticity"]!["signedFiles"] = new JsonArray("..");
                break;
        }

        if (mutation == "wrong-scheme")
        {
            var parsed = Parse(json);
            Assert.ThrowsException<InvalidDataException>(() =>
                AuthenticatedReleaseManifestPolicy.Evaluate(parsed, Receipt(), "0.1.0", LocalNow));
        }
        else
        {
            Assert.ThrowsException<InvalidDataException>(() => Parse(json));
        }
    }

    [TestMethod]
    public void DuplicateAndTrailingJsonFailClosed()
    {
        var valid = Manifest().ToJsonString();
        var duplicate = valid[..^1] + ",\"schemaVersion\":2}";

        Assert.ThrowsException<InvalidDataException>(() => Parse(duplicate));
        Assert.ThrowsException<InvalidDataException>(() => Parse(valid + "{}"));
        Assert.ThrowsException<InvalidDataException>(() =>
            AuthenticatedReleaseManifestParser.Parse(
                new byte[AuthenticatedReleaseManifestParser.MaximumManifestBytes + 1]));
    }

    [TestMethod]
    public void BoundedMalformedManifestCorpusFailsClosed()
    {
        var valid = Encoding.UTF8.GetBytes(Manifest().ToJsonString());
        var corpus = new List<byte[]>
        {
            Array.Empty<byte>(),
            new byte[] { 0xff, 0xfe, 0xfd },
            Encoding.UTF8.GetBytes("[]"),
            Encoding.UTF8.GetBytes(new string('[', 17) + new string(']', 17)),
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1e100}"),
        };
        for (var length = 1; length < Math.Min(valid.Length, 256); length++)
        {
            corpus.Add(valid[..length]);
        }
        for (var index = 0; index < corpus.Count; index++)
        {
            Assert.ThrowsException<InvalidDataException>(
                () => AuthenticatedReleaseManifestParser.Parse(corpus[index]),
                $"Malformed authenticated-manifest corpus item {index} was accepted.");
        }
    }

    [DataTestMethod]
    [DataRow("future-issued")]
    [DataRow("late-signing")]
    [DataRow("excess-validity")]
    [DataRow("expired")]
    public void TimingPolicyFailsClosed(string mutation)
    {
        var manifest = Parse(Manifest());
        var localNow = LocalNow;
        manifest = mutation switch
        {
            "future-issued" => manifest with { IssuedAt = RekorTime.AddMinutes(11) },
            "late-signing" => manifest with { IssuedAt = RekorTime.AddHours(-1).AddSeconds(-1) },
            "excess-validity" => manifest with { ExpiresAt = manifest.IssuedAt.AddDays(45).AddSeconds(1) },
            "expired" => manifest,
            _ => throw new AssertFailedException("Unknown timing mutation."),
        };
        if (mutation == "expired")
        {
            localNow = manifest.ExpiresAt.AddMinutes(11);
        }

        Assert.ThrowsException<InvalidDataException>(() =>
            AuthenticatedReleaseManifestPolicy.Evaluate(manifest, Receipt(), "0.1.0", localNow));
    }

    [TestMethod]
    public void ExactReplayCanRefreshObservationWithoutRebindingEvidence()
    {
        var manifest = Parse(Manifest());
        var first = AuthenticatedReleaseManifestPolicy.Evaluate(manifest, Receipt(), "0.1.0", LocalNow);

        var repeated = AuthenticatedReleaseManifestPolicy.Evaluate(
            manifest,
            Receipt(),
            "0.1.0",
            LocalNow.AddHours(1),
            first.State);

        Assert.AreEqual(first.State.FirstObservedUtc, repeated.State.FirstObservedUtc);
        Assert.AreEqual(LocalNow.AddHours(1), repeated.State.LastObservedUtc);

        var reboundReceipt = Receipt() with { ManifestSha256 = new string('c', 64) };
        Assert.ThrowsException<InvalidDataException>(() =>
            AuthenticatedReleaseManifestPolicy.Evaluate(
                manifest,
                reboundReceipt,
                "0.1.0",
                LocalNow.AddHours(1),
                first.State));
    }

    [TestMethod]
    public void SequenceVersionTrustAndClockRollbacksFailClosed()
    {
        var manifest = Parse(Manifest());
        var accepted = AuthenticatedReleaseManifestPolicy.Evaluate(manifest, Receipt(), "0.1.0", LocalNow);

        Assert.ThrowsException<InvalidDataException>(() =>
            AuthenticatedReleaseManifestPolicy.Evaluate(
                manifest with { ReleaseSequence = 41 },
                Receipt(),
                "0.1.0",
                LocalNow,
                accepted.State));

        var higherFloor = accepted.State with
        {
            HighestReleaseSequence = 41,
            HighestReleaseVersion = "0.3.0",
            Tag = "v0.3.0",
        };
        Assert.ThrowsException<InvalidDataException>(() =>
            AuthenticatedReleaseManifestPolicy.Evaluate(manifest, Receipt(), "0.1.0", LocalNow, higherFloor));

        var futureObservation = accepted.State with
        {
            LastObservedUtc = LocalNow.AddDays(2),
        };
        Assert.ThrowsException<InvalidDataException>(() =>
            AuthenticatedReleaseManifestPolicy.Evaluate(manifest, Receipt(), "0.1.0", LocalNow, futureObservation));

        var newerRoot = accepted.State with
        {
            TrustEpoch = 2,
            TrustedRootSha256 = new string('c', 64),
        };
        Assert.ThrowsException<InvalidDataException>(() =>
            AuthenticatedReleaseManifestPolicy.Evaluate(manifest, Receipt(), "0.1.0", LocalNow, newerRoot));
    }

    [TestMethod]
    public void WrongVerifiedProducerIdentityFailsClosed()
    {
        var manifest = Parse(Manifest());
        foreach (var receipt in new[]
                 {
                     Receipt() with { RepositoryId = "1" },
                     Receipt() with { Workflow = ".github/workflows/ci.yml" },
                     Receipt() with { VerificationMode = "online" },
                     Receipt() with { Runner = "self-hosted" },
                     Receipt() with { TrustEpoch = 2 },
                     Receipt() with { Checks = ["bundle-signature"] },
                 })
        {
            Assert.ThrowsException<InvalidDataException>(() =>
                AuthenticatedReleaseManifestPolicy.Evaluate(manifest, receipt, "0.1.0", LocalNow));
        }
    }

    [TestMethod]
    public void WithdrawalsAreAdditiveAndCannotSelectTheirOwnRelease()
    {
        var selfWithdrawn = Manifest();
        selfWithdrawn["withdrawals"]!.AsArray().Add(Withdrawal("release-sequence", "42"));
        Assert.ThrowsException<InvalidDataException>(() =>
            AuthenticatedReleaseManifestPolicy.Evaluate(Parse(selfWithdrawn), Receipt(), "0.1.0", LocalNow));

        var priorWithdrawal = new AuthenticatedReleaseWithdrawal(
            "manifest-sha256",
            new string('c', 64),
            IssuedAt.AddDays(-1),
            "security");
        var first = AuthenticatedReleaseManifestPolicy.Evaluate(Parse(Manifest()), Receipt(), "0.1.0", LocalNow);
        var previous = first.State with { Withdrawals = [priorWithdrawal] };
        var next = Parse(Manifest("0.3.0", 43));
        var nextReceipt = Receipt("v0.3.0");

        Assert.ThrowsException<InvalidDataException>(() =>
            AuthenticatedReleaseManifestPolicy.Evaluate(next, nextReceipt, "0.1.0", LocalNow, previous));

        var additive = Manifest("0.3.0", 43);
        additive["withdrawals"]!.AsArray().Add(Withdrawal("manifest-sha256", new string('c', 64), IssuedAt.AddDays(-1)));
        var accepted = AuthenticatedReleaseManifestPolicy.Evaluate(
            Parse(additive),
            nextReceipt,
            "0.1.0",
            LocalNow,
            previous);
        Assert.AreEqual(1, accepted.State.Withdrawals.Count);
    }

    [TestMethod]
    public void EveryWithdrawalSelectorCanDenyMatchingEvidence()
    {
        var artifacts = Parse(Manifest()).Artifacts;
        Assert.IsTrue(AuthenticatedReleaseManifestPolicy.IsWithdrawn(
            [new("release-sequence", "42", IssuedAt, "security")],
            42,
            ManifestDigest,
            artifacts));
        Assert.IsTrue(AuthenticatedReleaseManifestPolicy.IsWithdrawn(
            [new("manifest-sha256", ManifestDigest, IssuedAt, "security")],
            42,
            ManifestDigest,
            artifacts));
        Assert.IsTrue(AuthenticatedReleaseManifestPolicy.IsWithdrawn(
            [new("artifact-sha256", artifacts[0].Sha256, IssuedAt, "security")],
            42,
            ManifestDigest,
            artifacts));
    }

    [TestMethod]
    public void StateStoreAdvancesAtomicallyAndRefusesLowerFloors()
    {
        var root = Path.Combine(Path.GetTempPath(), $"stfc-auth-state-{Guid.NewGuid():N}");
        try
        {
            var store = new AuthenticatedReleaseStateStore(root);
            var first = AuthenticatedReleaseManifestPolicy.Evaluate(
                Parse(Manifest()),
                Receipt(),
                "0.1.0",
                LocalNow).State;
            store.Advance(first);
            AssertStateEquivalent(first, store.Load("stable"));

            var refreshed = first with { LastObservedUtc = first.LastObservedUtc.AddHours(1) };
            store.Advance(refreshed);
            AssertStateEquivalent(refreshed, store.Load("stable"));
            Assert.IsTrue(File.Exists(Path.Combine(root, "authenticated-release-state.v1.previous.json")));

            var lowered = refreshed with
            {
                HighestReleaseSequence = refreshed.HighestReleaseSequence - 1,
                LastObservedUtc = refreshed.LastObservedUtc.AddHours(1),
            };
            Assert.ThrowsException<InvalidDataException>(() => store.Advance(lowered));
            var nonAdvancingVersion = refreshed with
            {
                HighestReleaseSequence = refreshed.HighestReleaseSequence + 1,
                LastObservedUtc = refreshed.LastObservedUtc.AddHours(1),
            };
            Assert.ThrowsException<InvalidDataException>(() => store.Advance(nonAdvancingVersion));
            AssertStateEquivalent(refreshed, store.Load("stable"));

            File.Delete(Path.Combine(root, "authenticated-release-state.v1.json"));
            Assert.ThrowsException<InvalidDataException>(() => store.Load("stable"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void StateStoreRejectsUnknownAndDuplicatePersistedFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"stfc-auth-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "authenticated-release-state.v1.json");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"schemaVersion\":1,\"channels\":[]}");
            var store = new AuthenticatedReleaseStateStore(root);
            Assert.ThrowsException<InvalidDataException>(() => store.Load("stable"));

            File.WriteAllText(path, "{\"schemaVersion\":1,\"channels\":[],\"surprise\":true}");
            Assert.ThrowsException<InvalidDataException>(() => store.Load("stable"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void StateStoreTranslatesReadFailuresIntoFailClosedDataErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), $"stfc-auth-state-{Guid.NewGuid():N}");
        try
        {
            var store = new AuthenticatedReleaseStateStore(root);
            var state = AuthenticatedReleaseManifestPolicy.Evaluate(
                Parse(Manifest()),
                Receipt(),
                "0.1.0",
                LocalNow).State;
            store.Advance(state);
            var statePath = Path.Combine(root, "authenticated-release-state.v1.json");
            using var exclusive = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.None);

            var exception = Assert.ThrowsException<InvalidDataException>(() => store.Load("stable"));

            Assert.IsInstanceOfType<IOException>(exception.InnerException);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void StateStoreAllowsRootOverlapAdvanceButRejectsRootRebinding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"stfc-auth-state-{Guid.NewGuid():N}");
        try
        {
            var store = new AuthenticatedReleaseStateStore(root);
            var first = AuthenticatedReleaseManifestPolicy.Evaluate(
                Parse(Manifest()),
                Receipt(),
                "0.1.0",
                LocalNow).State;
            store.Advance(first);

            var rotated = first with
            {
                HighestReleaseSequence = 43,
                HighestReleaseVersion = "0.3.0",
                Tag = "v0.3.0",
                ManifestSha256 = new string('c', 64),
                BundleSha256 = new string('d', 64),
                TrustEpoch = 2,
                TrustedRootSha256 = new string('e', 64),
                FirstObservedUtc = first.LastObservedUtc.AddHours(1),
                LastObservedUtc = first.LastObservedUtc.AddHours(1),
            };
            store.Advance(rotated);
            AssertStateEquivalent(rotated, store.Load("stable"));

            var rebound = rotated with
            {
                TrustedRootSha256 = new string('f', 64),
                LastObservedUtc = rotated.LastObservedUtc.AddHours(1),
            };
            Assert.ThrowsException<InvalidDataException>(() => store.Advance(rebound));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static AuthenticatedWindowsReleaseManifest Parse(JsonObject json) => Parse(json.ToJsonString());

    private static void AssertStateEquivalent(
        AuthenticatedReleaseChannelState expected,
        AuthenticatedReleaseChannelState? actual)
    {
        Assert.IsNotNull(actual);
        var noWithdrawals = Array.Empty<AuthenticatedReleaseWithdrawal>();
        Assert.AreEqual(
            expected with { Withdrawals = noWithdrawals },
            actual with { Withdrawals = noWithdrawals });
        CollectionAssert.AreEqual(expected.Withdrawals.ToArray(), actual.Withdrawals.ToArray());
    }

    private static AuthenticatedWindowsReleaseManifest Parse(string json) =>
        AuthenticatedReleaseManifestParser.Parse(Encoding.UTF8.GetBytes(json));

    private static JsonObject Manifest(string releaseVersion = "0.2.0", long sequence = 42) => new()
    {
        ["schemaVersion"] = 2,
        ["releaseSequence"] = sequence,
        ["issuedAt"] = "2026-08-06T09:30:00Z",
        ["expiresAt"] = "2026-09-20T09:30:00Z",
        ["releaseVersion"] = releaseVersion,
        ["tag"] = $"v{releaseVersion}",
        ["channel"] = releaseVersion.Contains("-rc.", StringComparison.Ordinal) ? "preview" : "stable",
        ["releaseState"] = "active",
        ["minimumLauncherVersion"] = "0.1.0",
        ["source"] = new JsonObject
        {
            ["repository"] = "Guffawaffle/stfc-mod-bridge",
            ["targetCommit"] = Commit,
        },
        ["manifestAuthenticity"] = new JsonObject
        {
            ["scheme"] = AuthenticatedReleaseManifestPolicy.AuthenticityScheme,
        },
        ["artifacts"] = new JsonArray
        {
            Artifact(
                "windows-mod-bridge-archive-x64",
                "windows-mod-bridge",
                "stfc-mod-bridge-win-x64.zip",
                "application/zip",
                "contents",
                new JsonArray("STFCModBridge.exe", "STFCModBridge.Updater.exe")),
            Artifact(
                "windows-mod-bridge-msix-x64",
                "windows-mod-bridge-package",
                "STFCModBridge.msix",
                "application/msix",
                "artifact"),
        },
        ["withdrawals"] = new JsonArray(),
    };

    private static JsonObject Artifact(
        string id,
        string kind,
        string fileName,
        string mediaType,
        string scope,
        JsonArray? signedFiles = null)
    {
        var authenticity = new JsonObject
        {
            ["scheme"] = "authenticode",
            ["scope"] = scope,
        };
        if (signedFiles is not null)
        {
            authenticity["signedFiles"] = signedFiles;
        }
        return new()
        {
            ["id"] = id,
            ["kind"] = kind,
            ["platform"] = "windows",
            ["architecture"] = "x64",
            ["fileName"] = fileName,
            ["mediaType"] = mediaType,
            ["size"] = 123,
            ["sha256"] = id.StartsWith("windows-mod-bridge-archive", StringComparison.Ordinal)
                ? new string('d', 64)
                : new string('e', 64),
            ["authenticity"] = authenticity,
        };
    }

    private static JsonObject Withdrawal(
        string kind,
        string value,
        DateTimeOffset? withdrawnAt = null) => new()
        {
            ["kind"] = kind,
            ["value"] = value,
            ["withdrawnAt"] = (withdrawnAt ?? IssuedAt).ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture),
            ["reason"] = "security",
        };

    private static ReleaseSelectionVerificationReceipt Receipt(string tag = "v0.2.0") => new(
        1,
        true,
        "offline",
        "Guffawaffle/stfc-mod-bridge",
        "1320037274",
        "105761663",
        ".github/workflows/release.yml",
        $"refs/tags/{tag}",
        Commit,
        "push",
        "github-hosted",
        "https://in-toto.io/Statement/v1",
        "https://slsa.dev/provenance/v1",
        "https://actions.github.io/buildtypes/workflow/v1",
        "stfc-mod-bridge-release-manifest.json",
        ManifestDigest,
        BundleDigest,
        1,
        RootDigest,
        "https://token.actions.githubusercontent.com",
        $"https://github.com/Guffawaffle/stfc-mod-bridge/.github/workflows/release.yml@refs/tags/{tag}",
        [new("c0d23d6ad406973f9559f3ba2d1ca01f84147d8ffc5b8445c224f98b9591801d", 1, RekorTime)],
        ReleaseSelectionAttestationPolicy.RequiredChecks);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
