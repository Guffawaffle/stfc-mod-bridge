using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed partial class ModDeploymentService
{
    internal ModSourceReplacementReview CaptureSourceReplacementReview(string gameDirectory)
    {
        var validation = GameInstallValidator.Validate(gameDirectory);
        if (!validation.IsValid) throw new InvalidOperationException(validation.Message);
        var previous = ReadInstalledState(validation.GameDirectory)
            ?? throw new InvalidOperationException("The managed receipt changed during review.");
        var live = CaptureIdentity(Path.Combine(validation.GameDirectory, ManagedFileName));
        var runtimePath = RuntimeManifestTargetPath(validation.GameDirectory);
        return new(previous, live, File.Exists(runtimePath) ? CaptureIdentity(runtimePath) : null);
    }

    private static bool MatchesReplacementSource(
        ModSourceReplacementReview review, ModInstalledArtifactState? previous,
        ModArtifactIdentityReceipt? live, ModArtifactIdentityReceipt? runtime) =>
        previous is not null && live is not null
        && JsonSerializer.Serialize(review.PreviousInstalledState, JsonOptions)
            == JsonSerializer.Serialize(previous, JsonOptions)
        && review.LiveArtifactIdentity == live
        && review.LiveRuntimeManifestIdentity == runtime;

    private ModDetachedAdoptionBackupState? PriorAdoptionBackup(ModDeploymentJournal journal)
    {
        var previous = journal.PreviousInstalledState!;
        if (previous.PreviousArtifactBackupPath is null && previous.PreviousRuntimeManifestBackupPath is null)
            return null;
        return new(journal.TransactionId, journal.GameDirectory, timeProvider.GetUtcNow(),
            previous.ProviderId, previous.ReleaseChannelId, previous.RuntimeDistributionId,
            previous.PreviousArtifactBackupPath, previous.PreviousArtifactBackupIdentity,
            previous.PreviousRuntimeManifestBackupPath, previous.PreviousRuntimeManifestBackupIdentity);
    }

    private string? ValidatePriorAdoptionDetachment(ModDeploymentJournal journal)
    {
        if (!journal.AdoptChangedManagedArtifact) return null;
        var previous = journal.PreviousInstalledState!;
        var actual = ReadInstalledRegistry().DetachedAdoptionBackups?
            .SingleOrDefault(backup => backup.DetachmentId == journal.TransactionId);
        var expected = previous.PreviousArtifactBackupPath is not null
            || previous.PreviousRuntimeManifestBackupPath is not null;
        if (expected != (actual is not null)
            || actual is not null && (!PathEquals(actual.GameDirectory, journal.GameDirectory)
                || actual.ProviderId != previous.ProviderId || actual.ReleaseChannelId != previous.ReleaseChannelId
                || actual.RuntimeDistributionId != previous.RuntimeDistributionId
                || actual.PreviousArtifactBackupPath != previous.PreviousArtifactBackupPath
                || actual.PreviousArtifactBackupIdentity != previous.PreviousArtifactBackupIdentity
                || actual.PreviousRuntimeManifestBackupPath != previous.PreviousRuntimeManifestBackupPath
                || actual.PreviousRuntimeManifestBackupIdentity != previous.PreviousRuntimeManifestBackupIdentity))
            return "Committed replacement preserved an inconsistent older adoption-backup receipt; recovery is required.";
        var failure = ValidateDeclaredBackup(previous.PreviousArtifactBackupPath,
            previous.PreviousArtifactBackupIdentity, "older adopted DLL");
        return failure ?? ValidateDeclaredBackup(previous.PreviousRuntimeManifestBackupPath,
            previous.PreviousRuntimeManifestBackupIdentity, "older adopted runtime manifest");
    }

    private static bool MatchesBackupReceiptUpgrade(ModInstalledArtifactState original, ModInstalledArtifactState resolved) =>
        (original.PreviousArtifactBackupIdentity is null || original.PreviousArtifactBackupIdentity == resolved.PreviousArtifactBackupIdentity)
        && (original.PreviousRuntimeManifestBackupIdentity is null || original.PreviousRuntimeManifestBackupIdentity == resolved.PreviousRuntimeManifestBackupIdentity)
        && JsonSerializer.Serialize(original with
        {
            PreviousArtifactBackupIdentity = resolved.PreviousArtifactBackupIdentity,
            PreviousRuntimeManifestBackupIdentity = resolved.PreviousRuntimeManifestBackupIdentity,
        }, JsonOptions) == JsonSerializer.Serialize(resolved, JsonOptions);

    private void RestoreReplacementInstalledState(
        string gameDirectory, ModInstalledArtifactState previous, string transactionId,
        ModInstalledArtifactState? reviewedPreviousInstalledState)
    {
        if (!PathEquals(gameDirectory, previous.GameDirectory))
            throw new InvalidDataException("The replacement rollback receipt belongs to another installation.");
        var registry = ReadInstalledRegistry();
        var detached = (registry.DetachedAdoptionBackups ?? []).ToArray();
        var added = detached.SingleOrDefault(backup => backup.DetachmentId == transactionId);
        if (added is not null && (!PathEquals(added.GameDirectory, gameDirectory)
            || added.PreviousArtifactBackupPath != previous.PreviousArtifactBackupPath
            || added.PreviousArtifactBackupIdentity != previous.PreviousArtifactBackupIdentity
            || added.PreviousRuntimeManifestBackupPath != previous.PreviousRuntimeManifestBackupPath
            || added.PreviousRuntimeManifestBackupIdentity != previous.PreviousRuntimeManifestBackupIdentity))
            throw new InvalidDataException("The replacement rollback backup receipt changed.");
        WriteInstalledRegistry(registry with
        {
            Installations = registry.Installations.Where(state => !PathEquals(state.GameDirectory, gameDirectory))
                .Append(reviewedPreviousInstalledState ?? previous).ToArray(),
            DetachedAdoptionBackups = detached.Where(backup => backup.DetachmentId != transactionId).ToArray(),
        });
    }
}
