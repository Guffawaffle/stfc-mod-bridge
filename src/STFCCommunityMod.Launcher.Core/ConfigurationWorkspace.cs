using System.Collections.Frozen;

namespace STFCCommunityMod.Launcher.Core;

public enum ConfigurationWorkspaceExternalState
{
    Current,
    Stale,
}

[Flags]
public enum ConfigurationWorkspaceInvalidation
{
    None = 0,
    Values = 1 << 0,
    Validation = 1 << 1,
    Summary = 1 << 2,
    Query = 1 << 3,
    Layout = 1 << 4,
    ExternalState = 1 << 5,
}

public enum ConfigurationWorkspaceTransitionReason
{
    DraftChanged,
    Discarded,
    Committed,
    ExternalConflict,
    DocumentReplaced,
}

public sealed class ConfigurationWorkspaceChangedEventArgs : EventArgs
{
    public ConfigurationWorkspaceChangedEventArgs(
        long workspaceRevision,
        ConfigurationWorkspaceTransitionReason reason,
        IEnumerable<string>? changedIds = null,
        IEnumerable<string>? addedIds = null,
        IEnumerable<string>? removedIds = null,
        ConfigurationWorkspaceInvalidation invalidations =
            ConfigurationWorkspaceInvalidation.None)
    {
        WorkspaceRevision = workspaceRevision;
        Reason = reason;
        ChangedIds = ToReadOnlySet(changedIds);
        AddedIds = ToReadOnlySet(addedIds);
        RemovedIds = ToReadOnlySet(removedIds);
        Invalidations = invalidations;
    }

    public long WorkspaceRevision { get; }

    public ConfigurationWorkspaceTransitionReason Reason { get; }

    public IReadOnlySet<string> ChangedIds { get; }

    public IReadOnlySet<string> AddedIds { get; }

    public IReadOnlySet<string> RemovedIds { get; }

    public ConfigurationWorkspaceInvalidation Invalidations { get; }

