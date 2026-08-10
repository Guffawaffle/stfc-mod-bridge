using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

internal enum BattleLifecycleTerminalRecoveryState
{
    NoOperation,
    Recovered,
    Blocked,
    Unavailable,
}

internal sealed record BattleLifecycleTerminalRecoveryResult(
    BattleLifecycleTerminalRecoveryState State,
    string Code,
    bool RequiresSessionRecomposition = false);

internal enum BattleLifecycleCleanupCheckpoint
{
    CandidatesDeleted,
    RuntimeLockDeleted,
    MarkerDeleting,
}

internal sealed class BattleLifecycleTerminalRecoveryCoordinator
{
    private readonly string stateRoot;
    private readonly BattleLifecycleCommitCoordinator commitCoordinator;
    private readonly TimeProvider timeProvider;
    private readonly Func<BattleLifecycleCleanupCheckpoint, ValueTask>? checkpoint;

    public BattleLifecycleTerminalRecoveryCoordinator(
        string stateRoot,
        BattleLifecycleCommitCoordinator commitCoordinator,
        TimeProvider? timeProvider = null,
        Func<BattleLifecycleCleanupCheckpoint, ValueTask>? checkpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        this.stateRoot = Path.GetFullPath(stateRoot);
        this.commitCoordinator = commitCoordinator
            ?? throw new ArgumentNullException(nameof(commitCoordinator));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.checkpoint = checkpoint;
    }

    public async Task<BattleLifecycleTerminalRecoveryResult> RecoverAsync(
        LauncherOperationLease operationLease,
        BattleLifecycleJournalStore journal,
        ModInstallationEvidence installation,
        ModInstalledArtifactState installedState,
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(installedState);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        using var operationScope = operationLease.RetainFor(stateRoot);
        try
        {
            var inspection = await journal.RecoverAsync(operationLease, cancellationToken)
                .ConfigureAwait(false);
            if (inspection.State == BattleLifecycleJournalState.Absent)
            {
                return new(
                    BattleLifecycleTerminalRecoveryState.NoOperation,
                    "battle-terminal-recovery-absent");
            }
            if (inspection is not { State: BattleLifecycleJournalState.Readable, Marker: not null })
            {
                return Blocked();
            }

            if (inspection.Marker.Stage == BattleLifecycleStage.CommitStarted)
            {
                var resumed = await commitCoordinator.RecoverCommitStartedAsync(
                    operationLease,
                    journal,
                    installation,
                    installedState,
                    configurationPath,
                    cancellationToken).ConfigureAwait(false);
                if (resumed.State != BattleLifecycleCommitState.Succeeded)
                {
                    return resumed.State == BattleLifecycleCommitState.Blocked
                        ? Blocked()
                        : Unavailable();
                }
                inspection = journal.Inspect();
            }

            if (inspection is not
                {
                    State: BattleLifecycleJournalState.Readable,
                    Marker.Stage: BattleLifecycleStage.CommitVerified or BattleLifecycleStage.CleanupPending,
                })
            {
                return Blocked();
            }

            var verified = await commitCoordinator.VerifyCommittedAsync(
                operationLease,
                journal,
                installation,
                installedState,
                configurationPath,
                cancellationToken).ConfigureAwait(false);
            if (verified.State != BattleLifecycleCommitState.Succeeded)
            {
                return verified.State == BattleLifecycleCommitState.Blocked
                    ? Blocked()
                    : Unavailable();
            }

            var marker = inspection.Marker;
            if (!journal.VerifyRuntimeLockIdentity(
                    marker,
                    allowAbsent: marker.Stage == BattleLifecycleStage.CleanupPending))
            {
                return Blocked();
            }
            if (marker.Stage == BattleLifecycleStage.CommitVerified)
            {
                marker = marker with
                {
                    Stage = BattleLifecycleStage.CleanupPending,
                    UpdatedAtUtc = NextTimestamp(marker),
                };
                await journal.AdvanceAsync(operationLease, marker, cancellationToken).ConfigureAwait(false);
            }

            verified = await commitCoordinator.VerifyCommittedAsync(
                operationLease,
                journal,
                installation,
                installedState,
                configurationPath,
                cancellationToken).ConfigureAwait(false);
            if (verified.State != BattleLifecycleCommitState.Succeeded)
            {
                return verified.State == BattleLifecycleCommitState.Blocked
                    ? Blocked()
                    : Unavailable();
            }

            await journal.DeleteCommittedArtifactsAsync(
                    operationLease,
                    marker,
                    checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            return new(
                BattleLifecycleTerminalRecoveryState.Recovered,
                "battle-terminal-recovery-complete",
                RequiresSessionRecomposition: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException or CryptographicException
                or ArgumentException or JsonException)
        {
            return Blocked();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
        {
            return Unavailable();
        }
    }

    private DateTimeOffset NextTimestamp(BattleLifecycleMarker marker)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        return now < marker.UpdatedAtUtc ? marker.UpdatedAtUtc : now;
    }

    private static BattleLifecycleTerminalRecoveryResult Blocked() => new(
        BattleLifecycleTerminalRecoveryState.Blocked,
        "battle-terminal-recovery-blocked");

    private static BattleLifecycleTerminalRecoveryResult Unavailable() => new(
        BattleLifecycleTerminalRecoveryState.Unavailable,
        "battle-terminal-recovery-unavailable");
}
