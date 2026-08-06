using System.Collections.ObjectModel;

namespace STFCCommunityMod.Launcher.Core;

public enum ConfigurationMigrationPlanState
{
    Ready,
    NoChange,
    Rejected,
}

public enum ConfigurationMigrationPlanRejectionCode
{
    StaleDiagnosis,
    BindingMismatch,
    CatalogUnavailable,
    BlockingDiagnosis,
    InvalidSelection,
    IneligibleSelection,
    MutationFailed,
    RescanFailed,
}

public enum ConfigurationMigrationOperationKind
{
    MoveAlias,
    RemoveRedundantAlias,
}

public enum ConfigurationMigrationPreviewLineKind
{
    Removed,
    Added,
}

public sealed class ConfigurationMigrationPlanBinding
{
    public ConfigurationMigrationPlanBinding(
        ConfigurationDocumentRevision revision,
        string providerId,
        string channelId,
        string catalogId,
        string catalogVersion,
        IEnumerable<string> remediationIds)
    {
        Revision = revision ?? throw new ArgumentNullException(nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogVersion);
        ArgumentNullException.ThrowIfNull(remediationIds);

        ProviderId = providerId;
        ChannelId = channelId;
        CatalogId = catalogId;
        CatalogVersion = catalogVersion;
        RemediationIds = Array.AsReadOnly(remediationIds.ToArray());
    }

    public ConfigurationDocumentRevision Revision { get; }

    public string ProviderId { get; }

    public string ChannelId { get; }

    public string CatalogId { get; }

    public string CatalogVersion { get; }

    public ReadOnlyCollection<string> RemediationIds { get; }
}

public sealed record ConfigurationMigrationOperation(
    string RemediationId,
    ConfigurationMigrationOperationKind Kind,
    string SourcePath,
    string CanonicalPath,
    int OriginalLineNumber);

public sealed record ConfigurationMigrationPreviewLine(
    ConfigurationMigrationPreviewLineKind Kind,
    int? OriginalLineNumber,
    int? DesiredLineNumber,
    string Path,
    string Summary);

public sealed class ConfigurationMigrationPlanResult
{
    private readonly byte[]? desiredContents;

    internal ConfigurationMigrationPlanResult(
        ConfigurationMigrationPlanState state,
        ConfigurationMigrationPlanRejectionCode? rejectionCode,
        string? message,
        ConfigurationMigrationPlanBinding? binding,
        IEnumerable<ConfigurationMigrationOperation>? operations,
        IEnumerable<ConfigurationMigrationPreviewLine>? previewLines,
        byte[]? desiredContents,
        ConfigurationDiagnosisReport? resultingDiagnosis)
    {
        State = state;
        RejectionCode = rejectionCode;
        Message = message;
        Binding = binding;
        Operations = Array.AsReadOnly((operations ?? []).ToArray());
        PreviewLines = Array.AsReadOnly((previewLines ?? []).ToArray());
        this.desiredContents = desiredContents is null ? null : [.. desiredContents];
        ResultingDiagnosis = resultingDiagnosis;
    }

    public ConfigurationMigrationPlanState State { get; }

    public ConfigurationMigrationPlanRejectionCode? RejectionCode { get; }

    public string? Message { get; }

    public ConfigurationMigrationPlanBinding? Binding { get; }

    public ReadOnlyCollection<ConfigurationMigrationOperation> Operations { get; }

    public ReadOnlyCollection<ConfigurationMigrationPreviewLine> PreviewLines { get; }

    public byte[]? DesiredContents => desiredContents is null ? null : [.. desiredContents];

    public ConfigurationDiagnosisReport? ResultingDiagnosis { get; }
}

public sealed class ConfigurationMigrationPlanner(TimeProvider? timeProvider = null)
{
    private readonly ConfigurationHealthAnalyzer analyzer = new(timeProvider);

