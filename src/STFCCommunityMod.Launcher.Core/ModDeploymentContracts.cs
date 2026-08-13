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
    CleanupPending,
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
    string ExpectedVersion,
    ModRuntimeManifestArtifact? RuntimeManifest = null);

public sealed record ModRuntimeManifestArtifact(
    Uri DownloadUri,
    string FileName,
    long Size,
    string Sha256,
    string ExpectedSourceRevision,
    string ExpectedRepository,
    string ExpectedTag);

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
    string RuntimeDistributionId,
    ModInstalledRuntimeManifestState? RuntimeManifest = null,
    string? PreviousRuntimeManifestBackupPath = null,
    ModArtifactIdentityReceipt? PreviousArtifactBackupIdentity = null,
    ModArtifactIdentityReceipt? PreviousRuntimeManifestBackupIdentity = null);

public sealed record ModInstalledArtifactRegistry(
    int SchemaVersion,
    IReadOnlyList<ModInstalledArtifactState> Installations,
    IReadOnlyList<ModDetachedAdoptionBackupState>? DetachedAdoptionBackups = null);

public sealed record ModDetachedAdoptionBackupState(
    string DetachmentId,
    string GameDirectory,
    DateTimeOffset DetachedAtUtc,
    string ProviderId,
    string ReleaseChannelId,
    string RuntimeDistributionId,
    string? PreviousArtifactBackupPath,
    ModArtifactIdentityReceipt? PreviousArtifactBackupIdentity,
    string? PreviousRuntimeManifestBackupPath,
    ModArtifactIdentityReceipt? PreviousRuntimeManifestBackupIdentity);

public sealed record ModArtifactIdentityReceipt(long Size, string Sha256);

public sealed record ModInstalledRuntimeManifestState(
    string FileName,
    long Size,
    string Sha256,
    string SourceRevision,
    string Repository,
    string Tag);

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
    string? Error = null,
    bool HadExistingRuntimeManifest = false,
    bool HasCommitParticipant = false,
    bool CommitParticipantCompleted = false,
    ModArtifactIdentityReceipt? ExistingArtifactIdentity = null,
    ModArtifactIdentityReceipt? ExistingRuntimeManifestIdentity = null,
    ModInstallationAttribution? TargetInstallationAttribution = null);

public sealed record ModDeploymentResult(
    ModDeploymentResultState State,
    string Message,
    ModInstalledArtifactState? InstalledState = null,
    bool Changed = false,
    ReviewedRuntimeActivation? RuntimeActivation = null)
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

public enum AuthenticodeRevocationMode
{
    CachedOnly,
    OnlineRetrievalAllowed,
}

public enum AuthenticodeTimestampKind
{
    None,
    LegacyAuthenticode,
    Rfc3161,
}

public sealed record AuthenticodeSignatureEvidence(
    int Index,
    bool TrustPolicyPassed,
    bool PublisherMatched,
    bool HasCodeSigningEku,
    bool DurableIdentityMatched,
    AuthenticodeTimestampKind TimestampKind,
    DateTimeOffset? VerifiedAsOfUtc,
    string? SignerIdentitySha256);

public sealed record AuthenticodeVerificationEvidence(
    AuthenticodeRevocationMode RevocationMode,
    DateTimeOffset EvaluatedAtUtc,
    string RevocationFreshness,
    IReadOnlyList<AuthenticodeSignatureEvidence> Signatures);

public sealed record ModArtifactAuthenticityResult(
    bool IsTrusted,
    string Message,
    AuthenticodeVerificationEvidence? Evidence = null);

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
