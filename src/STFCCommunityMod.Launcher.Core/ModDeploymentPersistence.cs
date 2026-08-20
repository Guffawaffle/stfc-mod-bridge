using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed partial class ModDeploymentService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static bool IsStateReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidDataException
            or OverflowException;

    private void ValidatePersistedJournal(ModDeploymentJournal journal)
    {
        if (journal is null
            || journal.Artifact is null
            || journal.TransactionId is null
            || journal.GameDirectory is null
            || journal.StagePath is null
            || journal.SameVolumeBackupPath is null
            || journal.DurableBackupPath is null
            || journal.SchemaVersion is not 1 and not DeploymentJournalSchemaVersion
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !Enum.IsDefined(journal.Operation)
            || !Enum.IsDefined(journal.Phase)
            || !Path.IsPathFullyQualified(journal.GameDirectory))
        {
            throw new InvalidDataException("The deployment journal identity is invalid or unsupported.");
        }

        var gameDirectory = Path.GetFullPath(journal.GameDirectory);
        var expectedStagePath = Path.Combine(
            gameDirectory,
            $".{ManagedFileName}.{journal.TransactionId}.stage");
        var expectedSameVolumeBackupPath = Path.Combine(
            gameDirectory,
            $".{ManagedFileName}.{journal.TransactionId}.rollback");
        var expectedDurableBackupPath = Path.Combine(
            stateDirectory,
            "rollback",
            journal.TransactionId,
            ManagedFileName);
        if (!PathEquals(journal.StagePath, expectedStagePath)
            || !PathEquals(journal.SameVolumeBackupPath, expectedSameVolumeBackupPath)
            || !PathEquals(journal.DurableBackupPath, expectedDurableBackupPath)
            || !string.Equals(journal.Artifact.FileName, ManagedFileName, StringComparison.OrdinalIgnoreCase)
            || journal.Artifact.Size <= 0
            || journal.Artifact.Size > MaximumArtifactSize
            || string.IsNullOrWhiteSpace(journal.Artifact.ExpectedVersion)
            || journal.Artifact.ExpectedProductVersion is not null
                && (journal.Artifact.ExpectedProductVersion.Length is <= 0 or > 160
                    || journal.Artifact.ExpectedProductVersion.Any(char.IsControl))
            || !TryNormalizeSha256(journal.Artifact.Sha256, out _))
        {
            throw new InvalidDataException("The deployment journal contains an unsafe artifact or recovery path.");
        }

        if (journal.PreviousInstalledState is not null)
        {
            ValidatePersistedInstalledState(journal.PreviousInstalledState);
            if (!PathEquals(journal.PreviousInstalledState.GameDirectory, gameDirectory))
            {
                throw new InvalidDataException(
                    "The deployment journal prior installed state belongs to another game directory.");
            }
        }
        if (journal.Artifact.RuntimeManifest is not null)
        {
            ValidateRuntimeManifestDiscovery(journal.Artifact.RuntimeManifest);
        }
        if (journal.ExistingArtifactIdentity is not null)
        {
            ValidateIdentityReceipt(journal.ExistingArtifactIdentity, "deployment journal prior DLL");
        }
        if (journal.ExistingRuntimeManifestIdentity is not null)
        {
            ValidateIdentityReceipt(
                journal.ExistingRuntimeManifestIdentity,
                "deployment journal prior runtime manifest");
        }
        if (journal.TargetArtifactFileIdentity is not null)
        {
            ValidateFileIdentityReceipt(
                journal.TargetArtifactFileIdentity,
                "deployment journal target DLL");
        }
        if (journal.TargetRuntimeManifestFileIdentity is not null)
        {
            ValidateFileIdentityReceipt(
                journal.TargetRuntimeManifestFileIdentity,
                "deployment journal target runtime manifest");
            if (journal.Artifact.RuntimeManifest is null)
            {
                throw new InvalidDataException(
                    "The deployment journal records a target runtime-manifest identity without an artifact.");
            }
        }
        if (journal.RestoredAdoptedArtifactFileIdentity is not null)
        {
            ValidateFileIdentityReceipt(
                journal.RestoredAdoptedArtifactFileIdentity,
                "deployment journal restored adopted DLL");
        }
        if (journal.RestoredAdoptedRuntimeManifestFileIdentity is not null)
        {
            ValidateFileIdentityReceipt(
                journal.RestoredAdoptedRuntimeManifestFileIdentity,
                "deployment journal restored adopted runtime manifest");
        }
        if (journal.SchemaVersion == 1
            && (journal.TargetArtifactFileIdentity is not null
                || journal.TargetRuntimeManifestFileIdentity is not null
                || journal.RestoredAdoptedArtifactFileIdentity is not null
                || journal.RestoredAdoptedRuntimeManifestFileIdentity is not null))
        {
            throw new InvalidDataException(
                "A legacy deployment journal cannot carry v2 file-identity receipts.");
        }
        if (journal.Operation == ModDeploymentOperation.Deploy
            && (journal.RestoredAdoptedArtifactFileIdentity is not null
                || journal.RestoredAdoptedRuntimeManifestFileIdentity is not null)
            || journal.Operation == ModDeploymentOperation.Uninstall
                && (journal.TargetArtifactFileIdentity is not null
                    || journal.TargetRuntimeManifestFileIdentity is not null))
        {
            throw new InvalidDataException(
                "The deployment journal file-identity receipts do not match its operation.");
        }
        if (journal.TargetInstallationAttribution is not null
            && (!IsStableIdentity(journal.TargetInstallationAttribution.ProviderId)
                || !IsStableIdentity(journal.TargetInstallationAttribution.ReleaseChannelId)
                || !IsStableIdentity(journal.TargetInstallationAttribution.RuntimeDistributionId)))
        {
            throw new InvalidDataException("The deployment journal target attribution is invalid.");
        }
    }

    private static void ValidateFileIdentityReceipt(ModFileIdentityReceipt receipt, string subject)
    {
        if (receipt.VolumeSerialNumber is not { Length: 8 }
            || !receipt.VolumeSerialNumber.All(Uri.IsHexDigit)
            || receipt.FileIndex is not { Length: 16 }
            || !receipt.FileIndex.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"The {subject} file-identity receipt is invalid.");
        }
    }

    private void ValidatePersistedInstalledState(ModInstalledArtifactState state)
    {
        if (state is null
            || state.GameDirectory is null
            || state.FileName is null
            || state.Version is null
            || state.Sha256 is null
            || state.SchemaVersion != InstalledReceiptSchemaVersion
            || !Path.IsPathFullyQualified(state.GameDirectory)
            || !string.Equals(state.FileName, ManagedFileName, StringComparison.OrdinalIgnoreCase)
            || state.Size <= 0
            || state.Size > MaximumArtifactSize
            || string.IsNullOrWhiteSpace(state.Version)
            || !IsValidReleaseProductVersion(state.ReleaseProductVersion)
            || !TryNormalizeSha256(state.Sha256, out _)
            || !IsStableIdentity(state.ProviderId)
            || !IsStableIdentity(state.ReleaseChannelId)
            || !IsStableIdentity(state.RuntimeDistributionId)
            || state.ReleaseHighWaterMarks is not null
                && (!AreValidReleaseHighWaterMarks(state.ReleaseHighWaterMarks)
                    || state.ReleaseHighWaterMarks.Any(mark =>
                        string.Equals(mark.ProviderId, state.ProviderId, StringComparison.Ordinal)
                        && string.Equals(
                            mark.ReleaseChannelId,
                            state.ReleaseChannelId,
                            StringComparison.Ordinal)
                        && string.Equals(
                            mark.RuntimeDistributionId,
                            state.RuntimeDistributionId,
                            StringComparison.Ordinal))))
        {
            throw new InvalidDataException("The installed-mod state is invalid or unsupported.");
        }

        if (!string.IsNullOrWhiteSpace(state.PreviousArtifactBackupPath)
            && (!Path.IsPathFullyQualified(state.PreviousArtifactBackupPath)
                || !IsContainedBy(Path.Combine(stateDirectory, "rollback"), state.PreviousArtifactBackupPath)))
        {
            throw new InvalidDataException("The installed-mod rollback path escapes Mod Bridge-owned state.");
        }
        if (state.PreviousArtifactBackupIdentity is not null)
        {
            ValidateIdentityReceipt(state.PreviousArtifactBackupIdentity, "installed-state prior DLL");
        }
        if (state.RuntimeManifest is not null
            && (state.RuntimeManifest.FileName != ArtifactBoundRuntimeManifestParser.ManagedFileName
                || state.RuntimeManifest.Size is <= 0 or > ArtifactBoundRuntimeManifestParser.MaximumManifestBytes
                || !TryNormalizeSha256(state.RuntimeManifest.Sha256, out _)
                || state.RuntimeManifest.SourceRevision is not { Length: 40 }
                || !state.RuntimeManifest.SourceRevision.All(Uri.IsHexDigit)
                || state.RuntimeManifest.Repository is not { Length: > 0 and <= 160 }
                || state.RuntimeManifest.Repository.Count(character => character == '/') != 1
                || state.RuntimeManifest.Tag is not { Length: > 0 and <= 160 }))
        {
            throw new InvalidDataException("The installed runtime-manifest state is invalid or unsupported.");
        }
        if (!string.IsNullOrWhiteSpace(state.PreviousRuntimeManifestBackupPath)
            && (!Path.IsPathFullyQualified(state.PreviousRuntimeManifestBackupPath)
                || !IsContainedBy(
                    Path.Combine(stateDirectory, "rollback"),
                    state.PreviousRuntimeManifestBackupPath)))
        {
            throw new InvalidDataException("The runtime-manifest rollback path escapes Mod Bridge-owned state.");
        }
        if (state.PreviousRuntimeManifestBackupIdentity is not null)
        {
            ValidateIdentityReceipt(
                state.PreviousRuntimeManifestBackupIdentity,
                "installed-state prior runtime manifest");
        }
    }

    private static void ValidateIdentityReceipt(ModArtifactIdentityReceipt? receipt, string subject)
    {
        if (receipt is null
            || receipt.Size <= 0
            || receipt.Size > MaximumArtifactSize
            || !TryNormalizeSha256(receipt.Sha256, out _)
            || receipt.Attributes.HasValue != receipt.LastWriteTimeUtcTicks.HasValue
            || receipt.Attributes is { } attributes
                && (attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint)
                    || attributes.HasFlag(FileAttributes.Device))
            || receipt.LastWriteTimeUtcTicks is { } ticks
                && (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks))
        {
            throw new InvalidDataException($"The {subject} identity receipt is invalid or missing.");
        }
    }

    private static bool AreValidReleaseHighWaterMarks(
        IReadOnlyList<ModReleaseHighWaterState> marks)
    {
        if (marks.Count > 32)
        {
            return false;
        }
        var identities = new HashSet<(string ProviderId, string ChannelId, string RuntimeId)>();
        foreach (var mark in marks)
        {
            if (mark is null
                || !IsStableIdentity(mark.ProviderId)
                || !IsStableIdentity(mark.ReleaseChannelId)
                || !IsStableIdentity(mark.RuntimeDistributionId)
                || string.IsNullOrWhiteSpace(mark.ReleaseProductVersion)
                || !IsValidReleaseProductVersion(mark.ReleaseProductVersion)
                || mark.AcceptedArtifactSize <= 0
                || mark.AcceptedArtifactSize > MaximumArtifactSize
                || !TryNormalizeSha256(mark.AcceptedArtifactSha256, out _)
                || !identities.Add((
                    mark.ProviderId,
                    mark.ReleaseChannelId,
                    mark.RuntimeDistributionId)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValidReleaseProductVersion(string? value)
    {
        if (value is null)
        {
            return true;
        }
        if (value.Length is <= 0 or > 160 || value.Any(char.IsControl))
        {
            return false;
        }
        try
        {
            _ = WindowsReleaseSelectionPolicy.ParseProductReleaseOrderingVersion(value);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or FormatException
                or OverflowException
                or ArgumentException)
        {
            return false;
        }
    }

    private ModInstalledArtifactRegistry ReadInstalledRegistry()
    {
        if (!File.Exists(InstalledStatePath))
        {
            return EmptyInstalledRegistry();
        }

        var contents = File.ReadAllBytes(InstalledStatePath);
        using var document = JsonDocument.Parse(contents);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The installed-mod state root is invalid.");
        }

        ModInstalledArtifactRegistry registry;
        if (document.RootElement.TryGetProperty("installations", out _))
        {
            registry = JsonSerializer.Deserialize<ModInstalledArtifactRegistry>(contents, JsonOptions)
                ?? throw new InvalidDataException("The installed-mod registry is empty.");
        }
        else
        {
            var legacy = JsonSerializer.Deserialize<ModInstalledArtifactState>(contents, JsonOptions)
                ?? throw new InvalidDataException("The installed-mod state is empty.");
            ValidatePersistedInstalledState(legacy);
            registry = new(
                InstalledRegistrySchemaVersion,
                [legacy],
                DetachedAdoptionBackups: []);
        }

        return NormalizeAndValidateInstalledRegistry(registry);
    }

    private ModInstalledArtifactRegistry NormalizeAndValidateInstalledRegistry(
        ModInstalledArtifactRegistry registry)
    {
        if (registry.SchemaVersion != InstalledRegistrySchemaVersion
            || registry.Installations is null)
        {
            throw new InvalidDataException("The installed-mod registry is invalid or unsupported.");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var canonicalPaths = new HashSet<string>(comparison);
        var installations = new List<ModInstalledArtifactState>(registry.Installations.Count);
        foreach (var state in registry.Installations)
        {
            ValidatePersistedInstalledState(state);
            var normalizedState = NormalizeReleaseEvidence(state);
            ValidatePersistedInstalledState(normalizedState);
            var gameDirectory = NormalizeGameDirectory(normalizedState.GameDirectory);
            if (!canonicalPaths.Add(gameDirectory))
            {
                throw new InvalidDataException(
                    "The installed-mod registry contains duplicate canonical game installations.");
            }
            installations.Add(normalizedState with { GameDirectory = gameDirectory });
        }

        var detached = new List<ModDetachedAdoptionBackupState>();
        var detachmentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var backup in registry.DetachedAdoptionBackups ?? [])
        {
            ValidateDetachedAdoptionBackup(backup);
            if (!detachmentIds.Add(backup.DetachmentId))
            {
                throw new InvalidDataException(
                    "The installed-mod registry contains a duplicate detachment receipt.");
            }
            detached.Add(backup with { GameDirectory = NormalizeGameDirectory(backup.GameDirectory) });
        }

        return new(
            InstalledRegistrySchemaVersion,
            installations
                .OrderBy(state => state.GameDirectory, comparison)
                .ToArray(),
            detached
                .OrderBy(backup => backup.DetachedAtUtc)
                .ThenBy(backup => backup.DetachmentId, StringComparer.Ordinal)
                .ToArray());
    }

    private ModInstalledArtifactState NormalizeReleaseEvidence(ModInstalledArtifactState state)
    {
        var currentProductVersion = state.ReleaseProductVersion
            ?? InferReviewedReleaseProductVersion(state);
        var marks = (state.ReleaseHighWaterMarks ?? [])
            .Where(mark => currentProductVersion is null
                || !string.Equals(mark.ProviderId, state.ProviderId, StringComparison.Ordinal)
                || !string.Equals(
                    mark.ReleaseChannelId,
                    state.ReleaseChannelId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    mark.RuntimeDistributionId,
                    state.RuntimeDistributionId,
                    StringComparison.Ordinal))
            .GroupBy(mark => (
                mark.ProviderId,
                mark.ReleaseChannelId,
                mark.RuntimeDistributionId))
            .Select(group => group
                .OrderByDescending(mark =>
                    mark.ReleaseProductVersion,
                    Comparer<string>.Create(
                        WindowsReleaseSelectionPolicy.CompareProductReleaseOrderingVersions))
                .First())
            .OrderBy(mark => mark.ProviderId, StringComparer.Ordinal)
            .ThenBy(mark => mark.ReleaseChannelId, StringComparer.Ordinal)
            .ThenBy(mark => mark.RuntimeDistributionId, StringComparer.Ordinal)
            .ToArray();
        return state with
        {
            ReleaseProductVersion = currentProductVersion,
            ReleaseHighWaterMarks = marks.Length == 0 ? null : marks,
        };
    }

    private string? InferReviewedReleaseProductVersion(ModInstalledArtifactState state)
    {
        var matches = reviewedReleaseEvidence.Where(certification =>
                IsOrderableReleaseProductVersion(certification.Tag)
                && certification.ProviderId == state.ProviderId
                && certification.ChannelId == state.ReleaseChannelId
                && certification.RuntimeDistributionId == state.RuntimeDistributionId
                && certification.PayloadFileName.Equals(
                    state.FileName,
                    StringComparison.OrdinalIgnoreCase)
                && certification.PayloadSize == state.Size
                && certification.PayloadSha256.Equals(
                    state.Sha256,
                    StringComparison.OrdinalIgnoreCase)
                && certification.PayloadVersion == state.Version
                && (certification.RuntimeManifest is null
                    ? state.RuntimeManifest is null
                    : state.RuntimeManifest is not null
                        && certification.RuntimeManifest.FileName == state.RuntimeManifest.FileName
                        && certification.RuntimeManifest.Size == state.RuntimeManifest.Size
                        && certification.RuntimeManifest.Sha256.Equals(
                            state.RuntimeManifest.Sha256,
                            StringComparison.OrdinalIgnoreCase)
                        && certification.SourceCommit.Equals(
                            state.RuntimeManifest.SourceRevision,
                            StringComparison.OrdinalIgnoreCase)
                        && certification.Repository.Equals(
                            state.RuntimeManifest.Repository,
                            StringComparison.Ordinal)
                        && certification.Tag.Equals(
                            state.RuntimeManifest.Tag,
                            StringComparison.Ordinal)))
            .Select(certification => certification.Tag)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException(
                "The installed-mod state matches multiple reviewed release identities."),
        };
    }

    private void ValidateDetachedAdoptionBackup(ModDetachedAdoptionBackupState backup)
    {
        if (backup is null
            || !Guid.TryParseExact(backup.DetachmentId, "N", out _)
            || !Path.IsPathFullyQualified(backup.GameDirectory)
            || !IsStableIdentity(backup.ProviderId)
            || !IsStableIdentity(backup.ReleaseChannelId)
            || !IsStableIdentity(backup.RuntimeDistributionId)
            || string.IsNullOrWhiteSpace(backup.PreviousArtifactBackupPath)
                && string.IsNullOrWhiteSpace(backup.PreviousRuntimeManifestBackupPath))
        {
            throw new InvalidDataException("The detached adoption-backup receipt is invalid.");
        }

        ValidateDetachedBackupMember(
            backup.PreviousArtifactBackupPath,
            backup.PreviousArtifactBackupIdentity,
            "detached adopted DLL");
        ValidateDetachedBackupMember(
            backup.PreviousRuntimeManifestBackupPath,
            backup.PreviousRuntimeManifestBackupIdentity,
            "detached adopted runtime manifest");
    }

    private void ValidateDetachedBackupMember(
        string? path,
        ModArtifactIdentityReceipt? identity,
        string subject)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (identity is not null)
            {
                throw new InvalidDataException($"The {subject} identity has no backup path.");
            }
            return;
        }
        if (!Path.IsPathFullyQualified(path)
            || !IsContainedBy(Path.Combine(stateDirectory, "rollback"), path))
        {
            throw new InvalidDataException($"The {subject} path escapes Mod Bridge-owned state.");
        }
        ValidateIdentityReceipt(identity, subject);
    }

    private void WriteInstalledRegistry(ModInstalledArtifactRegistry registry)
    {
        var normalized = NormalizeAndValidateInstalledRegistry(registry);
        if (normalized.Installations.Count == 0
            && (normalized.DetachedAdoptionBackups?.Count ?? 0) == 0)
        {
            DeleteIfExists(InstalledStatePath);
            return;
        }
        WriteJsonAtomically(InstalledStatePath, normalized);
    }

    private void UpsertInstalledState(ModInstalledArtifactState state)
    {
        ValidatePersistedInstalledState(state);
        var registry = ReadInstalledRegistry();
        var installations = registry.Installations
            .Where(existing => !PathEquals(existing.GameDirectory, state.GameDirectory))
            .Append(state with { GameDirectory = NormalizeGameDirectory(state.GameDirectory) })
            .ToArray();
        WriteInstalledRegistry(registry with { Installations = installations });
    }

    private void RemoveInstalledState(string gameDirectory)
    {
        var registry = ReadInstalledRegistry();
        var installations = registry.Installations
            .Where(state => !PathEquals(state.GameDirectory, gameDirectory))
            .ToArray();
        WriteInstalledRegistry(registry with { Installations = installations });
    }

    private void DetachInstalledState(
        string gameDirectory,
        ModDetachedAdoptionBackupState? retainedBackup)
    {
        var registry = ReadInstalledRegistry();
        var installations = registry.Installations
            .Where(state => !PathEquals(state.GameDirectory, gameDirectory))
            .ToArray();
        var detached = (registry.DetachedAdoptionBackups ?? [])
            .Concat(retainedBackup is null ? [] : [retainedBackup])
            .ToArray();
        WriteInstalledRegistry(registry with
        {
            Installations = installations,
            DetachedAdoptionBackups = detached,
        });
    }

    private static ModInstalledArtifactRegistry EmptyInstalledRegistry() =>
        new(InstalledRegistrySchemaVersion, [], DetachedAdoptionBackups: []);

    private static void ValidateRuntimeManifestDiscovery(ModRuntimeManifestArtifact artifact)
    {
        if (artifact.FileName != ArtifactBoundRuntimeManifestParser.ManagedFileName
            || artifact.Size is <= 0 or > ArtifactBoundRuntimeManifestParser.MaximumManifestBytes
            || !TryNormalizeSha256(artifact.Sha256, out _)
            || artifact.ExpectedSourceRevision is not { Length: 40 }
            || !artifact.ExpectedSourceRevision.All(Uri.IsHexDigit)
            || artifact.ExpectedRepository is not { Length: > 0 and <= 160 }
            || artifact.ExpectedRepository.Count(character => character == '/') != 1
            || artifact.ExpectedTag is not { Length: > 0 and <= 160 }
            || artifact.DownloadUri is null
            || !artifact.DownloadUri.IsAbsoluteUri
            || artifact.DownloadUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The deployment journal contains unsafe runtime-manifest metadata.");
        }
    }

    private static bool IsStableIdentity(string? value) =>
        value is not null
        && value.Length is > 0 and <= 96
        && (char.IsAsciiDigit(value[0]) || char.IsAsciiLetterLower(value[0]))
        && value.All(character =>
            char.IsAsciiDigit(character)
            || char.IsAsciiLetterLower(character)
            || character is '-' or '_' or '.');

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            NormalizeGameDirectory(left),
            NormalizeGameDirectory(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizeGameDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return root is not null && fullPath.Length > root.Length
            ? Path.TrimEndingDirectorySeparator(fullPath)
            : fullPath;
    }

    private static bool IsContainedBy(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathFullyQualified(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions);
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

}
