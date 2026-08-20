using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ConfigurationDiagnosisTests
{
    private static readonly DateTimeOffset EvidenceTime =
        new(2026, 8, 2, 14, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void CatalogRetainsAliasProvenanceSensitivityAndStableIdentity()
    {
        var catalog = LoadCatalog();
        var setting = catalog.Settings.Single(
            item => item.Path == "input.bindings.hotkeys_disable");

        Assert.AreEqual(setting.Path, setting.StableId);
        Assert.AreEqual(LauncherConfigurationSensitivity.Public, setting.Sensitivity);
        Assert.AreEqual("input.bindings.hotkeys_disable", setting.Provenance.RuntimePath);
        Assert.AreEqual("input-action-registry", setting.Provenance.DefaultSource);
        Assert.AreEqual(3, setting.Aliases.Count);
        Assert.AreEqual("shortcuts.set_hotkeys_disable", setting.Aliases[0].Path);
        Assert.AreEqual(LauncherConfigurationAliasStatus.Compatibility, setting.Aliases[0].Status);
        Assert.AreEqual(
            LauncherConfigurationAliasPrecedence.CanonicalWins,
            setting.Aliases[0].Precedence);
    }

    [TestMethod]
    public void CatalogRejectsCanonicalAliasAndAliasOwnershipCollisions()
    {
        var root = JsonNode.Parse(File.ReadAllText(CatalogPath()))!.AsObject();
        var settings = root["settings"]!.AsArray();
        var first = settings[0]!.AsObject();
        var secondPath = settings[1]!["path"]!.GetValue<string>();
        first["aliases"] = new JsonArray(
            new JsonObject
            {
                ["path"] = secondPath,
                ["status"] = "deprecated",
                ["precedence"] = "canonical-wins",
            });

        using var canonicalCollision = JsonStream(root.ToJsonString());
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LauncherConfigurationSchemaLoader.Load(canonicalCollision));

        root = JsonNode.Parse(File.ReadAllText(CatalogPath()))!.AsObject();
        settings = root["settings"]!.AsArray();
        first = settings[0]!.AsObject();
        var second = settings[1]!.AsObject();
        const string sharedAlias = "compat.shared_alias";
        first["aliases"] = AliasArray(sharedAlias);
        second["aliases"] = AliasArray(sharedAlias);
        using var aliasCollision = JsonStream(root.ToJsonString());
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LauncherConfigurationSchemaLoader.Load(aliasCollision));

        root = JsonNode.Parse(File.ReadAllText(CatalogPath()))!.AsObject();
        settings = root["settings"]!.AsArray();
        first = settings[0]!.AsObject();
        second = settings[1]!.AsObject();
        first["aliases"] = AliasArray("ambiguous.*.value");
        second["path"] = "ambiguous.concrete.value";
        using var wildcardCollision = JsonStream(root.ToJsonString());
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LauncherConfigurationSchemaLoader.Load(wildcardCollision));
    }

    [TestMethod]
    public void ReportIsRevisionBoundAndByteIdenticalForConservativeCorpus()
    {
        const string text =
            "\uFEFF# café remains byte-identical\r\n"
            + "[graphics]\r\n"
            + "default_system_zoom = 1750 # ordering stays\r\n"
            + "\r\n"
            + "[future_extension]\r\n"
            + "private_endpoint = \"https://private.invalid/secret\"\r\n";
        var contents = Encoding.UTF8.GetBytes(text);
        var original = contents.ToArray();
        var snapshot = Snapshot(contents);

        var report = Analyzer().Analyze(snapshot, SupportedEvidence());

        CollectionAssert.AreEqual(original, contents);
        CollectionAssert.AreEqual(original, snapshot.Contents);
        Assert.AreEqual(snapshot.Revision, report.Binding.Revision);
        Assert.AreEqual("guffawaffle", report.Binding.ProviderId);
        Assert.AreEqual("stable", report.Binding.ChannelId);
        Assert.AreEqual("guffawaffle.configuration", report.Binding.CatalogId);
        Assert.AreEqual("1.0.0", report.Binding.CatalogVersion);
        Assert.AreEqual(EvidenceTime, report.Binding.EvidenceTimestampUtc);
        CollectionAssert.Contains(report.Findings.Select(item => item.Code).ToArray(), "CONFIG_UNKNOWN_KEY");
        CollectionAssert.Contains(report.Findings.Select(item => item.Code).ToArray(), "CONFIG_UNKNOWN_TABLE");
        Assert.IsTrue(report.Findings.Where(item => item.Code.StartsWith("CONFIG_UNKNOWN", StringComparison.Ordinal))
            .All(item => item.CanonicalPath is null && item.Sensitivity == LauncherConfigurationSensitivity.Private));
    }

    [TestMethod]
    public void AliasCorpusDistinguishesPresenceRedundancyAndConflicts()
    {
        var aliasOnly = Diagnose("shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        HasCode(aliasOnly, "CONFIG_ALIAS_PRESENT");
        LacksCode(aliasOnly, "CONFIG_CANONICAL_ALIAS_CONFLICT");

        var redundant = Diagnose(
            "input.bindings.hotkeys_disable = \"CTRL-ALT-MINUS\"\n"
            + "shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        HasCode(redundant, "CONFIG_CANONICAL_ALIAS_REDUNDANT");

        var conflict = Diagnose(
            "input.bindings.hotkeys_disable = \"CTRL-ALT-MINUS\"\n"
            + "shortcuts.set_hotkeys_disable = \"CTRL-ALT-PLUS\"\n");
        HasCode(conflict, "CONFIG_CANONICAL_ALIAS_CONFLICT");

        var multiple = Diagnose(
            "shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\"\n"
            + "shortcuts.set_hotkeys_disble = \"CTRL-ALT-PLUS\"\n");
        HasCode(multiple, "CONFIG_MULTIPLE_ALIASES_CONFLICT");

        var invalid = Diagnose("shortcuts.set_hotkeys_disable = \"???\"\n");
        HasCode(invalid, "CONFIG_VALUE_INVALID");
        HasCode(invalid, "CONFIG_ALIAS_PRESENT");
    }

    [TestMethod]
    public void KnownInvalidRangesEnumsKeybindingsAndPoliciesUseStableCode()
    {
        var report = Diagnose(
            "[graphics]\n"
            + "default_system_zoom = 5001\n"
            + "[advanced.diagnostics]\n"
            + "runtime_trace = \"impossible\"\n"
            + "[input.bindings]\n"
            + "hotkeys_disable = \"???\"\n"
            + "[notifications]\n"
            + "fleet_arrived_in_system = { system = true, audio = true, sound = \"private-tone\" }\n");

        Assert.AreEqual(4, report.Findings.Count(item => item.Code == "CONFIG_VALUE_INVALID"));
        Assert.IsTrue(report.Findings.Where(item => item.Code == "CONFIG_VALUE_INVALID")
            .All(item => item.Severity == ConfigurationDiagnosisSeverity.Attention
                && item.Confidence == ConfigurationDiagnosisConfidence.Established
                && item.RemediationId == "review-invalid-configuration-value"));
    }

    [DataTestMethod]
    [DataRow("value = true\nvalue = false\n", "CONFIG_DOCUMENT_DUPLICATE_ASSIGNMENT", false)]
    [DataRow("[same]\nvalue = true\n[same]\nother = false\n", "CONFIG_DOCUMENT_DUPLICATE_TABLE", false)]
    [DataRow("[[unsupported]]\nvalue = true\n", "CONFIG_DOCUMENT_SYNTAX_UNSUPPORTED", true)]
    [DataRow("quoted.\"key\" = true\n", "CONFIG_DOCUMENT_SYNTAX_UNSUPPORTED", true)]
    public void InvalidAndUnsupportedSyntaxCorpusFailsClosed(
        string text,
        string expectedCode,
        bool expectedUnknown)
    {
        var report = Diagnose(text);

        Assert.AreEqual(1, report.Findings.Count);
        Assert.AreEqual(expectedCode, report.Findings[0].Code);
        Assert.AreEqual(
            expectedUnknown
                ? ConfigurationDiagnosisSeverity.Unknown
                : ConfigurationDiagnosisSeverity.Error,
            report.Findings[0].Severity);
        Assert.AreEqual(
            expectedUnknown
                ? ConfigurationDiagnosisConfidence.Unsupported
                : ConfigurationDiagnosisConfidence.Established,
            report.Findings[0].Confidence);
    }

    [TestMethod]
    public void InvalidUtf8IsEstablishedWithoutEchoingDecoderOrSourceData()
    {
        var report = Analyzer().Analyze(
            Snapshot([0xff, 0xfe, 0xfd]),
            SupportedEvidence());

        Assert.AreEqual("CONFIG_DOCUMENT_INVALID_UTF8", report.Findings.Single().Code);
        Assert.IsFalse(report.Findings.Single().Summary.Contains("0xff", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void EmptySupportedConfigurationReportsEstablishedHealthyState()
    {
        var report = Diagnose("# No overrides are required.\n");

        Assert.AreEqual(1, report.Findings.Count);
        Assert.AreEqual("CONFIG_HEALTHY", report.Findings[0].Code);
        Assert.AreEqual(ConfigurationDiagnosisSeverity.Informational, report.Findings[0].Severity);
        Assert.AreEqual(ConfigurationDiagnosisConfidence.Established, report.Findings[0].Confidence);
    }

    [TestMethod]
    public void UnsupportedProviderIsExplicitUnknownAndNeverScansWithAnotherCatalog()
    {
        var source = Encoding.UTF8.GetBytes(
            "token = \"vip-secret\"\nthis is not supported TOML\n");
        var report = Analyzer().Analyze(
            Snapshot(source),
            LauncherConfigurationDiagnosisEvidence.Unavailable(
                "netniv",
                "main",
                LauncherProviderCapabilityStatus.Unknown));

        Assert.AreEqual(1, report.Findings.Count);
        Assert.AreEqual("CONFIG_PROVIDER_CATALOG_UNKNOWN", report.Findings[0].Code);
        Assert.AreEqual(ConfigurationDiagnosisSeverity.Unknown, report.Findings[0].Severity);
        Assert.AreEqual(ConfigurationDiagnosisConfidence.Unknown, report.Findings[0].Confidence);
        Assert.IsNull(report.Binding.CatalogId);
        Assert.IsNull(report.Binding.CatalogVersion);
        Assert.ThrowsException<ArgumentException>(
            () => LauncherConfigurationDiagnosisEvidence.Supported(
                "netniv",
                "main",
                LoadCatalog()));

        var unsupported = Analyzer().Analyze(
            Snapshot(source),
            LauncherConfigurationDiagnosisEvidence.Unavailable(
                "netniv",
                "main",
                LauncherProviderCapabilityStatus.Unsupported));
        Assert.AreEqual("CONFIG_PROVIDER_CATALOG_UNSUPPORTED", unsupported.Findings.Single().Code);
        Assert.AreEqual(ConfigurationDiagnosisSeverity.Unknown, unsupported.Findings.Single().Severity);
        Assert.AreEqual(ConfigurationDiagnosisConfidence.Unsupported, unsupported.Findings.Single().Confidence);
    }

    [TestMethod]
    public void PublicReportNeverContainsValuesEndpointsOrFilesystemPaths()
    {
        const string configurationPath = @"C:\Users\VeryPrivate\game\community_patch_settings.toml";
        const string secret = "vip-token-should-never-escape";
        const string endpoint = "https://internal.example.invalid/private";
        const string filePath = @"C:\Users\VeryPrivate\captures";
        var contents = Encoding.UTF8.GetBytes(
            "[advanced.diagnostics.files]\n"
            + $"root = {LauncherTomlValue.RenderString(filePath)}\n"
            + "[sidecar.sync]\n"
            + $"url = {LauncherTomlValue.RenderString(endpoint)}\n"
            + $"token = {LauncherTomlValue.RenderString(secret)}\n");
        var report = Analyzer().Analyze(
            new ConfigurationDocumentSnapshot(configurationPath, contents),
            SupportedEvidence());
        var serialized = JsonSerializer.Serialize(report);

        Assert.IsFalse(serialized.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(endpoint, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(filePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains(configurationPath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains("VeryPrivate", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ExistingSyncTopologyDiagnosticsProjectWithoutTargetNamesOrValues()
    {
        const string targetName = "private-destination-name";
        const string endpoint = "https://internal.example.invalid/sync";
        var report = Diagnose(
            $"[sync.targets.{targetName}]\n"
            + $"url = {LauncherTomlValue.RenderString(endpoint)}\n"
            + "token = \"configured\"\n"
            + "verify_ssl = false\n"
            + "allow_unsafe_tls_without_certificate_validation = false\n");
        var serialized = JsonSerializer.Serialize(report);

        HasCode(report, "SYNC_UNSAFE_TLS_PAIR_REQUIRED");
        Assert.IsFalse(serialized.Contains(targetName, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(endpoint, StringComparison.Ordinal));
    }

    [TestMethod]
    public void CatalogRuntimeStatusProducesActionableValueFreeWarning()
    {
        var root = JsonNode.Parse(File.ReadAllText(CatalogPath()))!.AsObject();
        var setting = root["settings"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(node => node["path"]!.GetValue<string>() == "graphics.default_system_zoom");
        setting["runtimeStatus"] = "parsed-unused";
        setting["featureGates"] = new JsonArray("patches.zoomhooks=true");
        using var stream = JsonStream(root.ToJsonString());
        var catalog = LauncherConfigurationSchemaLoader.Load(stream);

        var report = Analyzer().Analyze(
            Snapshot(Encoding.UTF8.GetBytes("[graphics]\ndefault_system_zoom = 4321\n")),
            LauncherConfigurationDiagnosisEvidence.Supported("guffawaffle", "stable", catalog));
        var finding = report.Findings.Single(item => item.Code == "CONFIG_SETTING_PARSED_UNUSED");

        Assert.AreEqual(ConfigurationDiagnosisSeverity.Attention, finding.Severity);
        Assert.AreEqual("graphics.default_system_zoom", finding.CanonicalPath);
        Assert.AreEqual("graphics.default_system_zoom", finding.SourcePath);
        Assert.IsNull(finding.RemediationId);
        Assert.IsFalse(JsonSerializer.Serialize(finding).Contains("4321", StringComparison.Ordinal));
    }

    private static ConfigurationDiagnosisReport Diagnose(string text) =>
        Analyzer().Analyze(Snapshot(Encoding.UTF8.GetBytes(text)), SupportedEvidence());

    private static ConfigurationHealthAnalyzer Analyzer() =>
        new(new FixedTimeProvider(EvidenceTime));

    private static LauncherConfigurationDiagnosisEvidence SupportedEvidence() =>
        LauncherConfigurationDiagnosisEvidence.Supported("guffawaffle", "stable", LoadCatalog());

    private static LauncherConfigurationCatalog LoadCatalog() =>
        LauncherConfigurationSchemaLoader.LoadFile(CatalogPath());

    private static string CatalogPath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Configuration",
            "config-schema.guffawaffle.v1.json");

    private static ConfigurationDocumentSnapshot Snapshot(byte[] contents) =>
        new(Path.Combine(Path.GetTempPath(), "configuration-diagnosis.toml"), contents);

    private static JsonArray AliasArray(string path) =>
        new(
            new JsonObject
            {
                ["path"] = path,
                ["status"] = "deprecated",
                ["precedence"] = "canonical-wins",
            });

    private static MemoryStream JsonStream(string json) =>
        new(Encoding.UTF8.GetBytes(json));

    private static void HasCode(ConfigurationDiagnosisReport report, string code) =>
        Assert.IsTrue(
            report.Findings.Any(item => item.Code == code),
            $"Expected finding code '{code}'. Actual: {string.Join(", ", report.Findings.Select(item => item.Code))}");

    private static void LacksCode(ConfigurationDiagnosisReport report, string code) =>
        Assert.IsFalse(report.Findings.Any(item => item.Code == code));

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
