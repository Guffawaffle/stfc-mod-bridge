using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

public sealed record ConfigurationDocumentRevision(string Sha256)
{
    public static ConfigurationDocumentRevision FromContents(ReadOnlySpan<byte> contents) =>
        new(Convert.ToHexString(SHA256.HashData(contents)));
}

public sealed class ConfigurationDocumentSnapshot
{
    private readonly byte[] contents;

    public ConfigurationDocumentSnapshot(
        string path,
        byte[] contents,
        bool existed = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        Path = System.IO.Path.GetFullPath(path);
        this.contents = [.. contents];
        Revision = ConfigurationDocumentRevision.FromContents(contents);
        Existed = existed;
    }

    public string Path { get; }

    public ConfigurationDocumentRevision Revision { get; }

    public bool Existed { get; }

    public byte[] Contents => [.. contents];
}

public enum ConfigurationSemanticChangeKind
{
    SetOverride,
    ClearOverride,
}

public sealed record ConfigurationSemanticChange(
    string StableId,
    LauncherConfigurationSetting Setting,
    ConfigurationSemanticChangeKind Kind,
    object? Value)
{
    public string CanonicalPath => Setting.Path;
}

public sealed class ConfigurationChangeSet
{
    public ConfigurationChangeSet(IEnumerable<ConfigurationSemanticChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var materialized = changes.ToArray();
        foreach (var change in materialized)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(change.StableId);
            ArgumentNullException.ThrowIfNull(change.Setting);
            if (change.Kind == ConfigurationSemanticChangeKind.SetOverride
                && change.Value is null)
            {
                throw new InvalidOperationException(
                    $"Configuration change '{change.StableId}' is missing its typed value.");
            }

            if (change.Kind == ConfigurationSemanticChangeKind.ClearOverride
                && change.Value is not null)
            {
                throw new InvalidOperationException(
                    $"Clear-override change '{change.StableId}' cannot carry a value.");
            }
        }

        var duplicate = materialized
            .GroupBy(change => change.CanonicalPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Configuration change set owns '{duplicate.Key}' more than once.");
        }

        Changes = Array.AsReadOnly(materialized);
    }

    public ReadOnlyCollection<ConfigurationSemanticChange> Changes { get; }

    public bool IsEmpty => Changes.Count == 0;
}

public enum ConfigurationRepositoryReadState
{
    Succeeded,
    NoConfigurationSelected,
    Invalid,
    IoFailure,
}

public sealed record ConfigurationRepositoryReadResult(
    ConfigurationRepositoryReadState State,
    ConfigurationDocumentSnapshot? Snapshot = null,
    SparseTomlError? ValidationError = null,
    string? Error = null)
{
    public bool IsSuccess =>
        State == ConfigurationRepositoryReadState.Succeeded
        && Snapshot is not null;
}

public sealed class ConfigurationCommitRequest
{
    private readonly byte[] baselineContents;

    public ConfigurationCommitRequest(
        string path,
        ConfigurationDocumentRevision expectedRevision,
        byte[] baselineContents,
        ConfigurationChangeSet changes,
        bool baselineExisted = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        ExpectedRevision =
            expectedRevision ?? throw new ArgumentNullException(nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(baselineContents);
        this.baselineContents = [.. baselineContents];
        Changes = changes ?? throw new ArgumentNullException(nameof(changes));
        BaselineExisted = baselineExisted;
    }

    public string Path { get; }

    public ConfigurationDocumentRevision ExpectedRevision { get; }

    public byte[] BaselineContents => [.. baselineContents];

    public ConfigurationChangeSet Changes { get; }

    public bool BaselineExisted { get; }

}

public sealed record ConfigurationRepositoryCommitResult(
    AtomicTomlWriteState State,
    ConfigurationDocumentSnapshot? CommittedSnapshot = null,
    string? BackupPath = null,
    SparseTomlError? ValidationError = null,
    string? Error = null,
    ConfigurationBackupReceipt? BackupReceipt = null)
{
    public bool IsSuccess =>
        State is AtomicTomlWriteState.Succeeded or AtomicTomlWriteState.NoChange
        && CommittedSnapshot is not null;

    public string? Warning { get; init; }
}

public sealed class ConfigurationDocumentCommitRequest
{
    private readonly byte[] baselineContents;
    private readonly byte[] desiredContents;

    public ConfigurationDocumentCommitRequest(
        string path,
        ConfigurationDocumentRevision expectedRevision,
        byte[] baselineContents,
        byte[] desiredContents,
        bool baselineExisted = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        ExpectedRevision = expectedRevision ?? throw new ArgumentNullException(nameof(expectedRevision));
        this.baselineContents = [.. baselineContents];
        this.desiredContents = [.. desiredContents];
        BaselineExisted = baselineExisted;
    }

    public string Path { get; }
    public ConfigurationDocumentRevision ExpectedRevision { get; }
    public byte[] BaselineContents => [.. baselineContents];
    public byte[] DesiredContents => [.. desiredContents];
    public bool BaselineExisted { get; }
}

public interface IConfigurationRepository
{
    bool ProducesVerifiedBackupReceipt => false;

    ConfigurationRepositoryReadResult Read(string? configurationPath);

    Task<ConfigurationRepositoryCommitResult> CommitAsync(
        ConfigurationCommitRequest request,
        CancellationToken cancellationToken = default);

    Task<ConfigurationRepositoryCommitResult> CommitDocumentAsync(
        ConfigurationDocumentCommitRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConfigurationRepositoryCommitResult(
            AtomicTomlWriteState.Invalid,
            Error: "This configuration repository does not support document transactions."));
}
