using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleBridgeActivationCorpusTests
{
    private const string FixtureName =
        "battle-bridge-activation-cases.v1.json";

    private const string InventoryPinSchema =
        "stfc.battle-bridge.capability-evidence-pin.v1";

    [TestMethod]
    public void ProvisionalCasesUseRealManifestDetectionAndFeatureResolution()
    {
        using var fixture = LoadFixture();
        var root = fixture.RootElement;
        Assert.AreEqual(
            "stfc.mod-bridge.battle-activation-cases.v1",
            root.GetProperty("fixtureSchema").GetString());
        Assert.IsTrue(root.GetProperty("provisional").GetBoolean());

        var capabilityInventory = root.GetProperty("provisionalCapabilityIds")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToFrozenSet(StringComparer.Ordinal);
        ValidateCapabilityInventoryPin(
            root.GetProperty("capabilityInventoryPin"),
            capabilityInventory);
        var definitions = ReadDefinitions(
            root.GetProperty("featureDefinitions"),
            capabilityInventory);
        var results = new Dictionary<string, CaseResult>(StringComparer.Ordinal);

        foreach (var scenario in root.GetProperty("cases").EnumerateArray())
        {
            var id = scenario.GetProperty("id").GetString()!;
            var evidenceClass = scenario.GetProperty("evidenceClass").GetString()!;
            var manifest = scenario.GetProperty("manifest");
            ValidateEvidenceClass(id, evidenceClass, manifest, capabilityInventory);

            using var stream = manifest.ValueKind == JsonValueKind.Null
                ? null
                : JsonStream(manifest.GetRawText());
            var profile = LauncherRuntimeManifestDetector.Detect(
                stream,
                $"golden corpus: {id}");
            var plan = LauncherFeatureResolver.Resolve(profile, definitions);
            var expectedActive = scenario.GetProperty("expectedActive")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToFrozenSet(StringComparer.Ordinal);
            var actualActive = plan.Features.Values
                .Where(decision => decision.IsActive)
                .Select(decision => decision.Id)
                .ToFrozenSet(StringComparer.Ordinal);

            Assert.AreEqual(
                scenario.GetProperty("expectedDistribution").GetString(),
                profile.DistributionId,
                id);
            CollectionAssert.AreEquivalent(
                expectedActive.ToArray(),
                actualActive.ToArray(),
                id);
            foreach (var definition in definitions)
            {
                var decision = plan.GetDecision(definition.Id);
                Assert.AreEqual(
                    expectedActive.Contains(definition.Id)
                        ? definition.ActiveImplementation
                        : definition.FallbackImplementation,
                    decision.SelectedImplementation,
                    $"{id}/{definition.Id}");
            }

            var history = scenario.GetProperty("retainedHistory");
            results.Add(
                id,
                new(
                    actualActive,
                    history.GetProperty("battleReadable").GetBoolean(),
                    history.GetProperty("fleetReadable").GetBoolean()));
        }

        AssertCapabilityLossDoesNotEraseRetainedHistory(
            root.GetProperty("capabilityLossTransition"),
            results);
    }

    [TestMethod]
    public void FixtureParserRejectsEscapedEquivalentDuplicateProperties()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """
            { "capabilities": [], "capabilit\u0069es": ["shadow"] }
            """);

        var exception = Assert.ThrowsException<InvalidDataException>(
            () => ParseFixture(bytes));

        StringAssert.Contains(exception.Message, "duplicate property 'capabilities'");
    }

    private static void ValidateCapabilityInventoryPin(
        JsonElement pin,
        IReadOnlySet<string> provisionalCapabilities)
    {
        Assert.AreEqual(
            InventoryPinSchema,
            pin.GetProperty("schema").GetString());
        var entries = pin.GetProperty("entries")
            .EnumerateArray()
            .Select(element => new PinEntry(
                element.GetProperty("id").GetString()!,
                element.GetProperty("schema").GetString()!))
            .ToArray();
        var ordered = entries
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(ordered, entries, "Capability pin entries must use ordinal ID order.");
        Assert.AreEqual(entries.Length, entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(provisionalCapabilities.All(id => entries.Any(entry => entry.Id == id)));

        var canonical = "[" + string.Join(
            ",",
            entries.Select(entry =>
                $"{{\"id\":{JsonSerializer.Serialize(entry.Id)},\"schema\":{JsonSerializer.Serialize(entry.Schema)}}}")) + "]";
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        Assert.AreEqual(pin.GetProperty("sha256").GetString(), digest);
    }

    private static List<LauncherFeatureDefinition> ReadDefinitions(
        JsonElement elements,
        IReadOnlySet<string> capabilityInventory)
    {
        var definitions = new List<LauncherFeatureDefinition>();
        foreach (var element in elements.EnumerateArray())
        {
            var requirements = element.GetProperty("requiredCapabilities")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToFrozenSet(StringComparer.Ordinal);
            Assert.IsTrue(
                requirements.All(capabilityInventory.Contains),
                "Every test-only feature requirement must exist in the provisional inventory.");
            definitions.Add(
                new(
                    element.GetProperty("id").GetString()!,
                    LauncherFeatureKind.CompatibilityGate,
                    LauncherFeatureActivationMode.StartupLatched,
                    requirements,
                    Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal),
                    LauncherFeatureDefault.EnabledWhenEligible,
                    element.GetProperty("activeImplementation").GetString()!,
                    element.GetProperty("fallbackImplementation").GetString()!));
        }

        return definitions;
    }

    private static void ValidateEvidenceClass(
        string id,
        string evidenceClass,
        JsonElement manifest,
        IReadOnlySet<string> provisionalCapabilities)
    {
        var advertised = manifest.ValueKind == JsonValueKind.Null
            ? []
            : manifest.GetProperty("capabilities")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .Where(provisionalCapabilities.Contains)
                .ToArray();
        if (!evidenceClass.StartsWith("hypothetical-", StringComparison.Ordinal))
        {
            Assert.AreEqual(0, advertised.Length, $"Current case '{id}' may not advertise provisional Battle capabilities.");
        }

        if (id == "current-netniv-runtime-unavailable")
        {
            Assert.AreEqual(JsonValueKind.Null, manifest.ValueKind, "Current NetniV evidence must remain unavailable/unknown.");
        }
    }

    private static void AssertCapabilityLossDoesNotEraseRetainedHistory(
        JsonElement transition,
        IReadOnlyDictionary<string, CaseResult> results)
    {
        var before = results[transition.GetProperty("before").GetString()!];
        var after = results[transition.GetProperty("after").GetString()!];
        Assert.IsTrue(before.ActiveFeatures.Count > 0);
        Assert.AreEqual(0, after.ActiveFeatures.Count);
        Assert.IsTrue(before.BattleReadable && after.BattleReadable);
        Assert.IsTrue(before.FleetReadable && after.FleetReadable);
    }

    private static JsonDocument LoadFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "BattleBridge",
            FixtureName);
        return ParseFixture(File.ReadAllBytes(path));
    }

    private static JsonDocument ParseFixture(byte[] json)
    {
        RejectDuplicateProperties(json);
        return JsonDocument.Parse(json);
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(
            json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.PropertyName:
                    var propertyName = reader.GetString()!;
                    if (!objectProperties.Peek().Add(propertyName))
                    {
                        throw new InvalidDataException(
                            $"Fixture JSON contains duplicate property '{propertyName}'.");
                    }

                    break;
                case JsonTokenType.EndObject:
                    objectProperties.Pop();
                    break;
            }
        }
    }

    private static MemoryStream JsonStream(string json) =>
        new(Encoding.UTF8.GetBytes(json));

    private sealed record CaseResult(
        IReadOnlySet<string> ActiveFeatures,
        bool BattleReadable,
        bool FleetReadable);

    private sealed record PinEntry(string Id, string Schema);
}