    private static FrozenSet<string> ToReadOnlySet(
        IEnumerable<string>? values) =>
        (values ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

public sealed record ConfigurationWorkspaceLoadResult(
    ConfigurationRepositoryReadState State,
    SparseTomlError? ValidationError = null,
    string? Error = null)
{
    public bool IsSuccess => State == ConfigurationRepositoryReadState.Succeeded;
}

public sealed class ConfigurationWorkspace
{
    private readonly IConfigurationRepository repository;
    private readonly LauncherConfigurationEditSession settingsSession;
    private ConfigurationDocumentSnapshot baseline;

    private ConfigurationWorkspace(
        IConfigurationRepository repository,
        ConfigurationDocumentSnapshot baseline,
        LauncherConfigurationEditSession settingsSession)
    {
        this.repository = repository;
        this.baseline = baseline;
        this.settingsSession = settingsSession;
    }

    public string DocumentPath => baseline.Path;

    public long Revision { get; private set; }

    public ConfigurationDocumentRevision BaselineRevision => baseline.Revision;

    public ConfigurationWorkspaceExternalState ExternalState { get; private set; } =
        ConfigurationWorkspaceExternalState.Current;

    public bool IsStale => ExternalState == ConfigurationWorkspaceExternalState.Stale;

    public int PendingChangeCount => settingsSession.PendingChangeCount;

    public bool HasPendingChanges => settingsSession.HasPendingChanges;

    public event EventHandler<ConfigurationWorkspaceChangedEventArgs>? WorkspaceChanged;

    public static ConfigurationWorkspaceLoadResult Load(
        string? configurationPath,
        LauncherConfigurationCatalog catalog,
        IConfigurationRepository repository,
        out ConfigurationWorkspace? workspace)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(repository);
        workspace = null;
        var read = repository.Read(configurationPath);
        if (!read.IsSuccess || read.Snapshot is null)
        {
            return new(read.State, read.ValidationError, read.Error);
        }

        var sessionLoad = LauncherConfigurationEditSession.Load(
            read.Snapshot.Contents,
            catalog,
            out var settingsSession);
        if (!sessionLoad.IsValid || settingsSession is null)
        {
            return new(
                ConfigurationRepositoryReadState.Invalid,
                sessionLoad.Error);
        }

        workspace = new(repository, read.Snapshot, settingsSession);
        return new(ConfigurationRepositoryReadState.Succeeded);
    }

    public LauncherConfigurationEntryState GetState(
        LauncherConfigurationSetting setting) =>
        settingsSession.GetState(setting);

    public SparseTomlEditResult StageSet(
        LauncherConfigurationSetting setting,
        string renderedTomlValue)
    {
        var previousState = settingsSession.GetState(setting);
        var result = settingsSession.StageSet(setting, renderedTomlValue);
        if (result.IsValid
            && previousState != settingsSession.GetState(setting))
        {
            PublishDraftChanged(setting.Path);
        }

        return result;
    }

    public SparseTomlEditResult StageRemove(
        LauncherConfigurationSetting setting)
    {
        var previousState = settingsSession.GetState(setting);
        var result = settingsSession.StageRemove(setting);
        if (result.IsValid
            && previousState != settingsSession.GetState(setting))
        {
            PublishDraftChanged(setting.Path);
        }

        return result;
    }

    public SparseTomlEditResult Revert(
        LauncherConfigurationSetting setting)
    {
        var previousState = settingsSession.GetState(setting);
        var result = settingsSession.Revert(setting);
        if (result.IsValid
            && previousState != settingsSession.GetState(setting))
        {
            PublishDraftChanged(setting.Path);
        }

        return result;
    }

    public void Discard()
    {
        var changedIds = PrepareChangeSet()
            .Changes
            .Select(change => change.StableId)
            .ToArray();
        settingsSession.Discard();
        if (changedIds.Length > 0)
        {
            Publish(
                ConfigurationWorkspaceTransitionReason.Discarded,
                changedIds,
                DraftInvalidations);
        }
    }

    public ConfigurationChangeSet PrepareChangeSet() =>
        settingsSession.BuildChangeSet();

    public async Task<ConfigurationRepositoryCommitResult> CommitAsync(
        CancellationToken cancellationToken = default)
    {
        var changeSet = PrepareChangeSet();
        var changedIds = changeSet.Changes
            .Select(change => change.StableId)
            .ToArray();
        var request = new ConfigurationCommitRequest(
            baseline.Path,
            baseline.Revision,
            baseline.Contents,
            changeSet);
        var result = await repository
            .CommitAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess || result.CommittedSnapshot is null)
        {
            if (result.State == AtomicTomlWriteState.Conflict)
            {
                ExternalState = ConfigurationWorkspaceExternalState.Stale;
                Publish(
                    ConfigurationWorkspaceTransitionReason.ExternalConflict,
                    [],
                    ConfigurationWorkspaceInvalidation.Summary
                        | ConfigurationWorkspaceInvalidation.ExternalState);
            }

            return result;
        }

        var accepted = settingsSession.AcceptCommittedBaseline(
            result.CommittedSnapshot.Contents);
        if (!accepted.IsValid)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                ValidationError: accepted.Error);
        }

        baseline = result.CommittedSnapshot;
        ExternalState = ConfigurationWorkspaceExternalState.Current;
        Publish(
            ConfigurationWorkspaceTransitionReason.Committed,
            changedIds,
            DraftInvalidations
                | ConfigurationWorkspaceInvalidation.ExternalState);
        return result;
    }

    private const ConfigurationWorkspaceInvalidation DraftInvalidations =
        ConfigurationWorkspaceInvalidation.Values
        | ConfigurationWorkspaceInvalidation.Validation
        | ConfigurationWorkspaceInvalidation.Summary
        | ConfigurationWorkspaceInvalidation.Query;

    private void PublishDraftChanged(string settingId) =>
        Publish(
            ConfigurationWorkspaceTransitionReason.DraftChanged,
            [settingId],
            DraftInvalidations);

    private void Publish(
        ConfigurationWorkspaceTransitionReason reason,
        IEnumerable<string> changedIds,
        ConfigurationWorkspaceInvalidation invalidations)
    {
        ++Revision;
        WorkspaceChanged?.Invoke(
            this,
            new(
                Revision,
                reason,
                changedIds,
                invalidations: invalidations));
    }
}
