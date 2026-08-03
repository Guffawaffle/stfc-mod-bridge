using System.Net;

namespace STFCCommunityMod.Launcher.Core;

public enum ModDeploymentResultState
{
    Succeeded,
    Busy,
    GameRunning,
    InvalidGameTarget,
    ExistingArtifactRequiresAdoption,
    ManagedArtifactChanged,
    DownloadRejected,
    VerificationFailed,
    FailedAndRolledBack,
    RecoveryRequired,
}

public enum ExistingArtifactPolicy
{
    Reject,
    AdoptAndPreserve,
}

public enum ModDeploymentPhase
{
    Planned,
    Downloading,
    Verified,
    Staged,
    Committing,
    Committed,
    RollingBack,
    RolledBack,
    Failed,
}

public enum ModDeploymentOperation
{
    Deploy,
    Uninstall,
}

public sealed record ModReleaseArtifact(
    Uri DownloadUri,
    string FileName,
    long Size,
    string Sha256,
    string ExpectedVersion);

public sealed record ModArtifactDownload(
    HttpStatusCode StatusCode,
    byte[] Contents,
    long? DeclaredContentLength = null);

public sealed record ModInstalledArtifactState(
    int SchemaVersion,
    string GameDirectory,
    string FileName,
    string Version,
    long Size,
    string Sha256,
    DateTimeOffset InstalledAtUtc,
    string? PreviousArtifactBackupPath,
    string ProviderId,
    string ReleaseChannelId,
    string RuntimeDistributionId);

public sealed record ModInstallationAttribution(
    string ProviderId,
    string ReleaseChannelId,
    string RuntimeDistributionId);

public sealed record ModDeploymentJournal(
    int SchemaVersion,
    string TransactionId,
    ModDeploymentOperation Operation,
    ModDeploymentPhase Phase,
    string GameDirectory,
    ModReleaseArtifact Artifact,
    string StagePath,
    string SameVolumeBackupPath,
    string DurableBackupPath,
    bool HadExistingArtifact,
    ModInstalledArtifactState? PreviousInstalledState,
    DateTimeOffset UpdatedAtUtc,
    string? Error = null);

public sealed record ModDeploymentResult(
    ModDeploymentResultState State,
    string Message,
    ModInstalledArtifactState? InstalledState = null,
    bool Changed = false)
{
    public bool IsSuccess => State == ModDeploymentResultState.Succeeded;
}

public interface IModArtifactDownloader
{
    Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken);
}

public interface IModArtifactVersionReader
{
    string? ReadVersion(string artifactPath);
}

public sealed record ModArtifactAuthenticityResult(bool IsTrusted, string Message);

public interface IModArtifactAuthenticityVerifier
{
    ModArtifactAuthenticityResult Verify(string artifactPath);
}

public sealed record ModDeploymentCommitContext(
    string TransactionId,
    string GameDirectory,
    ModReleaseArtifact TargetArtifact,
    ModInstalledArtifactState? PreviousInstalledState,
    bool HadExistingArtifact);

/// <summary>
/// Participates in the commit boundary of a verified DLL deployment. The
/// deployment service keeps its installation lease and exact prior-artifact
/// rollback copy until this participant and the deployment journal commit.
/// </summary>
public interface IModDeploymentCommitParticipant
{
    Task BeginAsync(ModDeploymentCommitContext context, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task CompleteAsync(CancellationToken cancellationToken);

    Task RollBackAsync(CancellationToken cancellationToken);
}
