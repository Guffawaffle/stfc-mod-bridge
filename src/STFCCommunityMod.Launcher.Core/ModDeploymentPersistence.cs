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
            || journal.SchemaVersion != SchemaVersion
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
            || state.SchemaVersion != SchemaVersion
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
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
