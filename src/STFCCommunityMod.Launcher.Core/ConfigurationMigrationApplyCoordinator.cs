namespace STFCCommunityMod.Launcher.Core;

public sealed class ConfigurationMigrationApplyRequest
{
    public ConfigurationMigrationApplyRequest(
        ConfigurationDocumentSnapshot baseline,
        ConfigurationMigrationPlanResult plan,
        LauncherConfigurationDiagnosisEvidence diagnosisEvidence)
    {
        Baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        DiagnosisEvidence = diagnosisEvidence
            ?? throw new ArgumentNullException(nameof(diagnosisEvidence));
    }

    public ConfigurationDocumentSnapshot Baseline { get; }

    public ConfigurationMigrationPlanResult Plan { get; }

    public LauncherConfigurationDiagnosisEvidence DiagnosisEvidence { get; }
}

public sealed record ConfigurationMigrationApplyResult(
    AtomicTomlWriteState State,
    ConfigurationDocumentSnapshot? CommittedSnapshot = null,
    ConfigurationBackupReceipt? BackupReceipt = null,
    ConfigurationDiagnosisReport? ResultingDiagnosis = null,
    SparseTomlError? ValidationError = null,
    string? Error = null)
{
    public bool IsSuccess =>
        State is AtomicTomlWriteState.Succeeded or AtomicTomlWriteState.NoChange
        && CommittedSnapshot is not null
        && ResultingDiagnosis is not null;

    public string? Warning { get; init; }
}

public sealed class ConfigurationMigrationApplyCoordinator(
    IConfigurationRepository repository,
    ConfigurationHealthAnalyzer? analyzer = null)
{
    private readonly IConfigurationRepository repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ConfigurationHealthAnalyzer analyzer = analyzer ?? new();

    public async Task<ConfigurationMigrationApplyResult> ApplyAsync(
        ConfigurationMigrationApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var baseline = request.Baseline;
        var plan = request.Plan;
        var binding = plan.Binding;
        var desiredContents = plan.DesiredContents;
        var plannedDiagnosis = plan.ResultingDiagnosis;
        if (plan.State == ConfigurationMigrationPlanState.Rejected
            || binding is null
            || desiredContents is null
            || plannedDiagnosis is null)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                Error: plan.Message ?? "The configuration migration plan is not applicable.");
        }

        if (binding.Revision != baseline.Revision
            || !BindingIdentityMatches(binding, plannedDiagnosis.Binding)
            || plannedDiagnosis.Binding.Revision
                != ConfigurationDocumentRevision.FromContents(desiredContents))
        {
            return new(
                AtomicTomlWriteState.Invalid,
                Error: "The configuration migration plan is not bound to its baseline and desired document revisions.");
        }

        if (!EvidenceMatches(binding, request.DiagnosisEvidence))
        {
            return new(
                AtomicTomlWriteState.Invalid,
                Error: "The selected provider, channel, or catalog no longer matches the migration plan.");
        }

        if (plan.State == ConfigurationMigrationPlanState.Ready
            && !repository.ProducesVerifiedBackupReceipt)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                Error: "Configuration migration requires a repository-owned verified backup receipt.");
        }

        var read = repository.Read(baseline.Path);
        if (!read.IsSuccess || read.Snapshot is null)
        {
            return ReadFailure(read);
        }

        if (read.Snapshot.Revision != baseline.Revision
            || !read.Snapshot.Contents.AsSpan().SequenceEqual(baseline.Contents))
        {
            return new(
                AtomicTomlWriteState.Conflict,
                Error:
                    "The configuration changed after the migration preview was created; "
                    + "the external changes were preserved.");
        }

        var currentDiagnosis = analyzer.Analyze(
            read.Snapshot,
            request.DiagnosisEvidence);
        if (!BindingMatches(binding, currentDiagnosis.Binding))
        {
            return new(
                AtomicTomlWriteState.Conflict,
                Error:
                    "The provider or catalog diagnosis changed after the migration preview was created; "
                    + "the configuration was preserved.");
        }

        var commit = await repository.CommitDocumentAsync(
            new(
                baseline.Path,
                binding.Revision,
                baseline.Contents,
                desiredContents),
            cancellationToken).ConfigureAwait(false);
        if (!commit.IsSuccess || commit.CommittedSnapshot is null)
        {
            return new(
                commit.State,
                BackupReceipt: commit.BackupReceipt,
                ValidationError: commit.ValidationError,
                Error: commit.Error);
        }

        if (plan.State == ConfigurationMigrationPlanState.Ready
            && commit.BackupReceipt is null)
        {
            throw new InvalidDataException(
                "The configuration repository committed a migration without its required verified backup receipt.");
        }

        var resultingDiagnosis = analyzer.Analyze(
            commit.CommittedSnapshot,
            request.DiagnosisEvidence);
        return new ConfigurationMigrationApplyResult(
            commit.State,
            commit.CommittedSnapshot,
            commit.BackupReceipt,
            resultingDiagnosis)
        {
            Warning = commit.Warning,
        };
    }

    private static bool BindingMatches(
        ConfigurationMigrationPlanBinding expected,
        ConfigurationDiagnosisBinding actual) =>
        expected.Revision == actual.Revision
        && BindingIdentityMatches(expected, actual);

    private static bool BindingIdentityMatches(
        ConfigurationMigrationPlanBinding expected,
        ConfigurationDiagnosisBinding actual) =>
        string.Equals(expected.ProviderId, actual.ProviderId, StringComparison.Ordinal)
        && string.Equals(expected.ChannelId, actual.ChannelId, StringComparison.Ordinal)
        && string.Equals(expected.CatalogId, actual.CatalogId, StringComparison.Ordinal)
        && string.Equals(expected.CatalogVersion, actual.CatalogVersion, StringComparison.Ordinal);

    private static bool EvidenceMatches(
        ConfigurationMigrationPlanBinding binding,
        LauncherConfigurationDiagnosisEvidence evidence)
    {
        var catalog = evidence.Catalog;
        return catalog is not null
            && string.Equals(binding.ProviderId, evidence.ProviderId, StringComparison.Ordinal)
            && string.Equals(binding.ChannelId, evidence.ChannelId, StringComparison.Ordinal)
            && string.Equals(
                binding.CatalogId,
                catalog.Identity.CatalogId,
                StringComparison.Ordinal)
            && string.Equals(
                binding.CatalogVersion,
                catalog.Identity.CatalogVersion.ToString(),
                StringComparison.Ordinal);
    }

    private static ConfigurationMigrationApplyResult ReadFailure(
        ConfigurationRepositoryReadResult read) =>
        read.State == ConfigurationRepositoryReadState.IoFailure
            ? new(AtomicTomlWriteState.IoFailure, Error: read.Error)
            : new(
                AtomicTomlWriteState.Conflict,
                ValidationError: read.ValidationError,
                Error: read.Error
                    ?? "The configuration is no longer the document used to create the migration preview; "
                        + "it was preserved.");
}
