using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class SyncTargetContractCorpusTests
{
    private static readonly string[] RequiredCaseIds =
    [
        "canonical-local-sidecar",
        "explicit-empty-proxy",
        "explicit-false-overrides-global",
        "global-only-defaults",
        "invalid-loopback-external-target",
        "legacy-root-conversion",
        "legacy-spocks-presets",
        "majel-realtime",
        "missing-partial-credentials",
        "mixed-multiple-targets",
        "unknown-source-content-preserved",
        "unsupported-external-fleet-runtime",
    ];

    private static readonly string[] LockedProvenanceStates =
    [
        "inherited",
        "explicit_value",
        "explicit_false",
        "explicit_empty",
    ];

    private static readonly string[] LocalSidecarKindOnly = ["local_sidecar"];

    [TestMethod]
    public void CorpusCoversLockedCompatibilityCasesAndPreservesEveryFixtureByte()
    {
        using var corpus = LoadJson("docs", "windows-launcher", "sync-target-corpus", "cases.json");
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();

        CollectionAssert.AreEquivalent(
            RequiredCaseIds,
            cases.Select(item => item.GetProperty("id").GetString()).ToArray());

        foreach (var corpusCase in cases)
        {
            var id = corpusCase.GetProperty("id").GetString();
            var fixturePath = FindRepositoryFile(
                "docs",
                "windows-launcher",
                "sync-target-corpus",
                corpusCase.GetProperty("file").GetString()!);
            var contents = File.ReadAllBytes(fixturePath);

            var load = SparseTomlDocument.Load(contents, out var document);
            Assert.IsTrue(load.IsValid, $"{id}: {load.Error?.Message}");
            Assert.IsNotNull(document, id);

            var validation = document.ValidateForMutation();
            Assert.IsTrue(validation.IsValid, $"{id}: {validation.Error?.Message}");
            Assert.IsFalse(validation.Changed, id);
            CollectionAssert.AreEqual(contents, validation.Contents, id);

            var read = document.ReadOverrides();
            Assert.IsTrue(read.IsValid, $"{id}: {read.Error?.Message}");
            CollectionAssert.AreEquivalent(
                corpusCase.GetProperty("expectedPaths")
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .ToArray(),
                read.Overrides!.Keys.ToArray(),
                id);

            foreach (var token in read.Overrides.Values.Where(
                         value => value.CanonicalPath.EndsWith(".token", StringComparison.Ordinal)))
            {
                StringAssert.Contains(token.RenderedValue, "fixture-redacted-", id);
            }
        }
    }

    [TestMethod]
    public void ContractLocksInheritanceSecurityAndSidecarBoundaries()
    {
        using var contract = LoadJson(
            "docs",
            "windows-launcher",
            "sync-target-contract.guffawaffle.v1.json");
        var root = contract.RootElement;

        CollectionAssert.AreEquivalent(
            LockedProvenanceStates,
            root.GetProperty("provenanceStates").EnumerateArray().Select(item => item.GetString()).ToArray());

        var kinds = root.GetProperty("targetKinds");
        Assert.IsFalse(kinds.GetProperty("local_sidecar").GetProperty("inheritsGlobalSync").GetBoolean());
        Assert.IsTrue(kinds.GetProperty("legacy_community").GetProperty("inheritsGlobalSync").GetBoolean());
        Assert.IsTrue(kinds.GetProperty("majel_ingest").GetProperty("inheritsGlobalSync").GetBoolean());

        var fields = root.GetProperty("fields");
        foreach (var field in new[] { "identity", "enabled", "url", "token", "mode" })
        {
            Assert.AreEqual("never", fields.GetProperty(field).GetProperty("inheritance").GetString(), field);
        }

        Assert.AreEqual("external_only", fields.GetProperty("proxy").GetProperty("inheritance").GetString());
        Assert.IsTrue(fields.GetProperty("proxy").GetProperty("allowsExplicitEmpty").GetBoolean());
        Assert.IsTrue(fields.GetProperty("token").GetProperty("secret").GetBoolean());

        var fleetRuntime = fields.GetProperty("fleet_runtime");
        Assert.IsTrue(fleetRuntime.GetProperty("sidecarOnly").GetBoolean());
        CollectionAssert.AreEqual(
            LocalSidecarKindOnly,
            fleetRuntime.GetProperty("kinds").EnumerateArray().Select(item => item.GetString()).ToArray());

        var mutation = root.GetProperty("mutationPolicy");
        Assert.AreEqual("never", mutation.GetProperty("load").GetString());
        Assert.AreEqual("never", mutation.GetProperty("resolve").GetString());
        Assert.IsTrue(mutation.GetProperty("preserveUnknownKeys").GetBoolean());
        Assert.IsTrue(mutation.GetProperty("preserveComments").GetBoolean());
        Assert.IsTrue(mutation.GetProperty("preserveOrdering").GetBoolean());

        var security = root.GetProperty("security");
        Assert.IsFalse(security.GetProperty("sidecarInheritsExternalNetworkPolicy").GetBoolean());
        Assert.IsTrue(security.GetProperty("unsafeTlsRequiresExplicitPair").GetBoolean());
        CollectionAssert.Contains(
            security.GetProperty("redactFields").EnumerateArray().Select(item => item.GetString()).ToArray(),
            "token");
    }

    [TestMethod]
    public void CorpusUsesOnlyContractVocabulary()
    {
        using var contract = LoadJson(
            "docs",
            "windows-launcher",
            "sync-target-contract.guffawaffle.v1.json");
        using var corpus = LoadJson("docs", "windows-launcher", "sync-target-corpus", "cases.json");

        var targetKinds = contract.RootElement.GetProperty("targetKinds")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var provenanceStates = contract.RootElement.GetProperty("provenanceStates")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var diagnostics = contract.RootElement.GetProperty("diagnostics")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var corpusCase in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = corpusCase.GetProperty("id").GetString();
            foreach (var targetKind in corpusCase.GetProperty("expectedTargetKinds").EnumerateArray())
            {
                Assert.IsTrue(targetKinds.Contains(targetKind.GetString()!), $"{id}: unknown target kind");
            }

            foreach (var provenance in corpusCase.GetProperty("provenance").EnumerateObject())
            {
                Assert.IsTrue(
                    provenanceStates.Contains(provenance.Value.GetString()!),
                    $"{id}: unknown provenance state for {provenance.Name}");
            }

            foreach (var diagnostic in corpusCase.GetProperty("diagnostics").EnumerateArray())
            {
                Assert.IsTrue(diagnostics.Contains(diagnostic.GetString()!), $"{id}: unknown diagnostic");
            }
        }
    }

    private static JsonDocument LoadJson(params string[] relativeParts) =>
        JsonDocument.Parse(File.ReadAllBytes(FindRepositoryFile(relativeParts)));

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find repository file '{Path.Combine(relativeParts)}'.");
        return string.Empty;
    }
}
