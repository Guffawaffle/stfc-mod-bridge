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
            or InvalidDataException;

    private void ValidatePersistedJournal(ModDeploymentJournal journal)
    {
        if (journal is null
            || journal.Artifact is null
            || journal.TransactionId is null
            || journal.GameDirectory is null
            || journal.StagePath is null
            || journal.SameVolumeBackupPath is null
            || journal.DurableBackupPath is null
            || journal.SchemaVersion != DeploymentJournalSchemaVersion
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
        if (journal.TargetInstallationAttribution is not null
            && (!IsStableIdentity(journal.TargetInstallationAttribution.ProviderId)
                || !IsStableIdentity(journal.TargetInstallationAttribution.ReleaseChannelId)
                || !IsStableIdentity(journal.TargetInstallationAttribution.RuntimeDistributionId)))
        {
            throw new InvalidDataException("The deployment journal target attribution is invalid.");
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
            || !TryNormalizeSha256(state.Sha256, out _)
            || !IsStableIdentity(state.ProviderId)
            || !IsStableIdentity(state.ReleaseChannelId)
            || !IsStableIdentity(state.RuntimeDistributionId))
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
            || !TryNormalizeSha256(receipt.Sha256, out _))
        {
            throw new InvalidDataException($"The {subject} identity receipt is invalid or missing.");
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
            var gameDirectory = NormalizeGameDirectory(state.GameDirectory);
            if (!canonicalPaths.Add(gameDirectory))
            {
                throw new InvalidDataException(
                    "The installed-mod registry contains duplicate canonical game installations.");
            }
            installations.Add(state with { GameDirectory = gameDirectory });
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