    public ConfigurationMigrationPlanResult Plan(
        ConfigurationDocumentSnapshot snapshot,
        LauncherConfigurationDiagnosisEvidence evidence,
        ConfigurationDiagnosisReport diagnosis,
        IEnumerable<string> selectedRemediationIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(diagnosis);
        ArgumentNullException.ThrowIfNull(selectedRemediationIds);

        var selected = selectedRemediationIds.ToArray();
        if (selected.Any(string.IsNullOrWhiteSpace)
            || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
        {
            return Reject(
                ConfigurationMigrationPlanRejectionCode.InvalidSelection,
                "Selected configuration remediations must be non-empty and unique.");
        }

        if (diagnosis.Binding.Revision != snapshot.Revision)
        {
            return Reject(
                ConfigurationMigrationPlanRejectionCode.StaleDiagnosis,
                "The diagnosed configuration revision is no longer current.");
        }

        var catalog = evidence.Catalog;
        if (catalog is null)
        {
            return Reject(
                ConfigurationMigrationPlanRejectionCode.CatalogUnavailable,
                "The selected provider has no verified configuration catalog for migration.");
        }

        var catalogId = catalog.Identity.CatalogId;
        var catalogVersion = catalog.Identity.CatalogVersion.ToString();
        if (!string.Equals(diagnosis.Binding.ProviderId, evidence.ProviderId, StringComparison.Ordinal)
            || !string.Equals(diagnosis.Binding.ChannelId, evidence.ChannelId, StringComparison.Ordinal)
            || !string.Equals(diagnosis.Binding.CatalogId, catalogId, StringComparison.Ordinal)
            || !string.Equals(diagnosis.Binding.CatalogVersion, catalogVersion, StringComparison.Ordinal))
        {
            return Reject(
                ConfigurationMigrationPlanRejectionCode.BindingMismatch,
                "The diagnosis does not match the selected provider, channel, and catalog.");
        }

        var currentDiagnosis = analyzer.Analyze(snapshot, evidence);
        if (currentDiagnosis.Findings.Any(IsBlocking))
        {
            return Reject(
                ConfigurationMigrationPlanRejectionCode.BlockingDiagnosis,
                "The configuration has blocking findings that must be resolved before migration.",
                currentDiagnosis);
        }

        var eligible = BuildEligibleOperations(currentDiagnosis, catalog);
        if (selected.Any(remediationId => !eligible.ContainsKey(remediationId)))
        {
            return Reject(
                ConfigurationMigrationPlanRejectionCode.IneligibleSelection,
                "One or more selected remediations are no longer eligible for this configuration.",
                currentDiagnosis);
        }

        var binding = new ConfigurationMigrationPlanBinding(
            snapshot.Revision,
            evidence.ProviderId,
            evidence.ChannelId,
            catalogId,
            catalogVersion,
            selected);
        if (selected.Length == 0)
        {
            return new(
                ConfigurationMigrationPlanState.NoChange,
                null,
                null,
                binding,
                [],
                [],
                snapshot.Contents,
                currentDiagnosis);
        }

        var operations = selected
            .Select(remediationId => eligible[remediationId])
            .OrderByDescending(operation => operation.OriginalLineNumber)
            .ThenBy(operation => operation.RemediationId, StringComparer.Ordinal)
            .ToArray();
        var desired = snapshot.Contents;
        foreach (var operation in operations)
        {
            var result = Apply(desired, operation, catalog);
            if (!result.IsValid || result.Contents is null)
            {
                return Reject(
                    ConfigurationMigrationPlanRejectionCode.MutationFailed,
                    "The authorized sparse configuration edit could not be applied safely.",
                    currentDiagnosis);
            }

            desired = result.Contents;
        }

        if (desired.AsSpan().SequenceEqual(snapshot.Contents))
        {
            return Reject(
                ConfigurationMigrationPlanRejectionCode.MutationFailed,
                "The selected remediations did not produce the authorized configuration change.",
                currentDiagnosis);
        }

        var desiredSnapshot = new ConfigurationDocumentSnapshot(snapshot.Path, desired);
        var resultingDiagnosis = analyzer.Analyze(desiredSnapshot, evidence);
        if (resultingDiagnosis.Findings.Any(IsBlocking)
            || resultingDiagnosis.Findings.Any(
                finding => finding.RemediationId is not null
                    && selected.Contains(finding.RemediationId, StringComparer.Ordinal)))
        {
            return Reject(
                ConfigurationMigrationPlanRejectionCode.RescanFailed,
                "The migrated configuration did not pass its bound catalog rescan.",
                resultingDiagnosis);
        }

        var preview = BuildPreview(desired, operations);
        return new(
            ConfigurationMigrationPlanState.Ready,
            null,
            null,
            binding,
            operations,
            preview,
            desired,
            resultingDiagnosis);
    }

    private static Dictionary<string, ConfigurationMigrationOperation> BuildEligibleOperations(
        ConfigurationDiagnosisReport diagnosis,
        LauncherConfigurationCatalog catalog)
    {
        var operations = new Dictionary<string, ConfigurationMigrationOperation>(StringComparer.Ordinal);
        var ambiguousRemediationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in diagnosis.Findings.Where(
                     finding => finding.RemediationId is not null
                         && finding.SourcePath is not null
                         && finding.CanonicalPath is not null
                         && finding.LineNumber.HasValue))
        {
            var kind = finding.Code switch
            {
                "CONFIG_ALIAS_PRESENT" => ConfigurationMigrationOperationKind.MoveAlias,
                "CONFIG_CANONICAL_ALIAS_REDUNDANT" =>
                    ConfigurationMigrationOperationKind.RemoveRedundantAlias,
                _ => (ConfigurationMigrationOperationKind?)null,
            };
            if (!kind.HasValue)
            {
                continue;
            }

            var setting = catalog.Settings.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Path,
                    finding.CanonicalPath,
                    StringComparison.Ordinal));
            if (setting is null
                || setting.IsTemplate
                || !setting.Aliases.Any(
                    alias => string.Equals(alias.Path, finding.SourcePath, StringComparison.Ordinal)))
            {
                continue;
            }

