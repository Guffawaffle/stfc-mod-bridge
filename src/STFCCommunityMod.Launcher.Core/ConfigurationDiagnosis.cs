using System.Collections.ObjectModel;

namespace STFCCommunityMod.Launcher.Core;

public enum ConfigurationDiagnosisSeverity
{
    Informational,
    Attention,
    Error,
    Unknown,
}

public enum ConfigurationDiagnosisConfidence
{
    Established,
    Unsupported,
    Unknown,
}

public sealed record ConfigurationDiagnosisBinding(
    ConfigurationDocumentRevision Revision,
    string ProviderId,
    string ChannelId,
    string? CatalogId,
    string? CatalogVersion,
    DateTimeOffset EvidenceTimestampUtc,
    string EvidenceSource);

public sealed record ConfigurationDiagnosisFinding(
    string Code,
    ConfigurationDiagnosisSeverity Severity,
    ConfigurationDiagnosisConfidence Confidence,
    string Summary,
    string? CanonicalPath,
    int? LineNumber,
    LauncherConfigurationSensitivity Sensitivity,
    string? RemediationId = null,
    string? SourcePath = null);

public sealed class ConfigurationDiagnosisReport
{
    public ConfigurationDiagnosisReport(
        ConfigurationDiagnosisBinding binding,
        IEnumerable<ConfigurationDiagnosisFinding> findings)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ArgumentNullException.ThrowIfNull(findings);
        Findings = Array.AsReadOnly(findings.ToArray());
    }

    public ConfigurationDiagnosisBinding Binding { get; }

    public ReadOnlyCollection<ConfigurationDiagnosisFinding> Findings { get; }
}

/// <summary>
/// Provider-owned evidence required before a configuration document may be interpreted.
/// Unsupported providers cannot carry a catalog, making cross-provider scanning impossible
/// through the public API.
/// </summary>
public sealed class LauncherConfigurationDiagnosisEvidence
{
    private LauncherConfigurationDiagnosisEvidence(
        string providerId,
        string channelId,
        LauncherProviderCapabilityStatus capabilityStatus,
        LauncherConfigurationCatalog? catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ProviderId = providerId;
        ChannelId = channelId;
        CapabilityStatus = capabilityStatus;
        Catalog = catalog;
    }

    public string ProviderId { get; }

    public string ChannelId { get; }

    public LauncherProviderCapabilityStatus CapabilityStatus { get; }

    internal LauncherConfigurationCatalog? Catalog { get; }

    public static LauncherConfigurationDiagnosisEvidence Supported(
        string providerId,
        string channelId,
        LauncherConfigurationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!string.Equals(providerId, catalog.Source.StableId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Catalog provider '{catalog.Source.StableId}' does not match evidence provider '{providerId}'.",
                nameof(catalog));
        }

        return new(providerId, channelId, LauncherProviderCapabilityStatus.Supported, catalog);
    }

    public static LauncherConfigurationDiagnosisEvidence Unavailable(
        string providerId,
        string channelId,
        LauncherProviderCapabilityStatus capabilityStatus)
    {
        if (capabilityStatus == LauncherProviderCapabilityStatus.Supported)
        {
            throw new ArgumentException(
                "Supported provider evidence requires its provider-owned configuration catalog.",
                nameof(capabilityStatus));
        }

        return new(providerId, channelId, capabilityStatus, null);
    }
}

