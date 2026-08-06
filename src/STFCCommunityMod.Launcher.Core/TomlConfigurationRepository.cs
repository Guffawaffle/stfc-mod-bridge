namespace STFCCommunityMod.Launcher.Core;

public sealed class TomlConfigurationRepository : IConfigurationRepository
{
    private readonly AtomicTomlStore store;

    public TomlConfigurationRepository(
        AtomicTomlStore? store = null,
        IConfigurationMutationBackup? mutationBackup = null)
    {
        if (store is not null && mutationBackup is not null)
        {
            throw new ArgumentException(
                "Supply either an atomic store or a mutation backup, not both.");
        }
        this.store = store
            ?? (mutationBackup is null
                ? new AtomicTomlStore()
                : new AtomicTomlStore(mutationBackup));
    }

    public bool ProducesVerifiedBackupReceipt => store.ProducesVerifiedBackupReceipt;

    public ConfigurationRepositoryReadResult Read(string? configurationPath)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new(ConfigurationRepositoryReadState.NoConfigurationSelected);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(configurationPath);
            var contents = File.ReadAllBytes(fullPath);
            var load = SparseTomlDocument.Load(contents, out var document);
            if (!load.IsValid || document is null)
            {
                return new(
                    ConfigurationRepositoryReadState.Invalid,
                    ValidationError: load.Error);
            }

            var validation = document.ValidateForMutation();
            if (!validation.IsValid)
            {
                return new(
                    ConfigurationRepositoryReadState.Invalid,
                    ValidationError: validation.Error);
            }

            return new(
                ConfigurationRepositoryReadState.Succeeded,
                new ConfigurationDocumentSnapshot(fullPath, contents));
        }
        catch (FileNotFoundException)
        {
            return new(ConfigurationRepositoryReadState.NoConfigurationSelected);
        }
        catch (DirectoryNotFoundException)
        {
            return new(ConfigurationRepositoryReadState.NoConfigurationSelected);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return new(
                ConfigurationRepositoryReadState.IoFailure,
                Error: exception.Message);
        }
    }

    public async Task<ConfigurationRepositoryCommitResult> CommitAsync(
        ConfigurationCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baselineContents = request.BaselineContents;
        if (ConfigurationDocumentRevision.FromContents(baselineContents)
            != request.ExpectedRevision)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                Error: "The expected configuration revision does not match its baseline contents.");
        }

        var transformed = ApplyChanges(baselineContents, request.Changes);
        if (!transformed.IsValid || transformed.Contents is null)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                ValidationError: transformed.Error);
        }

        var write = await store.SaveDocumentAsync(
            request.Path,
            baselineContents,
            transformed.Contents,
            cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess)
        {
            return new(
                write.State,
                BackupPath: write.BackupPath,
                ValidationError: write.ValidationError,
                Error: write.Error,
                BackupReceipt: write.BackupReceipt);
        }

        return new(
            write.State,
            new ConfigurationDocumentSnapshot(request.Path, transformed.Contents),
            write.BackupPath,
            BackupReceipt: write.BackupReceipt);
    }

    public async Task<ConfigurationRepositoryCommitResult> CommitDocumentAsync(
        ConfigurationDocumentCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baselineContents = request.BaselineContents;
        if (ConfigurationDocumentRevision.FromContents(baselineContents) != request.ExpectedRevision)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                Error: "The expected configuration revision does not match its baseline contents.");
        }

        var load = SparseTomlDocument.Load(request.DesiredContents, out var document);
        var validation = load.IsValid && document is not null ? document.ValidateForMutation() : load;
        if (!validation.IsValid)
        {
            return new(AtomicTomlWriteState.Invalid, ValidationError: validation.Error);
        }

        var write = await store.SaveDocumentAsync(
            request.Path,
            baselineContents,
            request.DesiredContents,
            cancellationToken).ConfigureAwait(false);
        return write.IsSuccess
            ? new(
                write.State,
                new ConfigurationDocumentSnapshot(request.Path, request.DesiredContents),
                write.BackupPath,
                BackupReceipt: write.BackupReceipt)
            : new(
                write.State,
                BackupPath: write.BackupPath,
                ValidationError: write.ValidationError,
                Error: write.Error,
                BackupReceipt: write.BackupReceipt);
    }

    private static SparseTomlEditResult ApplyChanges(
        byte[] baselineContents,
        ConfigurationChangeSet changeSet)
    {
        byte[] contents = [.. baselineContents];
        var changed = false;
        foreach (var change in changeSet.Changes)
        {
            var load = SparseTomlDocument.Load(contents, out var document);
            if (!load.IsValid || document is null)
            {
                return load;
            }

            var edit = change.Kind switch
            {
                ConfigurationSemanticChangeKind.SetOverride
                    when TryRenderTomlValue(change, out var renderedTomlValue) =>
                    document.SetOverride(
                        change.CanonicalPath,
                        renderedTomlValue),
                ConfigurationSemanticChangeKind.ClearOverride =>
                    document.RemoveOverride(change.CanonicalPath),
                _ => SparseTomlEditResult.Invalid(
                    new SparseTomlError(
                        SparseTomlErrorCode.InvalidValue,
                        $"Change '{change.StableId}' does not contain a valid persistence value.")),
            };
            if (!edit.IsValid || edit.Contents is null)
            {
                return edit;
            }

            contents = edit.Contents;
            changed |= edit.Changed;
        }

        return changed
            ? SparseTomlEditResult.Updated(contents)
            : SparseTomlEditResult.Unchanged(contents);
    }

    private static bool TryRenderTomlValue(
        ConfigurationSemanticChange change,
        out string rendered)
    {
        rendered = string.Empty;
        switch (change.Setting.ValueKind, change.Value)
        {
            case (LauncherConfigurationValueKind.Boolean, bool value):
                rendered = value ? "true" : "false";
                return true;

            case (LauncherConfigurationValueKind.Integer, long value):
                rendered = LauncherTomlValue.RenderInteger(value);
                return true;

            case (LauncherConfigurationValueKind.Number, double value):
                rendered = LauncherTomlValue.RenderNumber(value);
                return true;

            case (
                LauncherConfigurationValueKind.String
                    or LauncherConfigurationValueKind.Enum
                    or LauncherConfigurationValueKind.Keybinding,
                string value):
                rendered = LauncherTomlValue.RenderString(value);
                return true;

            case (
                LauncherConfigurationValueKind.Union,
                LauncherNotificationPolicy policy)
                when change.Setting.Control
                    == LauncherConfigurationControl.NotificationPolicy:
                rendered = policy.Render();
                return true;

            default:
                return false;
        }
    }
}