            var operation = new ConfigurationMigrationOperation(
                finding.RemediationId!,
                kind.Value,
                finding.SourcePath!,
                finding.CanonicalPath!,
                finding.LineNumber.GetValueOrDefault());
            if (!operations.TryAdd(operation.RemediationId, operation))
            {
                ambiguousRemediationIds.Add(operation.RemediationId);
            }
        }

        foreach (var remediationId in ambiguousRemediationIds)
        {
            operations.Remove(remediationId);
        }

        return operations;
    }

    private static SparseTomlEditResult Apply(
        byte[] contents,
        ConfigurationMigrationOperation operation,
        LauncherConfigurationCatalog catalog)
    {
        var setting = catalog.Settings.Single(
            candidate => string.Equals(candidate.Path, operation.CanonicalPath, StringComparison.Ordinal));
        var load = SparseTomlDocument.Load(contents, out var document);
        if (!load.IsValid || document is null)
        {
            return load;
        }

        var read = document.ReadOverrides();
        if (!read.IsValid
            || read.Overrides is null
            || !read.Overrides.TryGetValue(operation.SourcePath, out var source)
            || LauncherConfigurationEditSession.ValidateSettingValue(setting, source.RenderedValue) is not null)
        {
            return SparseTomlEditResult.Invalid(
                new(
                    SparseTomlErrorCode.UnsupportedTarget,
                    "The selected catalog-authorized alias is no longer safe to migrate."));
        }

        if (operation.Kind == ConfigurationMigrationOperationKind.MoveAlias)
        {
            if (read.Overrides.ContainsKey(operation.CanonicalPath))
            {
                return SparseTomlEditResult.Invalid(
                    new(
                        SparseTomlErrorCode.DuplicateTarget,
                        "The canonical setting is already present."));
            }

            var set = document.SetOverride(operation.CanonicalPath, source.RenderedValue);
            if (!set.IsValid || set.Contents is null)
            {
                return set;
            }

            load = SparseTomlDocument.Load(set.Contents, out document);
            if (!load.IsValid || document is null)
            {
                return load;
            }

            return document.RemoveOverride(operation.SourcePath);
        }

        if (!read.Overrides.TryGetValue(operation.CanonicalPath, out var canonical)
            || LauncherConfigurationEditSession.ValidateSettingValue(setting, canonical.RenderedValue) is not null
            || !LauncherConfigurationEditSession.AreEquivalentSettingValues(
                setting,
                canonical.RenderedValue,
                source.RenderedValue))
        {
            return SparseTomlEditResult.Invalid(
                new(
                    SparseTomlErrorCode.UnsupportedTarget,
                    "The selected alias is not redundant with the canonical setting."));
        }

        return document.RemoveOverride(operation.SourcePath);
    }

    private static ReadOnlyCollection<ConfigurationMigrationPreviewLine> BuildPreview(
        byte[] desiredContents,
        IReadOnlyList<ConfigurationMigrationOperation> operations)
    {
        var load = SparseTomlDocument.Load(desiredContents, out var desiredDocument);
        var desiredRead = load.IsValid && desiredDocument is not null
            ? desiredDocument.ReadOverrides()
            : SparseTomlReadResult.Invalid(
                new(SparseTomlErrorCode.UnsupportedDocument, "The desired document could not be previewed."));
        var lines = new List<ConfigurationMigrationPreviewLine>();
        foreach (var operation in operations.OrderBy(operation => operation.OriginalLineNumber))
        {
            lines.Add(
                new(
                    ConfigurationMigrationPreviewLineKind.Removed,
                    operation.OriginalLineNumber,
                    null,
                    operation.SourcePath,
                    "Remove the catalog-recognized alias assignment without displaying its value."));
            if (operation.Kind == ConfigurationMigrationOperationKind.MoveAlias)
            {
                var desiredLine = desiredRead.Overrides is not null
                    && desiredRead.Overrides.TryGetValue(operation.CanonicalPath, out var canonical)
                        ? canonical.LineNumber
                        : (int?)null;
                lines.Add(
                    new(
                        ConfigurationMigrationPreviewLineKind.Added,
                        null,
                        desiredLine,
                        operation.CanonicalPath,
                        "Add the canonical assignment using the preserved alias value."));
            }
        }

        return Array.AsReadOnly(lines.ToArray());
    }

    private static bool IsBlocking(ConfigurationDiagnosisFinding finding) =>
        finding.Severity is ConfigurationDiagnosisSeverity.Error or ConfigurationDiagnosisSeverity.Unknown;

    private static ConfigurationMigrationPlanResult Reject(
        ConfigurationMigrationPlanRejectionCode code,
        string message,
        ConfigurationDiagnosisReport? diagnosis = null) =>
        new(
            ConfigurationMigrationPlanState.Rejected,
            code,
            message,
            null,
            [],
            [],
            null,
            diagnosis);
}