public sealed class ConfigurationHealthAnalyzer(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public ConfigurationDiagnosisReport Analyze(
        ConfigurationDocumentSnapshot snapshot,
        LauncherConfigurationDiagnosisEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(evidence);

        var catalog = evidence.Catalog;
        var binding = new ConfigurationDiagnosisBinding(
            snapshot.Revision,
            evidence.ProviderId,
            evidence.ChannelId,
            catalog?.Identity.CatalogId,
            catalog?.Identity.CatalogVersion.ToString(),
            timeProvider.GetUtcNow(),
            "selected-configuration-document");
        if (catalog is null)
        {
            var unsupported = evidence.CapabilityStatus == LauncherProviderCapabilityStatus.Unsupported;
            return new(
                binding,
                [
                    new(
                        unsupported
                            ? "CONFIG_PROVIDER_CATALOG_UNSUPPORTED"
                            : "CONFIG_PROVIDER_CATALOG_UNKNOWN",
                        ConfigurationDiagnosisSeverity.Unknown,
                        unsupported
                            ? ConfigurationDiagnosisConfidence.Unsupported
                            : ConfigurationDiagnosisConfidence.Unknown,
                        unsupported
                            ? "The selected provider does not support Mod Bridge configuration diagnosis."
                            : "Configuration diagnosis is unknown because the selected provider has no verified catalog.",
                        null,
                        null,
                        LauncherConfigurationSensitivity.Public),
                ]);
        }

        var contents = snapshot.Contents;
        var load = SparseTomlDocument.Load(contents, out var document);
        if (!load.IsValid || document is null)
        {
            return new(binding, [FromDocumentError(load.Error)]);
        }

        var read = document.ReadOverrides();
        if (!read.IsValid || read.Overrides is null)
        {
            return new(binding, [FromDocumentError(read.Error)]);
        }

        var findings = new List<ConfigurationDiagnosisFinding>();
        var recognizedAssignments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in catalog.Settings)
        {
            DiagnoseSetting(setting, read.Overrides, recognizedAssignments, findings);
        }

        AddUnknownContent(catalog, read, recognizedAssignments, findings);
        AddSyncTopologyFindings(contents, catalog, findings);
        if (findings.Count == 0)
        {
            findings.Add(
                new(
                    "CONFIG_HEALTHY",
                    ConfigurationDiagnosisSeverity.Informational,
                    ConfigurationDiagnosisConfidence.Established,
                    "The configuration is valid for the selected provider catalog.",
                    null,
                    null,
                    LauncherConfigurationSensitivity.Public));
        }

        return new(binding, findings);
    }

    private static void DiagnoseSetting(
        LauncherConfigurationSetting setting,
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        HashSet<string> recognizedAssignments,
        List<ConfigurationDiagnosisFinding> findings)
    {
        var canonical = overrides.Values
            .Where(item => MatchesPath(setting.Path, item.CanonicalPath))
            .ToArray();
        foreach (var configured in canonical)
        {
            recognizedAssignments.Add(configured.CanonicalPath);
            AddInvalidValueFinding(setting, configured, findings);
            AddRuntimeStatusFinding(setting, configured, findings);
        }

        var aliases = setting.Aliases
            .SelectMany(alias =>
                overrides.TryGetValue(alias.Path, out var configured)
                    ? [(Alias: alias, Configured: configured)]
                    : Array.Empty<(LauncherConfigurationAlias Alias, SparseTomlOverride Configured)>())
            .ToArray();
        foreach (var (_, configured) in aliases)
        {
            recognizedAssignments.Add(configured.CanonicalPath);
            AddInvalidValueFinding(setting, configured, findings);
            AddRuntimeStatusFinding(setting, configured, findings);
            var canMoveAlias = canonical.Length == 0
                && aliases.Length == 1
                && LauncherConfigurationEditSession.ValidateSettingValue(
                    setting,
                    configured.RenderedValue) is null;
            findings.Add(
                Finding(
                    "CONFIG_ALIAS_PRESENT",
                    ConfigurationDiagnosisSeverity.Attention,
                    "A compatibility or deprecated alias is present for a known setting.",
                    setting,
                    configured.LineNumber,
                    canMoveAlias
                        ? AliasRemediationId("move", configured.CanonicalPath, setting.Path)
                        : null,
                    configured.CanonicalPath));
        }

        if (canonical.Length == 1)
        {
            foreach (var (_, alias) in aliases)
            {
                var equivalent =
                    LauncherConfigurationEditSession.ValidateSettingValue(setting, canonical[0].RenderedValue) is null
                    && LauncherConfigurationEditSession.ValidateSettingValue(setting, alias.RenderedValue) is null
                    && LauncherConfigurationEditSession.AreEquivalentSettingValues(
                        setting,
                        canonical[0].RenderedValue,
                        alias.RenderedValue);
                findings.Add(
                    Finding(
                        equivalent
                            ? "CONFIG_CANONICAL_ALIAS_REDUNDANT"
                            : "CONFIG_CANONICAL_ALIAS_CONFLICT",
                        equivalent
                            ? ConfigurationDiagnosisSeverity.Attention
                            : ConfigurationDiagnosisSeverity.Error,
                        equivalent
                            ? "A canonical setting and its alias both assign the same effective value."
                            : "A canonical setting and its alias assign conflicting or invalid values.",
                        setting,
                        alias.LineNumber,
                        equivalent
                            ? AliasRemediationId("remove", alias.CanonicalPath, setting.Path)
                            : null,
                        alias.CanonicalPath));
            }
        }

        if (canonical.Length == 0 && aliases.Length > 1)
        {
            var first = aliases[0].Configured;
            var allEquivalent =
                LauncherConfigurationEditSession.ValidateSettingValue(setting, first.RenderedValue) is null
                && aliases.Skip(1).All(
                    alias =>
                        LauncherConfigurationEditSession.ValidateSettingValue(
                            setting,
                            alias.Configured.RenderedValue) is null
                        && LauncherConfigurationEditSession.AreEquivalentSettingValues(
                            setting,
                            first.RenderedValue,
                            alias.Configured.RenderedValue));
            findings.Add(
                Finding(
                    allEquivalent
                        ? "CONFIG_MULTIPLE_ALIASES_REDUNDANT"
                        : "CONFIG_MULTIPLE_ALIASES_CONFLICT",
                    allEquivalent
                        ? ConfigurationDiagnosisSeverity.Attention
                        : ConfigurationDiagnosisSeverity.Error,
                    allEquivalent
                        ? "Multiple aliases assign the same known setting."
                        : "Multiple aliases assign conflicting or invalid values to one known setting.",
                    setting,
                    aliases[1].Configured.LineNumber,
                    null));
        }
    }

    private static void AddInvalidValueFinding(
        LauncherConfigurationSetting setting,
        SparseTomlOverride configured,
        List<ConfigurationDiagnosisFinding> findings)
    {
        if (LauncherConfigurationEditSession.ValidateSettingValue(setting, configured.RenderedValue) is null)
        {
            return;
        }

        findings.Add(
            Finding(
                "CONFIG_VALUE_INVALID",
                ConfigurationDiagnosisSeverity.Attention,
                "A known setting has a value outside its declared type or constraints. "
                + "The mod ignores the invalid override.",
                setting,
                configured.LineNumber,
                "review-invalid-configuration-value"));
    }

    private static void AddRuntimeStatusFinding(
        LauncherConfigurationSetting setting,
        SparseTomlOverride configured,
        List<ConfigurationDiagnosisFinding> findings)
    {
        var (code, severity, summary) = setting.RuntimeStatus switch
        {
            LauncherConfigurationRuntimeStatus.Conditional => (
                "CONFIG_SETTING_CONDITIONAL",
                ConfigurationDiagnosisSeverity.Informational,
                "This known setting is effective only when its catalog-declared feature gates are active."),
            LauncherConfigurationRuntimeStatus.ParsedUnused => (
                "CONFIG_SETTING_PARSED_UNUSED",
                ConfigurationDiagnosisSeverity.Attention,
                "The selected provider parses this setting but does not currently use it at runtime."),
            LauncherConfigurationRuntimeStatus.Ignored => (
                "CONFIG_SETTING_IGNORED",
                ConfigurationDiagnosisSeverity.Attention,
                "The selected provider currently ignores this known setting."),
            LauncherConfigurationRuntimeStatus.Legacy => (
                "CONFIG_SETTING_LEGACY",
                ConfigurationDiagnosisSeverity.Attention,
                "This is a known legacy setting whose behavior is limited by the selected provider catalog."),
            LauncherConfigurationRuntimeStatus.Removed => (
                "CONFIG_SETTING_REMOVED",
                ConfigurationDiagnosisSeverity.Attention,
                "This known setting has been removed from the selected provider runtime and is preserved without cleanup."),
            _ => (null, ConfigurationDiagnosisSeverity.Informational, null),
        };
        if (code is null || summary is null)
        {
            return;
        }

        findings.Add(
            new(
                code,
                severity,
                ConfigurationDiagnosisConfidence.Established,
                summary,
                setting.Path,
                configured.LineNumber,
                setting.Sensitivity,
                SourcePath: configured.CanonicalPath));
    }

    private static void AddUnknownContent(
        LauncherConfigurationCatalog catalog,
        SparseTomlReadResult read,
        HashSet<string> recognizedAssignments,
        List<ConfigurationDiagnosisFinding> findings)
    {
        foreach (var configured in read.Overrides!.Values.Where(
                     item => !recognizedAssignments.Contains(item.CanonicalPath)))
        {
            findings.Add(
                new(
                    "CONFIG_UNKNOWN_KEY",
                    ConfigurationDiagnosisSeverity.Informational,
                    ConfigurationDiagnosisConfidence.Established,
                    "An unrecognized setting is preserved and is not a cleanup candidate.",
                    null,
                    configured.LineNumber,
                    LauncherConfigurationSensitivity.Private));
        }

        foreach (var table in read.Tables ?? [])
        {
            if (catalog.Settings.Any(
                    setting => TableCanContain(table.CanonicalPath, setting.Path)
                        || setting.Aliases.Any(alias => TableCanContain(table.CanonicalPath, alias.Path))))
            {
                continue;
            }

            findings.Add(
                new(
                    "CONFIG_UNKNOWN_TABLE",
                    ConfigurationDiagnosisSeverity.Informational,
                    ConfigurationDiagnosisConfidence.Established,
                    "An unrecognized table is preserved and is not a cleanup candidate.",
                    null,
                    table.LineNumber,
                    LauncherConfigurationSensitivity.Private));
        }
    }

    private static void AddSyncTopologyFindings(
        byte[] contents,
        LauncherConfigurationCatalog catalog,
        List<ConfigurationDiagnosisFinding> findings)
    {
        var load = SyncTopologyTomlAdapter.Load(contents);
        if (!load.IsValid || load.Topology is null)
        {
            return;
        }

        var diagnostics = load.Diagnostics.Concat(load.Topology.Resolve().Diagnostics);
        foreach (var diagnostic in diagnostics.Where(item => item.Code != "SYNC_TOML_VALUE_INVALID"))
        {
            var setting = ResolveSyncSetting(catalog, diagnostic.Field);
            findings.Add(
                new(
                    diagnostic.Code,
                    diagnostic.Severity switch
                    {
                        SyncTopologyDiagnosticSeverity.Info => ConfigurationDiagnosisSeverity.Informational,
                        SyncTopologyDiagnosticSeverity.Warning => ConfigurationDiagnosisSeverity.Attention,
                        SyncTopologyDiagnosticSeverity.Error => ConfigurationDiagnosisSeverity.Error,
                        _ => ConfigurationDiagnosisSeverity.Unknown,
                    },
                    ConfigurationDiagnosisConfidence.Established,
                    "The resolved Data Sync topology reported a configuration condition.",
                    setting?.Path,
                    null,
                    setting?.Sensitivity ?? LauncherConfigurationSensitivity.Public,
                    "review-data-sync"));
        }
    }

    private static LauncherConfigurationSetting? ResolveSyncSetting(
        LauncherConfigurationCatalog catalog,
        string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return null;
        }

        return catalog.Settings.FirstOrDefault(
            setting =>
                (setting.Path.StartsWith("sync.", StringComparison.Ordinal)
                    || setting.Path.StartsWith("sidecar.sync.", StringComparison.Ordinal))
                && string.Equals(
                    setting.Path[(setting.Path.LastIndexOf('.') + 1)..],
                    field,
                    StringComparison.Ordinal));
    }

    private static ConfigurationDiagnosisFinding FromDocumentError(SparseTomlError? error)
    {
        var duplicateTable = error?.Code == SparseTomlErrorCode.UnsupportedDocument
            && error.Message.StartsWith("Table '[", StringComparison.Ordinal)
            && error.Message.EndsWith("is declared more than once.", StringComparison.Ordinal);
        var code = error?.Code switch
        {
            SparseTomlErrorCode.InvalidUtf8 => "CONFIG_DOCUMENT_INVALID_UTF8",
            SparseTomlErrorCode.DuplicateTarget => "CONFIG_DOCUMENT_DUPLICATE_ASSIGNMENT",
            SparseTomlErrorCode.UnsupportedDocument when duplicateTable =>
                "CONFIG_DOCUMENT_DUPLICATE_TABLE",
            SparseTomlErrorCode.UnsupportedDocument => "CONFIG_DOCUMENT_SYNTAX_UNSUPPORTED",
            _ => "CONFIG_DOCUMENT_UNREADABLE",
        };
        var unsupported = error?.Code == SparseTomlErrorCode.UnsupportedDocument && !duplicateTable;
        return new(
            code,
            unsupported ? ConfigurationDiagnosisSeverity.Unknown : ConfigurationDiagnosisSeverity.Error,
            unsupported
                ? ConfigurationDiagnosisConfidence.Unsupported
                : ConfigurationDiagnosisConfidence.Established,
            unsupported
                ? "The conservative parser cannot establish configuration health for this TOML syntax."
                : "The configuration document cannot be diagnosed safely.",
            null,
            error?.LineNumber,
            LauncherConfigurationSensitivity.Private);
    }

    private static ConfigurationDiagnosisFinding Finding(
        string code,
        ConfigurationDiagnosisSeverity severity,
        string summary,
        LauncherConfigurationSetting setting,
        int? lineNumber,
        string? remediationId,
        string? sourcePath = null) =>
        new(
            code,
            severity,
            ConfigurationDiagnosisConfidence.Established,
            summary,
            setting.Path,
            lineNumber,
            setting.Sensitivity,
            remediationId,
            sourcePath);

    private static string AliasRemediationId(
        string operation,
        string sourcePath,
        string canonicalPath) =>
        $"configuration.alias.{operation}:{sourcePath}->{canonicalPath}";

    private static bool MatchesPath(string pattern, string path)
    {
        var patternSegments = pattern.Split('.');
        var pathSegments = path.Split('.');
        return patternSegments.Length == pathSegments.Length
            && patternSegments.Zip(
                    pathSegments,
                    (expected, actual) => expected == "*" || expected == actual)
                .All(matches => matches);
    }

    private static bool TableCanContain(string tablePath, string settingPath)
    {
        var tableSegments = tablePath.Split('.');
        var settingSegments = settingPath.Split('.');
        if (tableSegments.Length >= settingSegments.Length)
        {
            return false;
        }

        return tableSegments.Zip(
                settingSegments,
                (actual, expected) => expected == "*" || expected == actual)
            .All(matches => matches);
    }
}
