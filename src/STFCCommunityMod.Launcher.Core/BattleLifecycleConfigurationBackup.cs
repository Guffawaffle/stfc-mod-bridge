using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

internal enum BattleLifecycleBackupState
{
    Succeeded,
    Blocked,
    Unavailable,
}

internal sealed record BattleLifecycleBackupResult(
    BattleLifecycleBackupState State,
    string Code,
    BattleLifecycleMarker? Marker = null,
    ConfigurationBackupReceipt? Receipt = null);

internal sealed class BattleLifecycleConfigurationBackupCoordinator(
    string stateRoot,
    ProviderScopedConfigurationBackupStore backupStore,
    TimeProvider? timeProvider = null)
{
    private const string BackupReason = "battle-first-activation";
    private readonly string stateRoot = Path.GetFullPath(stateRoot);
    private readonly ProviderScopedConfigurationBackupStore backupStore =
        backupStore ?? throw new ArgumentNullException(nameof(backupStore));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<BattleLifecycleBackupResult> PrepareVerifiedBackupAsync(
        LauncherOperationLease operationLease,
        BattleLifecycleJournalStore journal,
        ModInstallationEvidence installation,
        ModInstalledArtifactState installedState,
        ConfigurationDocumentSnapshot configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(installedState);
        ArgumentNullException.ThrowIfNull(configuration);
        using var operationScope = operationLease.RetainFor(stateRoot);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gameDirectory = Path.GetDirectoryName(configuration.Path);
            var inspection = journal.Inspect();
            if (inspection is not
                {
                    State: BattleLifecycleJournalState.Readable,
                    Marker.Stage: BattleLifecycleStage.Prepared or BattleLifecycleStage.Quiesced,
                }
                || inspection.Marker.SharedTargetBefore
                || inspection.Marker.Configuration is not { } binding
                || installation.State != ModInstallationEvidenceState.ManagedVerified
                || installation.IsGameRunning
                || !installation.HasCompleteAttribution
                || !IsStableId(installation.InstalledProviderId)
                || !IsStableId(installation.InstalledReleaseChannelId)
                || !IsStableId(installation.InstalledRuntimeDistributionId)
                || !IsSafeVersion(installation.InstalledVersion)
                || !IsSha256(installation.InstalledSha256)
                || string.IsNullOrWhiteSpace(gameDirectory)
                || !MatchesInstalledState(installation, installedState, gameDirectory)
                || BattleLifecycleJournalStore.PathIdentity(configuration.Path) != binding.SourcePathSha256)
            {
                return Blocked();
            }
            var providerId = installation.InstalledProviderId!;
            var releaseChannelId = installation.InstalledReleaseChannelId!;
            var runtimeDistributionId = installation.InstalledRuntimeDistributionId!;
            var installedVersion = installation.InstalledVersion!;
            var installedSha256 = installation.InstalledSha256!;

            var source = configuration.Contents;
            try
            {
                if (source.LongLength != binding.SourceByteCount
                    || Hash(source) != binding.SourceSha256)
                {
                    return Blocked();
                }
                var quiesced = inspection.Marker;
                if (quiesced.Stage == BattleLifecycleStage.Prepared)
                {
                    quiesced = quiesced with
                    {
                        Stage = BattleLifecycleStage.Quiesced,
                        UpdatedAtUtc = timeProvider.GetUtcNow().ToUniversalTime(),
                    };
                    await journal.AdvanceAsync(operationLease, quiesced, cancellationToken)
                        .ConfigureAwait(false);
                }

                var receipt = await backupStore.CreateAsync(
                    new(
                        gameDirectory,
                        providerId,
                        configuration.Path,
                        source,
                        BackupReason,
                        ReleaseIdentity:
                            $"{releaseChannelId}/"
                            + $"{runtimeDistributionId}/"
                            + $"{installedVersion}/"
                            + installedSha256),
                    cancellationToken).ConfigureAwait(false);
                var verified = backupStore.Read(
                    gameDirectory,
                    providerId,
                    receipt.BackupId);
                try
                {
                    if (!verified.AsSpan().SequenceEqual(source)
                        || !string.Equals(
                            receipt.ContentSha256,
                            Convert.ToHexString(SHA256.HashData(source)),
                            StringComparison.Ordinal))
                    {
                        return Blocked();
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(verified);
                }

                var backupVerified = quiesced with
                {
                    Stage = BattleLifecycleStage.BackupVerified,
                    UpdatedAtUtc = timeProvider.GetUtcNow().ToUniversalTime(),
                    Configuration = quiesced.Configuration! with
                    {
                        BackupId = receipt.BackupId,
                        BackupContentSha256 = receipt.ContentSha256.ToLowerInvariant(),
                    },
                };
                await journal.AdvanceAsync(operationLease, backupVerified, cancellationToken)
                    .ConfigureAwait(false);
                return new(
                    BattleLifecycleBackupState.Succeeded,
                    "battle-backup-verified",
                    backupVerified,
                    receipt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(source);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or CryptographicException or ArgumentException)
        {
            return Blocked();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(BattleLifecycleBackupState.Unavailable, "battle-backup-unavailable");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private static bool IsStableId(string? value) =>
        value is { Length: > 0 and <= 64 }
        && char.IsAsciiLetterOrDigit(value[0])
        && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsSafeVersion(string? value) =>
        value is { Length: > 0 and <= 64 }
        && value.All(character => !char.IsControl(character));

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

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static BattleLifecycleBackupResult Blocked() =>
        new(BattleLifecycleBackupState.Blocked, "battle-backup-blocked");
}
