using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

internal enum BattleLifecycleCommitState
{
    Succeeded,
    Compensated,
    Blocked,
    Unavailable,
}

internal enum BattleLifecycleCommitCheckpoint
{
    CredentialPromoted,
    ConfigurationPromoted,
    PreferencesPromoted,
    AuthoritativeStateVerified,
}

internal sealed record BattleLifecycleCommitResult(
    BattleLifecycleCommitState State,
    string Code,
    BattleLifecycleMarker? Marker = null);

internal sealed class BattleLifecycleCommitCoordinator
{
    private readonly string stateRoot;
    private readonly BattleIngestCredentialStore credentialStore;
    private readonly ProviderScopedConfigurationBackupStore backupStore;
    private readonly ILauncherBattlePreferencesCommitter preferencesCommitter;
    private readonly AtomicTomlStore configurationStore;
    private readonly TimeProvider timeProvider;
    private readonly Func<BattleLifecycleCommitCheckpoint, ValueTask>? checkpoint;

    public BattleLifecycleCommitCoordinator(
        string stateRoot,
        BattleIngestCredentialStore credentialStore,
        ProviderScopedConfigurationBackupStore backupStore,
        ILauncherUiPreferencesStore preferencesStore,
        AtomicTomlStore? configurationStore = null,
        TimeProvider? timeProvider = null,
        Func<BattleLifecycleCommitCheckpoint, ValueTask>? checkpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        this.stateRoot = Path.GetFullPath(stateRoot);
        this.credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        this.backupStore = backupStore ?? throw new ArgumentNullException(nameof(backupStore));
        ArgumentNullException.ThrowIfNull(preferencesStore);
        preferencesCommitter = preferencesStore as ILauncherBattlePreferencesCommitter
            ?? throw new ArgumentException(
                "The Battle lifecycle requires the existing preference compare-and-swap owner.",
                nameof(preferencesStore));
        this.configurationStore = configurationStore ?? new AtomicTomlStore(retainAdjacentBackup: false);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.checkpoint = checkpoint;
    }

    public async Task<BattleLifecycleCommitResult> CommitAsync(
        LauncherOperationLease operationLease,
        BattleLifecycleJournalStore journal,
        BattleLifecyclePreparedActivation prepared,
        ModInstallationEvidence installation,
        ModInstalledArtifactState installedState,
        ConfigurationDocumentSnapshot configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(configuration);
        return await CommitExactAsync(
            operationLease,
            journal,
            prepared.Marker.OperationId,
            prepared.RuntimeLease.Record.OwnerId,
            prepared.ProtectedCredentialCandidate,
            prepared.ConfigurationCandidate,
            installation,
            installedState,
            configuration.Path,
            configuration.Contents,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<BattleLifecycleCommitResult> RecoverCommitStartedAsync(
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

        BattleLifecycleJournalInspection recovered;
        try
        {
            recovered = await journal.RecoverAsync(operationLease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException or CryptographicException
                or ArgumentException or JsonException)
        {
            return Blocked();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Unavailable();
        }

        if (recovered is not
            {
                State: BattleLifecycleJournalState.Readable,
                Marker.Stage: BattleLifecycleStage.CommitStarted,
            }
            || recovered.Marker.Configuration is not { BackupId: not null } binding
            || recovered.Marker.Credential is not { } credential)
        {
            return Blocked();
        }

        var marker = recovered.Marker;
        var gameDirectory = Path.GetDirectoryName(Path.GetFullPath(configurationPath));
        if (string.IsNullOrWhiteSpace(gameDirectory)
            || installation.State != ModInstallationEvidenceState.ManagedVerified
            || installation.IsGameRunning
            || !installation.HasCompleteAttribution
            || !MatchesInstalledState(installation, installedState, gameDirectory)
            || BattleLifecycleJournalStore.PathIdentity(configurationPath) != binding.SourcePathSha256)
        {
            return Blocked();
        }

        try
        {
            if (!journal.VerifyRuntimeLockIdentity(marker, allowAbsent: false)) return Blocked();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException or CryptographicException
                or ArgumentException or JsonException)
        {
            return Blocked();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Unavailable();
        }

        byte[]? source = null;
        byte[]? configurationCandidate = null;
        byte[]? protectedCredential = null;
        try
        {
            try
            {
                source = backupStore.Read(
                    gameDirectory,
                    installation.InstalledProviderId!,
                    binding.BackupId!);
                configurationCandidate = journal.ReadExactCandidate(
                    marker,
                    binding.CandidateRelativePath,
                    new(binding.CandidateByteCount, binding.CandidateSha256));
                var credentialResource = marker.Resources.Single(resource => resource.Role == "ingest-credential");
                protectedCredential = journal.ReadExactCandidate(
                    marker,
                    credentialResource.CandidateRelativePath!,
                    new(credential.ProtectedByteCount, credential.ProtectedSha256));
            }
            catch (Exception exception) when (
                exception is InvalidDataException or CryptographicException or ArgumentException or JsonException)
            {
                return Blocked();
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                return Unavailable();
            }
            return await CommitExactAsync(
                operationLease,
                journal,
                marker.OperationId,
                marker.OwnerId,
                protectedCredential,
                configurationCandidate,
                installation,
                installedState,
                configurationPath,
                source,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (source is not null) CryptographicOperations.ZeroMemory(source);
            if (configurationCandidate is not null) CryptographicOperations.ZeroMemory(configurationCandidate);
            if (protectedCredential is not null) CryptographicOperations.ZeroMemory(protectedCredential);
        }
    }

    internal async Task<BattleLifecycleCommitResult> VerifyCommittedAsync(
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
            var inspection = journal.Inspect();
            if (inspection is not
                {
                    State: BattleLifecycleJournalState.Readable,
                    Marker.Stage: BattleLifecycleStage.CommitVerified or BattleLifecycleStage.CleanupPending,
                }
                || inspection.Marker.Configuration is not
                {
                    BackupId: not null,
                    BackupContentSha256: not null,
                } binding
                || inspection.Marker.Credential is not { } credential
                || !CredentialPathMatchesMarker(inspection.Marker))
            {
                return Blocked();
            }
            var gameDirectory = Path.GetDirectoryName(Path.GetFullPath(configurationPath));
            if (string.IsNullOrWhiteSpace(gameDirectory)
                || installation.State != ModInstallationEvidenceState.ManagedVerified
                || installation.IsGameRunning
                || !installation.HasCompleteAttribution
                || !MatchesInstalledState(installation, installedState, gameDirectory)
                || BattleLifecycleJournalStore.PathIdentity(configurationPath) != binding.SourcePathSha256)
            {
                return Blocked();
            }

            var backup = backupStore.Read(
                gameDirectory,
                installation.InstalledProviderId!,
                binding.BackupId!);
            try
            {
                if (!Matches(backup, binding.SourceByteCount, binding.BackupContentSha256!))
                {
                    return Blocked();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(backup);
            }

            var credentialState = credentialStore.InspectProtectedIdentity(new(
                credential.ProtectedByteCount,
                credential.ProtectedSha256));
            if (credentialState == BattleCredentialProtectedState.Unavailable)
            {
                return Unavailable();
            }
            if (credentialState != BattleCredentialProtectedState.Match
                || !await MatchesFileIdentityAsync(
                    configurationPath,
                    binding.CandidateByteCount,
                    binding.CandidateSha256,
                    cancellationToken).ConfigureAwait(false)
                || !preferencesCommitter.TryLoadBattlePreferences(out var current)
                || current != Preferences(inspection.Marker, before: false))
            {
                return Blocked();
            }
            return new(BattleLifecycleCommitState.Succeeded, "battle-commit-state-verified", inspection.Marker);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or CryptographicException or ArgumentException or JsonException)
        {
            return Blocked();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Unavailable();
        }
    }

    private async Task<BattleLifecycleCommitResult> CommitExactAsync(
        LauncherOperationLease operationLease,
        BattleLifecycleJournalStore journal,
        string expectedOperationId,
        string expectedOwnerId,
        ReadOnlyMemory<byte> protectedCredentialCandidate,
        ReadOnlyMemory<byte> configurationCandidate,
        ModInstallationEvidence installation,
        ModInstalledArtifactState installedState,
        string configurationPath,
        ReadOnlyMemory<byte> sourceConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerId);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(installedState);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        using var operationScope = operationLease.RetainFor(stateRoot);

        var source = sourceConfiguration.ToArray();
        var candidate = configurationCandidate.ToArray();
        var protectedCredential = protectedCredentialCandidate.ToArray();
        try
        {
            var inspection = journal.Inspect();
            var gameDirectory = Path.GetDirectoryName(configurationPath);
            if (inspection is not
                {
                    State: BattleLifecycleJournalState.Readable,
                    Marker.Stage: BattleLifecycleStage.BackupVerified or BattleLifecycleStage.CommitStarted,
                }
                || inspection.Marker.OperationId != expectedOperationId
                || inspection.Marker.OwnerId != expectedOwnerId
                || inspection.Marker.Configuration is not { } binding
                || inspection.Marker.Credential is not { } credential
                || binding.BackupId is null
                || binding.BackupContentSha256 is null
                || installation.State != ModInstallationEvidenceState.ManagedVerified
                || installation.IsGameRunning
                || !installation.HasCompleteAttribution
                || string.IsNullOrWhiteSpace(gameDirectory)
                || !MatchesInstalledState(installation, installedState, gameDirectory)
                || BattleLifecycleJournalStore.PathIdentity(configurationPath) != binding.SourcePathSha256
                || !Matches(source, binding.SourceByteCount, binding.SourceSha256)
                || !Matches(candidate, binding.CandidateByteCount, binding.CandidateSha256)
                || !Matches(
                    protectedCredential,
                    credential.ProtectedByteCount,
                    credential.ProtectedSha256)
                || !CredentialPathMatchesMarker(inspection.Marker))
            {
                return Blocked();
            }

            byte[] backup;
            try
            {
                backup = backupStore.Read(
                    gameDirectory,
                    installation.InstalledProviderId!,
                    binding.BackupId);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or CryptographicException or ArgumentException or JsonException)
            {
                return Blocked();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Unavailable();
            }
            try
            {
                if (!backup.AsSpan().SequenceEqual(source)
                    || !Matches(backup, binding.SourceByteCount, binding.BackupContentSha256))
                {
                    return Blocked();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(backup);
            }

            var before = Preferences(inspection.Marker, before: true);
            var after = Preferences(inspection.Marker, before: false);
            if (!preferencesCommitter.TryLoadBattlePreferences(out var currentPreferences)
                || currentPreferences != before && currentPreferences != after)
            {
                return Blocked();
            }

            var credentialIdentity = new BattleLifecycleFileIdentity(
                credential.ProtectedByteCount,
                credential.ProtectedSha256);
            var credentialState = credentialStore.InspectProtectedIdentity(credentialIdentity);
            var configurationState = await ReadAuthoritativeStateAsync(
                configurationPath,
                source,
                candidate,
                cancellationToken).ConfigureAwait(false);
            if (credentialState == BattleCredentialProtectedState.Unavailable
                || configurationState == AuthoritativeState.Unavailable)
            {
                return Unavailable();
            }
            if (credentialState == BattleCredentialProtectedState.Foreign
                || configurationState == AuthoritativeState.Foreign
                || inspection.Marker.Stage == BattleLifecycleStage.BackupVerified
                    && (credentialState != BattleCredentialProtectedState.Absent
                        || configurationState != AuthoritativeState.Before
                        || currentPreferences != before))
            {
                return Blocked();
            }

            var marker = inspection.Marker;
            if (marker.Stage == BattleLifecycleStage.BackupVerified)
            {
                marker = marker with
                {
                    Stage = BattleLifecycleStage.CommitStarted,
                    UpdatedAtUtc = NextTimestamp(marker),
                };
                try
                {
                    await journal.AdvanceAsync(operationLease, marker, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsUnavailable(exception))
                {
                    return Unavailable();
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or CryptographicException or ArgumentException)
                {
                    return Blocked();
                }
            }

            BattleCredentialPromotionLease? credentialPromotion = null;
            var configurationChanged = false;
            var preferencesChanged = false;
            var phase = "credential";
            try
            {
                credentialState = credentialStore.InspectProtectedIdentity(credentialIdentity);
                if (credentialState == BattleCredentialProtectedState.Absent)
                {
                    credentialPromotion = await credentialStore.CreateNewAsync(
                        protectedCredential,
                        credentialIdentity,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (credentialState == BattleCredentialProtectedState.Unavailable)
                {
                    throw new IOException();
                }
                else if (credentialState != BattleCredentialProtectedState.Match)
                {
                    throw new InvalidDataException();
                }
                await CheckpointAsync(BattleLifecycleCommitCheckpoint.CredentialPromoted)
                    .ConfigureAwait(false);

                phase = "configuration";
                configurationState = await ReadAuthoritativeStateAsync(
                    configurationPath,
                    source,
                    candidate,
                    cancellationToken).ConfigureAwait(false);
                if (configurationState == AuthoritativeState.Before)
                {
                    var write = await configurationStore.SaveDocumentAsync(
                        configurationPath,
                        source,
                        candidate,
                        cancellationToken).ConfigureAwait(false);
                    if (write.State != AtomicTomlWriteState.Succeeded) throw new InvalidDataException();
                    configurationChanged = true;
                }
                else if (configurationState != AuthoritativeState.After)
                {
                    throw new InvalidDataException();
                }
                await CheckpointAsync(BattleLifecycleCommitCheckpoint.ConfigurationPromoted)
                    .ConfigureAwait(false);

                phase = "preferences";
                if (!preferencesCommitter.TryLoadBattlePreferences(out currentPreferences))
                {
                    throw new InvalidDataException();
                }
                if (currentPreferences == before)
                {
                    if (!preferencesCommitter.TrySaveBattlePreferences(before, after))
                    {
                        throw new InvalidDataException();
                    }
                    preferencesChanged = true;
                }
                else if (currentPreferences != after)
                {
                    throw new InvalidDataException();
                }
                await CheckpointAsync(BattleLifecycleCommitCheckpoint.PreferencesPromoted)
                    .ConfigureAwait(false);

                phase = "credential-verification";
                if (!(credentialPromotion?.Matches(credentialIdentity)
                        ?? credentialStore.MatchesProtectedIdentity(credentialIdentity)))
                {
                    throw new InvalidDataException();
                }
                phase = "configuration-verification";
                if (await ReadAuthoritativeStateAsync(
                        configurationPath,
                        source,
                        candidate,
                        CancellationToken.None).ConfigureAwait(false) != AuthoritativeState.After)
                {
                    throw new InvalidDataException();
                }
                phase = "preferences-verification";
                if (!preferencesCommitter.TryLoadBattlePreferences(out currentPreferences)
                    || currentPreferences != after)
                {
                    throw new InvalidDataException();
                }
                await CheckpointAsync(BattleLifecycleCommitCheckpoint.AuthoritativeStateVerified)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException
                    or IOException
                    or UnauthorizedAccessException
                    or Win32Exception
                    or InvalidDataException
                    or CryptographicException
                    or NotSupportedException)
            {
                var compensated = await CompensateAsync(
                    credentialPromotion,
                    configurationChanged,
                    preferencesChanged,
                    configurationPath,
                    source,
                    candidate,
                    credentialIdentity,
                    before,
                    after).ConfigureAwait(false);
                if (exception is OperationCanceledException && compensated)
                {
                    throw;
                }
                return compensated
                    ? new(BattleLifecycleCommitState.Compensated, $"battle-commit-compensated-{phase}", marker)
                    : Unavailable(marker);
            }

            var committed = marker with
            {
                Stage = BattleLifecycleStage.CommitVerified,
                UpdatedAtUtc = NextTimestamp(marker),
            };
            var promotionReleased = false;
            try
            {
                await journal.AdvanceAsync(operationLease, committed, cancellationToken)
                    .ConfigureAwait(false);
                if (credentialPromotion is not null)
                {
                    promotionReleased = true;
                    await credentialPromotion.CommitAsync().ConfigureAwait(false);
                }
                return new(BattleLifecycleCommitState.Succeeded, "battle-commit-verified", committed);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException or UnauthorizedAccessException
                    or Win32Exception or InvalidDataException or CryptographicException or NotSupportedException)
            {
                if (credentialPromotion is not null && !promotionReleased)
                {
                    promotionReleased = true;
                    await credentialPromotion.CommitAsync().ConfigureAwait(false);
                }
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                return Unavailable(marker);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
            CryptographicOperations.ZeroMemory(candidate);
            CryptographicOperations.ZeroMemory(protectedCredential);
        }
    }

    private async Task<bool> CompensateAsync(
        BattleCredentialPromotionLease? credentialPromotion,
        bool configurationChanged,
        bool preferencesChanged,
        string configurationPath,
        byte[] source,
        byte[] candidate,
        BattleLifecycleFileIdentity credentialIdentity,
        LauncherBattlePreferences before,
        LauncherBattlePreferences after)
    {
        var credentialNeedsPreservation = credentialPromotion is not null;
        try
        {
            if (preferencesChanged && !preferencesCommitter.TrySaveBattlePreferences(after, before))
            {
                credentialNeedsPreservation = false;
                await PreserveCredentialAsync(credentialPromotion).ConfigureAwait(false);
                return false;
            }
            if (configurationChanged)
            {
                var rollback = await configurationStore.SaveDocumentAsync(
                    configurationPath,
                    candidate,
                    source,
                    CancellationToken.None).ConfigureAwait(false);
                if (rollback.State != AtomicTomlWriteState.Succeeded)
                {
                    credentialNeedsPreservation = false;
                    await PreserveCredentialAsync(credentialPromotion).ConfigureAwait(false);
                    return false;
                }
            }
            if (credentialPromotion is not null)
            {
                try
                {
                    await credentialPromotion.RollbackAsync().ConfigureAwait(false);
                    credentialNeedsPreservation = false;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
                {
                    credentialNeedsPreservation = false;
                    await PreserveCredentialAsync(credentialPromotion).ConfigureAwait(false);
                    return false;
                }
            }
            return credentialStore.InspectProtectedIdentity(credentialIdentity)
                == BattleCredentialProtectedState.Absent
                && await ReadAuthoritativeStateAsync(
                    configurationPath,
                    source,
                    candidate,
                    CancellationToken.None).ConfigureAwait(false) == AuthoritativeState.Before
                && preferencesCommitter.TryLoadBattlePreferences(out var current)
                && current == before;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception
                or InvalidDataException or CryptographicException or NotSupportedException)
        {
            if (credentialNeedsPreservation)
            {
                try
                {
                    await PreserveCredentialAsync(credentialPromotion).ConfigureAwait(false);
                }
                catch (Exception preserveException) when (
                    preserveException is IOException or UnauthorizedAccessException or Win32Exception
                        or NotSupportedException)
                {
                }
            }
            return false;
        }
    }

    private static async ValueTask PreserveCredentialAsync(
        BattleCredentialPromotionLease? credentialPromotion)
    {
        if (credentialPromotion is not null)
        {
            await credentialPromotion.CommitAsync().ConfigureAwait(false);
        }
    }

    private bool CredentialPathMatchesMarker(BattleLifecycleMarker marker)
    {
        var resource = marker.Resources.SingleOrDefault(item => item.Role == "ingest-credential");
        return resource is
        {
            Before: null,
            After: not null,
            PrimaryRelativePath: var relativePath,
        }
            && PathEquals(credentialStore.Path, Path.Combine(
                stateRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private async ValueTask CheckpointAsync(BattleLifecycleCommitCheckpoint value)
    {
        if (checkpoint is not null) await checkpoint(value).ConfigureAwait(false);
    }

    private static LauncherBattlePreferences Preferences(BattleLifecycleMarker marker, bool before)
    {
        var values = marker.FeatureTransitions.ToDictionary(
            item => item.FeatureId,
            item => before ? item.Before : item.After,
            StringComparer.Ordinal);
        return new(
            values[LauncherFeatureIds.BattleCollection],
            values[LauncherFeatureIds.FleetCollection]);
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, long expectedCount, string expectedSha256) =>
        bytes.Length == expectedCount
        && string.Equals(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            expectedSha256,
            StringComparison.Ordinal);

    private static async Task<AuthoritativeState> ReadAuthoritativeStateAsync(
        string path,
        byte[] before,
        byte[] after,
        CancellationToken cancellationToken)
    {
        byte[] current;
        try
        {
            current = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return AuthoritativeState.Foreign;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return AuthoritativeState.Unavailable;
        }
        try
        {
            if (current.AsSpan().SequenceEqual(before)) return AuthoritativeState.Before;
            if (current.AsSpan().SequenceEqual(after)) return AuthoritativeState.After;
            return AuthoritativeState.Foreign;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    private static async Task<bool> MatchesFileIdentityAsync(
        string path,
        long expectedCount,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedCount is < 0 or > 8 * 1024 * 1024) return false;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return stream.Length == expectedCount
                && string.Equals(
                    Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                        .ToLowerInvariant(),
                    expectedSha256,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool MatchesInstalledState(
        ModInstallationEvidence evidence,
        ModInstalledArtifactState state,
        string gameDirectory) =>
        PathEquals(state.GameDirectory, gameDirectory)
        && state.ProviderId == evidence.InstalledProviderId
        && state.ReleaseChannelId == evidence.InstalledReleaseChannelId
        && state.RuntimeDistributionId == evidence.InstalledRuntimeDistributionId
        && state.Version == evidence.InstalledVersion
        && string.Equals(state.Sha256, evidence.InstalledSha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnavailable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException;

    private DateTimeOffset NextTimestamp(BattleLifecycleMarker marker)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        return now < marker.UpdatedAtUtc ? marker.UpdatedAtUtc : now;
    }

    private static BattleLifecycleCommitResult Blocked() =>
        new(BattleLifecycleCommitState.Blocked, "battle-commit-blocked");

    private static BattleLifecycleCommitResult Unavailable(BattleLifecycleMarker? marker = null) =>
        new(BattleLifecycleCommitState.Unavailable, "battle-commit-unavailable", marker);

    private enum AuthoritativeState
    {
        Before,
        After,
        Foreign,
        Unavailable,
    }
}
